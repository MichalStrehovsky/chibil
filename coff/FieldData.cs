using System;
using System.Reflection.Metadata;

namespace Coff;

/// <summary>Sizes RVA field data using target pointer widths and explicit value-type layouts.</summary>
public static class FieldData
{
    public static int GetSize(MetadataReader metadata, FieldDefinition field, int pointerSize,
        Func<EntityHandle, TypeDefinition> resolveType = null)
    {
        if (pointerSize is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        var signature = metadata.GetBlobReader(field.Signature);
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Field)
            throw new BadImageFormatException("Invalid field storage signature.");
        var code = (SignatureTypeCode)signature.ReadByte();
        while (code is SignatureTypeCode.OptionalModifier or SignatureTypeCode.RequiredModifier)
        {
            signature.ReadTypeHandle();
            code = (SignatureTypeCode)signature.ReadByte();
        }
        switch (code)
        {
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
            case (SignatureTypeCode)SignatureTypeKind.ValueType:
                EntityHandle handle = signature.ReadTypeHandle();
                if (resolveType == null && handle.Kind != HandleKind.TypeDefinition)
                    throw new NotSupportedException(
                        $"FieldRVA field '{metadata.GetString(field.Name)}' references an unresolved value type.");
                var type = resolveType != null ? resolveType(handle) : metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                int size = type.GetLayout().Size;
                if (size <= 0)
                    throw new NotSupportedException(
                        $"FieldRVA field '{metadata.GetString(field.Name)}' requires an explicit ClassLayout size.");
                return size;
            default:
                throw new NotSupportedException(
                    $"Cannot determine FieldRVA data size for field '{metadata.GetString(field.Name)}' (signature type 0x{(byte)code:X2}).");
        }
    }
}
