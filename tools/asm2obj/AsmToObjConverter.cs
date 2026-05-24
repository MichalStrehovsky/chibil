using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

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

        var ilStream = new BlobBuilder();
        var ilRelocs = new BlobBuilder();
        var dataStream = new BlobBuilder();
        var dataRelocs = new BlobBuilder();
        var nepStream = new BlobBuilder();
        var nepRelocs = new BlobBuilder();
        var ilFixupStream = new BlobBuilder();
        var ilFixupRelocs = new BlobBuilder();

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStream, ilRelocs, symtab, coffHeader, codeViewSymbolBuilder: null);

        copier.EmitFieldData(symtab, peReader, dataStream);
        copier.EmitMethodBodies(symtab, bodyEncoder, peReader);

        copier.EmitNepThunks(
            machine, coffHeader, symtab,
            dataStream, dataRelocs,
            nepStream, nepRelocs,
            ilFixupStream, ilFixupRelocs);

        // Module row: emitted last so its name reflects the requested -o filename.
        outputMd.AddModule(0,
            outputMd.GetOrAddString(outputObjName),
            outputMd.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        var coffBuilder = new ManagedCoffBuilder(
            coffHeader, new MetadataRootBuilder(outputMd), symtab, codeViewSymbols: null,
            ilStream, ilRelocs,
            dataStream: dataStream, dataRelocs: dataRelocs,
            ilFixupStream: ilFixupStream, ilFixupRelocs: ilFixupRelocs,
            nepStream: nepStream, nepRelocs: nepRelocs);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
