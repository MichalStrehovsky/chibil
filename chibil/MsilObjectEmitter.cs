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
    private readonly BclBinder _binder;

    private MetadataBuilder _md;
    private ManagedAggregateRegistry _aggregates;
    private CoffHeaderBuilder _coffHeader;
    private ManagedCoffSymbolTableBuilder _symtab;
    private CodeViewSymbolBuilder _codeviewSymbols;
    private readonly Dictionary<string, CodeViewFileHandle> _codeViewFiles = new(StringComparer.Ordinal);
    private RelocatableMethodBodyStreamEncoder _bodyEncoder;

    private CoffSectionWithContentBuilder _ilSection;
    private CoffSectionWithContentBuilder _dataSection;
    private CoffSectionWithContentBuilder _rdataSection;
    private List<CoffSectionBuilder> _comdatSections;
    private UninitializedCoffSectionBuilder _bssSection;

    private TypeDefinitionHandle _moduleTypeDef;

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
    private readonly Dictionary<string, UnepSlot> _unepSlots = new();

    // __CxxPureMSILEntry state
    private Obj _mainObj;
    private MethodDefinitionHandle _cxxPureMsilEntry;
    private string _cxxPureMsilEntryMangledName;
    private Obj _cxxPureMsilEntryObj;

    // Architecture helpers derived from DataModel
    private int PtrSize => _dm.PointerSize;
    internal bool Is32 => _dm.PointerSize == 4;
    private string SymPrefix => Is32 ? "_" : "";
    private Machine TargetMachine => Is32 ? Machine.I386 : Machine.Amd64; // LP64: add ARM64
    private CodeViewMachine CvMachine => Is32 ? CodeViewMachine.I386 : CodeViewMachine.Amd64;

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
        _binder = new BclBinder(this);
    }

    internal BclBinder Binder => _binder;

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
            _binder.GetValueTypeRef(),
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

    internal MemberReferenceHandle AddMemberReference(EntityHandle parent, string name, byte[] signature) =>
        _md.AddMemberReference(
            parent,
            _md.GetOrAddString(name),
            _md.GetOrAddBlob(signature));

    internal AssemblyReferenceHandle AddAssemblyReference(string name, Version version, byte[] publicKeyToken) =>
        _md.AddAssemblyReference(
            _md.GetOrAddString(name),
            version,
            default,
            publicKeyToken is null ? default : _md.GetOrAddBlob(publicKeyToken),
            default,
            default);

    private static void Verify<THandle>(THandle actual, THandle predicted, string rowKind, string name)
        where THandle : struct, IEquatable<THandle>
    {
        if (!actual.Equals(predicted))
            throw new InvalidOperationException(
                $"{rowKind} handle mismatch for '{name}': predicted {predicted}, got {actual}");
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
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(_binder.GetIsConstRef()));
        }
        if (ty.IsVolatile)
        {
            sig.WriteByte((byte)SignatureTypeCode.RequiredModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(_binder.GetIsVolatileRef()));
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
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(_binder.GetIsSignUnspecifiedByteRef()));
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
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(_binder.GetIsLongRef()));
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
                sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(_binder.GetIsLongRef()));
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
                CallConv.Cdecl => _binder.GetCallConvCdeclRef(),
                CallConv.Stdcall => _binder.GetCallConvStdcallRef(),
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

        // -ffunction-sections: give each function its own COMDAT .text$mn. The
        // section-definition symbol must precede the function symbol in the symbol
        // table, and the pre-registration binds the function symbol to this section,
        // so both must happen here (before IL emission).
        CoffSectionBuilder textSection = _ilSection;
        if (_options.OptFunctionSections)
        {
            var fnSection = new CoffSectionWithContentBuilder(
                ".text$mn",
                SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute |
                    SectionCharacteristics.ContainsCode | SectionCharacteristics.Align16Bytes,
                CoffComdatSelection.NoDuplicates);
            var cv = new CodeViewSymbolBuilder(_coffHeader);
            var debugSection = new CodeViewSectionBuilder(cv, fnSection);
            _comdatSections.Add(fnSection);
            _comdatSections.Add(debugSection);
            _functionSections[fn] = new FunctionComdatSections(fnSection, cv);
            _symtab.AddComdatSectionSymbol(fnSection);
            _symtab.AddComdatSectionSymbol(debugSection);
            textSection = fnSection;
        }
        _symtab.PreRegisterFunctionClrToken(textSection, mangledName, methodDef);

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
            _cxxPureMsilEntryObj = entryFn;
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
        return _unepSlots[fn.Name].Field;
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

        var unepSection = new CoffSectionWithContentBuilder(
            ".rdata",
            SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | CoffSectionBuilder.AlignmentCharacteristics(PtrSize),
            CoffComdatSelection.Any);
        _comdatSections.Add(unepSection);

        unepSection.Content.WriteBytes(0, PtrSize);

        _unepSlots[fn.Name] = new UnepSlot(unepField, unepSection);
        _symtab.AddComdatSectionSymbol(unepSection);
        _symtab.AddDataClrToken(unepName, unepField, unepSection, 0, out _, isExternal: true);

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

        // MemberRef for .ctor(string)
        var ctorRef = _binder.GetDecoratedNameCtorRef();

        _md.AddCustomAttribute(target, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    private void RegisterGlobalField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, _types.FlexibleAggregateStorageType(g.Ty));

        string fieldName;
        if (g.IsStringLiteral && _options.OptDataSections)
        {
            // Pooled string literal: the field/symbol name is the content-derived
            // ??_C@ name the parser already assigned.
            fieldName = g.Name;
        }
        else if (g.StaticLocalFn != null)
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
        // MemberRef for .ctor()
        var ctorRef = _binder.GetNativeCppClassCtorRef();

        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteUInt16(0x0000); // NumNamed

        _md.AddCustomAttribute(handle, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    internal CodeViewFileHandle GetCodeViewFile(Token tok)
    {
        string fileName = tok.FileName
            ?? throw new InvalidOperationException("Token is missing a CodeView file name.");

        if (_codeViewFiles.TryGetValue(fileName, out CodeViewFileHandle handle))
            return handle;

        // #line can remap the debugger-visible name without changing the
        // physical tokenized file; don't attach a checksum for a different file.
        if (fileName == tok.File.Name && File.Exists(tok.File.Name))
        {
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(tok.File.Name));
            handle = _codeviewSymbols.GetOrAddFile(fileName, CodeViewChecksumType.SHA256, sourceHash);
        }
        else
        {
            handle = _codeviewSymbols.GetOrAddFile(fileName, CodeViewChecksumType.None, Array.Empty<byte>());
        }

        _codeViewFiles.Add(fileName, handle);
        return handle;
    }

    /// <summary>Pick the method-body encoder for a function: its own per-function
    /// COMDAT section under -ffunction-sections, otherwise the shared `.text$mn`.</summary>
    private RelocatableMethodBodyStreamEncoder GetBodyEncoder(Obj fn)
        => _options.OptFunctionSections
            ? new RelocatableMethodBodyStreamEncoder(
                _functionSections[fn].Text, _symtab, _coffHeader, _functionSections[fn].CodeView)
            : _bodyEncoder;

    private void EmitFunctions(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            CompiledMethod body = CodeGen.EmitFunction(_types, this, fn, _options.Optimize);

            // Finalize method body
            var methodDef = _methodDefs[fn];
            string mangledName = _nameMangler.MangleFunctionName(fn);

            GetBodyEncoder(fn).AddMethodBody(methodDef, mangledName, body.Instructions,
                body.MaxStack, body.LocalVariables, attributes: MethodBodyAttributes.InitLocals,
                debugName: fn.Name,
                localSlots: body.LocalDebugInfo,
                localScopes: body.LocalScopes);
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

        GetBodyEncoder(_cxxPureMsilEntryObj).AddMethodBody(_cxxPureMsilEntry, _cxxPureMsilEntryMangledName, enc,
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

            if (fn.Ty.CallConv != CallConv.Clrcall && _unepSlots.ContainsKey(fn.Name))
            {
                EmitUnepSlot(fn, bareSym);
            }
        }

        // Emit ADDR relocs for extern __unep@ fields (not defined in this TU)
        foreach (var (funcName, unepSlot) in _unepSlots)
        {
            if (_nepBareNameSymbols.ContainsKey(funcName)) continue; // already handled by local NEP

            // Create an undefined external bare-name symbol — linker resolves from defining TU
            var externBareSym = _symtab.AddUndefinedExternalSymbol(SymPrefix + funcName, CoffSymbolType.Null);
            new CoffRelocationEncoder(_coffHeader, unepSlot.Section.Relocations)
                .AddAddressRelocation(0, externBareSym);
        }
    }

    /// <summary>
    /// Emit NEP machinery for a single method: __mep@ slot, thunk, bare-name alias, ilfixup.
    /// </summary>
    private CoffSymbolHandle EmitNepForMethod(int methodToken, string bareName, string mangledSuffix)
    {
        var bareSym = ClrIjw.EmitComdatNepMachinery(
            TargetMachine, PtrSize, SymPrefix,
            _coffHeader, _symtab,
            _comdatSections,
            methodToken, bareName, mangledSuffix);
        _nepBareNameSymbols[bareName] = bareSym;
        return bareSym;
    }

    private void EmitUnepSlot(Obj fn, CoffSymbolHandle bareSym)
    {
        if (!_unepSlots.TryGetValue(fn.Name, out var unepSlot)) return;

        // ADDR relocation to the bare-name NEP thunk symbol
        new CoffRelocationEncoder(_coffHeader, unepSlot.Section.Relocations)
            .AddAddressRelocation(0, bareSym);
    }

    private readonly record struct UnepSlot(FieldDefinitionHandle Field, CoffSectionWithContentBuilder Section);

    private readonly record struct FunctionComdatSections(
        CoffSectionWithContentBuilder Text,
        CodeViewSymbolBuilder CodeView);

    /// <summary>Maps global Obj name → COFF data symbol handle for relocation targeting.</summary>
    private readonly Dictionary<string, CoffSymbolHandle> _dataCoffSymbols = new();

    /// <summary>When -ffunction-sections is on, each function body lives in its own
    /// COMDAT `.text$mn` with an associative `.debug$S` COMDAT. The module-wide
    /// `_codeviewSymbols` keeps S_OBJNAME/S_COMPILE3 and the shared file/string tables.</summary>
    private readonly Dictionary<Obj, FunctionComdatSections> _functionSections = new();

    /// <summary>Records where each initialized global's bytes were written, so the
    /// relocation pass can target the correct (possibly per-item COMDAT) section and
    /// offset instead of recomputing a cumulative merged-.data offset.</summary>
    private readonly Dictionary<Obj, (CoffSectionWithContentBuilder Section, int Offset)> _dataPlacement = new();

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

            // Pool string literals into their own read-only COMDAT under
            // -fdata-sections (MSVC /GF behavior, matching clang); otherwise they go
            // to the merged read-only section like before.
            bool pooledString = g.IsStringLiteral && _options.OptDataSections;
            // A pooled string is initialized data even when its bytes are all zero
            // (e.g. ""), so it must not be diverted to the zero-init .bss path.
            bool allZero = !g.IsStringLiteral && g.InitData != null && g.Rel == null &&
                Array.TrueForAll(g.InitData, b => b == 0);

            if (allZero && _options.OptDataSections)
            {
                var bss = new UninitializedCoffSectionBuilder(
                    ".bss",
                    SectionCharacteristics.ContainsUninitializedData | SectionCharacteristics.MemRead |
                        SectionCharacteristics.MemWrite | CoffSectionBuilder.AlignmentCharacteristics(g.Align),
                    CoffComdatSelection.Any);
                bss.Size = g.Ty.Size;
                _comdatSections.Add(bss);
                _symtab.AddComdatSectionSymbol(bss);
                var zeroSym = _symtab.AddDataClrToken(g.Name, fieldDef, bss, 0, out _, isExternal: !g.IsStatic && !g.IsLocal);
                _dataCoffSymbols[g.Name] = zeroSym;
            }
            else if (g.InitData != null && !allZero)
            {
                bool isExternal = pooledString || (!g.IsStatic && !g.IsLocal);
                CoffSectionWithContentBuilder section;
                int offset;

                if (_options.OptDataSections)
                {
                    // String literals fold across TUs (selection Any); other data
                    // items are unique per TU (NoDuplicates). Strings and const data
                    // are read-only (.rdata); everything else is writable (.data).
                    // For arrays the const qualifier sits on the element type, so
                    // walk through array nesting to the element before checking
                    // (matching MSVC, which puts const arrays in .rdata under /Gw).
                    CType elemTy = g.Ty;
                    while (elemTy.Kind == TypeKind.Array)
                        elemTy = elemTy.Base;
                    bool isReadOnly = g.IsStringLiteral || elemTy.IsConst || g.IsReadOnlyConst;
                    var selection = g.IsStringLiteral
                        ? CoffComdatSelection.Any
                        : CoffComdatSelection.NoDuplicates;
                    section = new CoffSectionWithContentBuilder(
                        isReadOnly ? ".rdata" : ".data",
                        SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead |
                            (isReadOnly ? 0 : SectionCharacteristics.MemWrite) | CoffSectionBuilder.AlignmentCharacteristics(g.Align),
                        selection);
                    _comdatSections.Add(section);
                    _symtab.AddComdatSectionSymbol(section);
                    offset = 0;
                }
                else
                {
                    section = g.IsStringLiteral || g.IsReadOnlyConst ? _rdataSection : _dataSection;

                    // Pad to required alignment
                    section.Content.Align(g.Align);

                    offset = section.Content.Count;
                }

                // Copy InitData, writing addends at relocation offsets
                byte[] data = (byte[])g.InitData.Clone();
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    if (rel.Addend != 0)
                        Util.WriteBuf(data, rel.Offset, rel.Addend, PtrSize);
                }
                section.Content.WriteBytes(data);

                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, section, offset, out _,
                    isExternal);
                _dataCoffSymbols[g.Name] = coffSym;
                _dataPlacement[g] = (section, offset);
            }
            else if (g.IsTentative && !g.IsStatic)
            {
                // External tentative → common symbol (linker allocates)
                var coffSym = _symtab.AddCommonDataClrToken(g.Name, fieldDef, g.Ty.Size, out _);
                _dataCoffSymbols[g.Name] = coffSym;
            }
            else
            {
                int bssOffset = AllocateMergedBss(g);
                bool isExternal = !g.IsTentative && !g.IsStatic && !g.IsLocal;
                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, _bssSection, bssOffset, out _,
                    isExternal);
                _dataCoffSymbols[g.Name] = coffSym;
            }
        }

    }

    private int AllocateMergedBss(Obj g)
    {
        int offset = Util.AlignTo(_bssSection.Size, g.Align);
        _bssSection.Size = offset + g.Ty.Size;
        return offset;
    }

    /// <summary>Write data relocations. Runs after NEP emission so
    /// bare-name symbols are available as relocation targets.</summary>
    private void EmitGlobalDataRelocations(Obj prog)
    {
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.ContainsKey(g)) continue;
            if (g.InitData == null) continue;
            if (g.Rel == null) continue;

            var placement = _dataPlacement[g];

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

                new CoffRelocationEncoder(_coffHeader, placement.Section.Relocations)
                    .AddAddressRelocation(placement.Offset + rel.Offset, targetSym);
            }
        }
    }

    public byte[] Generate(Obj prog, string objName)
    {
        _md = new MetadataBuilder();
        _coffHeader = new CoffHeaderBuilder(TargetMachine, 0);
        _symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        _ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        _dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        _rdataSection = new CoffSectionWithContentBuilder(".rdata", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);
        _comdatSections = new List<CoffSectionBuilder>();
        _bssSection = new UninitializedCoffSectionBuilder(".bss", SectionCharacteristics.ContainsUninitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);

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

        _bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            _ilSection, _symtab, _coffHeader, _codeviewSymbols);

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

        // Build COFF and serialize. Sections are only emitted when they carry
        // content, matching MSVC /clr reference objects (which omit even
        // .text$mn for translation units that define no functions).
        var sections = new List<CoffSectionBuilder>();
        if (_ilSection.Content.Count > 0) sections.Add(_ilSection);
        if (_dataSection.Content.Count > 0) sections.Add(_dataSection);
        if (_rdataSection.Content.Count > 0) sections.Add(_rdataSection);
        sections.AddRange(_comdatSections);
        if (_bssSection.Size > 0) sections.Add(_bssSection);

        var coffBuilder = new ManagedCoffBuilder(_coffHeader, new MetadataRootBuilder(_md), _symtab, _codeviewSymbols,
            sections);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
