using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Chilink;

internal static class ManagedPeEmitter
{
    public static void Emit(
        LinkOptions options,
        MetadataBuilder metadata,
        BlobBuilder ilStream,
        BlobBuilder mappedFieldData,
        MethodDefinitionHandle entryPoint)
    {
        var header = new PEHeaderBuilder(
            machine: options.Machine,
            sectionAlignment: 0x1000,
            imageBase: 0x0000000140000000,
            majorSubsystemVersion: 6,
            subsystem: options.Subsystem,
            dllCharacteristics:
                DllCharacteristics.HighEntropyVirtualAddressSpace |
                DllCharacteristics.DynamicBase |
                DllCharacteristics.NxCompatible |
                DllCharacteristics.TerminalServerAware,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware);

        var peBuilder = new ManagedPEBuilder(
            header,
            new MetadataRootBuilder(metadata),
            ilStream,
            mappedFieldData: mappedFieldData.Count == 0 ? null : mappedFieldData,
            debugDirectoryBuilder: new DebugDirectoryBuilder(),
            strongNameSignatureSize: 0,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: ComputeContentId);

        var image = new BlobBuilder();
        peBuilder.Serialize(image);

        try
        {
            string fullPath = Path.GetFullPath(options.OutputFile);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream stream = new(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            image.WriteContentTo(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ChilinkException($"cannot write output '{options.OutputFile}': {ex.Message}", ex);
        }
    }

    private static BlobContentId ComputeContentId(IEnumerable<Blob> blobs)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Blob blob in blobs)
        {
            ArraySegment<byte> bytes = blob.GetBytes();
            hash.AppendData(bytes.Array, bytes.Offset, bytes.Count);
        }
        return BlobContentId.FromHash(hash.GetHashAndReset());
    }
}
