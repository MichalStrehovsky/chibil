using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Coff;

namespace Chilink;

internal sealed class GlobalDataPlan
{
    public GlobalDataPlan(
        IReadOnlySet<MetadataSourceEntity> transformedFields,
        IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> fieldDefinitionBindings,
        IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> fieldRvaAliases,
        IReadOnlySet<CoffInputSection> handledSections,
        SyntheticMethodBody moduleInitializer)
    {
        TransformedFields = transformedFields;
        FieldDefinitionBindings = fieldDefinitionBindings;
        FieldRvaAliases = fieldRvaAliases;
        HandledSections = handledSections;
        ModuleInitializer = moduleInitializer;
    }

    public IReadOnlySet<MetadataSourceEntity> TransformedFields { get; }

    public IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> FieldDefinitionBindings { get; }

    public IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> FieldRvaAliases { get; }

    public IReadOnlySet<CoffInputSection> HandledSections { get; }

    public SyntheticMethodBody ModuleInitializer { get; }
}

internal sealed class SyntheticMethodBody
{
    private readonly byte[] _template;
    private readonly IReadOnlyList<SyntheticTokenFixup> _fixups;

    public SyntheticMethodBody(byte[] template, IReadOnlyList<SyntheticTokenFixup> fixups)
    {
        _template = template;
        _fixups = fixups;
    }

    public int Size => _template.Length;

    public byte[] Materialize(Func<CoffInput, int, int> mapToken)
    {
        byte[] result = (byte[])_template.Clone();
        foreach (SyntheticTokenFixup fixup in _fixups)
        {
            int sourceToken = MetadataTokens.GetToken(fixup.Field);
            int finalToken = mapToken(fixup.Input, sourceToken);
            BinaryPrimitives.WriteInt32LittleEndian(
                result.AsSpan(fixup.Offset, sizeof(int)),
                finalToken);
        }
        return result;
    }
}

internal readonly record struct SyntheticTokenFixup(
    int Offset,
    CoffInput Input,
    FieldDefinitionHandle Field);

