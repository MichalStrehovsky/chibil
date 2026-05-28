// Adapted from dotnet/runtime ILCompiler.DependencyAnalysis.EcmaSignatureRewriter.
// Removes Internal.TypeSystem dependency in favor of our own TokenMap.

using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

/// <summary>
/// Rewrites ECMA signature blobs (method, field, local-variables, standalone,
/// type-spec, method-spec, property, member-ref) by walking the input blob and
/// emitting an equivalent blob through a <see cref="BlobBuilder"/>, remapping
/// every embedded type/method/field token via a <see cref="TokenMap"/>.
/// </summary>
public struct EcmaSignatureRewriter
{
    private BlobReader _blobReader;
    private readonly TokenMap _tokenMap;

    // Slot-aware modifier injector consulted on method-signature rewrite paths
    // that opted-in via the corresponding overload. Null on field / local /
    // type-spec / member-ref-field / method-spec / property signatures and on
    // method signatures without an annotation plan. Mutable so that the
    // FunctionPointer case can save/clear it around the recursive
    // RewriteMethodSignature call for the FNPTR sub-signature — the injector's
    // (paramIndex, slot) cursor addresses *this* method's parameters and must
    // not bleed into a nested FNPTR's own parameter slots.
    private ISignatureModifierInjector _injector;

    private EcmaSignatureRewriter(BlobReader blobReader, TokenMap tokenMap, ISignatureModifierInjector injector = null)
    {
        _blobReader = blobReader;
        _tokenMap = tokenMap;
        _injector = injector;
    }

    private void RewriteCustomModifier(SignatureTypeCode typeCode, CustomModifiersEncoder encoder)
    {
        encoder.AddModifier(
            _tokenMap.MapEntity(_blobReader.ReadTypeHandle()),
            typeCode == SignatureTypeCode.OptionalModifier);
    }

    private void RewriteType(SignatureTypeEncoder encoder)
    {
        RewriteType(_blobReader.ReadSignatureTypeCode(), encoder);
    }

