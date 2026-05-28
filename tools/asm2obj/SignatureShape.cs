// Helpers for analysing the *shape* of a parameter signature (pointer
// depth, leaf type code, presence of unsupported aggregate kinds) so we
// can validate user-supplied Asm2Obj.* attributes and compute the leaf
// slot for IsLong / IsSignUnspecifiedByte.

using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

internal static class SignatureShape
{
    /// <summary>
    /// Information about one parameter's signature shape, computed by
    /// walking the input blob once at Phase A time.
    /// </summary>
    public readonly struct ParamShape
    {
        /// <summary>Number of Ptr layers wrapping the leaf type. 0 = leaf-only.</summary>
        public int PointerDepth { get; }
        /// <summary>The leaf SignatureTypeCode (or 0xFF for "unsupported aggregate").</summary>
        public SignatureTypeCode LeafCode { get; }
        /// <summary>True when the signature is something asm2obj cannot annotate (SZArray, Array, GenericInst, ByRef, FunctionPointer, generic-param).</summary>
        public bool IsUnsupportedShape { get; }
        /// <summary>True when the leaf is one of int8/uint8/int16/uint16/int32/uint32/int64/uint64 (i.e. an integral primitive).</summary>
        public bool IsIntegralLeaf { get; }

        public ParamShape(int depth, SignatureTypeCode leaf, bool unsupported, bool integralLeaf)
        {
            PointerDepth = depth;
            LeafCode = leaf;
            IsUnsupportedShape = unsupported;
            IsIntegralLeaf = integralLeaf;
        }

        /// <summary>The slot index of the leaf == PointerDepth (per ECMA II.23.2.12).</summary>
        public int LeafSlot => PointerDepth;
    }

    /// <summary>
    /// Walks a single parameter or return-type signature starting at the
    /// reader's current offset. The reader is restored to its starting
    /// position before returning.
    /// </summary>
    public static ParamShape Analyse(BlobReader sigReader)
    {
        var snap = sigReader; // struct copy — caller's position unchanged
        return AnalyseAdvancing(ref snap);
    }

    /// <summary>
    /// Walks one Type form starting at the reader's current offset and
    /// returns its shape, advancing the reader past the Type bytes.
    /// </summary>
    public static ParamShape AnalyseAdvancing(ref BlobReader sigReader)
    {
        int depth = 0;
    again:
        // Skip leading modopt/modreq markers.
        while (sigReader.RemainingBytes > 0)
        {
            int save = sigReader.Offset;
            byte b = sigReader.ReadByte();
            var tc = (SignatureTypeCode)b;
            if (tc != SignatureTypeCode.OptionalModifier && tc != SignatureTypeCode.RequiredModifier)
            {
                sigReader.Offset = save;
                break;
            }
            sigReader.ReadTypeHandle(); // skip the type-handle compressed integer
        }

        if (sigReader.RemainingBytes == 0)
            return new ParamShape(depth, 0, unsupported: true, integralLeaf: false);

        SignatureTypeCode tc2 = sigReader.ReadSignatureTypeCode();
        switch (tc2)
        {
            case SignatureTypeCode.Pointer:
                depth++;
                goto again;

            case SignatureTypeCode.Boolean:
            case SignatureTypeCode.Char:
            case SignatureTypeCode.SByte:
            case SignatureTypeCode.Byte:
            case SignatureTypeCode.Int16:
            case SignatureTypeCode.UInt16:
            case SignatureTypeCode.Int32:
            case SignatureTypeCode.UInt32:
            case SignatureTypeCode.Int64:
            case SignatureTypeCode.UInt64:
            case SignatureTypeCode.Single:
            case SignatureTypeCode.Double:
            case SignatureTypeCode.IntPtr:
            case SignatureTypeCode.UIntPtr:
            case SignatureTypeCode.Void:
                {
                    bool integral = tc2 switch
                    {
                        SignatureTypeCode.SByte or SignatureTypeCode.Byte or
                        SignatureTypeCode.Int16 or SignatureTypeCode.UInt16 or
                        SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or
                        SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 => true,
                        _ => false,
                    };
                    return new ParamShape(depth, tc2, unsupported: false, integralLeaf: integral);
                }

            case SignatureTypeCode.TypeHandle:
                sigReader.ReadTypeHandle();
                return new ParamShape(depth, tc2, unsupported: false, integralLeaf: false);

            // Everything below is currently unsupported for attribute
            // injection. We still walk the bytes so the reader is left at
            // a consistent position.
            case SignatureTypeCode.SZArray:
                {
                    // Walk the element type recursively to advance the reader,
                    // but mark the shape unsupported regardless of the element kind.
                    AnalyseAdvancing(ref sigReader);
                    return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
                }
            case SignatureTypeCode.Array:
                {
                    AnalyseAdvancing(ref sigReader);
                    int rank = sigReader.ReadCompressedInteger();
                    int boundsCount = sigReader.ReadCompressedInteger();
                    for (int i = 0; i < boundsCount; i++) sigReader.ReadCompressedInteger();
                    int loCount = sigReader.ReadCompressedInteger();
                    for (int j = 0; j < loCount; j++) sigReader.ReadCompressedSignedInteger();
                    return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
                }
            case SignatureTypeCode.GenericTypeInstance:
                {
                    sigReader.ReadByte(); // class/valuetype tag
                    sigReader.ReadTypeHandle();
                    int n = sigReader.ReadCompressedInteger();
                    for (int i = 0; i < n; i++) AnalyseAdvancing(ref sigReader);
                    return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
                }
            case SignatureTypeCode.FunctionPointer:
                {
                    // Walk the full FNPTR sub-signature so the reader is left
                    // positioned at the next sibling slot. Without this,
                    // GetSlotSignatureReader would walk past an unannotated
                    // FNPTR param into the middle of its inner method
                    // signature when computing the position of a later
                    // annotated parameter, and downstream validation /
                    // planning would inspect the wrong bytes.
                    SignatureHeader fnHeader = sigReader.ReadSignatureHeader();
                    if (fnHeader.IsGeneric)
                        sigReader.ReadCompressedInteger(); // generic arity
                    int fnParamCount = sigReader.ReadCompressedInteger();
                    // Return type
                    AnalyseAdvancing(ref sigReader);
                    for (int i = 0; i < fnParamCount; i++)
                        AnalyseAdvancing(ref sigReader);
                    return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
                }
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                sigReader.ReadCompressedInteger();
                return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
            case SignatureTypeCode.ByReference:
                AnalyseAdvancing(ref sigReader);
                return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
            case SignatureTypeCode.String:
            case SignatureTypeCode.Object:
                return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
            default:
                return new ParamShape(depth, tc2, unsupported: true, integralLeaf: false);
        }
    }
}