internal static class GlobalDataPlanner
{
    public static GlobalDataPlan Plan(
        IReadOnlyList<CoffInput> inputs,
        SymbolResolver symbols,
        IReadOnlySet<CoffInputSection> liveSections)
    {
        var fields = CollectFields(inputs, symbols, liveSections);
        var fieldsBySymbol = fields
            .GroupBy(field => (field.StorageSymbol.Input, field.StorageSymbol.Handle))
            .ToDictionary(group => group.Key, group => group.First());
        var vtfixupTargets = FindVtfixupTargets(inputs);
        var transformed = new HashSet<MetadataSourceEntity>();
        var fieldDefinitionBindings =
            new Dictionary<MetadataSourceEntity, MetadataSourceEntity>();
        var fieldRvaAliases =
            new Dictionary<MetadataSourceEntity, MetadataSourceEntity>();
        var handledSections = new HashSet<CoffInputSection>();
        var initializers = new List<GlobalFieldInitializer>();

        foreach (GlobalFieldInfo field in fields)
        {
            if (field.StorageSymbol.IsExternal &&
                !ReferenceEquals(
                    symbols.GetCanonicalDefinition(field.StorageSymbol),
                    field.StorageSymbol))
            {
                continue;
            }

            if (field.StorageSymbol.IsCommon)
            {
                transformed.Add(field.Source);
                continue;
            }

            CoffInputSection section = field.Section;
            bool writable =
                (section.Characteristics & SectionCharacteristics.MemWrite) != 0;
            bool uninitialized =
                (section.Characteristics & SectionCharacteristics.ContainsUninitializedData) != 0;
            IReadOnlyList<CoffInputRelocation> relocations = field.Relocations;
            bool hasAddressRelocations = relocations.Any(
                relocation => relocation.Type != ImageRelocation.Amd64_TOKEN);

            if (!writable && !uninitialized && !hasAddressRelocations)
            {
                continue;
            }

            transformed.Add(field.Source);
            handledSections.Add(section);

            if (vtfixupTargets.Contains((field.StorageSymbol.Input, field.StorageSymbol.Handle)))
            {
                throw new ChilinkException(
                    $"global field '{field.Name}' in '{field.Input.Path}' is associated with a vtfixup and is not supported");
            }

            byte[] data = GetFieldData(field);
            if (uninitialized || (relocations.Count == 0 && data.All(value => value == 0)))
            {
                continue;
            }

            var relocationInitializers = new List<GlobalRelocationInitializer>();
            foreach (CoffInputRelocation relocation in relocations)
            {
                if (relocation.Type != ImageRelocation.Amd64_ADDR64)
                {
                    throw new ChilinkException(
                        $"unsupported global initializer relocation {relocation.Type} in field '{field.Name}' of '{field.Input.Path}'");
                }

                int relativeOffset = checked((int)relocation.Offset - field.Offset);
                if (relativeOffset < 0 || relativeOffset > data.Length - sizeof(long))
                {
                    throw new ChilinkException(
                        $"global initializer relocation is outside field '{field.Name}' in '{field.Input.Path}'");
                }

                CoffInputSymbol target = symbols.ResolveRelocationTarget(
                    field.Input,
                    relocation);
                target = symbols.GetCanonicalDefinition(target);
                if (target.Section.Kind == CoffSectionHandleKind.Physical)
                {
                    CoffInputSection targetSection = target.Input.Sections.Single(
                        candidate => candidate.Handle == target.Section);
                    if (targetSection.IsNativeTransitionSection)
                    {
                        throw new ChilinkException(
                            $"global field '{field.Name}' in '{field.Input.Path}' has a function/vtfixup initializer relocation targeting '{target.Name}'");
                    }
                }

                if (vtfixupTargets.Contains((target.Input, target.Handle)))
                {
                    throw new ChilinkException(
                        $"global field '{field.Name}' in '{field.Input.Path}' has a vtfixup initializer relocation targeting '{target.Name}'");
                }

                if (!fieldsBySymbol.TryGetValue(
                        (target.Input, target.Handle),
                        out GlobalFieldInfo targetField))
                {
                    throw new ChilinkException(
                        $"global field '{field.Name}' in '{field.Input.Path}' has an initializer relocation to unsupported symbol '{target.Name}'");
                }

                long addend = BinaryPrimitives.ReadInt64LittleEndian(
                    data.AsSpan(relativeOffset, sizeof(long)));
                relocationInitializers.Add(new GlobalRelocationInitializer(
                    relativeOffset,
                    targetField.Input,
                    targetField.Field,
                    addend));
            }

            initializers.Add(new GlobalFieldInitializer(
                field.Input,
                field.Field,
                data,
                relocationInitializers));
        }

        foreach (GlobalFieldInfo field in fields.Where(field =>
                     field.StorageSymbol.IsExternal &&
                     !ReferenceEquals(
                         symbols.GetCanonicalDefinition(field.StorageSymbol),
                         field.StorageSymbol)))
        {
            CoffInputSymbol canonicalSymbol =
                symbols.GetCanonicalDefinition(field.StorageSymbol);
            if (!fieldsBySymbol.TryGetValue(
                    (canonicalSymbol.Input, canonicalSymbol.Handle),
                    out GlobalFieldInfo canonicalField))
            {
                throw new ChilinkException(
                    $"global field '{field.Name}' in '{field.Input.Path}' has no canonical metadata definition");
            }
            if (field.Size != canonicalField.Size ||
                field.SignatureIdentity != canonicalField.SignatureIdentity)
            {
                throw new ChilinkException(
                    $"common symbol '{field.StorageSymbol.Name}' has incompatible field definitions in '{field.Input.Path}' and '{canonicalField.Input.Path}'");
            }

            fieldDefinitionBindings.Add(field.Source, canonicalField.Source);
            if (transformed.Contains(canonicalField.Source))
            {
                transformed.Add(field.Source);
            }
            else
            {
                fieldRvaAliases.Add(field.Source, canonicalField.Source);
            }
        }

        ValidateHandledSections(
            fields,
            transformed,
            handledSections,
            vtfixupTargets);

        SyntheticMethodBody initializer = initializers.Count == 0
            ? null
            : ModuleInitializerBuilder.Build(initializers);
        return new GlobalDataPlan(
            transformed,
            fieldDefinitionBindings,
            fieldRvaAliases,
            handledSections,
            initializer);
    }