    private void RewriteType(SignatureTypeCode typeCode, SignatureTypeEncoder encoder)
    {
    again:
        switch (typeCode)
        {
            case SignatureTypeCode.Boolean: encoder.Boolean(); break;
            case SignatureTypeCode.SByte: encoder.SByte(); break;
            case SignatureTypeCode.Byte: encoder.Byte(); break;
            case SignatureTypeCode.Int16: encoder.Int16(); break;
            case SignatureTypeCode.UInt16: encoder.UInt16(); break;
            case SignatureTypeCode.Int32: encoder.Int32(); break;
            case SignatureTypeCode.UInt32: encoder.UInt32(); break;
            case SignatureTypeCode.Int64: encoder.Int64(); break;
            case SignatureTypeCode.UInt64: encoder.UInt64(); break;
            case SignatureTypeCode.Single: encoder.Single(); break;
            case SignatureTypeCode.Double: encoder.Double(); break;
            case SignatureTypeCode.Char: encoder.Char(); break;
            case SignatureTypeCode.String: encoder.String(); break;
            case SignatureTypeCode.IntPtr: encoder.IntPtr(); break;
            case SignatureTypeCode.UIntPtr: encoder.UIntPtr(); break;
            case SignatureTypeCode.Object: encoder.Object(); break;
            // Void is only legal here as the inner type of a Pointer
            // (`void*`, `const void*`, etc.). The enclosing Pointer case
            // below routes the inner Type through RewriteType so injected
            // modopts at the pointee slot get emitted before the Void byte.
            // SignatureTypeEncoder offers no Void() method; write the raw
            // byte directly on the underlying BlobBuilder.
            case SignatureTypeCode.Void:
                encoder.Builder.WriteByte((byte)SignatureTypeCode.Void);
                break;
            case SignatureTypeCode.TypeHandle:
                {
                    // S.R.Metadata collapses Class/ValueType into TypeHandle but we
                    // need to preserve the original kind. Step back one byte and read
                    // the raw class-or-valuetype tag.
                    _blobReader.Offset = _blobReader.Offset - 1;
                    byte classOrValueType = _blobReader.ReadByte();
                    System.Diagnostics.Debug.Assert(classOrValueType == 0x12 || classOrValueType == 0x11);
                    encoder.Type(
                        _tokenMap.MapEntity(_blobReader.ReadTypeHandle()),
                        isValueType: classOrValueType == 0x11);
                }
                break;
            case SignatureTypeCode.SZArray:
                _injector?.BeginParameterizedType(SignatureTypeCode.SZArray);
                RewriteType(encoder.SZArray());
                _injector?.EndParameterizedType(SignatureTypeCode.SZArray);
                break;
            case SignatureTypeCode.Array:
                encoder.Array(out var arrayEncoder, out var shapeEncoder);
                _injector?.BeginParameterizedType(SignatureTypeCode.Array);
                RewriteType(arrayEncoder);
                _injector?.EndParameterizedType(SignatureTypeCode.Array);
                var rank = _blobReader.ReadCompressedInteger();
                var boundsCount = _blobReader.ReadCompressedInteger();
                int[] bounds = boundsCount > 0 ? new int[boundsCount] : Array.Empty<int>();
                for (int i = 0; i < boundsCount; i++)
                    bounds[i] = _blobReader.ReadCompressedInteger();
                var lowerBoundsCount = _blobReader.ReadCompressedInteger();
                int[] lowerBounds = lowerBoundsCount > 0 ? new int[lowerBoundsCount] : Array.Empty<int>();
                for (int j = 0; j < lowerBoundsCount; j++)
                    lowerBounds[j] = _blobReader.ReadCompressedSignedInteger();
                shapeEncoder.Shape(rank, ImmutableArray.Create<int>(bounds), ImmutableArray.Create<int>(lowerBounds));
                break;
            case SignatureTypeCode.Pointer:
                {
                    // Always take the general "Pointer + recursive Type" path
                    // (never the VoidPointer() shortcut), so injected modopts
                    // at the pointee slot (e.g. `const void*` from
                    // `[IsConst(1)] void*`) end up between the Ptr byte and
                    // the pointee's type code. The byte sequence is identical
                    // to VoidPointer() when there are no inner modopts.
                    SignatureTypeCode inner = _blobReader.ReadSignatureTypeCode();
                    _injector?.BeginParameterizedType(SignatureTypeCode.Pointer);
                    var innerEnc = encoder.Pointer();
                    // Slot-(N+1) injection for the pointee — emitted BEFORE the
                    // recursive RewriteType call so injected modifiers precede
                    // any input modifiers at the pointee slot (which the
                    // recursive call drains via its `case Mod` below).
                    _injector?.EmitInjected(innerEnc.CustomModifiers());
                    RewriteType(inner, innerEnc);
                    _injector?.EndParameterizedType(SignatureTypeCode.Pointer);
                }
                break;
            case SignatureTypeCode.GenericTypeParameter:
                encoder.GenericTypeParameter(_blobReader.ReadCompressedInteger());
                break;
            case SignatureTypeCode.GenericMethodParameter:
                encoder.GenericMethodTypeParameter(_blobReader.ReadCompressedInteger());
                break;
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
                RewriteCustomModifier(typeCode, encoder.CustomModifiers());
                typeCode = _blobReader.ReadSignatureTypeCode();
                goto again;
            case SignatureTypeCode.GenericTypeInstance:
                {
                    int classOrValueType = _blobReader.ReadCompressedInteger();
                    System.Diagnostics.Debug.Assert(classOrValueType == 0x12 || classOrValueType == 0x11);
                    EntityHandle genericTypeDefHandle = _blobReader.ReadTypeHandle();
                    int numGenericArgs = _blobReader.ReadCompressedInteger();

                    GenericTypeArgumentsEncoder genericArgsEncoder = encoder.GenericInstantiation(
                        _tokenMap.MapEntity(genericTypeDefHandle),
                        numGenericArgs,
                        isValueType: classOrValueType == 0x11);

                    for (int i = 0; i < numGenericArgs; i++)
                        RewriteType(genericArgsEncoder.AddArgument());
                }
                break;
            case SignatureTypeCode.FunctionPointer:
                {
                    SignatureHeader header = _blobReader.ReadSignatureHeader();
                    int arity = header.IsGeneric ? _blobReader.ReadCompressedInteger() : 0;
                    MethodSignatureEncoder sigEncoder = encoder.FunctionPointer(header.CallingConvention, 0, arity);
                    int count = _blobReader.ReadCompressedInteger();
                    sigEncoder.Parameters(count, out ReturnTypeEncoder retTypeEncoder, out ParametersEncoder paramEncoder);
                    // The injector's (paramIndex, slot) cursor addresses the
                    // outer method's parameters. Detach it for the nested
                    // FNPTR sub-signature so its BeginParameter(i) calls don't
                    // alias the outer plan at index i (which would emit stray
                    // modifiers into the FNPTR sub-blob — and the mangler
                    // wouldn't see them, since MangleFunctionPointer keeps the
                    // outer (_currentParam, _currentSlot) cursor — producing a
                    // signature/symbol divergence that link.exe rejects).
                    var savedInjector = _injector;
                    _injector = null;
                    RewriteMethodSignature(count, retTypeEncoder, paramEncoder);
                    _injector = savedInjector;
                }
                break;
            default:
                throw new BadImageFormatException($"Unexpected signature type code 0x{(byte)typeCode:X2}");
        }
    }

    public static void RewriteStandaloneSignatureBlob(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteStandaloneSignatureBlob(blobBuilder);
    }

