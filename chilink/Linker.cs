using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Coff;

namespace Chilink;

internal sealed partial class Linker
{
    private const int PointerSize = 8; // Only x64 inputs are supported.
    private readonly LinkOptions _options;
    private readonly List<InputFile> _inputs = new();
    private readonly Dictionary<string, Definition> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceToken, Definition> _definitions = new();
    private readonly HashSet<SourceToken> _live = new();
    private readonly Queue<SourceToken> _pending = new();
    private MetadataMerger _merger;
    private SourceToken _nativeResolver;
    private const string NativeResolverSymbol = "__chilink_get_native_export";

    public Linker(LinkOptions options) => _options = options;

    public LinkResult Link()
    {
        try
        {
            var resolvedPaths = new HashSet<string>(LinkOptions.PathComparer);
            foreach (LinkInput input in _options.Inputs)
            {
                var (resolved, runtimeName) = input.SearchLibrary ? ResolveLibraryPath(input.Name) :
                    (Path.GetFullPath(input.Name), input.Name);
                string extension = Path.GetExtension(resolved);
                bool library = input.SearchLibrary ||
                    extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".a", StringComparison.OrdinalIgnoreCase) ||
                    LinkOptions.IsSharedLibraryPath(resolved);
                if (library)
                    AddLibrary(resolved, runtimeName, resolvedPaths, new HashSet<string>(LinkOptions.PathComparer));
                else
                {
                    resolvedPaths.Add(resolved);
                    AddInput(new InputFile(resolved, _inputs.Count));
                }
            }
            while (true)
            {
                if (_options.Dll && _options.Entry != null)
                    throw new LinkException("--entry is not supported for a shared managed library");
                if (resolvedPaths.Contains(Path.GetFullPath(_options.Output)))
                    throw new LinkException("output file must not overwrite an input");
                if (_options.Debug && !_options.EmbeddedPdb)
                {
                    _options.Pdb = Path.GetFullPath(string.IsNullOrEmpty(_options.Pdb)
                        ? Path.ChangeExtension(_options.Output, ".pdb") : _options.Pdb);
                    if (LinkOptions.PathComparer.Equals(_options.Pdb, Path.GetFullPath(_options.Output)) ||
                        resolvedPaths.Contains(_options.Pdb) ||
                        _libraries.Any(l => LinkOptions.PathComparer.Equals(l.Path, _options.Pdb)))
                        throw new LinkException("PDB file must not overwrite the output or an input");
                }
                if (_inputs.Count == 0)
                {
                    var roots = _options.Includes.Concat(_options.Dll ? [] : new[] { _options.Entry ?? "mainCRTStartup" });
                    if (roots.Any(root => SelectLibrarySymbol(root, null)))
                        continue;
                    throw new LinkException("no managed object selected from input libraries; specify an exact COFF --entry or --undefined symbol");
                }
                ResetAnalysis();
                if (ExpandLibraries())
                    continue;
                _merger.ValidateReferences();

                try
                {
                    SourceToken entry = default;
                    if (!_options.Dll)
                    {
                        entry = FindSymbol(_options.Entry ?? "mainCRTStartup");
                        ValidateEntry(entry);
                        Mark(entry);
                    }
                    foreach (string symbol in _options.Includes)
                        Mark(FindSymbol(symbol));
                    foreach (SourceToken root in _merger.GetRoots())
                        Mark(root);
                    foreach (var pair in _definitions)
                        if (pair.Value.IsCommon)
                            Mark(pair.Key);
                    foreach (InputSection section in _inputs.SelectMany(i => i.Sections))
                        if (!section.Ignored && (!section.IsComdat || !_options.GcSections) &&
                            section.Header.SizeOfRawData > 0)
                            MarkSection(section);
                    DrainPending();
                    if (_dataRelocations.Any(r => r.Import != null))
                    {
                        if (!HasSymbol(NativeResolverSymbol) && SelectLibrarySymbol(NativeResolverSymbol, null))
                            continue;
                        _nativeResolver = FindSymbol(NativeResolverSymbol);
                        ValidateNativeResolver(_nativeResolver);
                        Mark(_nativeResolver);
                        DrainPending();
                    }
                    ValidateFieldAddresses();
                    return Emit(entry);
                }
                catch (ArchiveExpansionException)
                {
                    // A live data relocation selected a member; rebuild all token identities.
                    continue;
                }
            }
        }
        finally
        {
            foreach (InputFile input in _inputs)
                input.Dispose();
            foreach (InputLibrary library in _libraries)
                library.Dispose();
        }
    }

    private void DrainPending()
    {
        while (_pending.TryDequeue(out SourceToken token))
        {
            foreach (SourceToken dependency in _merger.GetDependencies(token))
                Mark(dependency);
            if (_merger.GetImport(token) != null)
                continue;
            if (!_definitions.TryGetValue(token, out Definition definition))
            {
                if (token.Handle.Kind == HandleKind.MethodDefinition)
                {
                    var md = _inputs[token.Input].Metadata.GetMethodDefinition((MethodDefinitionHandle)token.Handle);
                    if ((md.Attributes & MethodAttributes.Abstract) == 0 &&
                        (md.ImplAttributes & (MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall)) == 0)
                        throw _inputs[token.Input].Error($"missing body for method '{_inputs[token.Input].Metadata.GetString(md.Name)}'");
                }
                else if (token.Handle.Kind == HandleKind.FieldDefinition &&
                    (_inputs[token.Input].Metadata.GetFieldDefinition((FieldDefinitionHandle)token.Handle).Attributes & FieldAttributes.HasFieldRVA) != 0)
                    throw _inputs[token.Input].Error("RVA field has no COFF storage definition");
                continue;
            }
            if (definition.Section == null && !definition.IsCommon)
                throw _inputs[token.Input].Error($"common or undefined storage '{definition.Symbol.Name}' is not supported");
            if (definition.Section != null)
                MarkSection(definition.Section);
            if (token.Handle.Kind == HandleKind.MethodDefinition)
            {
                var method = definition.Section.Input.Metadata.GetMethodDefinition((MethodDefinitionHandle)definition.Token.Handle);
                if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
                    (method.ImplAttributes & MethodImplAttributes.Unmanaged) != 0 || !definition.Section.IsCode)
                    throw definition.Section.Input.Error($"native method '{definition.Symbol.Name}' is not supported");
                if (definition.Offset < 0 || definition.Offset >= definition.Section.Header.SizeOfRawData)
                    throw definition.Section.Input.Error($"method '{definition.Symbol.Name}' is outside its section");
            }
            else if (token.Handle.Kind == HandleKind.FieldDefinition)
                GetFieldStorage(token);
        }
    }

    private void ValidateNativeResolver(SourceToken token)
    {
        if (token.Handle.Kind == HandleKind.MethodDefinition)
        {
            var reader = _inputs[token.Input].Metadata;
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)token.Handle);
            if ((method.Attributes & MethodAttributes.Static) != 0 &&
                reader.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(new byte[] {
                    0, 2, (byte)SignatureTypeCode.IntPtr, (byte)SignatureTypeCode.String, (byte)SignatureTypeCode.String }))
                return;
        }
        throw new LinkException($"{NativeResolverSymbol} must be a static managed method returning native int and accepting (string, string)");
    }

    private void SelectComdats()
    {
        var comdats = new Dictionary<string, InputSection>(StringComparer.Ordinal);
        foreach (InputSection section in _inputs.SelectMany(i => i.Sections))
        {
            if (section.Ignored || !section.IsComdat || section.Selection == CoffComdatSelection.Associative)
                continue;
            if (section.Selection is not (CoffComdatSelection.Any or CoffComdatSelection.NoDuplicates))
                throw section.Input.Error($"unsupported COMDAT selection {section.Selection} in '{section.Name}'");
            InputSymbol symbol = section.Symbols.FirstOrDefault(s => s.Coff.StorageClass == CoffSymbolStorageClass.External)
                ?? section.Symbols.FirstOrDefault(s => s.Coff.StorageClass == CoffSymbolStorageClass.Static &&
                    s.Coff.NumberOfAuxSymbols == 0);
            if (symbol == null)
                throw section.Input.Error($"COMDAT '{section.Name}' has no selection symbol");
            // Static selection symbols are local, even when their names match in other objects.
            if (symbol.Coff.StorageClass == CoffSymbolStorageClass.Static)
                continue;
            string key = symbol.Name;
            if (comdats.TryGetValue(key, out InputSection previous))
            {
                if (section.Selection != previous.Selection || section.Selection == CoffComdatSelection.NoDuplicates)
                    throw section.Input.Error($"duplicate symbol '{key}' (also in {previous.Input.Path})");
                section.Winner = previous;
            }
            else comdats.Add(key, section);
        }
        foreach (InputSection section in _inputs.SelectMany(i => i.Sections))
        {
            var visited = new HashSet<InputSection>();
            for (InputSection s = section; s.Associated != null; s = s.Associated)
                if (!visited.Add(s))
                    throw section.Input.Error("cyclic associative COMDAT sections");
        }
    }

    private void IndexDefinitions()
    {
        foreach (InputFile input in _inputs)
            foreach (Definition definition in input.Definitions.Values)
            {
                if (definition.Section?.Ignored == true || definition.Section?.Winner != definition.Section)
                    continue;
                SourceToken canonical = _merger.Resolve(definition.Token);
                if (definition.Symbol.Coff.StorageClass != CoffSymbolStorageClass.External ||
                    (canonical.Handle.Kind == HandleKind.MethodDefinition && _merger.HasUnmanagedCallersOnly(canonical)))
                {
                    _definitions.TryAdd(canonical, definition);
                    continue;
                }
                string name = definition.Symbol.Name;
                if (_symbols.TryGetValue(name, out Definition previous))
                {
                    if (_merger.Resolve(previous.Token) != canonical ||
                        (previous.IsCommon && previous.Symbol.Coff.Value != FieldSize(canonical)) ||
                        (definition.IsCommon && definition.Symbol.Coff.Value != FieldSize(canonical)))
                        throw input.Error($"incompatible storage for symbol '{name}'");
                    if (!previous.IsCommon && !definition.IsCommon)
                        throw input.Error($"duplicate symbol '{name}' (also in {_inputs[previous.Token.Input].Path})");
                    if (!previous.IsCommon)
                        continue;
                }
                _symbols[name] = definition;
                _definitions[canonical] = definition;
            }
    }

    private SourceToken FindSymbol(string name)
    {
        if (_symbols.TryGetValue(name, out Definition definition))
            return _merger.Resolve(definition.Token);
        // --entry=main names the managed method even though the bare COFF symbol
        // in /clr objects points at its unused native thunk.
        var candidates = _definitions.Keys.Where(t =>
        {
            if (t.Handle.Kind != HandleKind.MethodDefinition)
                return false;
            var md = _inputs[t.Input].Metadata.GetMethodDefinition((MethodDefinitionHandle)t.Handle);
            return _inputs[t.Input].Metadata.GetString(md.Name) == name &&
                MetadataTokens.GetRowNumber(md.GetDeclaringType()) == 1;
        }).ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        throw new LinkException(candidates.Length == 0
            ? $"unresolved managed symbol '{name}'"
            : $"ambiguous managed symbol '{name}'; specify its decorated COFF name");
    }

    private void ValidateEntry(SourceToken entry)
    {
        if (entry.Handle.Kind != HandleKind.MethodDefinition)
            throw new LinkException("entrypoint must be a managed method");
        var reader = _inputs[entry.Input].Metadata;
        var method = reader.GetMethodDefinition((MethodDefinitionHandle)entry.Handle);
        var sig = reader.GetBlobReader(method.Signature);
        var header = sig.ReadSignatureHeader();
        bool valid = (method.Attributes & MethodAttributes.Static) != 0 &&
            !header.IsGeneric && !header.IsInstance && header.CallingConvention == SignatureCallingConvention.Default;
        if (!valid)
            throw new LinkException("entrypoint must be a static, non-generic managed method");
        int count = sig.ReadCompressedInteger();
        SignatureTypeCode ReadType()
        {
            SignatureTypeCode type = sig.ReadSignatureTypeCode();
            while (type is SignatureTypeCode.OptionalModifier or SignatureTypeCode.RequiredModifier)
            {
                sig.ReadTypeHandle();
                type = sig.ReadSignatureTypeCode();
            }
            return type;
        }
        valid = ReadType() is SignatureTypeCode.Void or SignatureTypeCode.Int32;
        if (count == 1)
            valid &= ReadType() == SignatureTypeCode.SZArray && ReadType() == SignatureTypeCode.String;
        else valid &= count == 0;
        if (!valid || sig.RemainingBytes != 0)
            throw new LinkException("entrypoint must return void or int and accept no arguments or string[]");
    }

    private void Mark(SourceToken token)
    {
        if (token.Handle.IsNil)
            return;
        token = _merger.Resolve(token);
        if (_live.Add(token))
            _pending.Enqueue(token);
    }

    private void MarkSection(InputSection section)
    {
        section = section.Winner;
        if (section.Ignored)
            throw section.Input.Error($"retained dependency requires unsupported native/IJW section '{section.Name}'");
        if (section.Associated != null)
        {
            if (section.Associated.Winner != section.Associated)
                return;
            MarkSection(section.Associated);
        }
        if (section.Live)
            return;
        section.Live = true;
        var definitions = section.Input.Definitions.Values.Where(d => d.Section == section &&
            !section.IsUnsupportedVtableFixup(d.Offset)).ToArray();
        if (definitions.Length == 0 && section.Header.SizeOfRawData > 0 && section.VtableFixups.Count == 0)
            throw section.Input.Error($"unsupported retained section '{section.Name}' without managed definitions");
        foreach (Definition definition in definitions)
            Mark(definition.Token);
        var managedSlots = new HashSet<uint>();
        foreach (var fixup in section.VtableFixups.Where(f => f.Type == 0x02))
            for (int offset = fixup.Offset; offset < (long)fixup.Offset + fixup.Size; offset += PointerSize)
                if (!managedSlots.Add((uint)offset))
                    throw section.Input.Error($"overlapping managed vtfixup slots in '{section.Name}'");
        for (int i = 0; i < section.Relocations.NumberOfRelocations; i++)
        {
            CoffRelocation relocation = section.Relocations.GetRelocation(i);
            if (!section.IsCode)
            {
                if (!section.IsUnsupportedVtableFixup(relocation.Offset))
                {
                    MarkDataRelocation(section, definitions, relocation);
                    managedSlots.Remove(relocation.Offset);
                }
                continue;
            }
            if (relocation.Type != ImageRelocation.Amd64_TOKEN)
                throw section.Input.Error($"unsupported relocation {relocation.Type} in retained section '{section.Name}'");
            InputSymbol symbol = section.Input.Symbols[relocation.Symbol];
            if (symbol.Coff.StorageClass != CoffSymbolStorageClass.ClrToken || symbol.Token.IsNil)
                throw section.Input.Error("TOKEN relocation does not reference a valid CLR token");
            byte[] data = section.GetData();
            if (relocation.Offset > data.Length - 4)
                throw section.Input.Error($"TOKEN relocation extends beyond section '{section.Name}'");
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)relocation.Offset, 4)) != 0)
                throw section.Input.Error("nonzero TOKEN relocation addends are not supported");
            if (symbol.Token.Kind != HandleKind.UserString)
            {
                var token = _merger.Resolve(new SourceToken(section.Input.Index, (EntityHandle)symbol.Token));
                if (token.Handle.Kind == HandleKind.FieldDefinition && relocation.Offset > 0 &&
                    data[relocation.Offset - 1] == (byte)ILOpCode.Ldsflda)
                    _addressTakenFields.Add(token);
                Mark(token);
            }
        }
        if (managedSlots.Count != 0)
            throw section.Input.Error($"managed vtfixup slot requires a TOKEN relocation in '{section.Name}'");
        foreach (InputSection child in section.Input.Sections)
            if (child.Associated == section && !child.Ignored)
                MarkSection(child);
    }

    private LinkResult Emit(SourceToken entry)
    {
        var codeSections = _inputs.SelectMany(i => i.Sections).Where(s => s.Live && s.IsCode).ToArray();
        int ilSize = 0, codeAlignment = 4;
        foreach (InputSection section in codeSections)
        {
            int alignment = Math.Max(4, Alignment(section));
            codeAlignment = Math.Max(codeAlignment, alignment);
            ilSize = Align(ilSize, alignment);
            section.OutputCodeOffset = ilSize;
            ilSize = checked(ilSize + section.Header.SizeOfRawData);
        }
        var data = new BlobBuilder();
        int dataAlignment = ManagedPEBuilder.MappedFieldDataAlignment;
        foreach (InputSection section in _inputs.SelectMany(i => i.Sections))
        {
            if (!section.Live || section.IsCode || !_fieldStorage.Values.Any(f =>
                _live.Contains(f.Token) && !f.Convert && f.Definition.Section == section))
                continue;
            int alignment = Alignment(section);
            dataAlignment = Math.Max(dataAlignment, alignment);
            data.Align(alignment);
            section.OutputDataOffset = data.Count;
            data.WriteBytes(section.GetData());
        }
        var initialization = PlanInitialization(data);
        initialization.CodeOffset = Align(ilSize, 4);
        var md = new MetadataBuilder();
        md.AddModule(0, md.GetOrAddString(Path.GetFileName(_options.Output)), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        md.AddAssembly(md.GetOrAddString(Path.GetFileNameWithoutExtension(_options.Output)),
            new Version(0, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        _merger.Build(md, _live,
            token => _definitions.TryGetValue(_merger.Resolve(token), out Definition method) && method.Section?.OutputCodeOffset >= 0
                ? checked(method.Section.OutputCodeOffset + method.Offset) : -1,
            token => _definitions.TryGetValue(_merger.Resolve(token), out Definition d) && d.Section?.OutputDataOffset >= 0
                ? checked(d.Section.OutputDataOffset + d.Offset) : -1, initialization,
            _options.Debug ? token => _definitions.GetValueOrDefault(token)?.Token : null);
        var entryHandle = _options.Dll ? default : (MethodDefinitionHandle)_merger.Map(entry.Input, entry.Handle);
        var warnings = new HashSet<string>(_libraryWarnings, StringComparer.Ordinal);
        PortablePdb pdb = _options.Debug
            ? PortablePdbEmitter.Emit(md, _merger.OutputMethods, ReadDebugInformation(warnings), entryHandle, _merger)
            : null;
        DebugDirectoryBuilder debugDirectory = null;
        byte[] pdbBytes = null;
        if (pdb != null)
        {
            const ushort portablePdbVersion = 0x0100;
            byte[] serializedPdb = pdb.Image.ToArray();
            debugDirectory = new DebugDirectoryBuilder();
            string pdbPath = _options.EmbeddedPdb
                ? Path.ChangeExtension(Path.GetFileName(_options.Output), ".pdb") : _options.Pdb;
            debugDirectory.AddCodeViewEntry(pdbPath, pdb.Id, portablePdbVersion);
            debugDirectory.AddPdbChecksumEntry("SHA256", ImmutableArray.Create(SHA256.HashData(serializedPdb)));
            if (_options.EmbeddedPdb)
                debugDirectory.AddEmbeddedPortablePdbEntry(pdb.Image, portablePdbVersion);
            else
                pdbBytes = serializedPdb;
        }
        var il = new BlobBuilder();
        foreach (InputSection section in codeSections)
        {
            il.WriteBytes(0, section.OutputCodeOffset - il.Count);
            byte[] bytes = section.GetData();
            for (int i = 0; i < section.Relocations.NumberOfRelocations; i++)
            {
                CoffRelocation relocation = section.Relocations.GetRelocation(i);
                Handle token = section.Input.Symbols[relocation.Symbol].Token;
                int mapped = MetadataTokens.GetToken(_merger.Map(section.Input.Index, token));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)relocation.Offset, 4), mapped);
            }
            il.WriteBytes(bytes);
        }
        if (initialization.HasInitializer)
            initialization.Emit(il, _merger);
        ilSize = il.Count;
        int dataSize = data.Count;
        // SRM guarantees 4-byte IL and 8-byte mapped-data alignment. Reserve room
        // to honor larger COFF alignments once the actual stream RVAs are known.
        if (ilSize != 0) il.WriteBytes(0, codeAlignment - 4);
        if (dataSize != 0) data.WriteBytes(0, dataAlignment - ManagedPEBuilder.MappedFieldDataAlignment);
        var header = new PEHeaderBuilder(machine: Machine.Amd64,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware |
                (_options.Dll ? Characteristics.Dll : 0),
            subsystem: _options.Subsystem);
        var pe = new ManagedPEBuilder(header, new MetadataRootBuilder(md, "v4.0.30319"),
            il, mappedFieldData: data, strongNameSignatureSize: 0, entryPoint: entryHandle, flags: CorFlags.ILOnly,
            debugDirectoryBuilder: debugDirectory);
        var image = new BlobBuilder();
        pe.Serialize(image);
        byte[] result = image.ToArray();
        if (ilSize != 0)
        {
            var method = _definitions.Values.FirstOrDefault(d => d.Token.Handle.Kind == HandleKind.MethodDefinition &&
                d.Section?.OutputCodeOffset >= 0);
            AlignStream(result, ilSize, codeAlignment, code: true,
                method == null ? initialization.Method : (EntityHandle)_merger.Map(method.Token.Input, method.Token.Handle),
                method == null ? initialization.CodeOffset : method.Section.OutputCodeOffset + method.Offset);
        }
        if (dataSize != 0)
        {
            var field = _fieldStorage.Values.FirstOrDefault(f => _live.Contains(f.Token) &&
                !f.Convert && f.Definition.Section?.OutputDataOffset >= 0);
            AlignStream(result, dataSize, dataAlignment, code: false,
                field == null ? initialization.Copies[0].DataField : (EntityHandle)_merger.Map(field.Token.Input, field.Token.Handle),
                field == null ? initialization.Copies[0].Offset : field.Definition.Section.OutputDataOffset + field.Definition.Offset);
        }
        return new LinkResult(result, pdbBytes, warnings);
    }

    private static int Align(int value, int alignment) => checked(value + alignment - 1) & -alignment;

    private static void AlignStream(byte[] image, int size, int alignment, bool code, EntityHandle handle, int offset)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var md = pe.GetMetadataReader();
        int rva = code
            ? md.GetMethodDefinition((MethodDefinitionHandle)handle).RelativeVirtualAddress
            : md.GetFieldDefinition((FieldDefinitionHandle)handle).GetRelativeVirtualAddress();
        int streamRva = rva - offset;
        int padding = Align(streamRva, alignment) - streamRva;
        if (padding == 0)
            return;
        var section = pe.PEHeaders.SectionHeaders.Single(s =>
            streamRva >= s.VirtualAddress && streamRva < s.VirtualAddress + s.VirtualSize);
        int start = checked(section.PointerToRawData + streamRva - section.VirtualAddress);
        Array.Copy(image, start, image, start + padding, size);
        image.AsSpan(start, padding).Clear();

        TableIndex table = code ? TableIndex.MethodDef : TableIndex.FieldRva;
        int rowSize = md.GetTableRowSize(table);
        int tableStart = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(table);
        for (int row = 0; row < md.GetTableRowCount(table); row++)
        {
            var slot = image.AsSpan(tableStart + row * rowSize, 4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(slot);
            if (value != 0)
                BinaryPrimitives.WriteInt32LittleEndian(slot, checked(value + padding));
        }
    }

    private static int Alignment(InputSection section)
    {
        int code = ((int)section.Header.SectionCharacteristics >> 20) & 15;
        if (code == 15)
            throw section.Input.Error("invalid section alignment");
        return code == 0 ? 16 : 1 << (code - 1);
    }
}