    private static List<GlobalFieldInfo> CollectFields(
        IReadOnlyList<CoffInput> inputs,
        SymbolResolver symbols,
        IReadOnlySet<CoffInputSection> liveSections)
    {
        var result = new List<GlobalFieldInfo>();
        foreach (CoffInput input in inputs)
        {
            foreach ((EntityHandle token, CoffInputSymbol tokenSymbol) in input.DefinedClrTokens)
            {
                if (token.Kind != HandleKind.FieldDefinition ||
                    tokenSymbol.ClrTokenTarget is not CoffSymbolHandle targetHandle)
                {
                    continue;
                }

                CoffInputSymbol storageSymbol = input.SymbolsByHandle[targetHandle];
                var field = (FieldDefinitionHandle)token;
                FieldDefinition definition = input.Metadata.GetFieldDefinition(field);
                int size = FieldSizeCalculator.GetSize(input.Metadata, definition, 8);

                if (storageSymbol.IsCommon)
                {
                    result.Add(new GlobalFieldInfo(
                        input,
                        field,
                        storageSymbol,
                        null,
                        0,
                        size,
                        input.Metadata.GetString(definition.Name),
                        FieldSizeCalculator.GetIdentity(input.Metadata, definition),
                        Array.Empty<CoffInputRelocation>()));
                    continue;
                }

                if (!storageSymbol.IsDefined)
                {
                    continue;
                }

                CoffInputSection section = storageSymbol.Input.Sections.Single(
                    candidate => candidate.Handle == storageSymbol.Section);
                if (!liveSections.Contains(symbols.GetCanonicalSection(section)))
                {
                    continue;
                }

                int offset = checked((int)storageSymbol.Value);
                bool uninitialized =
                    (section.Characteristics &
                     SectionCharacteristics.ContainsUninitializedData) != 0;
                int sectionSize = Math.Max(
                    section.Content.Length,
                    checked((int)(section.SectionDefinition?.Length ?? 0)));
                if (!uninitialized &&
                    (offset < 0 || size < 0 || offset > sectionSize - size))
                {
                    throw new ChilinkException(
                        $"global field '{input.Metadata.GetString(definition.Name)}' in '{input.Path}' extends outside section '{section.Name}'");
                }

                CoffInputRelocation[] relocations = section.Relocations
                    .Where(relocation =>
                        relocation.Offset >= offset &&
                        relocation.Offset < offset + size)
                    .ToArray();
                result.Add(new GlobalFieldInfo(
                    input,
                    field,
                    storageSymbol,
                    section,
                    offset,
                    size,
                    input.Metadata.GetString(definition.Name),
                    FieldSizeCalculator.GetIdentity(input.Metadata, definition),
                    relocations));
            }
        }
        return result;
    }

    private static HashSet<(CoffInput Input, CoffSymbolHandle Symbol)> FindVtfixupTargets(
        IReadOnlyList<CoffInput> inputs)
    {
        var result = new HashSet<(CoffInput, CoffSymbolHandle)>();
        foreach (CoffInputSection section in inputs
                     .SelectMany(input => input.Sections)
                     .Where(section => section.Name == ".rdata$ilfixup"))
        {
            foreach (CoffInputRelocation relocation in section.Relocations)
            {
                CoffInputSymbol target = section.Input.SymbolsByHandle[relocation.Symbol];
                result.Add((target.Input, target.Handle));
            }
        }
        return result;
    }

