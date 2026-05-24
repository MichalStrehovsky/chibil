// Phase D-pre — Field data emission. Copies the raw bytes of every surviving
// HasFieldRVA field from the input PE into the output .data section and
// registers the corresponding CLR-token COFF data symbols.

using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    public void EmitFieldData(
        ManagedCoffSymbolTableBuilder symtab,
        PEReader peReader,
        BlobBuilder dataStream)
    {
        for (int inputRow = 1; inputRow <= _reader.GetTableRowCount(TableIndex.Field); inputRow++)
        {
            var inputH = MetadataTokens.FieldDefinitionHandle(inputRow);
            var fd = _reader.GetFieldDefinition(inputH);
            if ((fd.Attributes & FieldAttributes.HasFieldRVA) == 0) continue;

            var outH = TokenMap.MapField(inputH);
            if (outH.IsNil || MetadataTokens.GetRowNumber(outH) == 0) continue;

            int size = GetFieldDataSize(fd);
            int alignment = GetFieldDataAlignment(fd, size);
            while ((dataStream.Count & (alignment - 1)) != 0) dataStream.WriteByte(0);
            int offset = dataStream.Count;

            int rva = fd.GetRelativeVirtualAddress();
            var section = peReader.GetSectionData(rva);
            var srcReader = section.GetReader();
            byte[] bytes = srcReader.ReadBytes(size);
            dataStream.WriteBytes(bytes);

            string name = _reader.GetString(fd.Name);
            symtab.AddDataClrToken(name, outH, LogicalSection.Data, offset, out _);
        }
    }

    /// <summary>
    /// Determines the size in bytes of a HasFieldRVA field's initial data. The
    /// signature is either a primitive type (size implied by the type code) or
    /// a value type whose size comes from the ClassLayout table.
    /// </summary>
    private int GetFieldDataSize(FieldDefinition fd)
    {
        var sigReader = _reader.GetBlobReader(fd.Signature);
        sigReader.ReadSignatureHeader(); // FIELD = 0x06

        // Skip leading modopt/modreq markers.
    again:
        SignatureTypeCode tc = sigReader.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.OptionalModifier:
            case SignatureTypeCode.RequiredModifier:
                sigReader.ReadTypeHandle();
                goto again;

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
                return _ptrSize;
            case SignatureTypeCode.TypeHandle:
                {
                    sigReader.Offset -= 1;
                    sigReader.ReadByte(); // raw 0x11 / 0x12 tag
                    EntityHandle typeHandle = sigReader.ReadTypeHandle();
                    return GetTypeSize(typeHandle, fd);
                }
            default:
                throw new NotSupportedException(
                    $"Cannot determine FieldRVA data size for field '{_reader.GetString(fd.Name)}' (signature type 0x{(byte)tc:X2}).");
        }
    }

    private int GetTypeSize(EntityHandle typeHandle, FieldDefinition fd)
    {
        if (typeHandle.Kind != HandleKind.TypeDefinition)
            throw new NotSupportedException(
                $"FieldRVA field '{_reader.GetString(fd.Name)}' references a non-TypeDef value type. " +
                "v1 cannot determine its size.");

        var td = _reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
        var layout = td.GetLayout();
        if (layout.IsDefault || layout.Size == 0)
            throw new NotSupportedException(
                $"FieldRVA field '{_reader.GetString(fd.Name)}' references value type " +
                $"'{_reader.GetString(td.Name)}' without an explicit ClassLayout size.");
        return layout.Size;
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
