using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Coff;

namespace Chilink;

internal sealed class SectionLayoutPlan
{
    private readonly IReadOnlyList<PlannedSection> _codeSections;
    private readonly IReadOnlyList<PlannedSection> _dataSections;
    private readonly SyntheticMethodBody _moduleInitializer;

    internal SectionLayoutPlan(
        IReadOnlyList<PlannedSection> codeSections,
        IReadOnlyList<PlannedSection> dataSections,
        IReadOnlyDictionary<(CoffInput Input, MethodDefinitionHandle Method), int> methodBodyOffsets,
        IReadOnlyDictionary<(CoffInput Input, FieldDefinitionHandle Field), int> fieldDataOffsets,
        SyntheticMethodBody moduleInitializer,
        int moduleInitializerBodyOffset)
    {
        _codeSections = codeSections;
        _dataSections = dataSections;
        _moduleInitializer = moduleInitializer;
        MethodBodyOffsets = methodBodyOffsets;
        FieldDataOffsets = fieldDataOffsets;
        ModuleInitializerBodyOffset = moduleInitializerBodyOffset;
    }

    public IReadOnlyDictionary<(CoffInput Input, MethodDefinitionHandle Method), int> MethodBodyOffsets { get; }

    public IReadOnlyDictionary<(CoffInput Input, FieldDefinitionHandle Field), int> FieldDataOffsets { get; }

    public int ModuleInitializerBodyOffset { get; }

    public SectionStreams Materialize(Func<CoffInput, int, int> mapToken)
        => new(
            MaterializeCodeStream(mapToken),
            MaterializeStream(_dataSections, mapToken));

    private BlobBuilder MaterializeCodeStream(Func<CoffInput, int, int> mapToken)
    {
        BlobBuilder output = MaterializeStream(_codeSections, mapToken);
        if (_moduleInitializer != null)
        {
            output.Align(4);
            if (output.Count != ModuleInitializerBodyOffset)
            {
                throw new InvalidOperationException(
                    "Module initializer layout changed during materialization.");
            }
            output.WriteBytes(_moduleInitializer.Materialize(mapToken));
        }
        return output;
    }

    private static BlobBuilder MaterializeStream(
        IReadOnlyList<PlannedSection> sections,
        Func<CoffInput, int, int> mapToken)
    {
        var output = new BlobBuilder();
        foreach (PlannedSection planned in sections)
        {
            output.Align(Math.Max(planned.Section.Alignment, 1));
            if (output.Count != planned.Offset)
            {
                throw new InvalidOperationException("Section layout changed during materialization.");
            }

            byte[] content = planned.Section.Content.ToArray();
            ApplyRelocations(planned.Section, content, mapToken);
            output.WriteBytes(content);
        }
        return output;
    }

    private static void ApplyRelocations(
        CoffInputSection section,
        byte[] content,
        Func<CoffInput, int, int> mapToken)
    {
        foreach (CoffInputRelocation relocation in section.Relocations)
        {
            if (relocation.Type != ImageRelocation.Amd64_TOKEN)
            {
                CoffInputSymbol target = section.Input.SymbolsByHandle[relocation.Symbol];
                throw new ChilinkException(
                    $"unsupported relocation {relocation.Type} in live section '{section.Name}' " +
                    $"of '{section.Input.Path}' targeting '{target.Name}'");
            }

            if (relocation.Offset > content.Length - sizeof(int))
            {
                throw new ChilinkException(
                    $"relocation offset 0x{relocation.Offset:X} is outside section '{section.Name}' in '{section.Input.Path}'");
            }

            CoffInputSymbol tokenSymbol = section.Input.SymbolsByHandle[relocation.Symbol];
            if (!tokenSymbol.IsClrToken ||
                tokenSymbol.Name.Length != 8 ||
                !int.TryParse(
                    tokenSymbol.Name,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int sourceToken))
            {
                throw new ChilinkException(
                    $"TOKEN relocation in '{section.Input.Path}' does not target a CLR token symbol");
            }

            int finalToken = mapToken(section.Input, sourceToken);
            BinaryPrimitives.WriteInt32LittleEndian(
                content.AsSpan(checked((int)relocation.Offset), sizeof(int)),
                finalToken);
        }
    }
}