    private static void ValidateHandledSections(
        IReadOnlyList<GlobalFieldInfo> fields,
        IReadOnlySet<MetadataSourceEntity> transformed,
        IReadOnlySet<CoffInputSection> handledSections,
        IReadOnlySet<(CoffInput Input, CoffSymbolHandle Symbol)> vtfixupTargets)
    {
        foreach (CoffInputSection section in handledSections)
        {
            int virtualSize = Math.Max(
                section.Content.Length,
                fields.Where(field => ReferenceEquals(field.Section, section))
                    .Select(field => field.Offset + field.Size)
                    .DefaultIfEmpty(0)
                    .Max());
            var covered = new bool[virtualSize];
            foreach (GlobalFieldInfo field in fields.Where(field =>
                         ReferenceEquals(field.Section, section) &&
                         transformed.Contains(field.Source)))
            {
                int end = Math.Min(field.Offset + field.Size, covered.Length);
                for (int offset = field.Offset; offset < end; offset++)
                {
                    covered[offset] = true;
                }
            }

            foreach ((CoffInput input, CoffSymbolHandle handle) in vtfixupTargets)
            {
                if (!ReferenceEquals(input, section.Input))
                {
                    continue;
                }
                CoffInputSymbol symbol = input.SymbolsByHandle[handle];
                if (symbol.Section != section.Handle)
                {
                    continue;
                }
                int start = checked((int)symbol.Value);
                int end = Math.Min(start + sizeof(long), covered.Length);
                for (int offset = start; offset < end; offset++)
                {
                    covered[offset] = true;
                }
            }

            foreach (CoffInputRelocation relocation in section.Relocations)
            {
                if (relocation.Offset >= covered.Length ||
                    !covered[checked((int)relocation.Offset)])
                {
                    CoffInputSymbol target =
                        section.Input.SymbolsByHandle[relocation.Symbol];
                    throw new ChilinkException(
                        $"live writable section '{section.Name}' in '{section.Input.Path}' contains an unsupported relocation targeting '{target.Name}' outside managed global storage");
                }
            }

            for (int offset = 0; offset < section.Content.Length; offset++)
            {
                if (!covered[offset] && section.Content[offset] != 0)
                {
                    throw new ChilinkException(
                        $"live writable section '{section.Name}' in '{section.Input.Path}' contains unsupported data at offset 0x{offset:X}");
                }
            }

            foreach (CoffInputSymbol symbol in section.Symbols)
            {
                if (symbol.IsClrToken ||
                    symbol.SectionDefinition != null ||
                    symbol.Name == section.Name)
                {
                    continue;
                }
                int offset = checked((int)symbol.Value);
                bool withinManagedStorage =
                    offset >= 0 &&
                    offset < covered.Length &&
                    covered[offset];
                bool vtfixupTarget =
                    vtfixupTargets.Contains((symbol.Input, symbol.Handle));
                if (!withinManagedStorage && !vtfixupTarget)
                {
                    throw new ChilinkException(
                        $"live writable section '{section.Name}' in '{section.Input.Path}' contains unsupported symbol '{symbol.Name}' outside managed global storage");
                }
            }
        }
    }

    private static byte[] GetFieldData(GlobalFieldInfo field)
    {
        byte[] result = new byte[field.Size];
        if (field.Section.Content.Length == 0)
        {
            return result;
        }

        field.Section.Content.AsSpan(field.Offset, field.Size).CopyTo(result);
        return result;
    }
}

internal static class FieldSizeCalculator
{
    public static int GetSize(
        MetadataReader reader,
        FieldDefinition field,
        int pointerSize)
    {
        BlobReader signature = reader.GetBlobReader(field.Signature);
        signature.ReadSignatureHeader();

        while (true)
        {
            SignatureTypeCode type = signature.ReadSignatureTypeCode();
            switch (type)
            {
                case SignatureTypeCode.OptionalModifier:
                case SignatureTypeCode.RequiredModifier:
                    signature.ReadTypeHandle();
                    continue;
                case SignatureTypeCode.Boolean:
                case SignatureTypeCode.SByte:
                case SignatureTypeCode.Byte:
                    return 1;
                case SignatureTypeCode.Char:
                case SignatureTypeCode.Int16:
                case SignatureTypeCode.UInt16:
                    return 2;
                case SignatureTypeCode.Int32:
                case SignatureTypeCode.UInt32:
                case SignatureTypeCode.Single:
                    return 4;
                case SignatureTypeCode.Int64:
                case SignatureTypeCode.UInt64:
                case SignatureTypeCode.Double:
                    return 8;
                case SignatureTypeCode.IntPtr:
                case SignatureTypeCode.UIntPtr:
                case SignatureTypeCode.Pointer:
                case SignatureTypeCode.FunctionPointer:
                    return pointerSize;
                case SignatureTypeCode.TypeHandle:
                    signature.Offset -= 1;
                    signature.ReadByte();
                    EntityHandle typeHandle = signature.ReadTypeHandle();
                    TypeDefinitionHandle typeDefinition = typeHandle.Kind switch
                    {
                        HandleKind.TypeDefinition => (TypeDefinitionHandle)typeHandle,
                        HandleKind.TypeReference => ResolveLocalTypeReference(
                            reader,
                            (TypeReferenceHandle)typeHandle),
                        _ => default,
                    };
                    if (typeDefinition.IsNil)
                    {
                        throw new ChilinkException(
                            $"cannot determine storage size for global field '{reader.GetString(field.Name)}': value type handle is {typeHandle.Kind}");
                    }
                    TypeDefinition definition =
                        reader.GetTypeDefinition(typeDefinition);
                    TypeLayout layout = definition.GetLayout();
                    if (layout.Size > 0)
                    {
                        return layout.Size;
                    }
                    throw new ChilinkException(
                        $"cannot determine storage size for global field '{reader.GetString(field.Name)}': value type has no explicit size");
            }

            throw new ChilinkException(
                $"cannot determine storage size for global field '{reader.GetString(field.Name)}': signature type is 0x{(byte)type:X2}");
        }
    }

