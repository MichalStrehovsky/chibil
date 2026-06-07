using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

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

    private MetadataBuilder _md;
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

    private AssemblyReferenceHandle _mscorlibRef;
    private TypeDefinitionHandle _moduleTypeDef;

    // Lazy TypeRef handles (created on first use), keyed by type name (without namespace)
    private readonly Dictionary<string, TypeReferenceHandle> _lazyTypeRefs = new();
    // Lazy MemberRef handles (created on first use), keyed by "TypeName.MemberName"
    private readonly Dictionary<string, MemberReferenceHandle> _lazyMemberRefs = new();

    // Metadata row tracking
    private int _nextFieldRow = 1, _nextMethodRow = 1, _nextParamRow = 1;

    // Function/field registrations
    private readonly Dictionary<Obj, MethodDefinitionHandle> _methodDefs = new();
    private readonly Dictionary<Obj, FieldDefinitionHandle> _fieldDefs = new();
    private readonly Dictionary<string, MemberReferenceHandle> _externalFuncRefs = new();
    private readonly Dictionary<int, TypeDefinitionHandle> _structTypeDefs = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _arrayTypeDefs = new();
    private readonly Dictionary<string, TypeReferenceHandle> _forwardDeclTypeRefs = new();
    private readonly List<(int typeId, CType type, string name)> _pendingTypeDefs = new();
    private readonly Dictionary<string, FieldDefinitionHandle> _globalFieldsByName = new();

    // Tracks which functions have their address taken (need __unep@ slot)
    private readonly HashSet<string> _addressTakenFuncs = new();

    // Bare-name NEP COFF symbols (func name → COFF symbol for the NEP thunk alias)
    private readonly Dictionary<string, CoffSymbolHandle> _nepBareNameSymbols = new();

    // Anonymous global counter and TU hash
    private int _anonGlobalCounter;
    private string _tuHash;

    // __unep@ fields for address-taken cdecl functions
    private readonly Dictionary<string, FieldDefinitionHandle> _unepFields = new();

    // __CxxPureMSILEntry state
    private Obj _mainObj;
    private MethodDefinitionHandle _cxxPureMsilEntry;

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

    public MsilObjectEmitter(CompilerOptions options, Tokenizer tokenizer, TypeSystem types)
    {
        _options = options;
        _tokenizer = tokenizer;
        _types = types;
        _dm = options.DataModel;
    }

    public StandaloneSignatureHandle AddStandaloneSignature(BlobBuilder blob)
        => _md.AddStandaloneSignature(_md.GetOrAddBlob(blob));

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
                    string arrayName = NameMangler.MangleArrayTypeName(_types, ty);
                    TypeDefinitionHandle arrayTd = _arrayTypeDefs[arrayName];
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(arrayTd));
                }
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
                {
                    CType canonical = ty;
                    while (canonical.Origin != null) canonical = canonical.Origin;
                    if (canonical.IsNestedMember)
                        throw new InvalidOperationException(
                            $"Internal error: nested member type '{_types.GetStructName(canonical)}' reached signature encoding");
                    EntityHandle structHandle = GetStructTypeHandle(ty);
                    if (structHandle.IsNil)
                    {
                        // Forward-declared struct in a signature.
                        string name = _types.GetStructName(ty);
                        if (!_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
                        {
                            typeRef = _md.AddTypeReference(default, default, _md.GetOrAddString(name));
                            _forwardDeclTypeRefs[name] = typeRef;
                        }
                        structHandle = typeRef;
                    }

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
        PreAllocateStructTypeDefs(prog);

        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class, default, _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        RegisterFunctions(prog);
        RegisterUnepFields(prog);
        RegisterGlobalFields(prog);
        MaterializeStructTypeDefs();

        // Module row
        _md.AddModule(0, _md.GetOrAddString(objName), _md.GetOrAddGuid(Guid.NewGuid()), default, default);
    }

    // We predict handles based on row position. <Module> is TypeDef row 1.
    // All struct/array TypeDefs will be rows 2, 3, 4, ... in the order they're discovered.
    private int _nextStructTypeDefRow = 2; // starts at 2 since <Module> is row 1

    private void PreAllocateStructTypeDefs(Obj prog)
    {
        var visited = new HashSet<Node>();
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            PreAllocateFromType(fn.Ty);
            if (fn.IsFunction && fn.IsDefinition && fn.IsLive)
            {
                for (Obj local = fn.Locals; local != null; local = local.Next)
                    PreAllocateFromType(local.Ty);
                for (Obj param = fn.Params; param != null; param = param.Next)
                    PreAllocateFromType(param.Ty);
                if (fn.Body != null)
                    PreAllocateFromNode(fn.Body, visited);
            }
        }
    }

    private void PreAllocateFromType(CType ty)
    {
        if (ty == null) return;
        CType canonical = ty;
        while (canonical.Origin != null) canonical = canonical.Origin;

        switch (canonical.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                if (canonical.Members != null) // Only complete types
                {
                    // Skip nested member types — they're flattened into the parent
                    if (canonical.IsNestedMember) break;
                    int id = _types.GetTypeId(canonical);
                    if (!_structTypeDefs.ContainsKey(id))
                    {
                        // Reserve a predicted handle
                        var predictedHandle = MetadataTokens.TypeDefinitionHandle(_nextStructTypeDefRow++);
                        _structTypeDefs[id] = predictedHandle;
                        string name = _types.GetStructName(canonical);
                        _pendingTypeDefs.Add((id, canonical, name));

                        // Do NOT recurse into member types — nested structs/unions are
                        // flattened into the parent as opaque byte ranges, matching MSVC
                        // /clr /BC behavior. TypeDefs are only created for types that
                        // appear directly in function signatures, local/global variable
                        // types, and pointer targets.
                    }
                }
                break;
            case TypeKind.Array:
                if (canonical.ArrayLen >= 0)
                {
                    string arrayName = NameMangler.MangleArrayTypeName(_types, canonical);
                    if (!_arrayTypeDefs.ContainsKey(arrayName))
                    {
                        var predictedHandle = MetadataTokens.TypeDefinitionHandle(_nextStructTypeDefRow++);
                        _arrayTypeDefs[arrayName] = predictedHandle;
                        _pendingTypeDefs.Add((0, canonical, arrayName));
                    }
                    PreAllocateFromType(canonical.Base);
                }
                break;
            case TypeKind.Ptr:
                PreAllocateFromType(canonical.Base);
                break;
            case TypeKind.Func:
                PreAllocateFromType(canonical.ReturnTy);
                for (CType p = canonical.Params; p != null; p = p.Next)
                    PreAllocateFromType(p);
                break;
        }
    }

    private void PreAllocateFromNode(Node node, HashSet<Node> visited)
    {
        if (node == null || !visited.Add(node)) return;
        if (node.Ty != null)
        {
            // Don't create TypeDefs for struct/union types that appear only as
            // member-access intermediaries. MSVC flattens nested struct members
            // into the parent — no TypeDef for `struct Inner` in `o.inner.a`.
            // The type will still get a TypeDef if it's used independently in a
            // function signature, local variable, or global variable.
            bool isMemberAccess = node.Kind == NodeKind.Member &&
                (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union);
            if (!isMemberAccess)
                PreAllocateFromType(node.Ty);
        }
        if (node.FuncTy != null) PreAllocateFromType(node.FuncTy);
        PreAllocateFromNode(node.Lhs, visited);
        PreAllocateFromNode(node.Rhs, visited);
        PreAllocateFromNode(node.Cond, visited);
        PreAllocateFromNode(node.Then, visited);
        PreAllocateFromNode(node.Els, visited);
        PreAllocateFromNode(node.Init, visited);
        PreAllocateFromNode(node.Inc, visited);
        PreAllocateFromNode(node.Body, visited);
        PreAllocateFromNode(node.Next, visited);
        for (Node arg = node.Args; arg != null; arg = arg.Next)
            PreAllocateFromNode(arg, visited);
        PreAllocateFromNode(node.CasAddr, visited);
        PreAllocateFromNode(node.CasOld, visited);
        PreAllocateFromNode(node.CasNew, visited);
        PreAllocateFromNode(node.AtomicExpr, visited);
    }

    private void RegisterFunctions(Obj prog)
    {
        // Pass A: defined functions → MethodDef
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            RegisterFunction(fn);
        }

        // Pass B: External function MemberRefs are created on-demand during IL emission
        // (GenFunCall calls RegisterExternalFunction when it encounters a call to an
        // undefined function). MSVC only emits MemberRefs for functions that actually
        // appear in IL — declared-but-never-called functions don't get MemberRefs.
    }

    private void RegisterFunction(Obj fn)
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
            string paramName = p.Name != null ? Util.GetTokenText(p.Name) : $"_a{paramIdx}";
            _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString(paramName), paramIdx);
            _nextParamRow++;
            paramIdx++;
        }

        _methodDefs[fn] = methodDef;

        // Pre-register COFF symbol
        string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);
        _symtab.PreRegisterFunctionClrToken(mangledName, methodDef);

        // If this is main, register __CxxPureMSILEntry
        if (fn.Name == "main")
        {
            _mainObj = fn;
            RegisterCxxPureMSILEntry(fn);
        }
    }

    public EntityHandle GetFunctionToken(Obj fn)
        => _methodDefs.TryGetValue(fn, out var methodDef) ? methodDef : GetExternalFunctionToken(fn);

    public EntityHandle GetFieldToken(Obj var)
        => _fieldDefs.TryGetValue(var, out var fieldDef) ? fieldDef : _globalFieldsByName[var.Name];

    public FieldDefinitionHandle GetUnepFieldToken(Obj fn)
        => _unepFields[fn.Name];

    private void RegisterCxxPureMSILEntry(Obj mainFn)
    {
        // Signature: int __clrcall(int argc, char** argv, char** envp)
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT calling convention
        sig.WriteCompressedInteger(3); // 3 params

        // Return type: int32 (no CallConvCdecl modopt — this is __clrcall)
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        // Param 1: int argc
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        // Param 2: char** argv — Ptr Ptr modopt(IsSignUnspecifiedByte) SByte.
        // The IsSignUnspecifiedByte modopt marks plain `char` whose signedness
        // is implementation-defined; MSVC and asm2obj both emit it on `char**`
        // params and link.exe compares signature bytes including this marker.
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
        sig.WriteByte((byte)SignatureTypeCode.SByte);

        // Param 3: char** envp — same encoding, '0' backreference in mangling.
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
        sig.WriteByte((byte)SignatureTypeCode.SByte);

        _cxxPureMsilEntry = _md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            _md.GetOrAddString("__CxxPureMSILEntry"),
            _md.GetOrAddBlob(sig),
            0,
            MetadataTokens.ParameterHandle(_nextParamRow));
        _nextMethodRow++;

        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("argc"), 1);
        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("argv"), 2);
        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("envp"), 3);
        _nextParamRow += 3;

        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _symtab.PreRegisterFunctionClrToken(mangledName, _cxxPureMsilEntry);
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
        string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);
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

    private void RegisterUnepFields(Obj prog)
    {
        foreach (string funcName in _addressTakenFuncs)
        {
            // Find the function (defined or extern)
            Obj fn = null;
            for (Obj f = prog; f != null; f = f.Next)
            {
                if (f.IsFunction && f.Name == funcName && f.Ty.CallConv != CallConv.Clrcall)
                {
                    fn = f; break;
                }
            }
            if (fn == null) continue;

            string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);
            string unepName = $"__unep@{mangledName}";

            var unepFieldSig = new BlobBuilder();
            unepFieldSig.WriteByte(0x06); // FIELD
            unepFieldSig.WriteByte((byte)SignatureTypeCode.IntPtr);

            var unepField = _md.AddFieldDefinition(
                FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                _md.GetOrAddString(unepName), _md.GetOrAddBlob(unepFieldSig));
            _nextFieldRow++;
            _md.AddFieldRelativeVirtualAddress(unepField, 0);

            _unepFields[funcName] = unepField;
        }
    }

    private void RegisterGlobalFields(Obj prog)
    {
        // Definitions
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction || !g.IsDefinition) continue;
            RegisterGlobalField(g);
        }

        // Externs (not yet registered)
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction || g.IsDefinition) continue;
            if (_fieldDefs.ContainsKey(g) || _globalFieldsByName.ContainsKey(g.Name)) continue;
            RegisterExternField(g);
        }
    }

    private void RegisterGlobalField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        string fieldName;
        if (g.StaticLocalFn != null)
        {
            fieldName = NameMangler.MangleStaticLocalName(_tuHash, g);
        }
        else if (g.IsAnonymous)
        {
            fieldName = $"?A0x{_tuHash}.unnamed-global-{_anonGlobalCounter++}";
        }
        else if (g.IsStatic)
        {
            fieldName = NameMangler.MangleStaticGlobalName(_tuHash, g.Name);
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

    private void RegisterExternField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        FieldAttributes attrs = FieldAttributes.Assembly | FieldAttributes.Static;

        var fieldDef = _md.AddFieldDefinition(attrs,
            _md.GetOrAddString(g.Name), _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        _fieldDefs[g] = fieldDef;
        _globalFieldsByName[g.Name] = fieldDef;
    }

    private void MaterializeStructTypeDefs()
    {
        foreach (var (typeId, type, name) in _pendingTypeDefs)
        {
            TypeDefinitionHandle handle;

            if (type.Kind == TypeKind.Array)
            {
                var predicted = _arrayTypeDefs[name];
                handle = _md.AddTypeDefinition(
                    TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
                    default, _md.GetOrAddString(name),
                    GetValueTypeRef(),
                    MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                    MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

                Debug.Assert(handle == predicted, $"Array TypeDef handle mismatch: predicted {predicted}, got {handle}");
                _md.AddTypeLayout(handle, 0, (uint)type.Size);
            }
            else
            {
                var predicted = _structTypeDefs[typeId];
                // Unions use ExplicitLayout (all members at offset 0);
                // structs use SequentialLayout
                var layoutAttr = type.Kind == TypeKind.Union
                    ? TypeAttributes.ExplicitLayout
                    : TypeAttributes.SequentialLayout;
                handle = _md.AddTypeDefinition(
                    layoutAttr | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
                    default, _md.GetOrAddString(name),
                    GetValueTypeRef(),
                    MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                    MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

                Debug.Assert(handle == predicted, $"Struct TypeDef handle mismatch: predicted {predicted}, got {handle}");
                _md.AddTypeLayout(handle, 0, (uint)type.Size);
            }

            // NativeCppClassAttribute
            AddNativeCppClassAttribute(handle);

            // <alignment member> field (on 64-bit targets, structs/unions only — not arrays)
            if (!Is32 && type.Kind != TypeKind.Array)
            {
                var alignFieldSig = new BlobBuilder();
                alignFieldSig.WriteByte(0x06); // FIELD
                // Use int64 if any member needs 8-byte alignment, else int32
                bool needs8 = type.Align >= 8;
                alignFieldSig.WriteByte(needs8 ? (byte)SignatureTypeCode.Int64 : (byte)SignatureTypeCode.Int32);

                var alignField = _md.AddFieldDefinition(
                    FieldAttributes.Private,
                    _md.GetOrAddString("<alignment member>"),
                    _md.GetOrAddBlob(alignFieldSig));
                _nextFieldRow++;

                // For ExplicitLayout (unions), set field offset to 0
                // (MSVC /clr C++ uses offset 0; /clr /BC incorrectly uses 0xFFFFFFFF)
                if (type.Kind == TypeKind.Union)
                    _md.AddFieldLayout(alignField, 0);
            }
        }
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
            string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);

            _bodyEncoder.AddMethodBody(methodDef, mangledName, body.Instructions,
                body.MaxStack, body.LocalVariables, attributes: MethodBodyAttributes.InitLocals,
                debugName: fn.Name,
                localSlots: body.LocalDebugInfo);
        }
    }

    public EntityHandle GetStructTypeHandle(CType ty)
    {
        int typeId = _types.GetTypeId(ty);
        if (_structTypeDefs.TryGetValue(typeId, out var handle))
            return handle;
        return default;
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

        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _bodyEncoder.AddMethodBody(_cxxPureMsilEntry, mangledName, enc,
            maxStack: Math.Max(mainParamCount, 1), localVariablesSignature: default, attributes: MethodBodyAttributes.InitLocals,
            debugName: "__CxxPureMSILEntry");
    }

    private void EmitNepMachinery(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            var methodDef = _methodDefs[fn];
            string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);

            // Static functions use TU-hash-scoped bare names to avoid cross-TU collisions
            string bareName = fn.IsStatic ? $"{fn.Name}_?A0x{_tuHash}" : fn.Name;

            var bareSym = EmitNepForMethod(
                MetadataTokens.GetToken(methodDef), bareName, mangledName);

            // Also store under original name for __unep@ relocation lookup
            if (fn.IsStatic && !_nepBareNameSymbols.ContainsKey(fn.Name))
                _nepBareNameSymbols[fn.Name] = bareSym;

            if (fn.Ty.CallConv != CallConv.Clrcall && _addressTakenFuncs.Contains(fn.Name))
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
            var externBareSym = _symtab.AddUndefinedExternalSymbol(SymPrefix + funcName);
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

        // Pre-allocate __unep@ data slots
        foreach (var (funcName, unepField) in _unepFields)
        {
            Obj fn = null;
            for (Obj f = prog; f != null; f = f.Next)
                if (f.IsFunction && f.Name == funcName) { fn = f; break; }
            if (fn == null) continue;

            string mangledName = NameMangler.MangleFunctionName(_types, _tuHash, fn);
            string unepName = $"__unep@{mangledName}";

            int slotOffset = _dataStream.Count;
            for (int i = 0; i < PtrSize; i++) _dataStream.WriteByte(0);
            _unepSlotOffsets[funcName] = slotOffset;

            _symtab.AddDataClrToken(unepName, unepField, LogicalSection.Data, slotOffset, out _);
        }
    }

    private static bool IsReadOnlyData(Obj g) => g.IsStringLiteral;

    /// <summary>Write data relocations. Runs after NEP emission so
    /// bare-name symbols are available as relocation targets.</summary>
    private void EmitGlobalDataRelocations(Obj prog)
    {
        // Track cumulative offset through .data to match what we wrote before.
        // Read-only data (string literals) went to .rdata and must be skipped.
        int dataOffset = 0;
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
                    targetSym = _symtab.AddExternalDataSymbol(
                        SymPrefix + targetName, LogicalSection.Data, 0);
                }

                new CoffRelocationEncoder(_coffHeader, _dataRelocs)
                    .AddAddressRelocation(offset + rel.Offset, targetSym);
            }
        }
    }

    private void ScanAddressTaken(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            ScanAddressTakenNode(fn.Body);
        }

        // Also check global initializers that reference functions
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
            {
                string label = rel.Label;
                _addressTakenFuncs.Add(label);
            }
        }
    }

    private void ScanAddressTakenNode(Node node)
    {
        if (node == null) return;
        // Explicit address-of: &func
        if (node.Kind == NodeKind.Addr && node.Lhs?.Kind == NodeKind.Var &&
            node.Lhs.Var.IsFunction)
        {
            _addressTakenFuncs.Add(node.Lhs.Var.Name);
        }
        // Implicit function-to-pointer: using function name as a value
        // (e.g., `fp = add;` without `&`)
        if (node.Kind == NodeKind.Var && node.Var != null && node.Var.IsFunction &&
            node.Var.Ty.CallConv != CallConv.Clrcall)
        {
            _addressTakenFuncs.Add(node.Var.Name);
        }
        // Function passed as argument to another function (e.g., `apply(add, 1, 2)`)
        if (node.Kind == NodeKind.FunCall)
        {
            for (Node arg = node.Args; arg != null; arg = arg.Next)
            {
                if (arg.Kind == NodeKind.Var && arg.Var != null && arg.Var.IsFunction &&
                    arg.Var.Ty.CallConv != CallConv.Clrcall)
                    _addressTakenFuncs.Add(arg.Var.Name);
            }
        }
        ScanAddressTakenNode(node.Lhs);
        ScanAddressTakenNode(node.Rhs);
        ScanAddressTakenNode(node.Cond);
        ScanAddressTakenNode(node.Then);
        ScanAddressTakenNode(node.Els);
        ScanAddressTakenNode(node.Init);
        ScanAddressTakenNode(node.Inc);
        ScanAddressTakenNode(node.Body);
        ScanAddressTakenNode(node.Next);
        for (Node arg = node.Args; arg != null; arg = arg.Next)
            ScanAddressTakenNode(arg);
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

        // TU hash (from source path, matching MSVC behavior)
        byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceFile));
        _tuHash = BitConverter.ToString(pathHash, 0, 4).Replace("-", "").ToLowerInvariant();

        // Scan for address-taken functions before metadata registration
        ScanAddressTaken(prog);

        // Metadata
        RegisterMetadata(prog, objName);

        // Global data bytes + COFF token registration — BEFORE IL emission
        EmitGlobalDataBytesAndTokens(prog);

        // IL Emission
        EmitFunctions(prog);

        EmitCxxPureMSILEntry();

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
