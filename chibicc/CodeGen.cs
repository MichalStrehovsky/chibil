using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Chibicc;

/// <summary>
/// MSIL code generator — emits managed COFF .obj files from the AST.
/// Replaces the x86-64 assembly codegen with a two-pass MSIL backend.
/// </summary>
public class CodeGen
{
    private readonly CompilerOptions _options;
    private readonly Tokenizer _tokenizer;

    // ═══════════════════════════════════════════════════════════════
    //  Metadata state (built during Pass 1)
    // ═══════════════════════════════════════════════════════════════
    private MetadataBuilder _md;
    private CoffHeaderBuilder _coffHeader;
    private ManagedCoffSymbolTableBuilder _symtab;
    private BlobBuilder _ilStreamBuilder;
    private BlobBuilder _ilRelocBuilder;
    private CodeViewSymbolBuilder _codeviewSymbols;
    private RelocatableMethodBodyStreamEncoder _bodyEncoder;

    private TypeDefinitionHandle _moduleTypeDef;
    private AssemblyReferenceHandle _mscorlibRef;

    // Lazily-created TypeRefs
    private TypeReferenceHandle _valueTypeRef;
    private TypeReferenceHandle _isSignUnspecifiedByteRef;
    private TypeReferenceHandle _isConstRef;
    private TypeReferenceHandle _isVolatileRef;
    private TypeReferenceHandle _nativeCppClassAttrRef;
    private TypeReferenceHandle _unsafeValueTypeAttrRef;
    private TypeReferenceHandle _fixedAddressAttrRef;
    private MemberReferenceHandle _nativeCppCtorRef;
    private MemberReferenceHandle _unsafeVTCtorRef;
    private MemberReferenceHandle _fixedAddrCtorRef;

    // Function and field registrations from Pass 1
    private readonly Dictionary<Obj, MethodDefinitionHandle> _methodDefs = new();
    private readonly Dictionary<Obj, FieldDefinitionHandle> _fieldDefs = new();
    private readonly Dictionary<string, MemberReferenceHandle> _externalFuncRefs = new();
    private readonly Dictionary<CType, TypeDefinitionHandle> _structTypeDefs = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _arrayTypeDefs = new();

    // Translation-unit hash for static local mangling
    private string _tuHash;

    // Counters for metadata row ordering
    private int _nextFieldRow = 1;
    private int _nextMethodRow = 1;
    private int _nextParamRow = 1;

    // ═══════════════════════════════════════════════════════════════
    //  Per-function state (used during Pass 2)
    // ═══════════════════════════════════════════════════════════════
    private Obj _currentFn;
    private RelocatableInstructionEncoder _enc;
    private CodeViewFileHandle _cvFile;

    // Local variable slot mapping: Obj → IL slot index
    private readonly Dictionary<Obj, int> _localSlots = new();
    // Parameter index mapping: Obj → IL argument index
    private readonly Dictionary<Obj, int> _paramSlots = new();
    private int _maxStack;
    private int _stackDepth;
    private StandaloneSignatureHandle _localsSigHandle;

    // Label management
    private int _labelCount = 1;
    private readonly Dictionary<string, LabelHandle> _labels = new();

    // Data section for RVA fields
    private BlobBuilder _dataStream;
    private BlobBuilder _dataRelocs;

    // Scratch locals added during IL emission (for assignment dup, etc.)
    private readonly List<CType> _scratchLocals = new();
    private int _scratchLocalBase; // first slot index for scratch locals

    public CodeGen(CompilerOptions options, Tokenizer tokenizer)
    {
        _options = options;
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// Gets or adds a scratch local variable slot for temporary values during IL emission.
    /// These are added after the user's locals.
    /// </summary>
    private int GetOrAddScratchLocal(CType ty)
    {
        // Reuse existing scratch local of same type
        for (int i = 0; i < _scratchLocals.Count; i++)
        {
            if (_scratchLocals[i].Kind == ty.Kind && _scratchLocals[i].Size == ty.Size)
                return _scratchLocalBase + i;
        }
        _scratchLocals.Add(ty);
        return _scratchLocalBase + _scratchLocals.Count - 1;
    }

    private int Count() => _labelCount++;

    // ═══════════════════════════════════════════════════════════════
    //  Stack depth tracking
    // ═══════════════════════════════════════════════════════════════

    private void Push(int n = 1) { _stackDepth += n; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Pop(int n = 1) { _stackDepth -= n; System.Diagnostics.Debug.Assert(_stackDepth >= 0, $"Stack underflow: depth={_stackDepth}"); }

    // ═══════════════════════════════════════════════════════════════
    //  Lazy TypeRef accessors
    // ═══════════════════════════════════════════════════════════════

    private TypeReferenceHandle GetValueTypeRef()
    {
        if (_valueTypeRef.IsNil)
            _valueTypeRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System"), _md.GetOrAddString("ValueType"));
        return _valueTypeRef;
    }

    private TypeReferenceHandle GetIsSignUnspecifiedByteRef()
    {
        if (_isSignUnspecifiedByteRef.IsNil)
            _isSignUnspecifiedByteRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("IsSignUnspecifiedByte"));
        return _isSignUnspecifiedByteRef;
    }

    private TypeReferenceHandle GetIsConstRef()
    {
        if (_isConstRef.IsNil)
            _isConstRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("IsConst"));
        return _isConstRef;
    }

