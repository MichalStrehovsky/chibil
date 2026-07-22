using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Coff;

namespace Chilink;

internal sealed class CoffInput : IDisposable
{
    private readonly CoffReader _reader;

    public CoffInput(string path, int ordinal, Machine expectedMachine)
    {
        Path = path;
        Ordinal = ordinal;

        try
        {
            _reader = new CoffReader(
                File.OpenRead(path),
                PEStreamOptions.PrefetchEntireImage | PEStreamOptions.PrefetchMetadata);

            Machine = _reader.CoffHeaders.CoffHeader.Machine;
            if (Machine != expectedMachine)
            {
                throw new ChilinkException(
                    $"input '{path}' targets {Machine}, but chilink was invoked for {expectedMachine}");
            }
            if (!_reader.HasMetadata)
            {
                throw new ChilinkException($"input '{path}' does not contain a .cormeta section");
            }

            Metadata = _reader.GetMetadataReader(MetadataReaderOptions.None);
            ReadObject();
        }
        catch (ChilinkException)
        {
            _reader?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            _reader?.Dispose();
            throw new ChilinkException($"cannot read COFF input '{path}': {ex.Message}", ex);
        }
    }

    public string Path { get; }

    public int Ordinal { get; }

    public Machine Machine { get; }

    public MetadataReader Metadata { get; }

    public IReadOnlyList<CoffInputSection> Sections { get; private set; }

    public IReadOnlyList<CoffInputSymbol> Symbols { get; private set; }

    public Dictionary<CoffSymbolHandle, CoffInputSymbol> SymbolsByHandle { get; private set; }

    public Dictionary<EntityHandle, CoffInputSymbol> DefinedClrTokens { get; private set; }

    public void Dispose() => _reader.Dispose();

    private void ReadObject()
    {
        CoffSectionTableReader sectionReader = _reader.GetSectionTableReader();
        CoffSymbolTableReader symbolReader = _reader.GetSymbolTableReader();
        CoffStringTableReader stringReader = _reader.GetStringTableReader();

        var sections = new List<CoffInputSection>(sectionReader.NumberOfSections);
        var sectionsByHandle = new Dictionary<CoffSectionHandle, CoffInputSection>();

        foreach (CoffSectionHandle handle in sectionReader.Sections)
        {
            CoffSection section = sectionReader.GetCoffSection(handle);
            string name = stringReader.GetString(section.Name);
            ImmutableArray<byte> content = _reader.GetSectionData(section).GetContent();
            var relocations = new List<CoffInputRelocation>(section.NumberOfRelocations);
            CoffRelocationTableReader relocationReader = _reader.GetRelocationTableReader(section);

            for (int i = 0; i < relocationReader.NumberOfRelocations; i++)
            {
                CoffRelocation relocation = relocationReader.GetRelocation(i);
                relocations.Add(new CoffInputRelocation(relocation.Offset, relocation.Symbol, relocation.Type));
            }

            var inputSection = new CoffInputSection(
                this,
                handle,
                name,
                section.SectionCharacteristics,
                GetAlignment(section.SectionCharacteristics),
                content,
                relocations);
            sections.Add(inputSection);
            sectionsByHandle.Add(handle, inputSection);
        }

        var symbols = new List<CoffInputSymbol>();
        var symbolsByHandle = new Dictionary<CoffSymbolHandle, CoffInputSymbol>();
        var definedClrTokens = new Dictionary<EntityHandle, CoffInputSymbol>();

        foreach (CoffSymbolHandle handle in symbolReader.Symbols)
        {
            CoffSymbol symbol = symbolReader.GetCoffSymbol(handle);
            string name = stringReader.GetString(symbol.Name);
            CoffSymbolHandle? clrTarget = null;
            CoffSectionDefinition sectionDefinition = null;

            if (symbol.NumberOfAuxSymbols > 0)
            {
                CoffAuxiliarySymbol auxiliary = symbolReader.GetAuxiliarySymbol(handle, 0);
                if (symbol.StorageClass == CoffSymbolStorageClass.ClrToken)
                {
                    CoffClrTokenDefinitionAuxiliarySymbol token = auxiliary.AsClrTokenDefinition();
                    if (token.AuxiliaryType != 1)
                    {
                        throw new ChilinkException(
                            $"input '{Path}' contains unsupported CLR token auxiliary type {token.AuxiliaryType}");
                    }
                    clrTarget = token.TargetSymbol;
                }
                else if (symbol.StorageClass == CoffSymbolStorageClass.Static &&
                         symbol.SectionNumber.Kind == CoffSectionHandleKind.Physical)
                {
                    CoffSectionDefinitionAuxiliarySymbol definition = auxiliary.AsSectionDefinition();
                    sectionDefinition = new CoffSectionDefinition(
                        definition.Length,
                        definition.NumberOfRelocations,
                        definition.Checksum,
                        definition.AssociatedSection,
                        definition.Selection);
                }
            }

            EntityHandle clrToken = default;
            if (symbol.StorageClass == CoffSymbolStorageClass.ClrToken &&
                name.Length == 8 &&
                int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tokenValue))
            {
                Handle handleValue = MetadataTokens.Handle(tokenValue);
                if (handleValue.Kind != HandleKind.UserString)
                {
                    clrToken = (EntityHandle)handleValue;
                }
            }

            var inputSymbol = new CoffInputSymbol(
                this,
                handle,
                name,
                symbol.Value,
                symbol.SectionNumber,
                symbol.Type,
                symbol.StorageClass,
                symbol.NumberOfAuxSymbols,
                clrToken,
                clrTarget,
                sectionDefinition);
            symbols.Add(inputSymbol);
            symbolsByHandle.Add(handle, inputSymbol);

            if (!clrToken.IsNil && symbol.NumberOfAuxSymbols > 0)
            {
                if (!definedClrTokens.TryAdd(clrToken, inputSymbol))
                {
                    throw new ChilinkException(
                        $"input '{Path}' defines CLR token 0x{MetadataTokens.GetToken(clrToken):X8} more than once");
                }
            }
        }

