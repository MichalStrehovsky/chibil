using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Coff;

namespace Chilink;

internal sealed class InputFile : IDisposable
{
    public readonly string Path;
    public readonly int Index;
    public readonly CoffReader Reader;
    public readonly MetadataReader Metadata;
    public readonly List<InputSection> Sections = new();
    public readonly Dictionary<CoffSymbolHandle, InputSymbol> Symbols = new();
    public readonly Dictionary<EntityHandle, Definition> Definitions = new();

    public InputFile(string path, int index)
    {
        Path = path;
        Index = index;
        Reader = new CoffReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
        try
        {
            if (Reader.CoffHeaders.CoffHeader.Machine != Machine.Amd64)
                throw Error("only ordinary x64 COFF objects are supported");
            if (Reader.CoffHeaders.MetadataSize == 0)
                throw Error("object has no managed metadata");
            Metadata = Reader.GetMetadataReader(MetadataReaderOptions.None);
            var sections = Reader.GetSectionTableReader();
            var symbols = Reader.GetSymbolTableReader();
            var strings = Reader.GetStringTableReader();
            foreach (var handle in sections.Sections)
            {
                var section = sections.GetCoffSection(handle);
                if ((section.SectionCharacteristics & SectionCharacteristics.LinkerNRelocOvfl) != 0)
                    throw Error("COFF relocation overflow is not supported");
                var item = new InputSection(this, section, strings.GetString(section.Name));
                Sections.Add(item);
                for (int i = 0; i < item.Relocations.NumberOfRelocations; i++)
                {
                    if (item.Relocations.GetRelocation(i).Offset >= section.SizeOfRawData)
                        throw Error($"relocation outside section {item.Name}");
                }
            }
            foreach (var handle in symbols.Symbols)
            {
                var symbol = symbols.GetCoffSymbol(handle);
                int section = symbol.SectionNumber.Number;
                if (section > Sections.Count || section < -2)
                    throw Error($"invalid section number for symbol {handle.Index}");
                var item = new InputSymbol(
                    strings.GetString(symbol.Name), symbol,
                    section > 0 ? Sections[section - 1] : null);
                Symbols.Add(handle, item);
                item.Section?.Symbols.Add(item);

                if (symbol.StorageClass == CoffSymbolStorageClass.Static &&
                    item.Section != null && item.Name == item.Section.Name)
                {
                    var definition = symbol.GetSectionDefinition();
                    if (!definition.IsDefault && item.Section.IsComdat)
                    {
                        item.Section.Selection = definition.ComdatSelection;
                        if (definition.ComdatSelection == CoffComdatSelection.Associative)
                        {
                            int associated = definition.AssociatedSection.Number;
                            if (associated <= 0 || associated > Sections.Count || associated == section)
                                throw Error($"invalid associative COMDAT '{item.Name}'");
                            item.Section.Associated = Sections[associated - 1];
                        }
                    }
                }
                if (symbol.StorageClass != CoffSymbolStorageClass.ClrToken)
                    continue;
                if (!int.TryParse(item.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int token))
                    throw Error($"invalid CLR token symbol '{item.Name}'");
                item.Token = MetadataTokens.Handle(token);
                var tokenDefinition = symbol.GetClrTokenDefinition();
                if (!tokenDefinition.IsDefault)
                    item.AssociatedSymbol = tokenDefinition.AssociatedSymbol;
            }
            foreach (InputSymbol symbol in Symbols.Values)
            {
                if (symbol.AssociatedSymbol is not CoffSymbolHandle associatedSymbol)
                    continue;
                if (!Symbols.TryGetValue(associatedSymbol, out InputSymbol target))
                    throw Error($"CLR token '{symbol.Name}' references an invalid symbol");
                if (symbol.Token.Kind is not (HandleKind.MethodDefinition or HandleKind.FieldDefinition))
                    throw Error($"unsupported definition token '{symbol.Name}'");
                if (symbol.Section != target.Section || symbol.Coff.Value != target.Coff.Value)
                    throw Error($"inconsistent CLR token association '{symbol.Name}'");
                if (!Definitions.TryAdd((EntityHandle)symbol.Token,
                    new Definition(new SourceToken(index, (EntityHandle)symbol.Token), target)))
                    throw Error($"duplicate definition token '{symbol.Name}'");
            }
            foreach (InputSection section in Sections)
            {
                for (int i = 0; i < section.Relocations.NumberOfRelocations; i++)
                    if (!Symbols.ContainsKey(section.Relocations.GetRelocation(i).Symbol))
                        throw Error($"relocation in '{section.Name}' references an invalid symbol");
                section.Ignored = section.Name is ".cormeta" or ".drectve" or ".chks64" or ".nep" or ".rdata$ilfixup" ||
                    section.Name.StartsWith(".debug", StringComparison.Ordinal);
                var definitions = section.Symbols.Where(s =>
                    s.Coff.StorageClass != CoffSymbolStorageClass.ClrToken &&
                    !(s.Coff.StorageClass == CoffSymbolStorageClass.Static && s.Name == section.Name)).ToArray();
                if (definitions.Length != 0 &&
                    definitions.All(s => s.Name.StartsWith("__mep@", StringComparison.Ordinal) ||
                                         s.Name.StartsWith("__m2mep@", StringComparison.Ordinal)))
                    section.Ignored = true;
                if (section.IsComdat && section.Selection == 0)
                    throw Error($"missing COMDAT section definition for '{section.Name}'");
            }
        }
        catch
        {
            Reader.Dispose();
            throw;
        }
    }

    public LinkException Error(string message) => new($"{Path}: {message}");
    public void Dispose() => Reader.Dispose();
}

internal sealed class InputSection
{
    public readonly InputFile Input;
    public readonly CoffSection Header;
    public readonly string Name;
    public readonly List<InputSymbol> Symbols = new();
    public readonly CoffRelocationTableReader Relocations;
    public CoffComdatSelection Selection;
    public InputSection Associated, Winner;
    public bool Ignored, Live;
    public int OutputDataOffset = -1;
    public int OutputCodeOffset = -1;
    private byte[] _data;

    public InputSection(InputFile input, CoffSection header, string name)
    {
        Input = input;
        Header = header;
        Name = name;
        Relocations = input.Reader.GetRelocationTableReader(header);
        Winner = this;
    }

    public bool IsComdat => (Header.SectionCharacteristics & SectionCharacteristics.LinkerComdat) != 0;
    public bool IsWritable => (Header.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0;
    public bool IsCode => (Header.SectionCharacteristics & SectionCharacteristics.ContainsCode) != 0;
    public byte[] GetData() => _data ??= Input.Reader.GetSectionData(Header).GetContent().ToArray();
}

internal sealed class InputSymbol
{
    public readonly string Name;
    public readonly CoffSymbol Coff;
    public readonly InputSection Section;
    public Handle Token;
    public CoffSymbolHandle? AssociatedSymbol;

    public InputSymbol(string name, CoffSymbol coff, InputSection section) =>
        (Name, Coff, Section) = (name, coff, section);
}

internal sealed record Definition(SourceToken Token, InputSymbol Symbol)
{
    public InputSection Section => Symbol.Section;
    public int Offset => checked((int)Symbol.Coff.Value);
}