    public static string GetIdentity(
        MetadataReader reader,
        FieldDefinition field)
        => GetIdentity(reader, field.Signature);

    public static string GetIdentity(
        MetadataReader reader,
        BlobHandle signatureHandle)
    {
        BlobReader signature = reader.GetBlobReader(signatureHandle);
        signature.ReadSignatureHeader();
        return GetTypeIdentity(reader, ref signature);
    }

    private static string GetTypeIdentity(
        MetadataReader reader,
        ref BlobReader signature)
    {
        SignatureTypeCode type = signature.ReadSignatureTypeCode();
        switch (type)
        {
            case SignatureTypeCode.OptionalModifier:
            case SignatureTypeCode.RequiredModifier:
                signature.ReadTypeHandle();
                return GetTypeIdentity(reader, ref signature);
            case SignatureTypeCode.Pointer:
                return "NativeInt";
            case SignatureTypeCode.TypeHandle:
                signature.Offset -= 1;
                signature.ReadByte();
                return GetTypeIdentity(reader, signature.ReadTypeHandle());
            case SignatureTypeCode.Boolean:
            case SignatureTypeCode.SByte:
            case SignatureTypeCode.Byte:
                return "I1";
            case SignatureTypeCode.Char:
            case SignatureTypeCode.Int16:
            case SignatureTypeCode.UInt16:
                return "I2";
            case SignatureTypeCode.Int32:
            case SignatureTypeCode.UInt32:
                return "I4";
            case SignatureTypeCode.Int64:
            case SignatureTypeCode.UInt64:
                return "I8";
            case SignatureTypeCode.Single:
                return "R4";
            case SignatureTypeCode.Double:
                return "R8";
            case SignatureTypeCode.IntPtr:
            case SignatureTypeCode.UIntPtr:
            case SignatureTypeCode.FunctionPointer:
                return "NativeInt";
            default:
                if (signature.RemainingBytes == 0)
                {
                    return type.ToString();
                }
                byte[] remainder = signature.ReadBytes(signature.RemainingBytes);
                return $"{type}:{Convert.ToHexString(remainder)}";
        }
    }