        foreach (CoffInputSymbol symbol in symbols)
        {
            if (symbol.Section.Kind == CoffSectionHandleKind.Physical)
            {
                sectionsByHandle[symbol.Section].Symbols.Add(symbol);
            }
        }

        foreach (CoffInputSection section in sections)
        {
            CoffInputSymbol sectionSymbol = section.Symbols.FirstOrDefault(
                symbol => symbol.SectionDefinition != null && symbol.Name == section.Name);
            if (sectionSymbol != null)
            {
                section.SectionDefinition = sectionSymbol.SectionDefinition;
            }
        }

        Sections = sections;
        Symbols = symbols;
        SymbolsByHandle = symbolsByHandle;
        DefinedClrTokens = definedClrTokens;
    }

    private static int GetAlignment(SectionCharacteristics characteristics)
    {
        int encoded = ((int)characteristics >> 20) & 0xF;
        return encoded == 0 ? 1 : 1 << (encoded - 1);
    }
}

internal sealed class CoffInputSection
{
    public CoffInputSection(
        CoffInput input,
        CoffSectionHandle handle,
        string name,
        SectionCharacteristics characteristics,
        int alignment,
        ImmutableArray<byte> content,
        IReadOnlyList<CoffInputRelocation> relocations)
    {
        Input = input;
        Handle = handle;
        Name = name;
        Characteristics = characteristics;
        Alignment = alignment;
        Content = content;
        Relocations = relocations;
    }

    public CoffInput Input { get; }

    public CoffSectionHandle Handle { get; }

    public string Name { get; }

    public SectionCharacteristics Characteristics { get; }

    public int Alignment { get; }

    public ImmutableArray<byte> Content { get; }

    public IReadOnlyList<CoffInputRelocation> Relocations { get; }

    public List<CoffInputSymbol> Symbols { get; } = new();

    public CoffSectionDefinition SectionDefinition { get; set; }

    public bool IsComdat => (Characteristics & SectionCharacteristics.LinkerComdat) != 0;

    public bool IsDebug => Name.StartsWith(".debug", StringComparison.Ordinal);

    public bool IsNativeTransitionSection
    {
        get
        {
            if (Name is ".nep" or ".rdata$ilfixup")
            {
                return true;
            }
            if (Name != ".data")
            {
                return false;
            }

            bool hasMepSymbol = Symbols.Any(
                symbol => symbol.Name.StartsWith("__mep@", StringComparison.Ordinal) ||
                          symbol.Name.StartsWith("__m2mep@", StringComparison.Ordinal));
            bool hasManagedField = Symbols.Any(
                symbol => symbol.IsClrToken &&
                          symbol.IsDefined &&
                          symbol.ClrToken.Kind == HandleKind.FieldDefinition);
            return hasMepSymbol && !hasManagedField;
        }
    }
}

internal sealed record CoffInputSymbol(
    CoffInput Input,
    CoffSymbolHandle Handle,
    string Name,
    uint Value,
    CoffSectionHandle Section,
    CoffSymbolType Type,
    CoffSymbolStorageClass StorageClass,
    byte NumberOfAuxSymbols,
    EntityHandle ClrToken,
    CoffSymbolHandle? ClrTokenTarget,
    CoffSectionDefinition SectionDefinition)
{
    public bool IsDefined => Section.Kind == CoffSectionHandleKind.Physical;

    public bool IsExternal => StorageClass == CoffSymbolStorageClass.External;

    public bool IsClrToken => StorageClass == CoffSymbolStorageClass.ClrToken;

    public bool IsCommon =>
        IsExternal &&
        !IsDefined &&
        !IsClrToken &&
        Value > 0;
}

internal readonly record struct CoffInputRelocation(
    uint Offset,
    CoffSymbolHandle Symbol,
    ImageRelocation Type);

internal sealed record CoffSectionDefinition(
    uint Length,
    ushort NumberOfRelocations,
    uint Checksum,
    CoffSectionHandle AssociatedSection,
    CoffComdatSelection Selection);
