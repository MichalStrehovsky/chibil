// Phase D-pre — Field data emission. Copies the raw bytes of every surviving
// HasFieldRVA field from the input PE into the output .data section and
// registers the corresponding CLR-token COFF data symbols.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Coff;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    public void EmitFieldData(
        ManagedCoffSymbolTableBuilder symtab,
        PEReader peReader,
        CoffSectionWithContentBuilder dataSection)
    {
        for (int inputRow = 1; inputRow <= _reader.GetTableRowCount(TableIndex.Field); inputRow++)
        {
            var inputH = MetadataTokens.FieldDefinitionHandle(inputRow);
            var fd = _reader.GetFieldDefinition(inputH);
            if ((fd.Attributes & FieldAttributes.HasFieldRVA) == 0) continue;

            var outH = TokenMap.MapField(inputH);
            if (outH.IsNil || MetadataTokens.GetRowNumber(outH) == 0) continue;

            int size = FieldData.GetSize(_reader, fd, pointerSize: _ptrSize);
            int alignment = GetFieldDataAlignment(fd, size);
            while ((dataSection.Content.Count & (alignment - 1)) != 0) dataSection.Content.WriteByte(0);
            int offset = dataSection.Content.Count;

            int rva = fd.GetRelativeVirtualAddress();
            var section = peReader.GetSectionData(rva);
            var srcReader = section.GetReader();
            byte[] bytes = srcReader.ReadBytes(size);
            dataSection.Content.WriteBytes(bytes);

            string name = _reader.GetString(fd.Name);
            symtab.AddDataClrToken(name, outH, dataSection, offset, out _);
        }
    }

    private int GetFieldDataAlignment(FieldDefinition fd, int size)
    {
        // Primitive types align to their size, capped at the pointer width.
        // For value types we honour the ClassLayout PackingSize when present,
        // else fall back to min(size, ptrSize) — the same default used for
        // primitives. Returning 1 unconditionally would misalign multi-byte
        // HasFieldRVA data for structs without explicit pack and could shift
        // layout vs. the input PE.
        var sigReader = _reader.GetBlobReader(fd.Signature);
        sigReader.ReadSignatureHeader();
    again:
        SignatureTypeCode tc = sigReader.ReadSignatureTypeCode();
        if (tc == SignatureTypeCode.OptionalModifier || tc == SignatureTypeCode.RequiredModifier)
        {
            sigReader.ReadTypeHandle();
            goto again;
        }
        if (tc == SignatureTypeCode.TypeHandle)
        {
            sigReader.Offset -= 1;
            sigReader.ReadByte();
            EntityHandle typeHandle = sigReader.ReadTypeHandle();
            if (typeHandle.Kind == HandleKind.TypeDefinition)
            {
                var td = _reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                var layout = td.GetLayout();
                int pack = layout.IsDefault ? 0 : layout.PackingSize;
                if (pack > 0) return pack;
            }
        }
        return Math.Min(size, _ptrSize);
    }
}