    private void RewriteStandaloneSignatureBlob(BlobBuilder blobBuilder)
    {
        SignatureHeader header = _blobReader.ReadSignatureHeader();
        switch (header.Kind)
        {
            case SignatureKind.Method:
                RewriteMethodSignature(blobBuilder, header);
                break;
            case SignatureKind.LocalVariables:
                RewriteLocalVariablesBlob(blobBuilder, header);
                break;
            default:
                throw new BadImageFormatException($"Unexpected standalone signature kind {header.Kind}");
        }
    }

    private void RewriteLocalVariablesBlob(BlobBuilder blobBuilder, SignatureHeader header)
    {
        int varCount = _blobReader.ReadCompressedInteger();
        var encoder = new BlobEncoder(blobBuilder);
        var localEncoder = encoder.LocalVariableSignature(varCount);

        for (int i = 0; i < varCount; i++)
        {
            var localVarTypeEncoder = localEncoder.AddVariable();
            bool isPinned = false;
            bool isByRef = false;

        again:
            SignatureTypeCode typeCode = _blobReader.ReadSignatureTypeCode();
            if (typeCode == SignatureTypeCode.RequiredModifier || typeCode == SignatureTypeCode.OptionalModifier)
            {
                RewriteCustomModifier(typeCode, localVarTypeEncoder.CustomModifiers());
                goto again;
            }
            if (typeCode == SignatureTypeCode.Pinned)
            {
                isPinned = true;
                goto again;
            }
            if (typeCode == SignatureTypeCode.ByReference)
            {
                isByRef = true;
                goto again;
            }

            if (typeCode == SignatureTypeCode.TypedReference)
            {
                System.Diagnostics.Debug.Assert(!isPinned && !isByRef);
                localVarTypeEncoder.TypedReference();
            }
            else
            {
                RewriteType(typeCode, localVarTypeEncoder.Type(isByRef, isPinned));
            }
        }
    }

