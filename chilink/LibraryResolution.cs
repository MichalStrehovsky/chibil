using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

internal sealed partial class Linker
{
    private sealed class ArchiveExpansionException : Exception { }

    private readonly List<InputLibrary> _libraries = new();
    private readonly Dictionary<string, NativeImport> _nativeImports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _libraryWarnings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedScripts = new(LinkOptions.PathComparer);

    private static readonly string[] LinuxLibraryPaths =
    [
        "/usr/local/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu",
        "/usr/local/lib64", "/lib64", "/usr/lib64", "/usr/local/lib", "/lib", "/usr/lib",
    ];

    private (string Path, string RuntimeName) ResolveLibraryPath(string name)
    {
        string[] filenames = name.StartsWith(':') ? [name[1..]] :
            OperatingSystem.IsLinux() ?
            [$"lib{name}.so", $"lib{name}.a", $"lib{name}.dll.a", $"{name}.dll.a", $"{name}.lib", $"lib{name}.lib"] :
            [$"lib{name}.dll.a", $"{name}.dll.a", $"lib{name}.a", $"{name}.lib", $"lib{name}.lib"];
        var directories = _options.LibraryPaths.Concat(OperatingSystem.IsLinux() ? LinuxLibraryPaths : []);
        foreach (string directory in directories)
        {
            foreach (string filename in filenames)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, filename));
                if (File.Exists(candidate))
                    return (candidate, filename);
            }
        }
        throw new LinkException($"cannot find library '{name}' in the " +
            (OperatingSystem.IsLinux() ? "-L or built-in Linux search directories" : "-L search directories"));
    }

    private (string Path, string RuntimeName) ResolveScriptInput(string script, string name)
    {
        if (name.StartsWith('=') || name.StartsWith("$SYSROOT", StringComparison.Ordinal))
            throw new LinkException($"{script}: linker script: sysroot paths are not supported ('{name}')");
        if (name.StartsWith("-l", StringComparison.Ordinal) && name.Length > 2)
        {
            try
            {
                return ResolveLibraryPath(name[2..]);
            }
            catch (LinkException ex)
            {
                throw new LinkException($"{script}: linker script: {ex.Message}");
            }
        }
        if (name.StartsWith('-'))
            throw new LinkException($"{script}: linker script: unsupported input option '{name}'");
        if (Path.IsPathRooted(name))
        {
            if (File.Exists(name))
                return (Path.GetFullPath(name), name);
        }
        else
        {
            var directories = new[] { Path.GetDirectoryName(script), Directory.GetCurrentDirectory() }
                .Concat(_options.LibraryPaths).Concat(OperatingSystem.IsLinux() ? LinuxLibraryPaths : []);
            foreach (string directory in directories)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, name));
                if (File.Exists(candidate))
                    return (candidate, candidate);
            }
        }
        throw new LinkException($"{script}: linker script: cannot find input '{name}'");
    }

    private void AddLibrary(string path, string runtimeName, HashSet<string> resolvedPaths,
        HashSet<string> activeScripts)
    {
        if (activeScripts.Contains(path))
            throw new LinkException($"{path}: linker script cycle detected");
        resolvedPaths.Add(path);
        if (_expandedScripts.Contains(path) || _libraries.Any(l => LinkOptions.PathComparer.Equals(l.Path, path)))
            return;
        var library = new InputLibrary(path, runtimeName);
        if (library.ScriptInputs is { } inputs)
        {
            if (activeScripts.Count >= 64)
                throw new LinkException($"{path}: linker script nesting limit exceeded");
            activeScripts.Add(path);
            try
            {
                foreach (string name in inputs)
                {
                    var (resolved, identity) = ResolveScriptInput(path, name);
                    if (Path.GetExtension(resolved).Equals(".a", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPaths.Add(resolved);
                        _libraryWarnings.Add($"{path}: ignoring linker-script static archive '{resolved}'; " +
                            "native static linking is not supported");
                    }
                    else
                        AddLibrary(resolved, identity, resolvedPaths, activeScripts);
                }
                _expandedScripts.Add(path);
            }
            finally
            {
                activeScripts.Remove(path);
            }
        }
        else
            _libraries.Add(library);
    }

    private void AddInput(InputFile input) => _inputs.Add(input);

    private void ResetAnalysis()
    {
        _symbols.Clear();
        _definitions.Clear();
        _live.Clear();
        _pending.Clear();
        _fieldStorage.Clear();
        _fieldSizes.Clear();
        _dataRelocations.Clear();
        _addressTakenFields.Clear();
        _nativeResolver = default;
        foreach (InputSection section in _inputs.SelectMany(i => i.Sections))
        {
            section.Winner = section;
            section.Live = false;
            section.OutputCodeOffset = section.OutputDataOffset = -1;
        }
        SelectComdats();
        _merger = new MetadataMerger(_inputs.Select(i => i.Metadata).ToArray());
        IndexDefinitions();
    }

    private IEnumerable<InputLibrary> SearchLibraries(InputLibrary origin)
    {
        int start = origin == null ? 0 : _libraries.IndexOf(origin);
        for (int i = 0; i < _libraries.Count; i++)
            yield return _libraries[(start + i) % _libraries.Count];
    }

    private bool SelectLibrarySymbol(string symbol, InputLibrary origin)
    {
        foreach (InputLibrary library in SearchLibraries(origin))
            if (SelectLibrarySymbol(library, symbol))
                return true;
        return false;
    }

    private bool SelectLibrarySymbol(InputLibrary library, string symbol)
    {
        if (!library.TrySelect(symbol, _inputs.Count, out InputFile input, out NativeImport import))
            return false;
        if (input != null)
            AddInput(input);
        else
        {
            foreach (string name in import.PointerSymbol == null ? new[] { import.Symbol } :
                new[] { import.Symbol, import.PointerSymbol })
            {
                if (_nativeImports.TryGetValue(name, out var previous) && previous != import)
                    throw new LinkException($"conflicting native providers for '{name}' in {previous.Source} and {import.Source}");
                _nativeImports[name] = import;
            }
        }
        return true;
    }

    private bool ExpandLibraries()
    {
        foreach (var reference in _merger.GetUnresolvedGlobals()
            .OrderBy(r => _inputs[r.Token.Input].Library == null ? r.Token.Input : -r.Token.Input - 1).ToArray())
        {
            string[] names = _merger.GetLibrarySymbols(reference).Distinct().ToArray();
            NativeImport import = names.Select(n => _nativeImports.GetValueOrDefault(n)).FirstOrDefault(i => i != null);
            if (import == null)
            {
                int inputCount = _inputs.Count;
                var origin = _inputs[reference.Token.Input].Library;
                if (!SearchLibraries(origin).Any(library => names.Any(name => SelectLibrarySymbol(library, name))))
                    continue;
                if (_inputs.Count != inputCount)
                    return true;
                import = names.Select(n => _nativeImports.GetValueOrDefault(n)).First(i => i != null);
            }
            _merger.BindImport(reference, import);
        }
        foreach (string root in _options.Includes.Concat(_options.Dll ? [] : new[] { _options.Entry ?? "mainCRTStartup" }))
            if (!HasSymbol(root) && SelectLibrarySymbol(root, null))
                return true;
        return false;
    }

    private bool HasSymbol(string name)
    {
        if (_symbols.ContainsKey(name))
            return true;
        return _definitions.Keys.Any(t => t.Handle.Kind == HandleKind.MethodDefinition &&
            _inputs[t.Input].Metadata.GetMethodDefinition((MethodDefinitionHandle)t.Handle).GetDeclaringType() ==
                MetadataTokens.TypeDefinitionHandle(1) &&
            _inputs[t.Input].Metadata.GetString(_inputs[t.Input].Metadata.GetMethodDefinition(
                (MethodDefinitionHandle)t.Handle).Name) == name);
    }
}
