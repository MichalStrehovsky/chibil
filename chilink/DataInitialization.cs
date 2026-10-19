using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed partial class Linker
{
    private sealed record FieldStorage(SourceToken Token, Definition Definition, int Size, bool Convert, bool Zero);
    private readonly Dictionary<SourceToken, FieldStorage> _fieldStorage = new();
    private readonly Dictionary<SourceToken, int> _fieldSizes = new();
    private readonly List<DataRelocation> _dataRelocations = new();
    private readonly HashSet<SourceToken> _addressTakenFields = new();

    private int FieldSize(SourceToken token)
    {
        token = _merger.Resolve(token);
        if (_fieldSizes.TryGetValue(token, out int size))
            return size;
        var reader = _inputs[token.Input].Metadata;
        var field = reader.GetFieldDefinition((FieldDefinitionHandle)token.Handle);
        try
        {
            size = FieldData.GetSize(reader, field, pointerSize: PointerSize, resolveType: handle =>
            {
                var type = _merger.Resolve(new(token.Input, handle));
                if (type.Handle.Kind != HandleKind.TypeDefinition)
                    throw _inputs[token.Input].Error("RVA storage requires a resolved value type");
                return _inputs[type.Input].Metadata.GetTypeDefinition((TypeDefinitionHandle)type.Handle);
            });
        }
        catch (NotSupportedException ex)
        {
            throw _inputs[token.Input].Error(ex.Message);
        }
        _fieldSizes.Add(token, size);
        return size;
    }

    private FieldStorage GetFieldStorage(SourceToken token)
    {
        token = _merger.Resolve(token);
        if (_fieldStorage.TryGetValue(token, out var storage))
            return storage;
        if (!_definitions.TryGetValue(token, out var definition))
            throw _inputs[token.Input].Error("RVA field has no supported COFF storage definition");
        var input = _inputs[definition.Token.Input];
        var field = input.Metadata.GetFieldDefinition((FieldDefinitionHandle)definition.Token.Handle);
        if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.HasFieldRVA)) !=
            (FieldAttributes.Static | FieldAttributes.HasFieldRVA))
            throw input.Error($"COFF storage for non-RVA static field '{definition.Symbol.Name}'");
        int size = FieldSize(token);
        var section = definition.Section;
        bool zero = definition.IsCommon || section?.IsBss == true;
        bool relocated = false;
        if (definition.IsCommon)
        {
            if (definition.Symbol.Coff.Value != size)
                throw input.Error($"incompatible common storage size for '{definition.Symbol.Name}'");
        }
        else
        {
            if (section == null || section.Ignored || section.IsCode)
                throw input.Error($"unsupported native/IJW storage for '{definition.Symbol.Name}'");
            if (definition.Offset < 0 || (long)definition.Offset + size > section.Header.SizeOfRawData)
                throw input.Error($"field '{definition.Symbol.Name}' extends outside its section");
            if (section.IsUnsupportedVtableFixup(definition.Offset, size))
                throw input.Error($"vtfixup storage '{definition.Symbol.Name}' is not supported");
            foreach (var other in input.Definitions.Values)
            {
                if (other == definition || other.Section != section ||
                    other.Token.Handle.Kind != HandleKind.FieldDefinition)
                    continue;
                if (other.Offset < (long)definition.Offset + size &&
                    (long)other.Offset + FieldSize(other.Token) > definition.Offset)
                    throw input.Error($"overlapping field storage '{definition.Symbol.Name}' and '{other.Symbol.Name}'");
            }
            for (int i = 0; i < section.Relocations.NumberOfRelocations; i++)
            {
                var relocation = section.Relocations.GetRelocation(i);
                if (relocation.Offset >= definition.Offset && relocation.Offset < (long)definition.Offset + size)
                    relocated = true;
            }
            if (!zero && section.GetData().Length != section.Header.SizeOfRawData)
                throw input.Error($"missing initialized data for '{definition.Symbol.Name}'");
        }
        bool convert = zero || section.IsWritable || relocated;
        if (convert && MetadataTokens.GetRowNumber(field.GetDeclaringType()) != 1)
            throw input.Error($"RVA field conversion on named types is not supported: '{definition.Symbol.Name}'");
        storage = new(token, definition, size, convert, zero);
        _fieldStorage.Add(token, storage);
        return storage;
    }

    private void ValidateFieldAddresses()
    {
        foreach (SourceToken token in _addressTakenFields)
            if (_fieldStorage.TryGetValue(token, out var storage) && storage.Convert && !_merger.HasFixedAddress(token))
                throw _inputs[storage.Definition.Token.Input].Error(
                    $"address-taken RVA field conversion requires FixedAddressValueTypeAttribute: '{storage.Definition.Symbol.Name}'");
    }

    private void MarkDataRelocation(InputSection section, Definition[] definitions, CoffRelocation relocation)
    {
        bool managedSlot = section.IsManagedVtableSlot(relocation.Offset);
        if (managedSlot && relocation.Type != ImageRelocation.Amd64_TOKEN)
            throw section.Input.Error($"managed vtfixup slot requires a TOKEN relocation in '{section.Name}'");
        if (section.IsBss || (!managedSlot && relocation.Type != ImageRelocation.Amd64_ADDR64))
            throw section.Input.Error($"unsupported relocation {relocation.Type} in retained section '{section.Name}'");
        if ((long)relocation.Offset + PointerSize > section.GetData().Length ||
            (!managedSlot && section.IsVtableFixup(relocation.Offset, PointerSize)))
            throw section.Input.Error($"data relocation exceeds its storage in '{section.Name}'");
        var owners = definitions.Where(d => d.Token.Handle.Kind == HandleKind.FieldDefinition &&
            relocation.Offset >= d.Offset && relocation.Offset < (long)d.Offset + FieldSize(d.Token)).ToArray();
        if (owners.Length != 1 || (long)relocation.Offset + PointerSize > owners[0].Offset + (long)FieldSize(owners[0].Token))
            throw section.Input.Error($"data relocation crosses a field boundary in '{section.Name}'");
        var owner = GetFieldStorage(owners[0].Token);
        long addend = BinaryPrimitives.ReadInt64LittleEndian(section.GetData().AsSpan((int)relocation.Offset, PointerSize));
        var symbol = section.Input.Symbols[relocation.Symbol];
        SourceToken target;
        if (managedSlot)
        {
            if (addend != 0)
                throw section.Input.Error("nonzero TOKEN relocation addends are not supported");
            target = ResolveManagedVtableTarget(section.Input, symbol);
        }
        else
            target = ResolveDataTarget(section.Input, symbol);
        if (_dataRelocations.Any(r => r.Field == owner.Token &&
            r.Offset < relocation.Offset - owners[0].Offset + PointerSize && r.Offset + PointerSize > relocation.Offset - owners[0].Offset))
            throw section.Input.Error($"overlapping data relocations in '{section.Name}'");
        var import = _merger.GetImport(target)?.Import;
        _dataRelocations.Add(new(owner.Token, (int)relocation.Offset - owners[0].Offset, target, addend, import));
        if (import == null)
            Mark(target);
    }

    private SourceToken ResolveManagedVtableTarget(InputFile input, InputSymbol symbol)
    {
        if (symbol.Coff.StorageClass != CoffSymbolStorageClass.ClrToken ||
            symbol.Token.Kind is not (HandleKind.MethodDefinition or HandleKind.MemberReference))
            throw input.Error("managed vtfixup TOKEN relocation does not reference a method token");
        var token = _merger.Resolve(new(input.Index, (EntityHandle)symbol.Token));
        if (_merger.GetImport(token) != null)
            return token;
        if (token.Handle.Kind != HandleKind.MethodDefinition)
            throw input.Error($"unresolved managed vtfixup method '{symbol.Name}'");
        var reader = _inputs[token.Input].Metadata;
        var method = reader.GetMethodDefinition((MethodDefinitionHandle)token.Handle);
        var header = reader.GetBlobReader(method.Signature).ReadSignatureHeader();
        if (header.IsGeneric || header.IsInstance || header.CallingConvention != SignatureCallingConvention.Default ||
            (method.Attributes & MethodAttributes.Static) == 0)
            throw input.Error($"unsupported managed vtfixup method '{symbol.Name}'");
        return token;
    }

    private SourceToken ResolveDataTarget(InputFile input, InputSymbol symbol)
    {
        Definition target;
        if (symbol.Section?.Ignored == true || symbol.Section?.IsUnsupportedVtableFixup(symbol.Coff.Value) == true)
            throw input.Error($"data relocation requires unsupported native/IJW symbol '{symbol.Name}'");
        if (!input.SymbolDefinitions.TryGetValue(symbol, out target))
        {
            if (symbol.Coff.StorageClass == CoffSymbolStorageClass.External && symbol.Coff.SectionNumber.Number == 0)
            {
                if (!_symbols.TryGetValue(symbol.Name, out target))
                {
                    if (_nativeImports.ContainsKey(symbol.Name))
                        throw input.Error($"native import address '{symbol.Name}' requires an assisted TOKEN slot");
                    if (SelectLibrarySymbol(symbol.Name, input.Library))
                        throw new ArchiveExpansionException();
                    bool native = _inputs.Any(i => i.Symbols.Values.Any(s => s.Name == symbol.Name &&
                        s.Coff.StorageClass == CoffSymbolStorageClass.External && s.Section?.Ignored == true));
                    throw input.Error(native
                        ? $"data relocation requires unsupported native/IJW symbol '{symbol.Name}'"
                        : $"unresolved data relocation symbol '{symbol.Name}'");
                }
            }
            else
                throw input.Error($"unsupported native data relocation symbol '{symbol.Name}'");
        }
        var token = _merger.Resolve(target.Token);
        if (token.Handle.Kind == HandleKind.FieldDefinition)
        {
            GetFieldStorage(token);
            _addressTakenFields.Add(token);
        }
        else
            throw input.Error($"data relocation to method '{symbol.Name}' requires a managed vtfixup slot");
        return token;
    }

    private DataInitialization PlanInitialization(BlobBuilder data)
    {
        var result = new DataInitialization { NativeResolver = _nativeResolver };
        foreach (var field in _fieldStorage.Values.Where(f => _live.Contains(f.Token) && f.Convert)
            .OrderBy(f => f.Token.Input).ThenBy(f => MetadataTokens.GetRowNumber(f.Token.Handle)))
        {
            result.Converted.Add(field.Token);
            if (field.Zero)
                continue;
            byte[] bytes = field.Definition.Section.GetData();
            if (!bytes.AsSpan(field.Definition.Offset, field.Size).ContainsAnyExcept((byte)0))
                continue;
            data.Align(PointerSize);
            result.Copies.Add(new(field.Token, data.Count));
            data.WriteBytes(bytes, field.Definition.Offset, field.Size);
        }
        result.Relocations.AddRange(_dataRelocations.Where(r => _live.Contains(r.Field)));
        if (result.HasInitializer)
            foreach (var input in _inputs)
                foreach (var handle in input.Metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1)).GetMethods())
                    if (input.Metadata.GetString(input.Metadata.GetMethodDefinition(handle).Name) == ".cctor")
                        throw input.Error("an existing module initializer conflicts with RVA global initialization");
        return result;
    }
}