    private static string GetTypeIdentity(
        MetadataReader reader,
        EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeIdentity(
                reader,
                reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => GetTypeIdentity(
                reader,
                reader.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeSpecification => GetTypeSpecificationIdentity(
                reader,
                (TypeSpecificationHandle)handle),
            _ => handle.Kind.ToString(),
        };
    }

    private static string GetTypeIdentity(
        MetadataReader reader,
        TypeDefinition type)
        => $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string GetTypeIdentity(
        MetadataReader reader,
        TypeReference type)
        => $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string GetTypeSpecificationIdentity(
        MetadataReader reader,
        TypeSpecificationHandle handle)
    {
        BlobReader signature =
            reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        return GetTypeIdentity(reader, ref signature);
    }

    private static TypeDefinitionHandle ResolveLocalTypeReference(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        string ns = reader.GetString(reference.Namespace);
        foreach (TypeDefinitionHandle candidate in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(candidate);
            if (reader.GetString(definition.Name) == name &&
                reader.GetString(definition.Namespace) == ns)
            {
                return candidate;
            }
        }
        return default;
    }
}

internal readonly record struct GlobalFieldInfo(
    CoffInput Input,
    FieldDefinitionHandle Field,
    CoffInputSymbol StorageSymbol,
    CoffInputSection Section,
    int Offset,
    int Size,
    string Name,
    string SignatureIdentity,
    IReadOnlyList<CoffInputRelocation> Relocations)
{
    public MetadataSourceEntity Source =>
        new(Input.Ordinal.ToString(), Field);
}

internal readonly record struct GlobalFieldInitializer(
    CoffInput Input,
    FieldDefinitionHandle Field,
    byte[] Data,
    IReadOnlyList<GlobalRelocationInitializer> Relocations);

internal readonly record struct GlobalRelocationInitializer(
    int Offset,
    CoffInput TargetInput,
    FieldDefinitionHandle TargetField,
    long Addend);

internal static class ModuleInitializerBuilder
{
    public static SyntheticMethodBody Build(
        IReadOnlyList<GlobalFieldInitializer> initializers)
    {
        var code = new List<byte>();
        var codeFixups = new List<SyntheticTokenFixup>();

        foreach (GlobalFieldInitializer initializer in initializers)
        {
            var relocatedBytes = new bool[initializer.Data.Length];
            foreach (GlobalRelocationInitializer relocation in initializer.Relocations)
            {
                for (int i = 0; i < sizeof(long); i++)
                {
                    relocatedBytes[relocation.Offset + i] = true;
                }
            }

            for (int offset = 0; offset < initializer.Data.Length; offset++)
            {
                if (relocatedBytes[offset] || initializer.Data[offset] == 0)
                {
                    continue;
                }

                EmitFieldAddress(
                    code,
                    codeFixups,
                    initializer.Input,
                    initializer.Field,
                    offset);
                EmitInt32(code, initializer.Data[offset]);
                EmitOpCode(code, ILOpCode.Stind_i1);
            }

            foreach (GlobalRelocationInitializer relocation in initializer.Relocations)
            {
                EmitFieldAddress(
                    code,
                    codeFixups,
                    initializer.Input,
                    initializer.Field,
                    relocation.Offset);
                EmitFieldAddress(
                    code,
                    codeFixups,
                    relocation.TargetInput,
                    relocation.TargetField,
                    0);
                if (relocation.Addend != 0)
                {
                    EmitInt64(code, relocation.Addend);
                    EmitOpCode(code, ILOpCode.Conv_i);
                    EmitOpCode(code, ILOpCode.Add);
                }
                EmitOpCode(code, ILOpCode.Stind_i);
            }
        }

        EmitOpCode(code, ILOpCode.Ret);

        var body = new List<byte>(12 + code.Count);
        AddUInt16(body, 0x3003);
        AddUInt16(body, 3);
        AddInt32(body, code.Count);
        AddInt32(body, 0);
        body.AddRange(code);

        SyntheticTokenFixup[] fixups = codeFixups
            .Select(fixup => fixup with { Offset = fixup.Offset + 12 })
            .ToArray();
        return new SyntheticMethodBody(body.ToArray(), fixups);
    }

    private static void EmitFieldAddress(
        List<byte> code,
        List<SyntheticTokenFixup> fixups,
        CoffInput input,
        FieldDefinitionHandle field,
        int offset)
    {
        EmitOpCode(code, ILOpCode.Ldsflda);
        fixups.Add(new SyntheticTokenFixup(code.Count, input, field));
        AddInt32(code, 0);
        EmitOpCode(code, ILOpCode.Conv_u);
        if (offset != 0)
        {
            EmitInt32(code, offset);
            EmitOpCode(code, ILOpCode.Conv_i);
            EmitOpCode(code, ILOpCode.Add);
        }
    }

    private static void EmitInt32(List<byte> code, int value)
    {
        switch (value)
        {
            case -1:
                EmitOpCode(code, ILOpCode.Ldc_i4_m1);
                return;
            case >= 0 and <= 8:
                EmitOpCode(code, (ILOpCode)((int)ILOpCode.Ldc_i4_0 + value));
                return;
            case >= sbyte.MinValue and <= sbyte.MaxValue:
                EmitOpCode(code, ILOpCode.Ldc_i4_s);
                code.Add(unchecked((byte)(sbyte)value));
                return;
            default:
                EmitOpCode(code, ILOpCode.Ldc_i4);
                AddInt32(code, value);
                return;
        }
    }

    private static void EmitInt64(List<byte> code, long value)
    {
        EmitOpCode(code, ILOpCode.Ldc_i8);
        AddInt64(code, value);
    }

    private static void EmitOpCode(List<byte> code, ILOpCode opCode)
    {
        ushort value = (ushort)opCode;
        if (unchecked((byte)value) == value)
        {
            code.Add((byte)value);
        }
        else
        {
            code.Add((byte)(value >> 8));
            code.Add((byte)value);
        }
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static void AddInt32(List<byte> bytes, int value)
    {
        uint unsigned = unchecked((uint)value);
        for (int i = 0; i < sizeof(int); i++)
        {
            bytes.Add((byte)(unsigned >> (i * 8)));
        }
    }

    private static void AddInt64(List<byte> bytes, long value)
    {
        ulong unsigned = unchecked((ulong)value);
        for (int i = 0; i < sizeof(long); i++)
        {
            bytes.Add((byte)(unsigned >> (i * 8)));
        }
    }
}