internal readonly record struct SectionStreams(
    BlobBuilder IlStream,
    BlobBuilder MappedFieldData);

internal static class SectionLayout
{
    public static SectionLayoutPlan Plan(
        IReadOnlyList<CoffInput> inputs,
        IReadOnlySet<CoffInputSection> liveSections,
        GlobalDataPlan globalData)
    {
        var codeSections = new List<PlannedSection>();
        var dataSections = new List<PlannedSection>();
        var sectionOffsets = new Dictionary<CoffInputSection, int>();
        int codeSize = 0;
        int dataSize = 0;

        foreach (CoffInputSection section in inputs.SelectMany(input => input.Sections))
        {
            if (!liveSections.Contains(section) ||
                section.IsDebug ||
                section.IsNativeTransitionSection ||
                globalData.HandledSections.Contains(section) ||
                section.Name == ".cormeta" ||
                (section.Characteristics & SectionCharacteristics.LinkerInfo) != 0)
            {
                continue;
            }

            if ((section.Characteristics & SectionCharacteristics.ContainsCode) != 0)
            {
                codeSize = Align(codeSize, section.Alignment);
                codeSections.Add(new PlannedSection(section, codeSize));
                sectionOffsets.Add(section, codeSize);
                codeSize += section.Content.Length;
                continue;
            }

            if ((section.Characteristics & SectionCharacteristics.ContainsInitializedData) != 0 &&
                (section.Characteristics & SectionCharacteristics.MemWrite) == 0)
            {
                dataSize = Align(dataSize, section.Alignment);
                dataSections.Add(new PlannedSection(section, dataSize));
                sectionOffsets.Add(section, dataSize);
                dataSize += section.Content.Length;
                continue;
            }

            throw new ChilinkException(
                $"live section '{section.Name}' in '{section.Input.Path}' requires writable/global data support");
        }

        var methodOffsets = new Dictionary<(CoffInput, MethodDefinitionHandle), int>();
        var fieldOffsets = new Dictionary<(CoffInput, FieldDefinitionHandle), int>();
        int moduleInitializerBodyOffset = -1;
        if (globalData.ModuleInitializer != null)
        {
            codeSize = Align(codeSize, 4);
            moduleInitializerBodyOffset = codeSize;
            codeSize += globalData.ModuleInitializer.Size;
        }

        foreach (CoffInput input in inputs)
        {
            foreach ((EntityHandle token, CoffInputSymbol symbol) in input.DefinedClrTokens)
            {
                if (symbol.Section.Kind != CoffSectionHandleKind.Physical)
                {
                    continue;
                }
                CoffInputSection section = input.Sections.Single(candidate => candidate.Handle == symbol.Section);
                if (!sectionOffsets.TryGetValue(section, out int sectionOffset))
                {
                    continue;
                }

                int offset = checked(sectionOffset + (int)symbol.Value);
                switch (token.Kind)
                {
                    case HandleKind.MethodDefinition:
                        methodOffsets.Add((input, (MethodDefinitionHandle)token), offset);
                        break;

                    case HandleKind.FieldDefinition:
                        fieldOffsets.Add((input, (FieldDefinitionHandle)token), offset);
                        break;
                }
            }
        }

        return new SectionLayoutPlan(
            codeSections,
            dataSections,
            methodOffsets,
            fieldOffsets,
            globalData.ModuleInitializer,
            moduleInitializerBodyOffset);
    }

    private static int Align(int value, int alignment)
    {
        alignment = Math.Max(alignment, 1);
        return checked((value + alignment - 1) & -alignment);
    }
}

internal readonly record struct PlannedSection(CoffInputSection Section, int Offset);
