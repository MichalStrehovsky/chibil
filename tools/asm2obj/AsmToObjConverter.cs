using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Coff;

namespace Asm2Obj;

/// <summary>
/// Top-level driver that orchestrates the metadata-copier pipeline (Phases A–F)
/// to convert a .NET assembly into a managed COFF .obj.
/// </summary>
public static class AsmToObjConverter
{
    public static byte[] Convert(string inputAssemblyPath, Machine machine, string outputObjName)
    {
        using var fs = new FileStream(inputAssemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var peReader = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
        var reader = peReader.GetMetadataReader();

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var outputMd = new MetadataBuilder();

        var copier = new MetadataCopier(reader, outputMd, machine);
        copier.ClassifyAndPlan();
        copier.PopulateTables();

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        copier.EmitFieldData(symtab, peReader, dataSection);
        copier.EmitMethodBodies(symtab, ilSection, coffHeader, peReader);

        copier.EmitNepThunks(
            machine, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection);

        // Module row: emitted last so its name reflects the requested -o filename.
        outputMd.AddModule(0,
            outputMd.GetOrAddString(outputObjName),
            outputMd.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // Only emit sections that carry content, matching MSVC /clr reference
        // objects (which omit empty sections, including .text$mn).
        var sections = new System.Collections.Generic.List<CoffSectionBuilder>();
        if (ilSection.Content.Count > 0) sections.Add(ilSection);
        if (dataSection.Content.Count > 0) sections.Add(dataSection);
        if (ilFixupSection.Content.Count > 0) sections.Add(ilFixupSection);
        if (nepSection.Content.Count > 0) sections.Add(nepSection);

        var coffBuilder = new ManagedCoffBuilder(
            coffHeader, new MetadataRootBuilder(outputMd), symtab, codeViewSymbols: null,
            sections);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
