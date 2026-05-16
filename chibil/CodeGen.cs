using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class CodeGen
{
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
    private readonly Tokenizer _tokenizer;
    private readonly DataModel _dm;

    // ─── TU-level state ──────────────────────────────────────────
    private MetadataBuilder _md;
    private CoffHeaderBuilder _coffHeader;
    private ManagedCoffSymbolTableBuilder _symtab;
    private CodeViewSymbolBuilder _codeviewSymbols;
    private CodeViewFileHandle _cvFile;
    private RelocatableMethodBodyStreamEncoder _bodyEncoder;

    private BlobBuilder _ilStreamBuilder, _ilRelocBuilder;
    private BlobBuilder _dataStream, _dataRelocs;
    private BlobBuilder _nepStream, _nepRelocs;
    private BlobBuilder _ilFixupStream, _ilFixupRelocs;
    private int _bssSize;

    private AssemblyReferenceHandle _mscorlibRef;
    private TypeDefinitionHandle _moduleTypeDef;

    // Lazy TypeRef handles (created on first use)
    private TypeReferenceHandle _callConvCdeclRef;
    private TypeReferenceHandle _callConvStdcallRef;
    private TypeReferenceHandle _isSignUnspecifiedByteRef;
    private TypeReferenceHandle _isConstRef;
    private TypeReferenceHandle _isVolatileRef;
    private TypeReferenceHandle _isLongRef;
    private TypeReferenceHandle _nativeCppClassAttrRef;
    private TypeReferenceHandle _valueTypeRef;
    private TypeReferenceHandle _interlockedRef;
    private bool _callConvCdeclCreated, _callConvStdcallCreated;
    private bool _isSignUnspecifiedByteCreated, _isConstCreated, _isVolatileCreated, _isLongCreated;
    private bool _nativeCppClassAttrCreated, _valueTypeCreated, _interlockedCreated;

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
    private MethodDefinitionHandle _mainMethod;
    private Obj _mainObj;
    private MethodDefinitionHandle _cxxPureMsilEntry;
    private bool _hasMain;

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

    // ─── Per-function state ──────────────────────────────────────
    private RelocatableInstructionEncoder _enc;
    private Obj _currentFn;
    private Dictionary<Obj, int> _localSlots;
    private Dictionary<Obj, int> _paramSlots;
    private List<(CType ty, int slot)> _scratchLocals;
    private int _scratchLocalBase;
    private int _maxStack, _stackDepth;
    private Dictionary<string, LabelHandle> _labels;
    private int _labelCount;
    private StandaloneSignatureHandle _localsSigHandle;

    public CodeGen(CompilerOptions options, Tokenizer tokenizer, TypeSystem types)
    {
        _options = options;
        _tokenizer = tokenizer;
        _types = types;
        _dm = options.DataModel;
    }

    private int Count() => _labelCount++;

    // ═══════════════════════════════════════════════════════════════
    //  Stack tracking
    // ═══════════════════════════════════════════════════════════════

    private void Push() { _stackDepth++; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Push(int n) { _stackDepth += n; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Pop() { Debug.Assert(_stackDepth > 0, "stack underflow"); _stackDepth--; }
    private void Pop(int n) { Debug.Assert(_stackDepth >= n, "stack underflow"); _stackDepth -= n; }

    // ═══════════════════════════════════════════════════════════════
    //  Lazy TypeRef accessors
    // ═══════════════════════════════════════════════════════════════

    private TypeReferenceHandle GetCallConvCdeclRef()
    {
        if (!_callConvCdeclCreated)
        {
            _callConvCdeclRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("CallConvCdecl"));
            _callConvCdeclCreated = true;
        }
        return _callConvCdeclRef;
    }

    private TypeReferenceHandle GetCallConvStdcallRef()
    {
        if (!_callConvStdcallCreated)
        {
            _callConvStdcallRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("CallConvStdcall"));
            _callConvStdcallCreated = true;
        }
        return _callConvStdcallRef;
    }

    private TypeReferenceHandle GetIsSignUnspecifiedByteRef()
    {
        if (!_isSignUnspecifiedByteCreated)
        {
            _isSignUnspecifiedByteRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsSignUnspecifiedByte"));
            _isSignUnspecifiedByteCreated = true;
        }
        return _isSignUnspecifiedByteRef;
    }

    private TypeReferenceHandle GetIsConstRef()
    {
        if (!_isConstCreated)
        {
            _isConstRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsConst"));
            _isConstCreated = true;
        }
        return _isConstRef;
    }

    private TypeReferenceHandle GetIsVolatileRef()
    {
        if (!_isVolatileCreated)
        {
            _isVolatileRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsVolatile"));
            _isVolatileCreated = true;
        }
        return _isVolatileRef;
    }

    private TypeReferenceHandle GetIsLongRef()
    {
        if (!_isLongCreated)
        {
            _isLongRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsLong"));
            _isLongCreated = true;
        }
        return _isLongRef;
    }

    private TypeReferenceHandle GetNativeCppClassAttrRef()
    {
        if (!_nativeCppClassAttrCreated)
        {
            _nativeCppClassAttrRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("NativeCppClassAttribute"));
            _nativeCppClassAttrCreated = true;
        }
        return _nativeCppClassAttrRef;
    }

    private TypeReferenceHandle GetValueTypeRef()
    {
        if (!_valueTypeCreated)
        {
            _valueTypeRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System"),
                _md.GetOrAddString("ValueType"));
            _valueTypeCreated = true;
        }
        return _valueTypeRef;
    }

    private TypeReferenceHandle GetInterlockedRef()
    {
        if (!_interlockedCreated)
        {
            _interlockedRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Threading"),
                _md.GetOrAddString("Interlocked"));
            _interlockedCreated = true;
        }
        return _interlockedRef;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type encoding: CType → MSIL signature bytes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Encode a C type into an MSIL signature using the builder directly.
    /// Uses raw byte writes for modopt/modreq since the BlobEncoder API
    /// doesn't support all patterns we need.
    /// </summary>
    private void EncodeType(BlobBuilder sig, CType ty)
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
                sig.WriteByte((byte)SignatureTypeCode.Pointer);
                EncodeType(sig, ty.Base);
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
                    string arrayName = MangleArrayTypeName(ty);
                    if (_arrayTypeDefs.TryGetValue(arrayName, out var arrayTd))
                    {
                        sig.WriteByte((byte)(SignatureTypeCode)0x11);
                        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(arrayTd));
                    }
                    else
                    {
                        // Shouldn't happen if PreAllocate ran correctly
                        sig.WriteByte((byte)SignatureTypeCode.Pointer);
                        EncodeType(sig, ty.Base);
                    }
                }
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
            {
                int typeId = GetTypeId(ty);
                if (_structTypeDefs.TryGetValue(typeId, out var structTd))
                {
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(structTd));
                }
                else
                {
                    // Forward-declared struct → TypeRef
                    string name = GetStructName(ty);
                    if (!_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
                    {
                        typeRef = _md.AddTypeReference(default, default, _md.GetOrAddString(name));
                        _forwardDeclTypeRefs[name] = typeRef;
                    }
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(typeRef));
                }
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
                // Native int for anything else (shouldn't happen)
                sig.WriteByte((byte)SignatureTypeCode.IntPtr);
                break;
        }
    }

    /// <summary>Encode an inline function pointer signature for FNPTR in method/local signatures.</summary>
    private void EncodeFnPtrSignature(BlobBuilder sig, CType funcTy)
    {
        // Calling convention byte
        if (funcTy.CallConv == CallConv.Clrcall)
            sig.WriteByte((byte)SignatureCallingConvention.Default);
        else
            sig.WriteByte((byte)SignatureCallingConvention.Unmanaged | (byte)SignatureCallingConvention.Default);

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

    /// <summary>Encode the return type for a function, with modopt(CallConvCdecl) for cdecl.</summary>
    private void EncodeReturnType(BlobBuilder sig, CType funcTy)
    {
        // For cdecl functions: modopt(CallConvCdecl) on return type
        if (funcTy.CallConv == CallConv.Cdecl)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetCallConvCdeclRef()));
        }
        else if (funcTy.CallConv == CallConv.Stdcall)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetCallConvStdcallRef()));
        }

        if (funcTy.ReturnTy.Kind == TypeKind.Void)
            sig.WriteByte((byte)SignatureTypeCode.Void);
        else
            EncodeType(sig, funcTy.ReturnTy);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type identity helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get a stable identity for a struct/union type for dedup.</summary>
    private static int GetTypeId(CType ty)
    {
        // Walk through Origin chain to find the canonical type
        CType canonical = ty;
        while (canonical.Origin != null) canonical = canonical.Origin;
        return RuntimeHelpers.GetHashCode(canonical);
    }

    private static string GetStructName(CType ty)
    {
        if (ty.Name != null)
            return Util.GetTokenText(ty.Name);
        // Anonymous struct — use a generated name
        return $"<anon_{GetTypeId(ty):X8}>";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Name mangling
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Produce an MSVC-compatible decorated name for a C function.
    /// Format: ?name@@$$J0YA(ret)(params)@Z  for cdecl
    ///         ?name@@$$J0YM(ret)(params)@Z  for __clrcall
    /// </summary>
    private string MangleFunctionName(Obj fn)
    {
        CType funcTy = fn.Ty;
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only reaches here on x86 (normalized to Cdecl on x64)
            _ => "A", // cdecl
        };
        // Static functions get TU-hash-scoped names to avoid cross-TU collisions
        string name = fn.IsStatic ? $"{fn.Name}_?A0x{_tuHash}" : fn.Name;
        var sb = new StringBuilder();
        sb.Append($"?{name}@@$$J0Y{cc}");
        MangleType(sb, funcTy.ReturnTy, isReturn: true);
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            MangleType(sb, p, isReturn: false);
            paramCount++;
        }
        if (paramCount == 0) sb.Append('X'); // void params
        if (funcTy.IsVariadic)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
        return sb.ToString();
    }

    private void MangleType(StringBuilder sb, CType ty, bool isReturn)
    {
        // Strip qualifiers for mangling
        switch (ty.Kind)
        {
            case TypeKind.Void: sb.Append('X'); break;
            case TypeKind.Bool: sb.Append("_N"); break;
            case TypeKind.Char:
                if (ty.IsUnsigned) sb.Append('E');
                else if (ty.Origin?.Kind == TypeKind.Char && !ty.IsUnsigned) sb.Append('D'); // plain char
                else sb.Append('D'); // default char is plain char
                break;
            case TypeKind.Short:
                sb.Append(ty.IsUnsigned ? 'G' : 'F');
                break;
            case TypeKind.Int:
            case TypeKind.Enum:
                sb.Append(ty.IsUnsigned ? 'I' : 'H');
                break;
            case TypeKind.Long:
                if (_dm.LongSize == 4)
                    sb.Append(ty.IsUnsigned ? 'K' : 'J');
                else
                    sb.Append(ty.IsUnsigned ? "_K" : "_J"); // LP64: long=8 bytes
                break;
            case TypeKind.LLong:
                sb.Append(ty.IsUnsigned ? "_K" : "_J");
                break;
            case TypeKind.Float: sb.Append('M'); break;
            case TypeKind.Double: sb.Append('N'); break;
            case TypeKind.LDouble: sb.Append("O"); break; // long double in MSVC mangling
            case TypeKind.Ptr:
                ManglePointer(sb, ty);
                break;
            case TypeKind.Array:
                // Array parameter decays to pointer
                ManglePointer(sb, _types.PointerTo(ty.Base));
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
                if (isReturn) sb.Append("?A");
                sb.Append('U');
                sb.Append(GetStructName(ty));
                sb.Append("@@");
                break;
            case TypeKind.Func:
                // Function pointer type
                MangleFuncPtr(sb, ty);
                break;
        }
    }

    private void ManglePointer(StringBuilder sb, CType ty)
    {
        string e = Is32 ? "" : "E"; // __ptr64 on 64-bit
        CType baseTy = ty.Base;

        if (baseTy.Kind == TypeKind.Func)
        {
            // Function pointer: P6A<ret><params>@Z or P6M<ret><params>@Z
            MangleFuncPtr(sb, baseTy);
            return;
        }

        // Determine const/volatile qualifier letter
        if (baseTy.IsConst && baseTy.IsVolatile) sb.Append($"P{e}D");
        else if (baseTy.IsConst) sb.Append($"P{e}B");
        else if (baseTy.IsVolatile) sb.Append($"P{e}C");
        else sb.Append($"P{e}A");

        MangleType(sb, baseTy, isReturn: false);
    }

    private void MangleFuncPtr(StringBuilder sb, CType funcTy)
    {
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only on x86
            _ => "A",
        };
        sb.Append($"P6{cc}");
        MangleType(sb, funcTy.ReturnTy, isReturn: false);
        int count = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            MangleType(sb, p, isReturn: false);
            count++;
        }
        if (count == 0) sb.Append('X');
        // Only real variadic (has explicit ...) uses ZZ terminator.
        // K&R empty-paren (IsVariadic=true, Params=null) is unprototyped, not variadic.
        if (funcTy.IsVariadic && funcTy.Params != null)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
    }

    /// <summary>MSVC number encoding for array dimensions.</summary>
    private static string EncodeNumber(int value)
    {
        if (value == 0) return "A@";
        if (value >= 1 && value <= 10) return ((char)('0' + value - 1)).ToString();
        // Hex encoding: nibbles A-P (A=0, P=15), MSB first, terminated by @
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, (char)('A' + (value & 0xF)));
            value >>= 4;
        }
        sb.Append('@');
        return sb.ToString();
    }

    /// <summary>
    /// Generate array TypeDef name: $ArrayType$$$BY(ndims)(bounds)(elemtype)
    /// </summary>
    private string MangleArrayTypeName(CType ty)
    {
        Debug.Assert(ty.Kind == TypeKind.Array);
        var sb = new StringBuilder("$ArrayType$$$BY");

        // Count dimensions
        int ndims = 0;
        var dims = new List<int>();
        CType cur = ty;
        while (cur.Kind == TypeKind.Array)
        {
            ndims++;
            dims.Add(cur.ArrayLen);
            cur = cur.Base;
        }
        sb.Append(EncodeNumber(ndims));
        foreach (int dim in dims)
            sb.Append(EncodeNumber(dim));

        // Element type code
        MangleType(sb, cur, isReturn: false);
        return sb.ToString();
    }

    private string MangleStaticLocalName(Obj fn, Obj var)
    {
        return $"?A0x{_tuHash}.?{var.Name}@?1??{fn.Name}@@9@9";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 1: Metadata Registration
    // ═══════════════════════════════════════════════════════════════

    private void RegisterMetadata(Obj prog, string objName)
    {
        // Phase 1: Pre-allocate struct/array TypeDefs
        PreAllocateStructTypeDefs(prog);

        // Phase 2: <Module> TypeDef (must be row 1)
        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class, default, _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        // Phase 3: Register functions
        RegisterFunctions(prog);

        // Phase 3b: Pre-register __unep@ fields for address-taken cdecl functions
        RegisterUnepFields(prog);

        // Phase 4: Register global fields
        RegisterGlobalFields(prog);

        // Phase 5: Materialize struct/array TypeDefs
        MaterializeStructTypeDefs();

        // Module row
        _md.AddModule(0, _md.GetOrAddString(objName), _md.GetOrAddGuid(Guid.NewGuid()), default, default);
    }

    // ─── Phase 1: Pre-allocate struct/array TypeDefs ──────────────
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
                    int id = GetTypeId(canonical);
                    if (!_structTypeDefs.ContainsKey(id))
                    {
                        // Reserve a predicted handle
                        var predictedHandle = MetadataTokens.TypeDefinitionHandle(_nextStructTypeDefRow++);
                        _structTypeDefs[id] = predictedHandle;
                        string name = GetStructName(canonical);
                        _pendingTypeDefs.Add((id, canonical, name));

                        // Recurse into member types
                        for (Member m = canonical.Members; m != null; m = m.Next)
                            PreAllocateFromType(m.Ty);
                    }
                }
                break;
            case TypeKind.Array:
                if (canonical.ArrayLen >= 0)
                {
                    string arrayName = MangleArrayTypeName(canonical);
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
        if (node.Ty != null) PreAllocateFromType(node.Ty);
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

    // ─── Phase 3: Register functions ─────────────────────────────

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
        bool isCdecl = funcTy.CallConv != CallConv.Clrcall;

        // Build method signature
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT calling convention

        // Parameter count
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        sig.WriteCompressedInteger(paramCount);

        // Return type
        EncodeReturnType(sig, funcTy);

        // Parameters
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);

        // Method attributes
        MethodAttributes attrs = MethodAttributes.Assembly | MethodAttributes.Static;
        if (isCdecl && !fn.IsStatic)
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
        string mangledName = MangleFunctionName(fn);
        _symtab.PreRegisterFunctionClrToken(mangledName, methodDef);

        // If this is main, register __CxxPureMSILEntry
        if (fn.Name == "main")
        {
            _hasMain = true;
            _mainMethod = methodDef;
            _mainObj = fn;
            RegisterCxxPureMSILEntry(fn);
        }
    }

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

        // Param 2: char** argv — Ptr Ptr modopt(IsSignUnspecifiedByte) I1
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
        sig.WriteByte((byte)SignatureTypeCode.SByte);

        // Param 3: char** envp — same encoding, but use '0' backreference in mangling
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

        string e = Is32 ? "" : "E";
        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _symtab.PreRegisterFunctionClrToken(mangledName, _cxxPureMsilEntry);
    }

    private void RegisterExternalFunction(Obj fn)
    {
        CType funcTy = fn.Ty;

        // Build MemberRef signature
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        sig.WriteCompressedInteger(paramCount);
        EncodeReturnType(sig, funcTy);
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);

        var memberRef = _md.AddMemberReference(
            _moduleTypeDef, _md.GetOrAddString(fn.Name), _md.GetOrAddBlob(sig));
        _externalFuncRefs[fn.Name] = memberRef;

        // Add DecoratedNameAttribute
        string mangledName = MangleFunctionName(fn);
        AddDecoratedNameAttribute(memberRef, mangledName);

        // Register external CLR token
        _symtab.AddExternalClrToken(mangledName, memberRef);
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
        var decoratedNameRef = _md.AddTypeReference(_mscorlibRef,
            _md.GetOrAddString("System.Runtime.CompilerServices"),
            _md.GetOrAddString("DecoratedNameAttribute"));

        // MemberRef for .ctor(string)
        var ctorSig = new BlobBuilder();
        ctorSig.WriteByte(0x20); // HASTHIS
        ctorSig.WriteCompressedInteger(1); // 1 param
        ctorSig.WriteByte((byte)SignatureTypeCode.Void); // return void
        ctorSig.WriteByte((byte)SignatureTypeCode.String); // param: string

        var ctorRef = _md.AddMemberReference(decoratedNameRef, _md.GetOrAddString(".ctor"), _md.GetOrAddBlob(ctorSig));

        _md.AddCustomAttribute(target, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    // ─── Phase 3b: Pre-register __unep@ fields ─────────────────────

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

            string mangledName = MangleFunctionName(fn);
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

    // ─── Phase 4: Register global fields ─────────────────────────

    private void RegisterGlobalFields(Obj prog)
    {
        // Pass A: definitions
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction || !g.IsDefinition) continue;
            RegisterGlobalField(g);
        }

        // Pass B: externs (not yet registered)
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
        if (g.IsLocal)
        {
            // Static local variable
            fieldName = MangleStaticLocalName(_currentFn ?? _mainObj, g);
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

        // FieldRVA table entry required when HasFieldRVA is set.
        // Actual RVA is 0 — resolved via COFF relocations at link time.
        if ((fieldAttrs & FieldAttributes.HasFieldRVA) != 0)
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

    // ─── Phase 5: Materialize struct/array TypeDefs ───────────────

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
                handle = _md.AddTypeDefinition(
                    TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
                    default, _md.GetOrAddString(name),
                    GetValueTypeRef(),
                    MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                    MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

                Debug.Assert(handle == predicted, $"Struct TypeDef handle mismatch: predicted {predicted}, got {handle}");
                _md.AddTypeLayout(handle, 0, (uint)type.Size);
            }

            // NativeCppClassAttribute
            AddNativeCppClassAttribute(handle);

            // <alignment member> field (on 64-bit targets)
            if (!Is32)
            {
                var alignFieldSig = new BlobBuilder();
                alignFieldSig.WriteByte(0x06); // FIELD
                // Use int64 if any member needs 8-byte alignment, else int32
                bool needs8 = type.Align >= 8 || (type.Kind == TypeKind.Array && type.Base.Size >= 8);
                alignFieldSig.WriteByte(needs8 ? (byte)SignatureTypeCode.Int64 : (byte)SignatureTypeCode.Int32);

                _md.AddFieldDefinition(
                    FieldAttributes.Private,
                    _md.GetOrAddString("<alignment member>"),
                    _md.GetOrAddBlob(alignFieldSig));
                _nextFieldRow++;
            }
        }
    }

    private void AddNativeCppClassAttribute(TypeDefinitionHandle handle)
    {
        var attrRef = GetNativeCppClassAttrRef();

        // MemberRef for .ctor()
        var ctorSig = new BlobBuilder();
        ctorSig.WriteByte(0x20); // HASTHIS
        ctorSig.WriteCompressedInteger(0);
        ctorSig.WriteByte((byte)SignatureTypeCode.Void);

        var ctorRef = _md.AddMemberReference(attrRef, _md.GetOrAddString(".ctor"), _md.GetOrAddBlob(ctorSig));

        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteUInt16(0x0000); // NumNamed

        _md.AddCustomAttribute(handle, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 2: IL Emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitFunctions(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            EmitFunction(fn);
        }
    }

    private void EmitFunction(Obj fn)
    {
        _currentFn = fn;
        _enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
        _localSlots = new Dictionary<Obj, int>();
        _paramSlots = new Dictionary<Obj, int>();
        _scratchLocals = new List<(CType, int)>();
        _maxStack = 0;
        _stackDepth = 0;
        _labels = new Dictionary<string, LabelHandle>();
        _labelCount = 0;

        // Assign parameter slots
        int argIdx = 0;
        for (Obj param = fn.Params; param != null; param = param.Next)
            _paramSlots[param] = argIdx++;

        // Assign local slots for user locals
        int localIdx = 0;
        for (Obj local = fn.Locals; local != null; local = local.Next)
        {
            if (local.IsLocal && !_paramSlots.ContainsKey(local))
            {
                if (local == fn.AllocaBottom) continue; // skip alloca bottom
                if (local == fn.VaArea) continue; // skip va_area
                _localSlots[local] = localIdx++;
            }
        }
        _scratchLocalBase = localIdx;

        // Emit function body
        GenStmt(fn.Body);

        // Epilogue — fallthrough return
        if (_labels.TryGetValue($".L.return.{fn.Name}", out var retLabel))
            _enc.MarkLabel(retLabel);

        if (fn.Ty.ReturnTy.Kind != TypeKind.Void)
        {
            EmitDefaultValue(fn.Ty.ReturnTy);
        }
        _enc.OpCode(ILOpCode.Ret);

        // Build locals signature
        int totalLocals = _scratchLocalBase + _scratchLocals.Count;
        StandaloneSignatureHandle localsSig = default;
        if (totalLocals > 0)
        {
            var localsSigBlob = new BlobBuilder();
            var enc = new BlobEncoder(localsSigBlob).LocalVariableSignature(totalLocals);

            // User locals
            for (Obj local = fn.Locals; local != null; local = local.Next)
            {
                if (_localSlots.ContainsKey(local))
                    EncodeLocalType(enc.AddVariable().Type(), local.Ty);
            }

            // Scratch locals
            foreach (var (ty, _) in _scratchLocals)
                EncodeLocalType(enc.AddVariable().Type(), ty);

            localsSig = _md.AddStandaloneSignature(_md.GetOrAddBlob(localsSigBlob));
        }
        _localsSigHandle = localsSig;

        // Build CodeView local slot info
        var localSlotList = new List<CodeViewManSlot>();
        foreach (var (local, slot) in _localSlots)
        {
            if (local.Name != null && localsSig != default)
            {
                localSlotList.Add(new CodeViewManSlot(slot,
                    MetadataTokens.GetToken(localsSig), local.Name));
            }
        }

        // Finalize method body
        var methodDef = _methodDefs[fn];
        string mangledName = MangleFunctionName(fn);

        _bodyEncoder.AddMethodBody(methodDef, mangledName, _enc,
            maxStack: _maxStack, localVariablesSignature: localsSig, attributes: MethodBodyAttributes.InitLocals,
            debugName: fn.Name,
            localSlots: localSlotList.Count > 0 ? localSlotList.ToArray() : null);

        _currentFn = null;
    }

    private void EncodeLocalType(SignatureTypeEncoder enc, CType ty)
    {
        // Encode using the builder directly
        EncodeType(enc.Builder, ty);
    }

    private void EmitDefaultValue(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push(); break;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push(); break;
            case TypeKind.LLong:
                _enc.LoadConstantI8(0); Push(); break;
            case TypeKind.Struct:
            case TypeKind.Union:
                // For struct return, push a zeroed struct
                int scratch = GetOrAddScratchLocal(ty);
                _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)scratch); Push();
                _enc.OpCode(ILOpCode.Initobj); _enc.Token(GetStructTypeHandle(ty)); Pop();
                _enc.LoadLocal(scratch); Push();
                break;
            default:
                _enc.OpCode(ILOpCode.Ldc_i4_0); Push(); break;
        }
    }

    private EntityHandle GetStructTypeHandle(CType ty)
    {
        int typeId = GetTypeId(ty);
        if (_structTypeDefs.TryGetValue(typeId, out var handle))
            return handle;
        // Forward-declared
        string name = GetStructName(ty);
        if (_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
            return typeRef;
        return default;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scratch locals
    // ═══════════════════════════════════════════════════════════════

    private int GetOrAddScratchLocal(CType ty)
    {
        // Reuse existing scratch local of same type.
        // For struct/union/array, require exact type identity (TypeDef handle match)
        // since IL verifier requires assignment-compatible value types.
        foreach (var (existingTy, slot) in _scratchLocals)
        {
            if (existingTy.Kind == ty.Kind && existingTy.Size == ty.Size &&
                existingTy.IsUnsigned == ty.IsUnsigned)
            {
                // Struct/union/array: only reuse if same canonical type
                if (ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union || ty.Kind == TypeKind.Array)
                {
                    if (GetTypeId(existingTy) == GetTypeId(ty))
                        return slot;
                    continue; // different struct type, keep looking
                }
                return slot;
            }
        }
        int newSlot = _scratchLocalBase + _scratchLocals.Count;
        _scratchLocals.Add((ty, newSlot));
        return newSlot;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Address generation (GenAddr)
    // ═══════════════════════════════════════════════════════════════

    private void GenAddr(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.Ty.Kind == TypeKind.Vla)
                {
                    // VLA pointer — load the stored pointer
                    LoadLocalOrParam(node.Var);
                    return;
                }
                if (node.Var.IsFunction || node.Var.Ty.Kind == TypeKind.Func)
                {
                    // &func — emit function address (same as GenExpr Var for functions)
                    EmitFunctionAddress(node.Var, node.Tok);
                    return;
                }
                if (node.Var.IsLocal)
                {
                    if (_paramSlots.TryGetValue(node.Var, out int argIdx))
                    {
                        _enc.OpCode(ILOpCode.Ldarga_s); _enc.CodeBuilder.WriteByte((byte)argIdx); Push();
                    }
                    else if (_localSlots.TryGetValue(node.Var, out int localIdx))
                    {
                        _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)localIdx); Push();
                    }
                    return;
                }
                // Global variable
                if (_fieldDefs.TryGetValue(node.Var, out var fieldDef))
                {
                    _enc.OpCode(ILOpCode.Ldsflda); _enc.Token(fieldDef); Push();
                }
                else if (_globalFieldsByName.TryGetValue(node.Var.Name, out var fieldDef2))
                {
                    _enc.OpCode(ILOpCode.Ldsflda); _enc.Token(fieldDef2); Push();
                }
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                return;

            case NodeKind.Comma:
                GenExpr(node.Lhs); Pop(); // discard LHS value
                GenAddr(node.Rhs);
                return;

            case NodeKind.Member:
                GenAddr(node.Lhs);
                if (node.Member.Offset != 0)
                {
                    EmitConstI4(node.Member.Offset);
                    _enc.OpCode(ILOpCode.Add); Pop();
                }
                return;

            case NodeKind.FunCall:
                // Struct-returning call — evaluate, spill to scratch, return address
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                {
                    GenExpr(node);
                    int scratch = GetOrAddScratchLocal(node.Ty);
                    _enc.StoreLocal(scratch); Pop();
                    _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)scratch); Push();
                    return;
                }
                break;

            case NodeKind.Assign:
            case NodeKind.Cond:
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                {
                    GenExpr(node);
                    // Spill to scratch local
                    int scratch = GetOrAddScratchLocal(node.Ty);
                    _enc.StoreLocal(scratch); Pop();
                    _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)scratch); Push();
                    return;
                }
                break;

            case NodeKind.VlaPtr:
                if (_localSlots.TryGetValue(node.Var, out int vlaSlot))
                {
                    _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)vlaSlot); Push();
                }
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
            case TypeKind.Array:
            case TypeKind.Func:
            case TypeKind.Vla:
                // Address IS the value
                return;
            case TypeKind.Struct:
            case TypeKind.Union:
                _enc.OpCode(ILOpCode.Ldobj); _enc.Token(GetStructTypeHandle(ty));
                // Stack effect: pop ptr, push struct (net 0)
                return;
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Ldind_r4); return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Ldind_r8); return;
        }

        // Integer types
        if (ty.Size == 1)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u1 : ILOpCode.Ldind_i1);
        else if (ty.Size == 2)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u2 : ILOpCode.Ldind_i2);
        else if (ty.Size == 4)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u4 : ILOpCode.Ldind_i4);
        else
            _enc.OpCode(ILOpCode.Ldind_i8);
        // Stack effect: pop ptr, push value (net 0)
    }

    private void Store(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                _enc.OpCode(ILOpCode.Stobj); _enc.Token(GetStructTypeHandle(ty));
                Pop(2); // pops address + value
                return;
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Stind_r4); Pop(2); return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Stind_r8); Pop(2); return;
        }

        if (ty.Size == 1) _enc.OpCode(ILOpCode.Stind_i1);
        else if (ty.Size == 2) _enc.OpCode(ILOpCode.Stind_i2);
        else if (ty.Size == 4) _enc.OpCode(ILOpCode.Stind_i4);
        else _enc.OpCode(ILOpCode.Stind_i8);
        Pop(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helper: Load local or parameter
    // ═══════════════════════════════════════════════════════════════

    private void LoadLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.LoadArgument(argIdx); Push();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.LoadLocal(localIdx); Push();
        }
    }

    private void StoreLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.OpCode(ILOpCode.Starg_s); _enc.CodeBuilder.WriteByte((byte)argIdx); Pop();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.StoreLocal(localIdx); Pop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant loading helpers
    // ═══════════════════════════════════════════════════════════════

    private void EmitConstI4(int value)
    {
        _enc.LoadConstantI4(value); Push();
    }

    private void EmitConstI4(long value)
    {
        _enc.LoadConstantI4((int)value); Push();
    }

    private void EmitConstI8(long value)
    {
        _enc.LoadConstantI8(value); Push();
    }

    /// <summary>Emit conv.i8 for pointer arithmetic widening on 64-bit.</summary>
    private void ConvI8IfNeeded()
    {
        if (!Is32) _enc.OpCode(ILOpCode.Conv_i8);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch normalization helpers
    // ═══════════════════════════════════════════════════════════════

    private void NormalizeToBranchable(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
            case TypeKind.Double:
            case TypeKind.LDouble:
            case TypeKind.LLong:
            case TypeKind.Long when _dm.LongSize == 8:
                EmitTypedZero(ty);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                break;
        }
    }

    private void EmitTypedZero(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                return;
            case TypeKind.LLong:
                EmitConstI8(0);
                return;
            case TypeKind.Long:
                // LP64: long is 8 bytes = int64
                if (_dm.LongSize == 8) { EmitConstI8(0); return; }
                EmitConstI4(0);
                return;
            default:
                EmitConstI4(0);
                return;
        }
    }

    private static bool IsAggregateType(CType ty) =>
        ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union || ty.Kind == TypeKind.Array;

    /// <summary>Push a callable function address onto the evaluation stack.</summary>
    private void EmitFunctionAddress(Obj fn, Token tok = null)
    {
        CType funcTy = fn.Ty;
        if (funcTy.CallConv == CallConv.Clrcall)
        {
            if (_methodDefs.TryGetValue(fn, out var md))
            {
                _enc.OpCode(ILOpCode.Ldftn); _enc.Token(md); Push();
            }
            else
            {
                Util.ErrorTok(tok ?? fn.Tok, "cannot take address of external __clrcall function");
            }
        }
        else
        {
            // cdecl: load the native function pointer from __unep@ field
            if (_unepFields.TryGetValue(fn.Name, out var unepField))
            {
                _enc.OpCode(ILOpCode.Ldsfld); _enc.Token(unepField); Push();
            }
            else
            {
                Util.ErrorTok(tok ?? fn.Tok, $"cannot take address of cdecl function '{fn.Name}' — __unep@ field not registered");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression code generation (GenExpr)
    // ═══════════════════════════════════════════════════════════════

    private void GenExpr(Node node)
    {
        // Mark line number for debug info
        if (node.Tok?.File != null)
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);

        switch (node.Kind)
        {
            case NodeKind.NullExpr: return;

            case NodeKind.Num:
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float:
                        _enc.LoadConstantR4((float)node.FVal); Push(); return;
                    case TypeKind.Double:
                    case TypeKind.LDouble:
                        _enc.LoadConstantR8(node.FVal); Push(); return;
                    case TypeKind.LLong:
                        EmitConstI8(node.Val); return;
                    default:
                        if (node.Ty.Kind == TypeKind.Long && _dm.LongSize == 8)
                            EmitConstI8(node.Val);
                        else
                            EmitConstI4(node.Val);
                        return;
                }

            case NodeKind.Neg:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Neg);
                return;

            case NodeKind.Var:
                if (node.Ty.Kind == TypeKind.Func || node.Var.IsFunction)
                {
                    EmitFunctionAddress(node.Var, node.Tok);
                    return;
                }
                if (node.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    // Simple scalar local/param — use direct load
                    LoadLocalOrParam(node.Var);
                    return;
                }
                GenAddr(node);
                Load(node.Ty);
                return;

            case NodeKind.Member:
                GenAddr(node);
                Load(node.Ty);
                if (node.Member.IsBitfield)
                {
                    int shift = (node.Member.Ty.Size * 8) - node.Member.BitWidth - node.Member.BitOffset;
                    if (shift > 0)
                    {
                        EmitConstI4(shift);
                        _enc.OpCode(ILOpCode.Shl); Pop();
                    }
                    int rightShift = (node.Member.Ty.Size * 8) - node.Member.BitWidth;
                    if (rightShift > 0)
                    {
                        EmitConstI4(rightShift);
                        _enc.OpCode(node.Member.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop();
                    }
                }
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                Load(node.Ty);
                return;

            case NodeKind.Addr:
                GenAddr(node.Lhs);
                return;

            case NodeKind.Assign:
                // Handle bitfield assignment
                if (node.Lhs.Kind == NodeKind.Member && node.Lhs.Member.IsBitfield)
                {
                    GenBitfieldAssign(node);
                    return;
                }
                // Optimize: direct store to local/param for simple scalars
                if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    GenExpr(node.Rhs);
                    _enc.OpCode(ILOpCode.Dup); Push();
                    StoreLocalOrParam(node.Lhs.Var);
                    return;
                }
                GenAddr(node.Lhs);
                GenExpr(node.Rhs);
                // dup + stloc scratch, store, ldloc scratch
                int assignScratch = GetOrAddScratchLocal(node.Ty);
                _enc.OpCode(ILOpCode.Dup); Push();
                _enc.StoreLocal(assignScratch); Pop();
                Store(node.Ty);
                _enc.LoadLocal(assignScratch); Push();
                return;

            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                {
                    if (n.Next == null && n.Kind == NodeKind.ExprStmt)
                    {
                        // Last expression in statement expression — its value IS the result.
                        GenExpr(n.Lhs);
                    }
                    else
                    {
                        GenStmt(n);
                    }
                }
                return;

            case NodeKind.Comma:
            {
                int depthBeforeComma = _stackDepth;
                GenExpr(node.Lhs);
                // Discard LHS result if it pushed anything
                while (_stackDepth > depthBeforeComma)
                {
                    _enc.OpCode(ILOpCode.Pop); Pop();
                }
                GenExpr(node.Rhs);
                return;
            }

            case NodeKind.Cast:
                GenExpr(node.Lhs);
                EmitCast(node.Lhs.Ty, node.Ty);
                return;

            case NodeKind.MemZero:
                if (_localSlots.TryGetValue(node.Var, out int mzSlot))
                {
                    _enc.OpCode(ILOpCode.Ldloca_s); _enc.CodeBuilder.WriteByte((byte)mzSlot); Push();
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                else
                {
                    GenAddr(new Node { Kind = NodeKind.Var, Var = node.Var, Tok = node.Tok, Ty = node.Var.Ty });
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                return;

            case NodeKind.Cond:
            {
                int savedDepth = _stackDepth;
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(elseLabel);
                GenExpr(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.Not:
                GenExpr(node.Lhs);
                EmitTypedZero(node.Lhs.Ty);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;

            case NodeKind.BitNot:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Not);
                return;

            case NodeKind.LogAnd:
            {
                int savedDepth = _stackDepth;
                var falseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                NormalizeToBranchable(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                NormalizeToBranchable(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseLabel); Pop();
                _stackDepth = savedDepth;
                EmitConstI4(1);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(falseLabel);
                EmitConstI4(0);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.LogOr:
            {
                int savedDepth = _stackDepth;
                var trueLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                NormalizeToBranchable(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                NormalizeToBranchable(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueLabel); Pop();
                _stackDepth = savedDepth;
                EmitConstI4(0);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(trueLabel);
                EmitConstI4(1);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.FunCall:
                GenFunCall(node);
                return;

            case NodeKind.LabelVal:
                Util.ErrorTok(node.Tok, "labels-as-values not supported in MSIL");
                return;

            case NodeKind.Cas:
                GenCas(node);
                return;

            case NodeKind.Exch:
                GenExch(node);
                return;
        }

        // Binary operations
        GenExpr(node.Lhs);
        GenExpr(node.Rhs);

        switch (node.Kind)
        {
            case NodeKind.Add: _enc.OpCode(ILOpCode.Add); Pop(); return;
            case NodeKind.Sub: _enc.OpCode(ILOpCode.Sub); Pop(); return;
            case NodeKind.Mul: _enc.OpCode(ILOpCode.Mul); Pop(); return;
            case NodeKind.Div:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Div_un : ILOpCode.Div); Pop(); return;
            case NodeKind.Mod:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Rem_un : ILOpCode.Rem); Pop(); return;
            case NodeKind.BitAnd: _enc.OpCode(ILOpCode.And); Pop(); return;
            case NodeKind.BitOr: _enc.OpCode(ILOpCode.Or); Pop(); return;
            case NodeKind.BitXor: _enc.OpCode(ILOpCode.Xor); Pop(); return;
            case NodeKind.Shl: _enc.OpCode(ILOpCode.Shl); Pop(); return;
            case NodeKind.Shr:
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop(); return;
            case NodeKind.Eq: _enc.OpCode(ILOpCode.Ceq); Pop(); return;
            case NodeKind.Ne:
                _enc.OpCode(ILOpCode.Ceq); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case NodeKind.Lt:
                // clt already returns 0 for NaN (unordered), which is correct for C's
                // "NaN < x is false". Only Le needs the _un variant (via inverted cgt.un).
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Clt_un : ILOpCode.Clt); Pop(); return;
            case NodeKind.Le:
                // a <= b  ≡  !(a > b)  ≡  (cgt_un == 0) for unsigned/float
                // For floats, must use Cgt_un so NaN comparisons return unordered=1→false
                _enc.OpCode((node.Lhs.Ty.IsUnsigned || TypeSystem.IsFlonum(node.Lhs.Ty))
                    ? ILOpCode.Cgt_un : ILOpCode.Cgt); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
        }
        Util.ErrorTok(node.Tok, "invalid expression");
    }

    // ─── Function call ───────────────────────────────────────────

    private void GenFunCall(Node node)
    {
        CType funcTy = node.FuncTy;
        bool isIndirect = node.Lhs.Kind != NodeKind.Var || !node.Lhs.Var.IsFunction;

        // Check for alloca
        if (!isIndirect && node.Lhs.Var.Name == "alloca")
        {
            GenExpr(node.Args);
            _enc.OpCode(ILOpCode.Localloc);
            // Stack: size → ptr (net 0)
            return;
        }

        // Push arguments
        int argCount = 0;
        for (Node arg = node.Args; arg != null; arg = arg.Next)
        {
            GenExpr(arg);
            argCount++;
        }

        if (isIndirect)
        {
            // Indirect call — push function pointer, then calli
            GenExpr(node.Lhs);

            // Build standalone signature for calli
            var calliSig = new BlobBuilder();
            if (funcTy.CallConv == CallConv.Clrcall)
                calliSig.WriteByte((byte)SignatureCallingConvention.Default);
            else
                calliSig.WriteByte((byte)SignatureCallingConvention.Unmanaged | (byte)SignatureCallingConvention.Default);

            int paramCount = 0;
            for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
            calliSig.WriteCompressedInteger(paramCount);

            // Return type
            if (funcTy.ReturnTy.Kind == TypeKind.Void)
                calliSig.WriteByte((byte)SignatureTypeCode.Void);
            else
                EncodeType(calliSig, funcTy.ReturnTy);

            // Params
            for (CType p = funcTy.Params; p != null; p = p.Next)
                EncodeType(calliSig, p);

            var calliSigHandle = _md.AddStandaloneSignature(_md.GetOrAddBlob(calliSig));
            _enc.CallIndirect(calliSigHandle);
            Pop(argCount + 1); // pop args + function pointer
        }
        else
        {
            // Direct call
            string targetName = node.Lhs.Var.Name;
            if (_methodDefs.TryGetValue(node.Lhs.Var, out var methodDef))
            {
                _enc.Call(methodDef);
            }
            else if (_externalFuncRefs.TryGetValue(targetName, out var memberRef))
            {
                _enc.Call(memberRef);
            }
            else
            {
                // Register on the fly (might be a forward reference)
                RegisterExternalFunction(node.Lhs.Var);
                _enc.Call(_externalFuncRefs[targetName]);
            }
            Pop(argCount);
        }

        // Push return value if non-void
        if (funcTy.ReturnTy.Kind != TypeKind.Void)
            Push();
    }

    // ─── Atomic operations ───────────────────────────────────────

    private void GenCas(Node node)
    {
        // __atomic_compare_exchange_n → Interlocked.CompareExchange(ref, value, comparand)
        // Returns bool: true if exchange happened
        GenExpr(node.CasAddr);  // address
        GenExpr(node.CasNew);   // desired value
        GenExpr(node.CasOld);   // Load old value from *old_ptr
        Load(node.CasOld.Ty.Base);

        // Call Interlocked.CompareExchange(ref int, int, int)
        var interlocked = GetInterlockedRef();
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT
        sig.WriteCompressedInteger(3);
        sig.WriteByte((byte)SignatureTypeCode.Int32); // return
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Int32); // ref param
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        var cxchgRef = _md.AddMemberReference(interlocked,
            _md.GetOrAddString("CompareExchange"), _md.GetOrAddBlob(sig));
        _enc.Call(cxchgRef);
        Pop(2); // 3 args → 1 result

        // Compare result with comparand to get bool
        GenExpr(node.CasOld);
        Load(node.CasOld.Ty.Base);
        _enc.OpCode(ILOpCode.Ceq); Pop();
    }

    private void GenExch(Node node)
    {
        // __atomic_exchange_n → Interlocked.Exchange(ref int, int)
        GenExpr(node.Lhs); // address
        GenExpr(node.Rhs); // new value

        var interlocked = GetInterlockedRef();
        var sig = new BlobBuilder();
        sig.WriteByte(0x00);
        sig.WriteCompressedInteger(2);
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        var xchgRef = _md.AddMemberReference(interlocked,
            _md.GetOrAddString("Exchange"), _md.GetOrAddBlob(sig));
        _enc.Call(xchgRef);
        Pop(); // 2 args → 1 result
    }

    // ─── Bitfield assignment ─────────────────────────────────────

    private void GenBitfieldAssign(Node node)
    {
        Member mem = node.Lhs.Member;
        GenAddr(node.Lhs);

        // Save address for later store
        _enc.OpCode(ILOpCode.Dup); Push();

        GenExpr(node.Rhs);

        // Save the truncated value for the expression result
        long mask = (1L << mem.BitWidth) - 1;
        int assignScratch = GetOrAddScratchLocal(node.Ty);
        _enc.OpCode(ILOpCode.Dup); Push();
        _enc.StoreLocal(assignScratch); Pop();

        // Mask and shift new value into position
        EmitConstI4(mask);
        _enc.OpCode(ILOpCode.And); Pop();
        if (mem.BitOffset > 0)
        {
            EmitConstI4(mem.BitOffset);
            _enc.OpCode(ILOpCode.Shl); Pop();
        }

        // Load old value, mask out old bits, OR in new bits
        // Stack: addr, shifted_new
        // We need: addr, (old & ~field_mask) | shifted_new
        // Duplicate addr, load old value
        // This requires reordering; use scratch
        int newValScratch = GetOrAddScratchLocal(_types.TyInt);
        _enc.StoreLocal(newValScratch); Pop();
        _enc.OpCode(ILOpCode.Dup); Push(); // dup addr
        Load(mem.Ty); // load old value

        long clearMask = ~(mask << mem.BitOffset);
        EmitConstI4(clearMask);
        _enc.OpCode(ILOpCode.And); Pop();
        _enc.LoadLocal(newValScratch); Push();
        _enc.OpCode(ILOpCode.Or); Pop();

        Store(node.Ty);

        _enc.LoadLocal(assignScratch); Push();
    }

    // ─── Type cast ───────────────────────────────────────────────

    private void EmitCast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) { if (from.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); } return; }
        if (to.Kind == TypeKind.Bool)
        {
            // Non-zero → 1, zero → 0
            switch (from.Kind)
            {
                case TypeKind.Float:
                case TypeKind.Double:
                case TypeKind.LDouble:
                    if (from.Kind == TypeKind.Float)
                        _enc.LoadConstantR4(0.0f);
                    else
                        _enc.LoadConstantR8(0.0);
                    Push();
                    _enc.OpCode(ILOpCode.Ceq); Pop();
                    EmitConstI4(0);
                    _enc.OpCode(ILOpCode.Ceq); Pop();
                    break;
                default:
                    EmitConstI4(0);
                    if (from.Kind == TypeKind.Ptr) _enc.OpCode(ILOpCode.Conv_i);
                    else if (from.Size == 8) _enc.OpCode(ILOpCode.Conv_i8);
                    _enc.OpCode(ILOpCode.Cgt_un); Pop();
                    break;
            }
            return;
        }

        // From float/double
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsInteger(to))
        {
            if (to.Size <= 4)
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u4 : ILOpCode.Conv_i4);
            else
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (TypeSystem.IsInteger(from) && TypeSystem.IsFlonum(to))
        {
            if (from.IsUnsigned)
            {
                // conv.r.un interprets the stack value as unsigned for all integer sizes
                _enc.OpCode(ILOpCode.Conv_r_un);
                if (to.Kind == TypeKind.Float)
                    _enc.OpCode(ILOpCode.Conv_r4); // conv.r.un produces float64, narrow to float32
            }
            else if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsFlonum(to))
        {
            if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }

        // Integer → integer
        if (to.Kind == TypeKind.Ptr)
        {
            // Pointer: use conv.i/conv.u (native int) — correct for both 32-bit and 64-bit
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u : ILOpCode.Conv_i);
            return;
        }
        if ((to.Kind == TypeKind.Long && _dm.LongSize == 8) || to.Kind == TypeKind.LLong)
        {
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (to.Size == 1)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u1 : ILOpCode.Conv_i1);
        else if (to.Size == 2)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u2 : ILOpCode.Conv_i2);
        else if (to.Size == 4 && from.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i4);
        // If same size, no conv needed
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statement code generation (GenStmt)
    // ═══════════════════════════════════════════════════════════════

    private void GenStmt(Node node)
    {
        if (node.Tok?.File != null)
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);

        switch (node.Kind)
        {
            case NodeKind.If:
            {
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseLabel); Pop();
                GenStmt(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _enc.MarkLabel(elseLabel);
                if (node.Els != null) GenStmt(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.For:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = _enc.DefineLabel();
                var brkLabel = _enc.DefineLabel();
                if (node.ContLabel != null) _labels[node.ContLabel] = contLabel;
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
                if (node.Cond != null)
                {
                    GenExpr(node.Cond);
                    NormalizeToBranchable(node.Cond.Ty);
                    _enc.Branch(ILOpCode.Brfalse, brkLabel); Pop();
                }
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                if (node.Inc != null)
                {
                    int incDepth = _stackDepth;
                    GenExpr(node.Inc);
                    while (_stackDepth > incDepth) { _enc.OpCode(ILOpCode.Pop); Pop(); }
                }
                _enc.Branch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Do:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = _enc.DefineLabel();
                var brkLabel = _enc.DefineLabel();
                if (node.ContLabel != null) _labels[node.ContLabel] = contLabel;
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                _enc.MarkLabel(beginLabel);
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brtrue, beginLabel); Pop();
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Switch:
            {
                var brkLabel = _enc.DefineLabel();
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                // x64: always if/else chain (no IL switch)
                GenExpr(node.Cond);
                int condScratch = GetOrAddScratchLocal(node.Cond.Ty);
                _enc.StoreLocal(condScratch); Pop();

                for (Node c = node.CaseNext; c != null; c = c.CaseNext)
                {
                    var caseLabel = _enc.DefineLabel();
                    _labels[c.Label] = caseLabel;
                    bool is64 = node.Cond.Ty.Size == 8;

                    if (c.Begin == c.End)
                    {
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.Branch(ILOpCode.Beq, caseLabel); Pop(2);
                    }
                    else
                    {
                        // Range case: val - begin <= (end - begin)
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.OpCode(ILOpCode.Sub); Pop();
                        if (is64) EmitConstI8(c.End - c.Begin); else EmitConstI4(c.End - c.Begin);
                        _enc.Branch(ILOpCode.Ble_un, caseLabel); Pop(2);
                    }
                }

                if (node.DefaultCase != null)
                {
                    var defaultLabel = _enc.DefineLabel();
                    _labels[node.DefaultCase.Label] = defaultLabel;
                    _enc.Branch(ILOpCode.Br, defaultLabel);
                }
                else
                {
                    _enc.Branch(ILOpCode.Br, brkLabel);
                }

                GenStmt(node.Then);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Case:
                if (_labels.TryGetValue(node.Label, out var cLabel))
                    _enc.MarkLabel(cLabel);
                GenStmt(node.Lhs);
                return;

            case NodeKind.Block:
                for (Node n = node.Body; n != null; n = n.Next)
                    GenStmt(n);
                return;

            case NodeKind.Goto:
                if (!_labels.TryGetValue(node.UniqueLabel, out var gotoTarget))
                {
                    gotoTarget = _enc.DefineLabel();
                    _labels[node.UniqueLabel] = gotoTarget;
                }
                _enc.Branch(ILOpCode.Br, gotoTarget);
                return;

            case NodeKind.GotoExpr:
                Util.ErrorTok(node.Tok, "computed goto not supported in MSIL");
                return;

            case NodeKind.Label:
                if (!_labels.TryGetValue(node.UniqueLabel, out var labelTarget))
                {
                    labelTarget = _enc.DefineLabel();
                    _labels[node.UniqueLabel] = labelTarget;
                }
                _enc.MarkLabel(labelTarget);
                GenStmt(node.Lhs);
                return;

            case NodeKind.Return:
                if (node.Lhs != null)
                {
                    GenExpr(node.Lhs);
                    Pop(); // ret consumes
                }
                _enc.OpCode(ILOpCode.Ret);
                return;

            case NodeKind.ExprStmt:
            {
                int depthBefore = _stackDepth;
                GenExpr(node.Lhs);
                // Pop any leftover value to maintain stack neutrality
                while (_stackDepth > depthBefore)
                {
                    _enc.OpCode(ILOpCode.Pop); Pop();
                }
                return;
            }

            case NodeKind.Asm:
                Util.ErrorTok(node.Tok, "inline assembly not supported in MSIL");
                return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }

    // ═══════════════════════════════════════════════════════════════
    //  __CxxPureMSILEntry body emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitCxxPureMSILEntry()
    {
        if (!_hasMain) return;

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

        enc.Call(_mainMethod);

        // If main returns void, push 0
        if (_mainObj.Ty.ReturnTy.Kind == TypeKind.Void)
            enc.OpCode(ILOpCode.Ldc_i4_0);

        enc.OpCode(ILOpCode.Ret);

        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _bodyEncoder.AddMethodBody(_cxxPureMsilEntry, mangledName, enc,
            maxStack: Math.Max(mainParamCount, 1), localVariablesSignature: default, attributes: MethodBodyAttributes.InitLocals,
            debugName: "__CxxPureMSILEntry");
    }

    // ═══════════════════════════════════════════════════════════════
    //  NEP machinery emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitNepMachinery(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            var methodDef = _methodDefs[fn];
            string mangledName = MangleFunctionName(fn);

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

        // NEP for __CxxPureMSILEntry
        if (_hasMain)
        {
            string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
            EmitNepForMethod(
                MetadataTokens.GetToken(_cxxPureMsilEntry), "__CxxPureMSILEntry", mangledName);
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
    /// Inlined from ClrIjw.EmitNepMachinery to avoid cross-project dependency.
    /// </summary>
    private CoffSymbolHandle EmitNepForMethod(int methodToken, string bareName, string mangledSuffix)
    {
        // (1) __mep@?fn slot in .data
        int slotOffset = _dataStream.Count;
        for (int i = 0; i < PtrSize; i++) _dataStream.WriteByte(0);

        var mepDataSym = _symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, LogicalSection.Data, slotOffset);
        var tokenSym = _symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(_coffHeader, _dataRelocs).AddTokenRelocation(slotOffset, tokenSym);

        // (2) NEP thunk: FF 25 [rel32] indirect jump
        int thunkOffset = _nepStream.Count;
        _nepStream.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
        _nepRelocs.WriteInt32(thunkOffset + 2);
        _nepRelocs.WriteInt32(mepDataSym._value);
        _nepRelocs.WriteUInt16(Is32 ? (ushort)0x0006 : (ushort)0x0004); // DIR32 / REL32

        // (3) Bare-name COFF alias
        var bareSym = _symtab.AddExternalDataSymbol(SymPrefix + bareName, LogicalSection.Nep, thunkOffset);
        _nepBareNameSymbols[bareName] = bareSym;

        // (4) ILFixup entry
        int ilfixupOffset = _ilFixupStream.Count;
        _ilFixupStream.WriteInt32(0); // RVA placeholder
        _ilFixupStream.WriteInt16(1); // Count
        _ilFixupStream.WriteInt16(Is32 ? (short)0x0009 : (short)0x000A);
        new CoffRelocationEncoder(_coffHeader, _ilFixupRelocs).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);

        return bareSym;
    }

    private void EmitUnepSlot(Obj fn, CoffSymbolHandle bareSym)
    {
        if (!_unepSlotOffsets.TryGetValue(fn.Name, out int slotOffset)) return;

        // ADDR relocation to the bare-name NEP thunk symbol
        new CoffRelocationEncoder(_coffHeader, _dataRelocs)
            .AddAddressRelocation(slotOffset, bareSym);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Global data emission
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Maps __unep@ field name → pre-allocated offset in .data for the slot.</summary>
    private readonly Dictionary<string, int> _unepSlotOffsets = new();

    /// <summary>Maps global Obj name → COFF data symbol handle for relocation targeting.</summary>
    private readonly Dictionary<string, CoffSymbolHandle> _dataCoffSymbols = new();

    /// <summary>Phase A: Write data bytes and register COFF data token symbols.
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
                int offset = _dataStream.Count;

                // Copy InitData, writing addends at relocation offsets
                byte[] data = (byte[])g.InitData.Clone();
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    if (rel.Addend != 0)
                        Util.WriteBuf(data, rel.Offset, rel.Addend, PtrSize);
                }
                _dataStream.WriteBytes(data);

                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Data, offset, out _);
                _dataCoffSymbols[g.Name] = coffSym;
            }
            else if (g.IsTentative)
            {
                var coffSym = _symtab.AddCommonDataClrToken(g.Name, fieldDef, g.Ty.Size, out _);
                _dataCoffSymbols[g.Name] = coffSym;
            }
            else
            {
                int bssOffset = _bssSize;
                _bssSize = Util.AlignTo(_bssSize + g.Ty.Size, g.Align);
                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Bss, bssOffset, out _);
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

            string mangledName = MangleFunctionName(fn);
            string unepName = $"__unep@{mangledName}";

            int slotOffset = _dataStream.Count;
            for (int i = 0; i < PtrSize; i++) _dataStream.WriteByte(0);
            _unepSlotOffsets[funcName] = slotOffset;

            _symtab.AddDataClrToken(unepName, unepField, LogicalSection.Data, slotOffset, out _);
        }
    }

    /// <summary>Phase B: Write data relocations. Runs after NEP emission so
    /// bare-name symbols are available as relocation targets.</summary>
    private void EmitGlobalDataRelocations(Obj prog)
    {
        // Track cumulative offset through .data to match what Phase A wrote
        int dataOffset = 0;
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.ContainsKey(g)) continue;
            if (g.InitData == null) continue;

            int offset = dataOffset;
            dataOffset += g.InitData.Length;

            for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
            {
                string targetName = rel.Label();
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

    // ═══════════════════════════════════════════════════════════════
    //  Scan for address-taken functions
    // ═══════════════════════════════════════════════════════════════

    private void ScanAddressTaken(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            ScanAddressTakenNode(fn.Body);

            // Also check global initializers that reference functions
            for (Obj g = prog; g != null; g = g.Next)
            {
                if (g.IsFunction) continue;
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    string label = rel.Label();
                    _addressTakenFuncs.Add(label);
                }
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

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public byte[] Generate(Obj prog, string objName, string sourceFile)
    {
        _md = new MetadataBuilder();
        _coffHeader = new CoffHeaderBuilder(TargetMachine, 0);
        _symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        _ilStreamBuilder = new BlobBuilder();
        _ilRelocBuilder = new BlobBuilder();
        _dataStream = new BlobBuilder();
        _dataRelocs = new BlobBuilder();
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

        // Pass 1: Metadata
        RegisterMetadata(prog, objName);

        // Global data bytes + COFF token registration — BEFORE IL emission
        EmitGlobalDataBytesAndTokens(prog);

        // Pass 2: IL Emission
        EmitFunctions(prog);

        // Post-pass: __CxxPureMSILEntry
        EmitCxxPureMSILEntry();

        // NEP machinery (creates bare-name symbols for functions)
        EmitNepMachinery(prog);

        // Global data relocations — AFTER NEP so bare-name symbols exist
        EmitGlobalDataRelocations(prog);

        // Build COFF and serialize
        var coffBuilder = new ManagedCoffBuilder(_coffHeader, new MetadataRootBuilder(_md), _symtab, _codeviewSymbols,
            _ilStreamBuilder, _ilRelocBuilder,
            dataStream: _dataStream, dataRelocs: _dataRelocs,
            ilFixupStream: _ilFixupStream, ilFixupRelocs: _ilFixupRelocs,
            nepStream: _nepStream, nepRelocs: _nepRelocs,
            bssSize: _bssSize);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