    public static void RewriteMethodSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteMethodSignature(blobBuilder);
    }

    /// <summary>
    /// Overload that consults <paramref name="injector"/> at each
    /// <c>CustomMod*</c> position so the rewritten signature carries any
    /// asm2obj-injected modifier bytes (matching the symbols emitted by
    /// <see cref="MsvcNameMangler"/> for the same method). When
    /// <paramref name="injector"/> is <c>null</c>, this is equivalent to
    /// the plain overload.
    /// </summary>
    public static void RewriteMethodSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder,
        ISignatureModifierInjector injector)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap, injector).RewriteMethodSignature(blobBuilder);
    }

    private void RewriteMethodSignature(BlobBuilder blobBuilder)
    {
        SignatureHeader header = _blobReader.ReadSignatureHeader();
        RewriteMethodSignature(blobBuilder, header);
    }

    private void RewriteMethodSignature(BlobBuilder blobBuilder, SignatureHeader header)
    {
        int arity = header.IsGeneric ? _blobReader.ReadCompressedInteger() : 0;
        var encoder = new BlobEncoder(blobBuilder);
        var sigEncoder = encoder.MethodSignature(header.CallingConvention, arity, header.IsInstance);
        RewriteMethodSignature(sigEncoder);
    }

    private void RewriteMethodSignature(MethodSignatureEncoder sigEncoder)
    {
        int count = _blobReader.ReadCompressedInteger();
        sigEncoder.Parameters(count, out ReturnTypeEncoder returnTypeEncoder, out ParametersEncoder paramsEncoder);
        RewriteMethodSignature(count, returnTypeEncoder, paramsEncoder);
    }

    private void RewriteMethodSignature(int count, ReturnTypeEncoder returnTypeEncoder, ParametersEncoder paramsEncoder)
    {
        // ── Return type (paramIndex 0) ──────────────────────────────────────
        _injector?.BeginParameter(0);
        bool isByRef = false;
    againReturnType:
        SignatureTypeCode typeCode = _blobReader.ReadSignatureTypeCode();
        if (typeCode == SignatureTypeCode.ByReference)
        {
            isByRef = true;
            goto againReturnType;
        }
        if (typeCode == SignatureTypeCode.RequiredModifier || typeCode == SignatureTypeCode.OptionalModifier)
        {
            RewriteCustomModifier(typeCode, returnTypeEncoder.CustomModifiers());
            goto againReturnType;
        }

        // Slot-0 injection for the return type. When the input also has
        // modifiers at slot 0, the byte order ends up "input first, injected
        // after" — that's a degenerate case (the C# author asked asm2obj to
        // inject a modifier that's already in the input blob); the rewriter
        // doesn't try to interleave canonically since there's no canonical
        // truth between two competing authors.
        _injector?.EmitInjected(returnTypeEncoder.CustomModifiers());

        if (typeCode == SignatureTypeCode.Void) returnTypeEncoder.Void();
        else if (typeCode == SignatureTypeCode.TypedReference) returnTypeEncoder.TypedReference();
        else RewriteType(typeCode, returnTypeEncoder.Type(isByRef));
        _injector?.EndParameter();

        for (int i = 0; i < count; i++)
        {
            ParameterTypeEncoder paramEncoder = paramsEncoder.AddParameter();
            _injector?.BeginParameter(i + 1);
            isByRef = false;

        againParameter:
            typeCode = _blobReader.ReadSignatureTypeCode();
            if (typeCode == SignatureTypeCode.RequiredModifier || typeCode == SignatureTypeCode.OptionalModifier)
            {
                RewriteCustomModifier(typeCode, paramEncoder.CustomModifiers());
                goto againParameter;
            }
            if (typeCode == SignatureTypeCode.ByReference)
            {
                isByRef = true;
                goto againParameter;
            }

            // Slot-0 injection. See return-type comment above re: ordering
            // when the input also has modifiers at slot 0.
            _injector?.EmitInjected(paramEncoder.CustomModifiers());

            if (typeCode == SignatureTypeCode.TypedReference) paramEncoder.TypedReference();
            else RewriteType(typeCode, paramEncoder.Type(isByRef));
            _injector?.EndParameter();
        }
    }

    public static void RewriteFieldSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteFieldSignature(blobBuilder);
    }

    private void RewriteFieldSignature(BlobBuilder blobBuilder)
    {
        SignatureHeader header = _blobReader.ReadSignatureHeader();
        RewriteFieldSignature(blobBuilder, header);
    }

    private void RewriteFieldSignature(BlobBuilder blobBuilder, SignatureHeader header)
    {
        var encoder = new BlobEncoder(blobBuilder);
        var fieldEncoder = encoder.Field();
        bool isByRef = false;
    again:
        SignatureTypeCode typeCode = _blobReader.ReadSignatureTypeCode();
        if (typeCode == SignatureTypeCode.ByReference)
        {
            isByRef = true;
            goto again;
        }
        if (typeCode == SignatureTypeCode.RequiredModifier || typeCode == SignatureTypeCode.OptionalModifier)
        {
            RewriteCustomModifier(typeCode, fieldEncoder.CustomModifiers());
            goto again;
        }
        if (typeCode == SignatureTypeCode.TypedReference) fieldEncoder.TypedReference();
        else RewriteType(typeCode, fieldEncoder.Type(isByRef));
    }

    public static void RewriteMemberReferenceSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteMemberReferenceSignature(blobBuilder);
    }

    private void RewriteMemberReferenceSignature(BlobBuilder blobBuilder)
    {
        SignatureHeader header = _blobReader.ReadSignatureHeader();
        if (header.Kind == SignatureKind.Method)
            RewriteMethodSignature(blobBuilder, header);
        else
        {
            System.Diagnostics.Debug.Assert(header.Kind == SignatureKind.Field);
            RewriteFieldSignature(blobBuilder, header);
        }
    }

    public static void RewriteTypeSpecSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteTypeSpecSignature(blobBuilder);
    }

    private void RewriteTypeSpecSignature(BlobBuilder blobBuilder)
    {
        var encoder = new SignatureTypeEncoder(blobBuilder);
        RewriteType(encoder);
    }

    public static void RewriteMethodSpecSignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewriteMethodSpecSignature(blobBuilder);
    }

    private void RewriteMethodSpecSignature(BlobBuilder blobBuilder)
    {
        var encoder = new BlobEncoder(blobBuilder);
        if (_blobReader.ReadSignatureHeader().Kind != SignatureKind.MethodSpecification)
            throw new BadImageFormatException("Expected MethodSpecification signature kind");
        int count = _blobReader.ReadCompressedInteger();
        var methodSpecEncoder = encoder.MethodSpecificationSignature(count);
        for (int i = 0; i < count; i++)
            RewriteType(methodSpecEncoder.AddArgument());
    }

    public static void RewritePropertySignature(BlobReader signatureReader, TokenMap tokenMap, BlobBuilder blobBuilder)
    {
        new EcmaSignatureRewriter(signatureReader, tokenMap).RewritePropertySignature(blobBuilder);
    }

    private void RewritePropertySignature(BlobBuilder blobBuilder)
    {
        SignatureHeader header = _blobReader.ReadSignatureHeader();
        RewritePropertySignature(blobBuilder, header);
    }

    private void RewritePropertySignature(BlobBuilder blobBuilder, SignatureHeader header)
    {
        var encoder = new BlobEncoder(blobBuilder);
        var sigEncoder = encoder.PropertySignature(header.IsInstance);
        RewriteMethodSignature(sigEncoder);
    }
}