    private TypeReferenceHandle GetIsVolatileRef()
    {
        if (_isVolatileRef.IsNil)
            _isVolatileRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("IsVolatile"));
        return _isVolatileRef;
    }

    private MemberReferenceHandle GetNativeCppCtorRef()
    {
        if (_nativeCppCtorRef.IsNil)
        {
            if (_nativeCppClassAttrRef.IsNil)
                _nativeCppClassAttrRef = _md.AddTypeReference(_mscorlibRef,
                    _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("NativeCppClassAttribute"));
            _nativeCppCtorRef = _md.AddMemberReference(_nativeCppClassAttrRef, _md.GetOrAddString(".ctor"), GetVoidCtorBlob());
        }
        return _nativeCppCtorRef;
    }

    private MemberReferenceHandle GetUnsafeVTCtorRef()
    {
        if (_unsafeVTCtorRef.IsNil)
        {
            if (_unsafeValueTypeAttrRef.IsNil)
                _unsafeValueTypeAttrRef = _md.AddTypeReference(_mscorlibRef,
                    _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("UnsafeValueTypeAttribute"));
            _unsafeVTCtorRef = _md.AddMemberReference(_unsafeValueTypeAttrRef, _md.GetOrAddString(".ctor"), GetVoidCtorBlob());
        }
        return _unsafeVTCtorRef;
    }

    private MemberReferenceHandle GetFixedAddrCtorRef()
    {
        if (_fixedAddrCtorRef.IsNil)
        {
            if (_fixedAddressAttrRef.IsNil)
                _fixedAddressAttrRef = _md.AddTypeReference(_mscorlibRef,
                    _md.GetOrAddString("System.Runtime.CompilerServices"), _md.GetOrAddString("FixedAddressValueTypeAttribute"));
            _fixedAddrCtorRef = _md.AddMemberReference(_fixedAddressAttrRef, _md.GetOrAddString(".ctor"), GetVoidCtorBlob());
        }
        return _fixedAddrCtorRef;
    }

    private BlobHandle _voidCtorBlob;
    private BlobHandle GetVoidCtorBlob()
    {
        if (_voidCtorBlob.IsNil)
        {
            var sig = new BlobBuilder();
            new BlobEncoder(sig).MethodSignature(SignatureCallingConvention.Default, 0, true)
                .Parameters(0, out var ret, out var par);
            ret.Void();
            _voidCtorBlob = _md.GetOrAddBlob(sig);
        }
        return _voidCtorBlob;
    }

    private BlobHandle _defaultCtorAttrBlob;
    private BlobHandle GetDefaultCtorAttrBlob()
    {
        if (_defaultCtorAttrBlob.IsNil)
            _defaultCtorAttrBlob = _md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        return _defaultCtorAttrBlob;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Name Mangling
    // ═══════════════════════════════════════════════════════════════

    private string MangleFunctionName(Obj fn)
    {
        var sb = new StringBuilder();
        sb.Append('?');
        sb.Append(fn.Name);
        sb.Append("@@$$J0YM");
        AppendMangledType(sb, fn.Ty.ReturnTy, isReturn: true);
        bool hasParams = false;
        for (CType p = fn.Ty.Params; p != null; p = p.Next)
        {
            AppendMangledType(sb, p, isReturn: false);
            hasParams = true;
        }
        if (!hasParams) sb.Append('X');
        sb.Append("@Z");
        return sb.ToString();
    }

    private void AppendMangledType(StringBuilder sb, CType ty, bool isReturn)
    {
        if (ty.Kind == TypeKind.Ptr)
        {
            sb.Append("PEA");
            AppendMangledType(sb, ty.Base, isReturn: false);
            return;
        }

        if ((ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union) && isReturn)
            sb.Append("?A");

        switch (ty.Kind)
        {
            case TypeKind.Void: sb.Append('X'); break;
            case TypeKind.Bool: sb.Append("_N"); break;
            case TypeKind.Char:
                if (ty.IsUnsigned) sb.Append('E');
                else sb.Append('D');
                break;
            case TypeKind.Short:
                sb.Append(ty.IsUnsigned ? 'G' : 'F');
                break;
            case TypeKind.Int: case TypeKind.Enum:
                sb.Append(ty.IsUnsigned ? 'I' : 'H');
                break;
            case TypeKind.Long:
                sb.Append(ty.IsUnsigned ? "_K" : "_J");
                break;
            case TypeKind.Float: sb.Append('M'); break;
            case TypeKind.Double: sb.Append('N'); break;
            case TypeKind.LDouble: sb.Append('N'); break;
            case TypeKind.Struct: case TypeKind.Union:
                sb.Append('U');
                sb.Append(GetStructName(ty));
                sb.Append("@@");
                break;
            case TypeKind.Func:
                sb.Append("P6M");
                AppendMangledType(sb, ty.ReturnTy, isReturn: false);
                bool hasP = false;
                for (CType p = ty.Params; p != null; p = p.Next)
                {
                    AppendMangledType(sb, p, isReturn: false);
                    hasP = true;
                }
                if (!hasP) sb.Append('X');
                sb.Append("@Z");
                break;
            default:
                sb.Append('H');
                break;
        }
    }

    private static string GetStructName(CType ty)
    {
        if (ty.Name != null)
            return Util.GetTokenText(ty.Name);
        return $"__anon_{ty.GetHashCode():x}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Signature Builder — CType → MSIL signature encoding
    // ═══════════════════════════════════════════════════════════════

    private void EncodeType(BlobBuilder sig, CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Void:
                sig.WriteByte((byte)SignatureTypeCode.Void);
                break;
            case TypeKind.Bool:
                sig.WriteByte((byte)SignatureTypeCode.Boolean);
                break;
            case TypeKind.Char:
                if (!ty.IsUnsigned)
                {
                    sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
                }
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.Byte : (byte)SignatureTypeCode.SByte);
                break;
            case TypeKind.Short:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt16 : (byte)SignatureTypeCode.Int16);
                break;
            case TypeKind.Int: case TypeKind.Enum:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Long:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt64 : (byte)SignatureTypeCode.Int64);
                break;
            case TypeKind.Float:
                sig.WriteByte((byte)SignatureTypeCode.Single);
                break;
            case TypeKind.Double:
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.LDouble:
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.Ptr:
                sig.WriteByte((byte)SignatureTypeCode.Pointer);
                EncodeType(sig, ty.Base);
                break;
            case TypeKind.Array:
                EncodeValueType(sig, GetOrCreateArrayTypeDef(ty));
                break;
            case TypeKind.Struct: case TypeKind.Union:
                if (_structTypeDefs.TryGetValue(ty, out var sth))
                    EncodeValueType(sig, sth);
                else
                    sig.WriteByte((byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Func:
                sig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                sig.WriteByte(0x00);
                int paramCount = 0;
                for (CType p = ty.Params; p != null; p = p.Next) paramCount++;
                sig.WriteCompressedInteger(paramCount);
                EncodeType(sig, ty.ReturnTy);
                for (CType p = ty.Params; p != null; p = p.Next)
                    EncodeType(sig, p);
                break;
            case TypeKind.Vla:
                sig.WriteByte((byte)SignatureTypeCode.Pointer);
                EncodeType(sig, ty.Base);
                break;
            default:
                sig.WriteByte((byte)SignatureTypeCode.Int32);
                break;
        }
    }

    private void EncodeValueType(BlobBuilder sig, TypeDefinitionHandle handle)
    {
        sig.WriteByte(0x11); // ELEMENT_TYPE_VALUETYPE
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(handle));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Array TypeDef creation
    // ═══════════════════════════════════════════════════════════════

    private TypeDefinitionHandle GetOrCreateArrayTypeDef(CType ty)
    {
        string name = BuildArrayTypeName(ty);
        if (_arrayTypeDefs.TryGetValue(name, out var existing))
            return existing;

        // This should have been pre-allocated. If we get here during IL emission,
        // it means we missed a type during the pre-allocation walk.
        // Create it now as a fallback (it will get wrong FieldList/MethodList).
        System.Diagnostics.Debug.Fail($"Array TypeDef '{name}' was not pre-allocated");

        var handle = _md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class |
            TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
            _md.GetOrAddString("<CppImplementationDetails>"),
            _md.GetOrAddString(name),
            GetValueTypeRef(),
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        _md.AddTypeLayout(handle, 0, (uint)ty.Size);
        _md.AddCustomAttribute(handle, GetNativeCppCtorRef(), GetDefaultCtorAttrBlob());
        _md.AddCustomAttribute(handle, GetUnsafeVTCtorRef(), GetDefaultCtorAttrBlob());

        _arrayTypeDefs[name] = handle;
        return handle;
    }

    private static string BuildArrayTypeName(CType ty)
    {
        var sb = new StringBuilder("$ArrayType$$$BY");
        sb.Append('0');
        AppendMsvcNumber(sb, ty.ArrayLen);
        AppendElementTypeCode(sb, ty.Base);
        return sb.ToString();
    }

    private static void AppendMsvcNumber(StringBuilder sb, int value)
    {
        if (value <= 0) { sb.Append("A@"); return; }
        if (value >= 1 && value <= 10) { sb.Append((char)('0' + value - 1)); return; }
        var nibbles = new List<char>();
        int v = value;
        while (v > 0) { nibbles.Add((char)('A' + (v & 0xF))); v >>= 4; }
        nibbles.Reverse();
        foreach (char c in nibbles) sb.Append(c);
        sb.Append('@');
    }

    private static void AppendElementTypeCode(StringBuilder sb, CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Char: sb.Append(ty.IsUnsigned ? 'E' : 'D'); break;
            case TypeKind.Short: sb.Append(ty.IsUnsigned ? 'G' : 'F'); break;
            case TypeKind.Int: case TypeKind.Enum: sb.Append(ty.IsUnsigned ? 'I' : 'H'); break;
            case TypeKind.Long: sb.Append(ty.IsUnsigned ? "_K" : "_J"); break;
            case TypeKind.Float: sb.Append('M'); break;
            case TypeKind.Double: sb.Append('N'); break;
            default: sb.Append('H'); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Translation-unit hash
    // ═══════════════════════════════════════════════════════════════

    private string GetTuHash()
    {
        if (_tuHash != null) return _tuHash;
        CFile[] files = _tokenizer.GetInputFiles();
        string input = files.Length > 0 ? files[0].Name : "unknown";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        _tuHash = $"?A0x{BitConverter.ToUInt32(hash, 0):x8}";
        return _tuHash;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 1: Metadata Registration
    // ═══════════════════════════════════════════════════════════════

    private void RegisterMetadata(Obj prog, string objName)
    {
        byte[] mscorlibHash = new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };

        _mscorlibRef = _md.AddAssemblyReference(
            _md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            _md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            _md.GetOrAddBlob(mscorlibHash));

        // Phase 1: Pre-allocate TypeDef handles for struct/union/array types.
        // <Module> is row 1; struct TypeDefs start at row 2.
        int nextTypeDefRow = 2; // row 1 reserved for <Module>
        PreAllocateStructTypeDefs(prog, ref nextTypeDefRow);

        // Phase 2: Add <Module> TypeDef (row 1).
        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        // Phase 3: Register all function MethodDefs (owned by <Module>).
        RegisterFunctions(prog);

        // Phase 4: Register all global/static field FieldDefs (owned by <Module>).
        RegisterGlobalFields(prog);

        // Phase 5: Materialize struct/union/array TypeDefs with correct
        // FieldList/MethodList pointing past all <Module>-owned rows.
        MaterializeStructTypeDefs();

        // Module row
        _md.AddModule(0,
            _md.GetOrAddString(objName),
            _md.GetOrAddGuid(Guid.NewGuid()),
            default, default);
    }

    // Ordered list of pre-allocated struct TypeDefs, for materialization
    private readonly List<(CType ty, string ns, string name, TypeDefinitionHandle handle)> _pendingTypeDefs = new();

    private void PreAllocateStructTypeDefs(Obj prog, ref int nextTypeDefRow)
    {
        var registered = new HashSet<CType>();
        for (Obj obj = prog; obj != null; obj = obj.Next)
        {
            if (obj.IsFunction && obj.IsDefinition && obj.IsLive)
            {
                PreAllocateTypeDefsFromType(obj.Ty, registered, ref nextTypeDefRow);
                for (Obj local = obj.Locals; local != null; local = local.Next)
                    PreAllocateTypeDefsFromType(local.Ty, registered, ref nextTypeDefRow);
                for (Obj param = obj.Params; param != null; param = param.Next)
                    PreAllocateTypeDefsFromType(param.Ty, registered, ref nextTypeDefRow);
            }
            else if (!obj.IsFunction && obj.IsDefinition)
            {
                PreAllocateTypeDefsFromType(obj.Ty, registered, ref nextTypeDefRow);
            }
        }
    }

    private void PreAllocateTypeDefsFromType(CType ty, HashSet<CType> registered, ref int nextTypeDefRow)
    {
        if (ty == null || !registered.Add(ty)) return;
        switch (ty.Kind)
        {
            case TypeKind.Struct: case TypeKind.Union:
                if (!_structTypeDefs.ContainsKey(ty))
                {
                    string name = GetStructName(ty);
                    var handle = MetadataTokens.TypeDefinitionHandle(nextTypeDefRow++);
                    _structTypeDefs[ty] = handle;
                    _pendingTypeDefs.Add((ty, null, name, handle));
                }
                for (Member m = ty.Members; m != null; m = m.Next)
                    PreAllocateTypeDefsFromType(m.Ty, registered, ref nextTypeDefRow);
                break;
            case TypeKind.Ptr:
                PreAllocateTypeDefsFromType(ty.Base, registered, ref nextTypeDefRow);
                break;
            case TypeKind.Array:
                PreAllocateTypeDefsFromType(ty.Base, registered, ref nextTypeDefRow);
                PreAllocateArrayTypeDef(ty, ref nextTypeDefRow);
                break;
            case TypeKind.Func:
                PreAllocateTypeDefsFromType(ty.ReturnTy, registered, ref nextTypeDefRow);
                for (CType p = ty.Params; p != null; p = p.Next)
                    PreAllocateTypeDefsFromType(p, registered, ref nextTypeDefRow);
                break;
        }
    }

    private void PreAllocateArrayTypeDef(CType ty, ref int nextTypeDefRow)
    {
        string name = BuildArrayTypeName(ty);
        if (_arrayTypeDefs.ContainsKey(name)) return;
        var handle = MetadataTokens.TypeDefinitionHandle(nextTypeDefRow++);
        _arrayTypeDefs[name] = handle;
        _pendingTypeDefs.Add((ty, "<CppImplementationDetails>", name, handle));
    }

    private void MaterializeStructTypeDefs()
    {
        // Now that all <Module>-owned methods and fields have been added,
        // create the actual TypeDef rows. Their FieldList/MethodList point
        // past all Module-owned rows, so they own nothing.
        foreach (var (ty, ns, name, expectedHandle) in _pendingTypeDefs)
        {
            var actualHandle = _md.AddTypeDefinition(
                TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class |
                TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
                ns != null ? _md.GetOrAddString(ns) : default,
                _md.GetOrAddString(name),
                GetValueTypeRef(),
                MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

            System.Diagnostics.Debug.Assert(actualHandle == expectedHandle,
                $"TypeDef handle mismatch: expected {MetadataTokens.GetRowNumber(expectedHandle)}, got {MetadataTokens.GetRowNumber(actualHandle)} for '{name}'");

            _md.AddTypeLayout(actualHandle, 0, (uint)ty.Size);
            _md.AddCustomAttribute(actualHandle, GetNativeCppCtorRef(), GetDefaultCtorAttrBlob());
            _md.AddCustomAttribute(actualHandle, GetUnsafeVTCtorRef(), GetDefaultCtorAttrBlob());
        }
    }

    private void RegisterFunctions(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            var sig = new BlobBuilder();
            var sigEnc = new BlobEncoder(sig).MethodSignature();
            int paramCount = 0;
            for (CType p = fn.Ty.Params; p != null; p = p.Next) paramCount++;

            sigEnc.Parameters(paramCount, out var retEnc, out var parEnc);
            if (fn.Ty.ReturnTy.Kind == TypeKind.Void)
                retEnc.Void();
            else
            {
                var retTypeEnc = retEnc.Type();
                EncodeType(retTypeEnc.Builder, fn.Ty.ReturnTy);
            }

            for (CType p = fn.Ty.Params; p != null; p = p.Next)
            {
                var pEnc = parEnc.AddParameter().Type();
                EncodeType(pEnc.Builder, p);
            }

            var methodHandle = _md.AddMethodDefinition(
                MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                _md.GetOrAddString(fn.Name),
                _md.GetOrAddBlob(sig),
                0,
                MetadataTokens.ParameterHandle(_nextParamRow));
            _nextMethodRow++;

            int paramIdx = 1;
            for (Obj p = fn.Params; p != null; p = p.Next)
            {
                _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString(p.Name), paramIdx);
                _nextParamRow++;
                paramIdx++;
            }

            _methodDefs[fn] = methodHandle;
        }
    }

    private void RegisterGlobalFields(Obj prog)
    {
        for (Obj v = prog; v != null; v = v.Next)
        {
            if (v.IsFunction || !v.IsDefinition) continue;

            var fieldSig = new BlobBuilder();
            var fieldSigEnc = new BlobEncoder(fieldSig).Field().Type();
            EncodeType(fieldSigEnc.Builder, v.Ty);

            FieldAttributes attrs = FieldAttributes.Assembly | FieldAttributes.Static;
            if (v.InitData != null)
                attrs |= FieldAttributes.HasFieldRVA;

            var fieldHandle = _md.AddFieldDefinition(
                attrs,
                _md.GetOrAddString(v.Name),
                _md.GetOrAddBlob(fieldSig));
            _nextFieldRow++;

            if (v.InitData != null)
            {
                // Write init data to the data section
                int rva = _dataStream.Count;
                _dataStream.WriteBytes(v.InitData);
                _md.AddFieldRelativeVirtualAddress(fieldHandle, rva);
            }

            _fieldDefs[v] = fieldHandle;

            if (v.InitData != null)
            {
                LogicalSection section = LogicalSection.RData;
                _symtab.AddDataClrToken(v.Name, fieldHandle, section, _dataStream.Count - v.InitData.Length, out _);
            }
            // Zero-initialized globals: no COFF data symbol needed, just the FieldDef
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 2: IL Emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitFunctions(Obj prog)
    {
        // Collect live functions, then emit in reverse order so callees
        // are registered before callers (AddMethodBody registers the COFF
        // token symbol, and call instructions create undefined token
        // references that must not precede the definition).
        var fns = new List<Obj>();
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            fns.Add(fn);
        }
        fns.Reverse();
        foreach (Obj fn in fns)
            EmitFunction(fn);
    }

    private void EmitFunction(Obj fn)
    {
        _currentFn = fn;
        _localSlots.Clear();
        _paramSlots.Clear();
        _labels.Clear();
        _scratchLocals.Clear();
        _maxStack = 0;
        _stackDepth = 0;
        _labelCount = 1;

        _enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

        // Assign parameter slots
        int paramIdx = 0;
        for (Obj p = fn.Params; p != null; p = p.Next)
            _paramSlots[p] = paramIdx++;

        // Assign local variable slots
        int localIdx = 0;
        for (Obj local = fn.Locals; local != null; local = local.Next)
        {
            if (_paramSlots.ContainsKey(local)) continue;
            _localSlots[local] = localIdx++;
        }
        _scratchLocalBase = localIdx;

        if (fn.Body != null && fn.Body.Tok != null)
            _enc.MarkLineNumber(_cvFile, fn.Body.Tok.LineNo);

        GenStmt(fn.Body);

        // Function return label
        string retLabel = $".L.return.{fn.Name}";
        if (_labels.TryGetValue(retLabel, out var retLabelHandle))
            _enc.MarkLabel(retLabelHandle);

        // Ensure non-void functions have a return value on fallthrough
        if (fn.Ty.ReturnTy.Kind != TypeKind.Void)
        {
            EmitDefaultValue(fn.Ty.ReturnTy);
            Push();
        }
        _enc.OpCode(ILOpCode.Ret);
        if (_stackDepth > 0) Pop();

        // Build locals signature (including any scratch locals added during emission)
        int totalLocals = localIdx + _scratchLocals.Count;
        _localsSigHandle = default;
        if (totalLocals > 0)
        {
            var localsSig = new BlobBuilder();
            var localsEnc = new BlobEncoder(localsSig).LocalVariableSignature(totalLocals);
            for (Obj local = fn.Locals; local != null; local = local.Next)
            {
                if (!_localSlots.ContainsKey(local)) continue;
                var varEnc = localsEnc.AddVariable().Type();
                EncodeType(varEnc.Builder, local.Ty);
            }
            foreach (var scratchTy in _scratchLocals)
            {
                var varEnc = localsEnc.AddVariable().Type();
                EncodeType(varEnc.Builder, scratchTy);
            }
            _localsSigHandle = _md.AddStandaloneSignature(_md.GetOrAddBlob(localsSig));
        }

        var methodHandle = _methodDefs[fn];
        string mangledName = MangleFunctionName(fn);
        _bodyEncoder.AddMethodBody(methodHandle, mangledName, _enc,
            maxStack: Math.Max(1, _maxStack),
            localVariablesSignature: _localsSigHandle,
            attributes: MethodBodyAttributes.InitLocals,
            debugName: fn.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Address generation
    // ═══════════════════════════════════════════════════════════════

    private void GenAddr(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Var:
                // Function address → ldftn
                if (node.Var.Ty.Kind == TypeKind.Func && !node.Var.IsLocal)
                {
                    if (_methodDefs.TryGetValue(node.Var, out var mh))
                    {
                        _enc.OpCode(ILOpCode.Ldftn);
                        _enc.Token(mh);
                        Push();
                    }
                    else
                    {
                        // External function
                        if (!_externalFuncRefs.TryGetValue(node.Var.Name, out var mr))
                        {
                            mr = CreateExternalMemberRef(node.Var);
                            _externalFuncRefs[node.Var.Name] = mr;
                        }
                        _enc.OpCode(ILOpCode.Ldftn);
                        _enc.Token((EntityHandle)mr);
                        Push();
                    }
                    return;
                }
                if (node.Var.IsLocal)
                {
                    if (_paramSlots.TryGetValue(node.Var, out int pIdx))
                    {
                        _enc.LoadArgumentAddress(pIdx);
                        Push();
                    }
                    else if (_localSlots.TryGetValue(node.Var, out int lIdx))
                    {
                        _enc.LoadLocalAddress(lIdx);
                        Push();
                    }
                    else
                        Util.ErrorTok(node.Tok, "variable not found in local slots");
                }
                else
                {
                    if (_fieldDefs.TryGetValue(node.Var, out var fh))
                    {
                        _enc.OpCode(ILOpCode.Ldsflda);
                        _enc.Token(fh);
                        Push();
                    }
                    else
                    {
                        // External (non-definition) global — create on demand
                        var extFh = GetOrCreateExternalField(node.Var);
                        _enc.OpCode(ILOpCode.Ldsflda);
                        _enc.Token(extFh);
                        Push();
                    }
                }
                return;
            case NodeKind.Deref:
                GenExpr(node.Lhs);
                return;
            case NodeKind.Comma:
                GenExpr(node.Lhs);
                if (node.Lhs.Ty.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); }
                GenAddr(node.Rhs);
                return;
            case NodeKind.Member:
                GenAddr(node.Lhs);
                if (node.Member.Offset != 0)
                {
                    _enc.LoadConstantI4(node.Member.Offset);
                    Push();
                    _enc.OpCode(ILOpCode.Add);
                    Pop();
                }
                return;
            case NodeKind.FunCall:
                break;
            case NodeKind.Assign:
            case NodeKind.Cond:
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union) { GenExpr(node); return; }
                break;
            case NodeKind.VlaPtr:
                // VLA pointer is stored in a local slot
                if (_localSlots.TryGetValue(node.Var, out int vlaSlot))
                {
                    _enc.LoadLocalAddress(vlaSlot);
                    Push();
                }
                else
                    Util.ErrorTok(node.Tok, "VLA variable not found");
                return;
        }
        Util.ErrorTok(node.Tok, "not an lvalue");
    }


    // ═══════════════════════════════════════════════════════════════
    //  Load and Store
    // ═══════════════════════════════════════════════════════════════

    private void Load(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Array: case TypeKind.Struct: case TypeKind.Union:
            case TypeKind.Func: case TypeKind.Vla:
                return;
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Ldind_r4); return;
            case TypeKind.Double: case TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Ldind_r8); return;
            case TypeKind.Bool:
                _enc.OpCode(ILOpCode.Ldind_u1); return;
        }
        switch (ty.Size)
        {
            case 1: _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u1 : ILOpCode.Ldind_i1); break;
            case 2: _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u2 : ILOpCode.Ldind_i2); break;
            case 4: _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u4 : ILOpCode.Ldind_i4); break;
            case 8: _enc.OpCode(ILOpCode.Ldind_i8); break;
            default: _enc.OpCode(ILOpCode.Ldind_i4); break;
        }
    }

    private void Store(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Struct: case TypeKind.Union:
                _enc.LoadConstantI4(ty.Size); Push();
                _enc.OpCode(ILOpCode.Cpblk); Pop(3);
                return;
            case TypeKind.Float: _enc.OpCode(ILOpCode.Stind_r4); Pop(2); return;
            case TypeKind.Double: case TypeKind.LDouble: _enc.OpCode(ILOpCode.Stind_r8); Pop(2); return;
            case TypeKind.Bool: _enc.OpCode(ILOpCode.Stind_i1); Pop(2); return;
        }
        switch (ty.Size)
        {
            case 1: _enc.OpCode(ILOpCode.Stind_i1); break;
            case 2: _enc.OpCode(ILOpCode.Stind_i2); break;
            case 4: _enc.OpCode(ILOpCode.Stind_i4); break;
            case 8: _enc.OpCode(ILOpCode.Stind_i8); break;
            default: _enc.OpCode(ILOpCode.Stind_i4); break;
        }
        Pop(2);
    }

    private void Cast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) return;
        if (to.Kind == TypeKind.Bool) { CmpZero(from); return; }
        switch (to.Kind)
        {
            case TypeKind.Char: _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u1 : ILOpCode.Conv_i1); break;
            case TypeKind.Short: _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u2 : ILOpCode.Conv_i2); break;
            case TypeKind.Int: case TypeKind.Enum:
                if (from.Kind == TypeKind.Long || from.Kind == TypeKind.Ptr || TypeSystem.IsFlonum(from))
                    _enc.OpCode(ILOpCode.Conv_i4);
                break;
            case TypeKind.Long:
                if (from.Kind == TypeKind.Ptr) _enc.OpCode(ILOpCode.Conv_i8);
                else if (from.Size < 8) _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
                break;
            case TypeKind.Float:
                if (from.IsUnsigned && (from.Kind == TypeKind.Long || from.Kind == TypeKind.Int))
                    _enc.OpCode(ILOpCode.Conv_r_un);
                _enc.OpCode(ILOpCode.Conv_r4); break;
            case TypeKind.Double: case TypeKind.LDouble:
                if (from.IsUnsigned && (from.Kind == TypeKind.Long || from.Kind == TypeKind.Int))
                    _enc.OpCode(ILOpCode.Conv_r_un);
                _enc.OpCode(ILOpCode.Conv_r8); break;
            case TypeKind.Ptr:
                if (TypeSystem.IsInteger(from)) _enc.OpCode(ILOpCode.Conv_i); break;
        }
    }

    private void EmitDefaultValue(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float: _enc.LoadConstantR4(0.0f); break;
            case TypeKind.Double: case TypeKind.LDouble: _enc.LoadConstantR8(0.0); break;
            case TypeKind.Long: _enc.LoadConstantI8(0); break;
            case TypeKind.Struct: case TypeKind.Union:
                // For struct return default, load a zero-initialized local
                int scratchSlot = GetOrAddScratchLocal(ty);
                _enc.LoadLocal(scratchSlot);
                break;
            default: _enc.LoadConstantI4(0); break;
        }
    }

    /// <summary>
    /// Converts the value on top of the stack to an int32 boolean (0 or 1)
    /// suitable for brfalse/brtrue. Handles float/double/long/pointer types.
    /// </summary>
    private void Booleanize(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();       // 1 if equal to 0
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();       // negate: 1 if NOT zero
                return;
            case TypeKind.Double: case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case TypeKind.Long:
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Conv_i8);
                _enc.OpCode(ILOpCode.Ceq); Pop();       // 1 if equal to 0
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();       // negate
                return;
            case TypeKind.Ptr:
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Conv_i);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
        }
        // int32 types: brfalse works directly, no conversion needed
    }

    private void CmpZero(CType ty)
    {
        // Compare-to-zero: value → 0 or 1
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                // ceq gives 1 if equal to zero; we want "not zero"
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case TypeKind.Double: case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
        }
        _enc.LoadConstantI4(0); Push();
        if (ty.Kind == TypeKind.Long || ty.Kind == TypeKind.Ptr || ty.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i8);
        _enc.OpCode(ILOpCode.Cgt_un); Pop();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression code generation
    // ═══════════════════════════════════════════════════════════════

    private void GenExpr(Node node)
    {
        if (node == null) return;
        if (node.Tok != null) _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);
        switch (node.Kind)
        {
            case NodeKind.NullExpr: return;
            case NodeKind.Num:
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float: _enc.LoadConstantR4((float)node.FVal); Push(); return;
                    case TypeKind.Double: case TypeKind.LDouble: _enc.LoadConstantR8(node.FVal); Push(); return;
                    case TypeKind.Long: _enc.LoadConstantI8(node.Val); Push(); return;
                }
                _enc.LoadConstantI4((int)node.Val); Push(); return;
            case NodeKind.Neg: GenExpr(node.Lhs); _enc.OpCode(ILOpCode.Neg); return;
            case NodeKind.Var: GenAddr(node); Load(node.Ty); return;
            case NodeKind.Member: GenAddr(node); Load(node.Ty);
                if (node.Member.IsBitfield)
                {
                    int totalBits = node.Ty.Size * 8;
                    int unusedHigh = totalBits - node.Member.BitWidth - node.Member.BitOffset;
                    if (unusedHigh > 0) { _enc.LoadConstantI4(unusedHigh); Push(); _enc.OpCode(ILOpCode.Shl); Pop(); }
                    int shiftRight = totalBits - node.Member.BitWidth;
                    if (shiftRight > 0) { _enc.LoadConstantI4(shiftRight); Push(); _enc.OpCode(node.Member.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop(); }
                }
                return;
            case NodeKind.Deref: GenExpr(node.Lhs); Load(node.Ty); return;
            case NodeKind.Addr: GenAddr(node.Lhs); return;
            case NodeKind.Assign:
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                {
                    // Struct assign: dest_addr, src_addr, size → cpblk
                    GenAddr(node.Lhs);  // [dest_addr]
                    GenExpr(node.Rhs);  // [dest_addr, src_addr] (Load is no-op for structs)
                    Store(node.Ty);     // [] — cpblk consumes dest, src, size
                    // Struct assignment as expression leaves address of dest
                    GenAddr(node.Lhs);
                }
                else
                {
                    GenAddr(node.Lhs);  // [addr]
                    GenExpr(node.Rhs);  // [addr, val]
                    // Save val to scratch so assignment expression leaves its result
                    int scratchSlot = GetOrAddScratchLocal(node.Ty);
                    _enc.OpCode(ILOpCode.Dup); Push(); // [addr, val, val]
                    _enc.StoreLocal(scratchSlot); Pop();  // [addr, val]
                    Store(node.Ty);                       // []
                    _enc.LoadLocal(scratchSlot); Push();   // [val]
                }
                return;
            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                    if (n.Next == null && n.Kind == NodeKind.ExprStmt) GenExpr(n.Lhs);
                    else GenStmt(n);
                return;
            case NodeKind.Comma:
                GenExpr(node.Lhs);
                if (node.Lhs.Ty != null && node.Lhs.Ty.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); }
                GenExpr(node.Rhs); return;
            case NodeKind.Cast: GenExpr(node.Lhs); Cast(node.Lhs.Ty, node.Ty); return;
            case NodeKind.MemZero:
                if (_localSlots.TryGetValue(node.Var, out int mzSlot))
                {
                    _enc.LoadLocalAddress(mzSlot); Push();
                    _enc.LoadConstantI4(0); Push();
                    _enc.LoadConstantI4(node.Var.Ty.Size); Push();
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                return;
            case NodeKind.Cond:
            {
                var elseL = _enc.DefineLabel(); var endL = _enc.DefineLabel();
                int depthBeforeCond = _stackDepth;
                GenExpr(node.Cond); Booleanize(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseL); Pop();
                GenExpr(node.Then); _enc.Branch(ILOpCode.Br, endL);
                // Reset depth for else path — at elseL, stack is at depthBeforeCond
                _stackDepth = depthBeforeCond;
                _enc.MarkLabel(elseL); GenExpr(node.Els); _enc.MarkLabel(endL);
                // After merge: depthBeforeCond + 1 (one result value)
                return;
            }
            case NodeKind.Not:
                GenExpr(node.Lhs);
                // Use type-aware comparison to zero
                CmpZero(node.Lhs.Ty);
                // CmpZero gives 1 if non-zero; we want Not, so negate
                _enc.LoadConstantI4(0); Push();
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case NodeKind.BitNot: GenExpr(node.Lhs); _enc.OpCode(ILOpCode.Not); return;
            case NodeKind.LogAnd:
            {
                var falseL = _enc.DefineLabel(); var endL = _enc.DefineLabel();
                int depthBefore = _stackDepth;
                GenExpr(node.Lhs); Booleanize(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseL); Pop();
                GenExpr(node.Rhs); Booleanize(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseL); Pop();
                _enc.LoadConstantI4(1); Push(); _enc.Branch(ILOpCode.Br, endL);
                // Reset depth for false path
                _stackDepth = depthBefore;
                _enc.MarkLabel(falseL); _enc.LoadConstantI4(0); Push(); _enc.MarkLabel(endL);
                // After merge: depthBefore + 1
                return;
            }
            case NodeKind.LogOr:
            {
                var trueL = _enc.DefineLabel(); var endL = _enc.DefineLabel();
                int depthBefore = _stackDepth;
                GenExpr(node.Lhs); Booleanize(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueL); Pop();
                GenExpr(node.Rhs); Booleanize(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueL); Pop();
                _enc.LoadConstantI4(0); Push(); _enc.Branch(ILOpCode.Br, endL);
                // Reset depth for true path
                _stackDepth = depthBefore;
                _enc.MarkLabel(trueL); _enc.LoadConstantI4(1); Push(); _enc.MarkLabel(endL);
                // After merge: depthBefore + 1
                return;
            }
            case NodeKind.FunCall:
            {
                if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.Name == "alloca")
                {
                    GenExpr(node.Args); _enc.OpCode(ILOpCode.Localloc); return;
                }
                int argCount = 0;
                for (Node arg = node.Args; arg != null; arg = arg.Next) { GenExpr(arg); argCount++; }
                EntityHandle callTarget = default;
                if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.Ty.Kind == TypeKind.Func)
                {
                    Obj fnVar = node.Lhs.Var;
                    if (_methodDefs.TryGetValue(fnVar, out var mh)) callTarget = mh;
                    else
                    {
                        if (!_externalFuncRefs.TryGetValue(fnVar.Name, out var mr))
                        {
                            mr = CreateExternalMemberRef(fnVar);
                            _externalFuncRefs[fnVar.Name] = mr;
                        }
                        callTarget = mr;
                    }
                }
                if (!callTarget.IsNil)
                {
                    _enc.Call(callTarget); Pop(argCount);
                    if (node.Ty.Kind != TypeKind.Void) Push();
                }
                else
                {
                    // Indirect call via function pointer — use calli
                    GenExpr(node.Lhs); // pushes the function pointer
                    // Build standalone signature for the call
                    var calliSig = new BlobBuilder();
                    calliSig.WriteByte(0x00); // DEFAULT calling convention
                    int cParamCount = 0;
                    for (CType cp = node.FuncTy.Params; cp != null; cp = cp.Next) cParamCount++;
                    calliSig.WriteCompressedInteger(cParamCount);
                    EncodeType(calliSig, node.FuncTy.ReturnTy);
                    for (CType cp = node.FuncTy.Params; cp != null; cp = cp.Next)
                        EncodeType(calliSig, cp);
                    var calliSigHandle = _md.AddStandaloneSignature(_md.GetOrAddBlob(calliSig));
                    _enc.CallIndirect(calliSigHandle);
                    Pop(argCount + 1); // pops args + function pointer
                    if (node.Ty.Kind != TypeKind.Void) Push();
                }
                return;
            }
            case NodeKind.Cas: case NodeKind.Exch:
                _enc.LoadConstantI4(0); Push(); return;
            case NodeKind.LabelVal:
                Util.ErrorTok(node.Tok, "labels-as-values not supported in MSIL"); return;
        }
        // Binary ops
        GenExpr(node.Lhs); GenExpr(node.Rhs);
        switch (node.Kind)
        {
            case NodeKind.Add: _enc.OpCode(ILOpCode.Add); Pop(); return;
            case NodeKind.Sub: _enc.OpCode(ILOpCode.Sub); Pop(); return;
            case NodeKind.Mul: _enc.OpCode(ILOpCode.Mul); Pop(); return;
            case NodeKind.Div: _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Div_un : ILOpCode.Div); Pop(); return;
            case NodeKind.Mod: _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Rem_un : ILOpCode.Rem); Pop(); return;
            case NodeKind.BitAnd: _enc.OpCode(ILOpCode.And); Pop(); return;
            case NodeKind.BitOr: _enc.OpCode(ILOpCode.Or); Pop(); return;
            case NodeKind.BitXor: _enc.OpCode(ILOpCode.Xor); Pop(); return;
            case NodeKind.Shl: _enc.OpCode(ILOpCode.Shl); Pop(); return;
            case NodeKind.Shr: _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop(); return;
            case NodeKind.Eq: _enc.OpCode(ILOpCode.Ceq); Pop(); return;
            case NodeKind.Ne: _enc.OpCode(ILOpCode.Ceq); Pop(); _enc.LoadConstantI4(0); Push(); _enc.OpCode(ILOpCode.Ceq); Pop(); return;
            case NodeKind.Lt: _enc.OpCode(node.Lhs.Ty.IsUnsigned || node.Lhs.Ty.Kind == TypeKind.Ptr ? ILOpCode.Clt_un : ILOpCode.Clt); Pop(); return;
            case NodeKind.Le: _enc.OpCode(node.Lhs.Ty.IsUnsigned || node.Lhs.Ty.Kind == TypeKind.Ptr ? ILOpCode.Cgt_un : ILOpCode.Cgt); Pop(); _enc.LoadConstantI4(0); Push(); _enc.OpCode(ILOpCode.Ceq); Pop(); return;
        }
        Util.ErrorTok(node.Tok, "invalid expression");
    }

    private MemberReferenceHandle CreateExternalMemberRef(Obj fn)
    {
        var sig = new BlobBuilder();
        var sigEnc = new BlobEncoder(sig).MethodSignature();
        int paramCount = 0;
        for (CType p = fn.Ty.Params; p != null; p = p.Next) paramCount++;
        sigEnc.Parameters(paramCount, out var retEnc, out var parEnc);
        if (fn.Ty.ReturnTy.Kind == TypeKind.Void) retEnc.Void();
        else { var r = retEnc.Type(); EncodeType(r.Builder, fn.Ty.ReturnTy); }
        for (CType p = fn.Ty.Params; p != null; p = p.Next) { var pe = parEnc.AddParameter().Type(); EncodeType(pe.Builder, p); }
        var memberRef = _md.AddMemberReference(_moduleTypeDef, _md.GetOrAddString(fn.Name), _md.GetOrAddBlob(sig));
        string mangledName = MangleFunctionName(fn);
        _symtab.AddExternalClrToken(mangledName, memberRef);
        return memberRef;
    }

    private FieldDefinitionHandle GetOrCreateExternalField(Obj v)
    {
        // Check if already registered
        if (_fieldDefs.TryGetValue(v, out var existing))
            return existing;

        var fieldSig = new BlobBuilder();
        var fieldSigEnc = new BlobEncoder(fieldSig).Field().Type();
        EncodeType(fieldSigEnc.Builder, v.Ty);

        var fieldHandle = _md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static,
            _md.GetOrAddString(v.Name),
            _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        _fieldDefs[v] = fieldHandle;
        _symtab.AddDataClrToken(v.Name, fieldHandle, LogicalSection.Data, 0, out _);
        return fieldHandle;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statement code generation
    // ═══════════════════════════════════════════════════════════════

    private void GenStmt(Node node)
    {
        if (node == null) return;
        if (node.Tok != null) _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);
        switch (node.Kind)
        {
            case NodeKind.If:
            {
                var elseL = _enc.DefineLabel(); var endL = _enc.DefineLabel();
                GenExpr(node.Cond); Booleanize(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseL); Pop();
                GenStmt(node.Then); _enc.Branch(ILOpCode.Br, endL);
                _enc.MarkLabel(elseL); if (node.Els != null) GenStmt(node.Els); _enc.MarkLabel(endL);
                return;
            }
            case NodeKind.For:
            {
                var beginL = _enc.DefineLabel(); var endL = _enc.DefineLabel(); var contL = _enc.DefineLabel();
                if (node.BrkLabel != null) _labels[node.BrkLabel] = endL;
                if (node.ContLabel != null) _labels[node.ContLabel] = contL;
                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginL);
                if (node.Cond != null) { GenExpr(node.Cond); Booleanize(node.Cond.Ty); _enc.Branch(ILOpCode.Brfalse, endL); Pop(); }
                GenStmt(node.Then); _enc.MarkLabel(contL);
                if (node.Inc != null) { GenExpr(node.Inc); if (node.Inc.Ty != null && node.Inc.Ty.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); } }
                _enc.Branch(ILOpCode.Br, beginL); _enc.MarkLabel(endL);
                return;
            }
            case NodeKind.Do:
            {
                var beginL = _enc.DefineLabel(); var endL = _enc.DefineLabel(); var contL = _enc.DefineLabel();
                if (node.BrkLabel != null) _labels[node.BrkLabel] = endL;
                if (node.ContLabel != null) _labels[node.ContLabel] = contL;
                _enc.MarkLabel(beginL); GenStmt(node.Then); _enc.MarkLabel(contL);
                GenExpr(node.Cond); Booleanize(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brtrue, beginL); Pop(); _enc.MarkLabel(endL);
                return;
            }
            case NodeKind.Switch:
            {
                GenExpr(node.Cond);
                // Store condition in a scratch local to avoid stack leak at case labels
                int switchLocal = GetOrAddScratchLocal(node.Cond.Ty);
                _enc.StoreLocal(switchLocal); Pop();

                var endL = _enc.DefineLabel();
                if (node.BrkLabel != null) _labels[node.BrkLabel] = endL;
                for (Node n = node.CaseNext; n != null; n = n.CaseNext)
                {
                    var caseL = _enc.DefineLabel();
                    _labels[n.Label] = caseL;
                    _enc.LoadLocal(switchLocal); Push();
                    if (node.Cond.Ty.Kind == TypeKind.Long || node.Cond.Ty.Size == 8)
                        _enc.LoadConstantI8(n.Begin);
                    else
                        _enc.LoadConstantI4((int)n.Begin);
                    Push();
                    if (n.Begin == n.End)
                    {
                        _enc.Branch(ILOpCode.Beq, caseL); Pop(2);
                    }
                    else
                    {
                        // Range case: n.Begin ... n.End
                        // subtract Begin, compare unsigned <= (End - Begin)
                        _enc.OpCode(ILOpCode.Sub); Pop();
                        if (node.Cond.Ty.Kind == TypeKind.Long || node.Cond.Ty.Size == 8)
                            _enc.LoadConstantI8(n.End - n.Begin);
                        else
                            _enc.LoadConstantI4((int)(n.End - n.Begin));
                        Push();
                        _enc.Branch(ILOpCode.Ble_un, caseL); Pop(2);
                    }
                }
                if (node.DefaultCase != null)
                {
                    var defL = _enc.DefineLabel();
                    _labels[node.DefaultCase.Label] = defL;
                    _enc.Branch(ILOpCode.Br, defL);
                }
                else
                {
                    _enc.Branch(ILOpCode.Br, endL);
                }
                GenStmt(node.Then); _enc.MarkLabel(endL);
                return;
            }
            case NodeKind.Case:
                if (_labels.TryGetValue(node.Label, out var caseLH)) _enc.MarkLabel(caseLH);
                GenStmt(node.Lhs); return;
            case NodeKind.Block:
                for (Node n = node.Body; n != null; n = n.Next) GenStmt(n); return;
            case NodeKind.Goto:
                if (!_labels.TryGetValue(node.UniqueLabel, out var gotoT)) { gotoT = _enc.DefineLabel(); _labels[node.UniqueLabel] = gotoT; }
                _enc.Branch(ILOpCode.Br, gotoT); return;
            case NodeKind.GotoExpr:
                Util.ErrorTok(node.Tok, "computed goto not supported in MSIL"); return;
            case NodeKind.Label:
                if (!_labels.TryGetValue(node.UniqueLabel, out var labelT)) { labelT = _enc.DefineLabel(); _labels[node.UniqueLabel] = labelT; }
                _enc.MarkLabel(labelT); GenStmt(node.Lhs); return;
            case NodeKind.Return:
                if (node.Lhs != null)
                {
                    GenExpr(node.Lhs);
                    // For struct/union returns, GenExpr leaves an address on the stack
                    // but ret expects a value type. Use ldobj to load the value.
                    CType retTy = node.Lhs.Ty;
                    if ((retTy.Kind == TypeKind.Struct || retTy.Kind == TypeKind.Union) &&
                        _structTypeDefs.TryGetValue(retTy, out var retTypeDef))
                    {
                        _enc.OpCode(ILOpCode.Ldobj);
                        _enc.Token(retTypeDef);
                        // ldobj pops address, pushes value — net 0
                    }
                    _enc.OpCode(ILOpCode.Ret); Pop();
                }
                else _enc.OpCode(ILOpCode.Ret);
                return;
            case NodeKind.ExprStmt:
                if (node.Lhs == null) return;
                {
                    int depthBefore = _stackDepth;
                    GenExpr(node.Lhs);
                    // Pop any leftover values to restore stack-neutral state
                    while (_stackDepth > depthBefore) { _enc.OpCode(ILOpCode.Pop); Pop(); }
                }
                return;
            case NodeKind.Asm: Util.ErrorTok(node.Tok, "inline assembly not supported in MSIL"); return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public byte[] Generate(Obj prog, string outputPath)
    {
        string objName = Path.GetFileName(outputPath ?? "output.obj");
        _md = new MetadataBuilder();
        _coffHeader = new CoffHeaderBuilder(Machine.Amd64, 0);
        _symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);
        _ilStreamBuilder = new BlobBuilder();
        _ilRelocBuilder = new BlobBuilder();
        _dataStream = new BlobBuilder();
        _dataRelocs = new BlobBuilder();

        _codeviewSymbols = new CodeViewSymbolBuilder(_coffHeader);
        _codeviewSymbols.AddObjNameAndCompile3(objName,
            language: CodeViewLanguage.C, machine: CodeViewMachine.Amd64,
            feMajor: 1, feMinor: 0, feBuild: 0, beMajor: 1, beMinor: 0, beBuild: 0,
            "chibicc MSIL",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        CFile[] files = _tokenizer.GetInputFiles();
        if (files.Length > 0)
        {
            string sf = files[0].Name;
            byte[] sh; try { sh = SHA256.HashData(File.ReadAllBytes(sf)); } catch { sh = new byte[32]; }
            _cvFile = _codeviewSymbols.GetOrAddFile(sf, CodeViewChecksumType.SHA256, sh);
        }
        else _cvFile = _codeviewSymbols.GetOrAddFile("unknown.c");

        _bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            _ilStreamBuilder, _ilRelocBuilder, _symtab, _coffHeader, _codeviewSymbols);

        RegisterMetadata(prog, objName);
        EmitFunctions(prog);

        var coffBuilder = new ManagedCoffBuilder(_coffHeader, new MetadataRootBuilder(_md), _symtab,
            _codeviewSymbols, _ilStreamBuilder, _ilRelocBuilder,
            _dataStream.Count > 0 ? _dataStream : null, _dataRelocs.Count > 0 ? _dataRelocs : null);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