internal sealed record DataRelocation(SourceToken Field, int Offset, SourceToken Target, long Addend, NativeImport Import);

internal sealed record DataCopy(SourceToken Field, int Offset)
{
    public FieldDefinitionHandle DataField;
}

internal sealed class DataInitialization
{
    public readonly HashSet<SourceToken> Converted = new();
    public readonly List<DataCopy> Copies = new();
    public readonly List<DataRelocation> Relocations = new();
    public bool HasInitializer => Copies.Count != 0 || Relocations.Count != 0;
    public SourceToken NativeResolver;
    public int CodeOffset;
    public MethodDefinitionHandle Method;

    public void Emit(BlobBuilder il, MetadataMerger merger)
    {
        var code = new BlobBuilder();
        var encoder = new InstructionEncoder(code);
        EntityHandle Map(SourceToken token) => (EntityHandle)merger.Map(token.Input, token.Handle);
        void Address(EntityHandle handle, long offset, bool unmanaged)
        {
            encoder.OpCode(handle.Kind == HandleKind.MethodDefinition ? ILOpCode.Ldftn : ILOpCode.Ldsflda);
            encoder.Token(handle);
            if (unmanaged)
                encoder.OpCode(ILOpCode.Conv_u);
            if (offset != 0)
            {
                encoder.LoadConstantI8(offset);
                encoder.OpCode(ILOpCode.Conv_i);
                encoder.OpCode(ILOpCode.Add);
            }
        }
        foreach (var copy in Copies)
        {
            encoder.OpCode(ILOpCode.Ldsfld);
            encoder.Token(copy.DataField);
            encoder.OpCode(ILOpCode.Stsfld);
            encoder.Token(Map(copy.Field));
        }
        foreach (var relocation in Relocations)
        {
            // Keep the destination GC-tracked while resolving the source address.
            Address(Map(relocation.Field), relocation.Offset, unmanaged: false);
            if (relocation.Import != null)
            {
                encoder.LoadString(merger.GetUserString(relocation.Import.Library));
                encoder.LoadString(merger.GetUserString(relocation.Import.EntryPoint));
                encoder.Call((MethodDefinitionHandle)Map(NativeResolver));
            }
            else
                Address(Map(relocation.Target), relocation.Addend, unmanaged: true);
            encoder.OpCode(ILOpCode.Unaligned);
            code.WriteByte(1);
            encoder.OpCode(ILOpCode.Stind_i);
        }
        encoder.OpCode(ILOpCode.Ret);
        il.Align(4);
        if (new MethodBodyStreamEncoder(il).AddMethodBody(encoder, maxStack: 3) != CodeOffset)
            throw new LinkException("module initializer body offset mismatch");
    }
}
