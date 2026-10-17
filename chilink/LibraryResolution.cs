using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace Chilink;

internal sealed partial class Linker
{
    private sealed class ArchiveExpansionException : Exception { }

    private readonly List<InputLibrary> _libraries = new();
    private readonly Dictionary<string, NativeImport> _nativeImports = new(StringComparer.Ordinal);

    private string ResolveLibraryPath(string path)
    {
        if (Path.GetFileName(path) != path)
            return Path.GetFullPath(path);
        foreach (string directory in new[] { Directory.GetCurrentDirectory() }
            .Concat(_options.LibraryPaths)
            .Concat((_options.LibraryEnvironment ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)))
        {
            string candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), path));
            if (File.Exists(candidate))
                return candidate;
        }
        throw new LinkException($"cannot find library '{path}' in the working directory, /LIBPATH, or LIB");
    }

    private void AddInput(InputFile input)
    {
        _inputs.Add(input);
        foreach (InputSection section in input.Sections.Where(s => s.Name == ".drectve"))
            foreach (string arg in Driver.SplitArguments(Encoding.UTF8.GetString(section.GetData()).TrimStart('\uFEFF')))
                Driver.Parse(_options, arg, directive: true);
    }

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
            foreach (string name in new[] { import.Symbol, "__imp_" + import.Symbol })
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
            if (import != null)
            {
                _merger.BindImport(reference, import);
                continue;
            }
            var origin = _inputs[reference.Token.Input].Library;
            foreach (var library in SearchLibraries(origin))
                foreach (string name in names)
                    if (SelectLibrarySymbol(library, name))
                        return true;
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
