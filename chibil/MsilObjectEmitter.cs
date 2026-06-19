using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class MsilObjectEmitter
{
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
    private readonly Tokenizer _tokenizer;
    private readonly DataModel _dm;
    private readonly NameMangler _nameMangler;
    private readonly ManagedAggregateModel _aggregateModel;

    private MetadataBuilder _md;
    private ManagedAggregateRegistry _aggregates;
    private CoffHeaderBuilder _coffHeader;
    private ManagedCoffSymbolTableBuilder _symtab;
    private CodeViewSymbolBuilder _codeviewSymbols;
    private CodeViewFileHandle _cvFile;
    private RelocatableMethodBodyStreamEncoder _bodyEncoder;

    private BlobBuilder _ilStreamBuilder, _ilRelocBuilder;
    private BlobBuilder _dataStream, _dataRelocs;
    private BlobBuilder _rdataStream;
    private BlobBuilder _nepStream, _nepRelocs;
    private BlobBuilder _ilFixupStream, _ilFixupRelocs;
    private int _bssSize;
    private int _dataGlobalsStartOffset;

    private AssemblyReferenceHandle _mscorlibRef;
    private TypeDefinitionHandle _moduleTypeDef;

    // Lazy TypeRef handles (created on first use), keyed by type name (without namespace)
    private readonly Dictionary<string, TypeReferenceHandle> _lazyTypeRefs = new();
    // Lazy MemberRef handles (created on first use), keyed by "TypeName.MemberName"
    private readonly Dictionary<string, MemberReferenceHandle> _lazyMemberRefs = new();

    // Metadata row tracking
    private int _nextFieldRow = 1, _nextMethodRow = 1, _nextParamRow = 1;
    private int _nextTypeDefRow = 2; // starts at 2 since <Module> is row 1

    // Function/field registrations
    private readonly Dictionary<Obj, MethodDefinitionHandle> _methodDefs = new();
    private readonly Dictionary<Obj, FieldDefinitionHandle> _fieldDefs = new();
    private readonly Dictionary<string, MemberReferenceHandle> _externalFuncRefs = new();
    private readonly Dictionary<string, FieldDefinitionHandle> _globalFieldsByName = new();

    // Bare-name NEP COFF symbols (func name → COFF symbol for the NEP thunk alias)
    private readonly Dictionary<string, CoffSymbolHandle> _nepBareNameSymbols = new();

    // __unep@ fields for address-taken cdecl functions
    private readonly Dictionary<string, FieldDefinitionHandle> _unepFields = new();

    // __CxxPureMSILEntry state
    private Obj _mainObj;
    private MethodDefinitionHandle _cxxPureMsilEntry;
    private string _cxxPureMsilEntryMangledName;

    // Architecture helpers derived from DataModel
    private int PtrSize => _dm.PointerSize;
    private bool Is32 => _dm.PointerSize == 4;
    private string SymPrefix => Is32 ? "_" : "";
    private Machine TargetMachine => Is32 ? Machine.I386 : Machine.Amd64; // LP64: add ARM64
    private CodeViewMachine CvMachine => Is32 ? CodeViewMachine.I386 : CodeViewMachine.Amd64;

    // Mscorlib hashes
    private byte[] MscorlibHash => Is32
        ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
        : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
    private static readonly byte[] MscorlibPkt = { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 };

    public MsilObjectEmitter(
        CompilerOptions options,
        Tokenizer tokenizer,
        TypeSystem types,
        NameMangler nameMangler,
        ManagedAggregateModel aggregateModel)
    {
        _options = options;
        _tokenizer = tokenizer;
        _types = types;
        _dm = options.DataModel;
        _nameMangler = nameMangler;
        _aggregateModel = aggregateModel;
    }

    public StandaloneSignatureHandle AddStandaloneSignature(BlobBuilder blob)
        => _md.AddStandaloneSignature(_md.GetOrAddBlob(blob));

    internal TypeDefinitionHandle ReserveTypeDefinition() =>
        MetadataTokens.TypeDefinitionHandle(_nextTypeDefRow++);

    internal FieldDefinitionHandle ReserveFieldDefinition() =>
        MetadataTokens.FieldDefinitionHandle(_nextFieldRow++);

    internal void AddAggregateTypeDefinition(
        TypeDefinitionHandle predicted,
        TypeAttributes attributes,
        string name,
        FieldDefinitionHandle? fieldList,
        ushort packingSize,
        uint size)
    {
        var actual = _md.AddTypeDefinition(
            attributes,
            default,
            _md.GetOrAddString(name),
            GetValueTypeRef(),
            fieldList ?? MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        Verify(actual, predicted, "TypeDef", name);
        _md.AddTypeLayout(actual, packingSize, size);
        AddNativeCppClassAttribute(actual);
    }

    internal void AddAggregateFieldDefinition(
        FieldDefinitionHandle predicted,
        FieldAttributes attributes,
        string name,
        BlobBuilder signature,
        int? offset)
    {
        var actual = _md.AddFieldDefinition(
            attributes,
            _md.GetOrAddString(name),
            _md.GetOrAddBlob(signature));

        Verify(actual, predicted, "FieldDef", name);
        if (offset is int fieldOffset)
            _md.AddFieldLayout(actual, fieldOffset);
    }

    internal void AddNestedType(TypeDefinitionHandle nestedType, TypeDefinitionHandle enclosingType) =>
        _md.AddNestedType(nestedType, enclosingType);

    internal TypeReferenceHandle AddTypeReference(EntityHandle resolutionScope, string @namespace, string name) =>
        _md.AddTypeReference(
            resolutionScope,
            string.IsNullOrEmpty(@namespace) ? default : _md.GetOrAddString(@namespace),
            _md.GetOrAddString(name));

    private static void Verify<THandle>(THandle actual, THandle predicted, string rowKind, string name)
        where THandle : struct, IEquatable<THandle>
    {
        if (!actual.Equals(predicted))
            throw new InvalidOperationException(
                $"{rowKind} handle mismatch for '{name}': predicted {predicted}, got {actual}");
    }

    private TypeReferenceHandle GetLazyTypeRef(string @namespace, string name)
    {
        if (!_lazyTypeRefs.TryGetValue(name, out var handle))
        {
            handle = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString(@namespace),
                _md.GetOrAddString(name));
            _lazyTypeRefs[name] = handle;
        }
        return handle;
    }

    private TypeReferenceHandle GetCallConvCdeclRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "CallConvCdecl");
    private TypeReferenceHandle GetCallConvStdcallRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "CallConvStdcall");
    private TypeReferenceHandle GetIsSignUnspecifiedByteRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsSignUnspecifiedByte");
    private TypeReferenceHandle GetIsConstRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsConst");
    private TypeReferenceHandle GetIsVolatileRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsVolatile");
    private TypeReferenceHandle GetIsLongRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsLong");
    private TypeReferenceHandle GetNativeCppClassAttrRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "NativeCppClassAttribute");
    private TypeReferenceHandle GetValueTypeRef() => GetLazyTypeRef("System", "ValueType");
    public TypeReferenceHandle GetInterlockedRef() => GetLazyTypeRef("System.Threading", "Interlocked");

    public MemberReferenceHandle GetLazyMemberRef(string key, EntityHandle parent, string memberName, Func<BlobBuilder> buildSignature)
    {
        if (!_lazyMemberRefs.TryGetValue(key, out var handle))
        {
            handle = _md.AddMemberReference(parent, _md.GetOrAddString(memberName), _md.GetOrAddBlob(buildSignature()));
            _lazyMemberRefs[key] = handle;
        }
        return handle;
    }

    /// <summary>
    /// Encode a C type into an MSIL signature using the builder directly.
    /// Uses raw byte writes for modopt/modreq since the BlobEncoder API
    /// doesn't support all patterns we need.
    /// </summary>
    public void EncodeType(BlobBuilder sig, CType ty)
    {
        // Handle const/volatile on this type (for pointer-level qualifiers)
        if (ty.IsConst)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsConstRef()));
        }
        if (ty.IsVolatile)
        {
            sig.WriteByte((byte)SignatureTypeCode.RequiredModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsVolatileRef()));
        }

        switch (ty.Kind)
        {
            case TypeKind.Void:
                sig.WriteByte((byte)SignatureTypeCode.Void);
                break;
            case TypeKind.Bool:
                sig.WriteByte((byte)SignatureTypeCode.Boolean);
                break;
            case TypeKind.Char:
                if (ty.IsUnsigned)
                {
                    // unsigned char: uint8 (no modopt)
                    sig.WriteByte((byte)SignatureTypeCode.Byte);
                }
                else
                {
                    // plain char or signed char: modopt(IsSignUnspecifiedByte) int8
                    // Note: C distinguishes plain char from signed char, but both map
                    // to int8 with the modopt marker. The modopt is harmless for
                    // signed char and required for plain char.
                    sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
                    sig.WriteByte((byte)SignatureTypeCode.SByte);
                }
                break;
            case TypeKind.Short:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt16 : (byte)SignatureTypeCode.Int16);
                break;
            case TypeKind.Int:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Enum:
                // Enums are plain int32
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Long:
                // LLP64: long = 4 bytes with modopt(IsLong)
                // LP64: long = 8 bytes, would be int64
                if (_dm.LongSize == 4)
                {
                    sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsLongRef()));
                    sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                }
                else
                {
                    // LP64: long is 8 bytes = int64
                    sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt64 : (byte)SignatureTypeCode.Int64);
                }
                break;
            case TypeKind.LLong:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt64 : (byte)SignatureTypeCode.Int64);
                break;
            case TypeKind.Float:
                sig.WriteByte((byte)SignatureTypeCode.Single);
                break;
            case TypeKind.Double:
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.LDouble:
                // long double → modopt(IsLong) float64 (both LP64 and LLP64)
                sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsLongRef()));
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.Ptr:
                if (ty.Base.Kind == TypeKind.Func)
                {
                    // Pointer to function → FNPTR directly (no extra Ptr wrapper)
                    sig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                    EncodeFnPtrSignature(sig, ty.Base);
                }
                else
                {
                    sig.WriteByte((byte)SignatureTypeCode.Pointer);
                    EncodeType(sig, ty.Base);
                }
                break;
            case TypeKind.Array:
                if (ty.ArrayLen < 0)
                {
                    // Incomplete array → pointer to element
                    sig.WriteByte((byte)SignatureTypeCode.Pointer);
                    EncodeType(sig, ty.Base);
                }
                else
                {
                    // Fixed-size array → ValueType of array TypeDef
                    EntityHandle arrayTd = _aggregates.GetSignatureTypeHandle(ty);
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(arrayTd));
                }
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
                {
                    EntityHandle structHandle = _aggregates.GetSignatureTypeHandle(ty);
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(structHandle));
                    break;
                }
            case TypeKind.Func:
                {
                    // Function type used as a value (function pointer parameter) → FNPTR
                    sig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                    EncodeFnPtrSignature(sig, ty);
                    break;
                }
            case TypeKind.Vla:
                // VLA → pointer to base element
                sig.WriteByte((byte)SignatureTypeCode.Pointer);
                EncodeType(sig, ty.Base);
                break;
            default:
                throw new InvalidOperationException("Internal error");
        }
    }

    /// <summary>Encode an inline function pointer signature for FNPTR in method/local signatures.</summary>
    public void EncodeFnPtrSignature(BlobBuilder sig, CType funcTy)
    {
        EncodeFunctionSignature(sig, funcTy, funcTy.CallConv switch
        {
            CallConv.Clrcall => (byte)SignatureCallingConvention.Default,
            CallConv.Stdcall => (byte)SignatureCallingConvention.StdCall,
            _ => (byte)SignatureCallingConvention.CDecl,
        });
    }

    private void EncodeFunctionSignature(BlobBuilder sig, CType funcTy, byte callConv = 0)
    {
        sig.WriteByte(callConv);

        // Count parameters
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        sig.WriteCompressedInteger(paramCount);

        // Return type
        EncodeReturnType(sig, funcTy);

        // Parameters
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);
    }

    /// <summary>Encode the return type for a function, with modopt(CallConvCdecl) for unmanaged calling conventions.</summary>
    private void EncodeReturnType(BlobBuilder sig, CType funcTy)
    {
        if (funcTy.CallConv != CallConv.Clrcall)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(funcTy.CallConv switch
            {
                CallConv.Cdecl => GetCallConvCdeclRef(),
                CallConv.Stdcall => GetCallConvStdcallRef(),
                _ => throw new UnreachableException()
            }
            ));
        }

        EncodeType(sig, funcTy.ReturnTy);
    }

    private void RegisterMetadata(Obj prog, string objName)
    {
        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class, default, _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        for (Obj o = prog; o != null; o = o.Next)
        {
            if (o.IsFunction)
            {
                if (o.IsDefinition && o.IsLive)
                    RegisterFunction(o);
                if (o.IsLive && o.IsAddressTaken && o.Ty.CallConv != CallConv.Clrcall)
                    RegisterUnepField(o);
            }
            else
            {
                if (o.IsDefinition)
                    RegisterGlobalField(o);
            }
        }

        for (Obj o = prog; o != null; o = o.Next)
        {
            if (!o.IsFunction && !o.IsDefinition && o.IsLive)
                RegisterExternalField(o);
        }

        _md.AddModule(0, _md.GetOrAddString(objName), _md.GetOrAddGuid(Guid.NewGuid()), default, default);
    }

    public EntityHandle GetStructTypeHandle(CType ty)
        => _aggregates.GetTypeDefinitionHandle(ty);

    public ManagedAggregateRepresentationKind GetAggregateRepresentationKind(CType ty) =>
        _aggregateModel.GetRepresentationKind(ty);

    public ManagedAggregateMemberAccessKind GetMemberAccessKind(CType owner, Member member) =>
        _aggregateModel.GetMemberAccessKind(owner, member);

    public FieldDefinitionHandle GetAggregateFieldToken(CType owner, Member member) =>
        _aggregates.GetFieldToken(owner, member);

    private MethodDefinitionHandle RegisterFunction(Obj fn, string[] parameterNames = null)
    {
        CType funcTy = fn.Ty;
        bool isUnmanaged = funcTy.CallConv != CallConv.Clrcall;

        // Build method signature
        var sig = new BlobBuilder();
        EncodeFunctionSignature(sig, funcTy);

        // Method attributes
        MethodAttributes attrs = MethodAttributes.Assembly | MethodAttributes.Static;
        if (isUnmanaged && !fn.IsStatic)
            attrs |= (MethodAttributes)0x0008; // UnmanagedExport

        var methodDef = _md.AddMethodDefinition(
            attrs,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            _md.GetOrAddString(fn.Name),
            _md.GetOrAddBlob(sig),
            0,
            MetadataTokens.ParameterHandle(_nextParamRow));
        _nextMethodRow++;

        // Add parameter rows
        int paramIdx = 1;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            string paramName = parameterNames != null && paramIdx <= parameterNames.Length
                ? parameterNames[paramIdx - 1]
                : p.Name != null ? Util.GetTokenText(p.Name) : $"_a{paramIdx}";
            _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString(paramName), paramIdx);
            _nextParamRow++;
            paramIdx++;
        }

        _methodDefs[fn] = methodDef;

        // Pre-register COFF symbol
        string mangledName = _nameMangler.MangleFunctionName(fn);
        _symtab.PreRegisterFunctionClrToken(mangledName, methodDef);

        // If this is main, register __CxxPureMSILEntry
        if (fn.Name == "main")
        {
            _mainObj = fn;

            CType ty = TypeSystem.FuncType(TypeSystem.CopyType(_types.TyInt));
            ty.CallConv = CallConv.Clrcall;
            ty.Params = TypeSystem.CopyType(_types.TyInt);
            ty.Params.Next = _types.PointerTo(_types.PointerTo(_types.TyChar));
            ty.Params.Next.Next = _types.PointerTo(_types.PointerTo(_types.TyChar));

            var entryFn = new Obj { Name = "__CxxPureMSILEntry", Ty = ty };
            _cxxPureMsilEntry = RegisterFunction(entryFn, ["argc", "argv", "envp"]);
            _cxxPureMsilEntryMangledName = _nameMangler.MangleFunctionName(entryFn);
        }

        return methodDef;
    }

    public EntityHandle GetFunctionToken(Obj fn)
        => _methodDefs.TryGetValue(fn, out var methodDef) ? methodDef : GetExternalFunctionToken(fn);

    public EntityHandle GetFieldToken(Obj var)
        => _fieldDefs.TryGetValue(var, out var fieldDef) ? fieldDef : GetExternalFieldToken(var);

    public FieldDefinitionHandle GetOrReserveUnepFieldToken(Obj fn)
    {
        Debug.Assert(fn.Ty.CallConv != CallConv.Clrcall);
        return _unepFields[fn.Name];
    }

    private FieldDefinitionHandle RegisterUnepField(Obj fn)
    {
        string unepName = _nameMangler.MangleUnmanagedEntryPointName(fn);

        var unepFieldSig = new BlobBuilder();
        unepFieldSig.WriteByte(0x06); // FIELD
        unepFieldSig.WriteByte((byte)SignatureTypeCode.IntPtr);

        FieldDefinitionHandle unepField = _md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            _md.GetOrAddString(unepName), _md.GetOrAddBlob(unepFieldSig));
        _nextFieldRow++;
        _md.AddFieldRelativeVirtualAddress(unepField, 0);

        int slotOffset = _dataStream.Count;
        for (int i = 0; i < PtrSize; i++) _dataStream.WriteByte(0);

        _unepFields[fn.Name] = unepField;
        _unepSlotOffsets[fn.Name] = slotOffset;
        _symtab.AddDataClrToken(unepName, unepField, LogicalSection.Data, slotOffset, out _);

        return unepField;
    }

    private MemberReferenceHandle GetExternalFunctionToken(Obj fn)
    {
        if (_externalFuncRefs.TryGetValue(fn.Name, out MemberReferenceHandle memberRef))
            return memberRef;

        CType funcTy = fn.Ty;

        // Build MemberRef signature
        var sig = new BlobBuilder();
        EncodeFunctionSignature(sig, funcTy);

        memberRef = _md.AddMemberReference(
            _moduleTypeDef, _md.GetOrAddString(fn.Name), _md.GetOrAddBlob(sig));
        _externalFuncRefs[fn.Name] = memberRef;

        // Add DecoratedNameAttribute
        string mangledName = _nameMangler.MangleFunctionName(fn);
        AddDecoratedNameAttribute(memberRef, mangledName);

        // Register external CLR token
        _symtab.AddExternalClrToken(mangledName, memberRef);

        return memberRef;
    }

    private void AddDecoratedNameAttribute(EntityHandle target, string mangledName)
    {
        // DecoratedNameAttribute custom attribute
        // We need a MemberRef to the constructor: .ctor(string)
        // For now, use raw blob encoding
        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteSerializedString(mangledName);
        attrBlob.WriteUInt16(0x0000); // NumNamed

        // TypeRef for DecoratedNameAttribute
        var decoratedNameRef = GetLazyTypeRef("System.Runtime.CompilerServices", "DecoratedNameAttribute");

        // MemberRef for .ctor(string)
        var ctorRef = GetLazyMemberRef("DecoratedNameAttribute..ctor", decoratedNameRef, ".ctor", () =>
        {
            var ctorSig = new BlobBuilder();
            ctorSig.WriteByte(0x20); // HASTHIS
            ctorSig.WriteCompressedInteger(1); // 1 param
            ctorSig.WriteByte((byte)SignatureTypeCode.Void); // return void
            ctorSig.WriteByte((byte)SignatureTypeCode.String); // param: string
            return ctorSig;
        });

        _md.AddCustomAttribute(target, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    private void RegisterGlobalField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        string fieldName;
        if (g.StaticLocalFn != null)
        {
            fieldName = _nameMangler.MangleStaticLocalName(g);
        }
        else if (g.IsAnonymous)
        {
            fieldName = _nameMangler.GenerateAnonymousGlobalName();
        }
        else if (g.IsStatic)
        {
            fieldName = _nameMangler.MangleStaticGlobalName(g.Name);
        }
        else
        {
            fieldName = g.Name;
        }

        FieldAttributes fieldAttrs = FieldAttributes.Assembly | FieldAttributes.Static;

        // All global definitions get HasFieldRVA — even tentative (common) definitions
        // and zero-initialized globals. The COFF symbol table determines whether
        // the symbol is section-bound (.data/.bss) or common (Sect=0, Value=size).
        fieldAttrs |= FieldAttributes.HasFieldRVA;

        var fieldDef = _md.AddFieldDefinition(fieldAttrs,
            _md.GetOrAddString(fieldName), _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        _md.AddFieldRelativeVirtualAddress(fieldDef, 0);

        _fieldDefs[g] = fieldDef;
        _globalFieldsByName[g.Name] = fieldDef;
    }

    private FieldDefinitionHandle GetExternalFieldToken(Obj g)
    {
        if (_globalFieldsByName.TryGetValue(g.Name, out FieldDefinitionHandle fieldDef))
        {
            _fieldDefs[g] = fieldDef;
            return fieldDef;
        }

        return _fieldDefs[g];
    }

    private FieldDefinitionHandle RegisterExternalField(Obj g)
    {
        if (_globalFieldsByName.TryGetValue(g.Name, out FieldDefinitionHandle fieldDef))
        {
            _fieldDefs[g] = fieldDef;
            return fieldDef;
        }

        Debug.Assert(!g.IsFunction && !g.IsDefinition);

        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        FieldAttributes attrs = FieldAttributes.Assembly | FieldAttributes.Static;

        fieldDef = _md.AddFieldDefinition(attrs,
            _md.GetOrAddString(g.Name), _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        _fieldDefs[g] = fieldDef;
        _globalFieldsByName[g.Name] = fieldDef;
        return fieldDef;
    }

    private void AddNativeCppClassAttribute(TypeDefinitionHandle handle)
    {
        var attrRef = GetNativeCppClassAttrRef();

        // MemberRef for .ctor()
        var ctorRef = GetLazyMemberRef("NativeCppClassAttribute..ctor", attrRef, ".ctor", () =>
        {
            var ctorSig = new BlobBuilder();
            ctorSig.WriteByte(0x20); // HASTHIS
            ctorSig.WriteCompressedInteger(0);
            ctorSig.WriteByte((byte)SignatureTypeCode.Void);
            return ctorSig;
        });

        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteUInt16(0x0000); // NumNamed

        _md.AddCustomAttribute(handle, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    private void EmitFunctions(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            CompiledMethod body = CodeGen.EmitFunction(_types, this, fn, _cvFile);

            // Finalize method body
            var methodDef = _methodDefs[fn];
            string mangledName = _nameMangler.MangleFunctionName(fn);

            _bodyEncoder.AddMethodBody(methodDef, mangledName, body.Instructions,
                body.MaxStack, body.LocalVariables, attributes: MethodBodyAttributes.InitLocals,
                debugName: fn.Name,
                localSlots: body.LocalDebugInfo);
        }
    }

    private void EmitCxxPureMSILEntry()
    {
        if (_mainObj == null) return;

        var enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

        // Count main's parameters
        int mainParamCount = 0;
        for (CType p = _mainObj.Ty.Params; p != null; p = p.Next)
            mainParamCount++;

        // Load argc, argv, and envp up to what main declares
        if (mainParamCount >= 1)
        {
            enc.OpCode(ILOpCode.Ldarg_0); // argc
        }
        if (mainParamCount >= 2)
        {
            enc.OpCode(ILOpCode.Ldarg_1); // argv
        }
        if (mainParamCount >= 3)
        {
            enc.OpCode(ILOpCode.Ldarg_2); // envp
        }

        enc.Call(_methodDefs[_mainObj]);

        // If main returns void, push 0
        if (_mainObj.Ty.ReturnTy.Kind == TypeKind.Void)
            enc.OpCode(ILOpCode.Ldc_i4_0);

        enc.OpCode(ILOpCode.Ret);

        _bodyEncoder.AddMethodBody(_cxxPureMsilEntry, _cxxPureMsilEntryMangledName, enc,
            maxStack: Math.Max(mainParamCount, 1), localVariablesSignature: default, attributes: MethodBodyAttributes.InitLocals,
            debugName: "__CxxPureMSILEntry");
    }

    private void EmitNepMachinery(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            var methodDef = _methodDefs[fn];
            string mangledName = _nameMangler.MangleFunctionName(fn);
            string bareName = _nameMangler.MangleFunctionBaseName(fn);

            var bareSym = EmitNepForMethod(
                MetadataTokens.GetToken(methodDef), bareName, mangledName);

            // Also store under original name for __unep@ relocation lookup
            if (fn.IsStatic && !_nepBareNameSymbols.ContainsKey(fn.Name))
                _nepBareNameSymbols[fn.Name] = bareSym;

            if (fn.Ty.CallConv != CallConv.Clrcall && _unepFields.ContainsKey(fn.Name))
            {
                EmitUnepSlot(fn, bareSym);
            }
        }

        // Emit ADDR relocs for extern __unep@ fields (not defined in this TU)
        foreach (var (funcName, _) in _unepFields)
        {
            if (_nepBareNameSymbols.ContainsKey(funcName)) continue; // already handled by local NEP
            if (!_unepSlotOffsets.TryGetValue(funcName, out int slotOffset)) continue;

            // Create an undefined external bare-name symbol — linker resolves from defining TU
            var externBareSym = _symtab.AddUndefinedExternalSymbol(SymPrefix + funcName, CoffSymbolType.Null);
            new CoffRelocationEncoder(_coffHeader, _dataRelocs)
                .AddAddressRelocation(slotOffset, externBareSym);
        }
    }

    /// <summary>
    /// Emit NEP machinery for a single method: __mep@ slot, thunk, bare-name alias, ilfixup.
    /// </summary>
    private CoffSymbolHandle EmitNepForMethod(int methodToken, string bareName, string mangledSuffix)
    {
        var bareSym = ClrIjw.EmitNepMachinery(
            TargetMachine, Is32, PtrSize, SymPrefix,
            _coffHeader, _symtab,
            _dataStream, _dataRelocs,
            _nepStream, _nepRelocs,
            _ilFixupStream, _ilFixupRelocs,
            methodToken, bareName, mangledSuffix);
        _nepBareNameSymbols[bareName] = bareSym;
        return bareSym;
    }

    private void EmitUnepSlot(Obj fn, CoffSymbolHandle bareSym)
    {
        if (!_unepSlotOffsets.TryGetValue(fn.Name, out int slotOffset)) return;

        // ADDR relocation to the bare-name NEP thunk symbol
        new CoffRelocationEncoder(_coffHeader, _dataRelocs)
            .AddAddressRelocation(slotOffset, bareSym);
    }

    /// <summary>Maps __unep@ field name → pre-allocated offset in .data for the slot.</summary>
    private readonly Dictionary<string, int> _unepSlotOffsets = new();

    /// <summary>Maps global Obj name → COFF data symbol handle for relocation targeting.</summary>
    private readonly Dictionary<string, CoffSymbolHandle> _dataCoffSymbols = new();

    /// <summary>Write data bytes and register COFF data token symbols.
    /// Must run before IL emission so token ordering is correct.</summary>
    private void EmitGlobalDataBytesAndTokens(Obj prog)
    {
        _dataGlobalsStartOffset = _dataStream.Count;

        // Register all global data symbols
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.TryGetValue(g, out var fieldDef)) continue;

            if (g.InitData != null)
            {
                bool isReadOnly = IsReadOnlyData(g);
                var stream = isReadOnly ? _rdataStream : _dataStream;
                var section = isReadOnly ? LogicalSection.RData : LogicalSection.Data;

                // Pad to required alignment
                int aligned = Util.AlignTo(stream.Count, g.Align);
                while (stream.Count < aligned) stream.WriteByte(0);

                int offset = stream.Count;

                // Copy InitData, writing addends at relocation offsets
                byte[] data = (byte[])g.InitData.Clone();
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    if (rel.Addend != 0)
                        Util.WriteBuf(data, rel.Offset, rel.Addend, PtrSize);
                }
                stream.WriteBytes(data);

                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, section, offset, out _,
                    isExternal: !g.IsStatic && !g.IsLocal);
                _dataCoffSymbols[g.Name] = coffSym;
            }
            else if (g.IsTentative)
            {
                if (g.IsStatic)
                {
                    // Static tentative → BSS with Static storage class (internal linkage)
                    int bssOffset = _bssSize;
                    _bssSize = Util.AlignTo(_bssSize + g.Ty.Size, g.Align);
                    var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Bss, bssOffset, out _,
                        isExternal: false);
                    _dataCoffSymbols[g.Name] = coffSym;
                }
                else
                {
                    // External tentative → common symbol (linker allocates)
                    var coffSym = _symtab.AddCommonDataClrToken(g.Name, fieldDef, g.Ty.Size, out _);
                    _dataCoffSymbols[g.Name] = coffSym;
                }
            }
            else
            {
                int bssOffset = _bssSize;
                _bssSize = Util.AlignTo(_bssSize + g.Ty.Size, g.Align);
                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Bss, bssOffset, out _,
                    isExternal: !g.IsStatic && !g.IsLocal);
                _dataCoffSymbols[g.Name] = coffSym;
            }
        }

    }

    private static bool IsReadOnlyData(Obj g) => g.IsStringLiteral;

    /// <summary>Write data relocations. Runs after NEP emission so
    /// bare-name symbols are available as relocation targets.</summary>
    private void EmitGlobalDataRelocations(Obj prog)
    {
        // Track cumulative offset through .data to match what we wrote before.
        // Read-only data (string literals) went to .rdata and must be skipped.
        int dataOffset = _dataGlobalsStartOffset;
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.ContainsKey(g)) continue;
            if (g.InitData == null) continue;
            if (IsReadOnlyData(g)) continue;

            int offset = Util.AlignTo(dataOffset, g.Align);
            dataOffset = offset + g.InitData.Length;

            for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
            {
                string targetName = rel.Label;
                CoffSymbolHandle targetSym;

                if (_dataCoffSymbols.TryGetValue(targetName, out targetSym))
                {
                    // Data-to-data relocation (e.g., char* e = &hello[1])
                }
                else if (_nepBareNameSymbols.TryGetValue(targetName, out targetSym))
                {
                    // Function pointer relocation (e.g., int (*m)() = &get)
                }
                else
                {
                    // Unknown target — create as undefined external
                    targetSym = _symtab.AddUndefinedExternalSymbol(
                        SymPrefix + targetName, CoffSymbolType.Null);
                }

                new CoffRelocationEncoder(_coffHeader, _dataRelocs)
                    .AddAddressRelocation(offset + rel.Offset, targetSym);
            }
        }
    }

    public byte[] Generate(Obj prog, string objName, string sourceFile)
    {
        _md = new MetadataBuilder();
        _coffHeader = new CoffHeaderBuilder(TargetMachine, 0);
        _symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        _ilStreamBuilder = new BlobBuilder();
        _ilRelocBuilder = new BlobBuilder();
        _dataStream = new BlobBuilder();
        _dataRelocs = new BlobBuilder();
        _rdataStream = new BlobBuilder();
        _nepStream = new BlobBuilder();
        _nepRelocs = new BlobBuilder();
        _ilFixupStream = new BlobBuilder();
        _ilFixupRelocs = new BlobBuilder();
        _bssSize = 0;

        // AssemblyRef: mscorlib
        _mscorlibRef = _md.AddAssemblyReference(
            _md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            _md.GetOrAddBlob(MscorlibPkt),
            default,
            _md.GetOrAddBlob(MscorlibHash));

        _aggregates = new ManagedAggregateRegistry(
            _types,
            _nameMangler,
            _aggregateModel,
            this);

        // CodeView debug info
        _codeviewSymbols = new CodeViewSymbolBuilder(_coffHeader);
        _codeviewSymbols.AddObjNameAndCompile3(objName,
            language: CodeViewLanguage.C,
            machine: CvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "chibil C compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        // Source file registration
        if (File.Exists(sourceFile))
        {
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
            _cvFile = _codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);
        }
        else
        {
            _cvFile = _codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.None, Array.Empty<byte>());
        }

        _bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            _ilStreamBuilder, _ilRelocBuilder, _symtab, _coffHeader, _codeviewSymbols);

        // Metadata
        RegisterMetadata(prog, objName);

        // Global data bytes + COFF token registration — BEFORE IL emission
        EmitGlobalDataBytesAndTokens(prog);

        // IL Emission
        EmitFunctions(prog);

        EmitCxxPureMSILEntry();

        _aggregates.MaterializeAll();

        // NEP machinery (creates bare-name symbols for functions)
        EmitNepMachinery(prog);

        // Global data relocations — AFTER NEP so bare-name symbols exist
        EmitGlobalDataRelocations(prog);

        // Build COFF and serialize
        var coffBuilder = new ManagedCoffBuilder(_coffHeader, new MetadataRootBuilder(_md), _symtab, _codeviewSymbols,
            _ilStreamBuilder, _ilRelocBuilder,
            dataStream: _dataStream, dataRelocs: _dataRelocs,
            rdataStream: _rdataStream,
            ilFixupStream: _ilFixupStream, ilFixupRelocs: _ilFixupRelocs,
            nepStream: _nepStream, nepRelocs: _nepRelocs,
            bssSize: _bssSize);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
