using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

using Coff;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class MsilObjectEmitter
{
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
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

    // Metadata references and emitted definitions
    private readonly Dictionary<Obj, MemberReferenceHandle> _objectRefs = new();
    private readonly Dictionary<Obj, string> _globalFieldNames = new();
    private readonly List<Obj> _globalsWithRelocations = new();

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
        TypeSystem types,
        NameMangler nameMangler,
        ManagedAggregateModel aggregateModel)
    {
        _options = options;
        _types = types;
        _dm = options.DataModel;
        _nameMangler = nameMangler;
        _aggregateModel = aggregateModel;
        _binder = new BclBinder(this);
    }

    internal BclBinder Binder => _binder;

    public StandaloneSignatureHandle AddStandaloneSignature(BlobBuilder blob)
        => _md.AddStandaloneSignature(_md.GetOrAddBlob(blob));

    internal BlobBuilder CreateFieldSignature(CType type)
    {
        var signature = new BlobBuilder();
        signature.WriteByte(0x06); // FIELD
        EncodeType(signature, type);
        return signature;
    }

    internal TypeDefinitionHandle AddAggregateTypeDefinition(
        TypeAttributes attributes,
        string name,
        ushort packingSize,
        uint size)
    {
        var handle = _md.AddTypeDefinition(
            attributes,
            default,
            _md.GetOrAddString(name),
            _binder.GetValueTypeRef(),
            MetadataTokens.FieldDefinitionHandle(_md.GetRowCount(TableIndex.Field) + 1),
            MetadataTokens.MethodDefinitionHandle(_md.GetRowCount(TableIndex.MethodDef) + 1));

        _md.AddTypeLayout(handle, packingSize, size);
        AddNativeCppClassAttribute(handle);
        return handle;
    }

    internal FieldDefinitionHandle AddAggregateFieldDefinition(
        FieldAttributes attributes,
        string name,
        BlobBuilder signature,
        int? offset)
    {
        var handle = _md.AddFieldDefinition(
            attributes,
            _md.GetOrAddString(name),
            _md.GetOrAddBlob(signature));

        if (offset is int fieldOffset)
            _md.AddFieldLayout(handle, fieldOffset);
        return handle;
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

    internal void EnsureMemberRefTokenSymbol(MemberReferenceHandle handle, bool isFunction) =>
        _symtab.GetOrAddUndefinedClrTokenSymbol(
            handle,
            isFunction ? CoffSymbolType.Function : CoffSymbolType.Null);

    internal AssemblyReferenceHandle AddAssemblyReference(string name, Version version, byte[] publicKeyToken) =>
        _md.AddAssemblyReference(
            _md.GetOrAddString(name),
            version,
            default,
            publicKeyToken is null ? default : _md.GetOrAddBlob(publicKeyToken),
            default,
            default);

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
                    EntityHandle arrayTd = _aggregates.GetTypeHandle(ty);
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(arrayTd));
                }
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
                {
                    EntityHandle structHandle = _aggregates.GetTypeHandle(ty);
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

    public EntityHandle GetStructTypeHandle(CType ty)
        => _aggregates.GetTypeHandle(ty);

    public ManagedAggregateRepresentationKind GetAggregateRepresentationKind(CType ty) =>
        _aggregateModel.GetRepresentationKind(ty);

    public ManagedAggregateMemberAccessKind GetMemberAccessKind(CType owner, Member member) =>
        _aggregateModel.GetMemberAccessKind(owner, member);

    public EntityHandle GetAggregateFieldToken(CType owner, Member member) =>
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
            MetadataTokens.ParameterHandle(_md.GetRowCount(TableIndex.Param) + 1));

        // Add parameter rows
        int paramIdx = 1;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            string paramName = parameterNames != null && paramIdx <= parameterNames.Length
                ? parameterNames[paramIdx - 1]
                : p.Name != null ? Util.GetTokenText(p.Name) : $"_a{paramIdx}";
            _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString(paramName), paramIdx);
            paramIdx++;
        }

        // -ffunction-sections: give each function its own COMDAT .text$mn.
        // Its section-definition symbol must precede the function symbol.
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
        }
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
        => GetObjectReference(fn);

    public EntityHandle GetFieldToken(Obj var)
        => GetObjectReference(var);

    public FieldDefinitionHandle GetOrCreateUnepFieldToken(Obj fn)
    {
        Debug.Assert(fn.Ty.CallConv != CallConv.Clrcall);
        return _unepSlots.TryGetValue(fn.Name, out UnepSlot slot)
            ? slot.Field
            : RegisterUnepField(fn);
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

        CoffSymbolHandle target = fn.IsDefinition
            ? GetOrCreateNepForDefinition(fn)
            : _symtab.AddUndefinedExternalSymbol(SymPrefix + fn.Name, CoffSymbolType.Null);
        new CoffRelocationEncoder(_coffHeader, unepSection.Relocations)
            .AddAddressRelocation(0, target);

        return unepField;
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

    private void FinalizeReferencedFunctionSymbols()
    {
        foreach (Obj obj in _objectRefs.Keys)
        {
            if (obj.IsFunction)
                _symtab.AddUndefinedExternalSymbol(_nameMangler.MangleFunctionName(obj));
        }
    }

    private string GetGlobalFieldMetadataName(Obj g)
    {
        if (_globalFieldNames.TryGetValue(g, out string name))
            return name;

        name = g.IsStringLiteral && _options.OptDataSections
            ? g.Name
            : g.StaticLocalFn != null
                ? _nameMangler.MangleStaticLocalName(g)
                : g.IsAnonymous
                    ? _nameMangler.GenerateAnonymousGlobalName()
                    : g.IsStatic
                        ? _nameMangler.MangleStaticGlobalName(g.Name)
                        : g.Name;
        _globalFieldNames.Add(g, name);
        return name;
    }

    private FieldDefinitionHandle RegisterGlobalField(Obj g)
    {
        BlobBuilder fieldSig = CreateFieldSignature(_types.FlexibleAggregateStorageType(g.Ty));

        string fieldName = GetGlobalFieldMetadataName(g);

        FieldAttributes fieldAttrs = FieldAttributes.Assembly | FieldAttributes.Static;

        // All global definitions get HasFieldRVA — even tentative (common) definitions
        // and zero-initialized globals. The COFF symbol table determines whether
        // the symbol is section-bound (.data/.bss) or common (Sect=0, Value=size).
        fieldAttrs |= FieldAttributes.HasFieldRVA;

        var fieldDef = _md.AddFieldDefinition(fieldAttrs,
            _md.GetOrAddString(fieldName), _md.GetOrAddBlob(fieldSig));

        _md.AddFieldRelativeVirtualAddress(fieldDef, 0);
        return fieldDef;
    }

    private MemberReferenceHandle GetObjectReference(Obj obj)
    {
        if (_objectRefs.TryGetValue(obj, out MemberReferenceHandle memberRef))
            return memberRef;

        BlobBuilder signature;
        string metadataName;
        if (obj.IsFunction)
        {
            signature = new BlobBuilder();
            EncodeFunctionSignature(signature, obj.Ty);
            metadataName = obj.Name;
        }
        else
        {
            signature = CreateFieldSignature(_types.FlexibleAggregateStorageType(obj.Ty));
            metadataName = GetGlobalFieldMetadataName(obj);
        }

        memberRef = _md.AddMemberReference(
            _moduleTypeDef,
            _md.GetOrAddString(metadataName),
            _md.GetOrAddBlob(signature));
        _objectRefs.Add(obj, memberRef);

        if (obj.IsFunction)
            AddDecoratedNameAttribute(memberRef, _nameMangler.MangleFunctionName(obj));
        EnsureMemberRefTokenSymbol(memberRef, obj.IsFunction);
        return memberRef;
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

    private void EmitObjects(Obj prog)
    {
        for (Obj obj = prog; obj != null; obj = obj.Next)
        {
            if (!obj.IsFunction)
            {
                if (!obj.IsDefinition)
                    continue;

                FieldDefinitionHandle fieldDef = RegisterGlobalField(obj);
                EmitGlobalDataBytesAndToken(obj, fieldDef);
                if (obj.Rel != null)
                    _globalsWithRelocations.Add(obj);
                continue;
            }

            if (!obj.IsDefinition || !obj.IsLive)
                continue;

            MethodDefinitionHandle methodDef = RegisterFunction(obj);
            GetOrCreateNepForDefinition(obj);
            CompiledMethod body = CodeGen.EmitFunction(_types, this, obj, _options.Optimize);

            // Finalize method body
            string mangledName = _nameMangler.MangleFunctionName(obj);

            GetBodyEncoder(obj).AddMethodBody(methodDef, mangledName, body.Instructions,
                body.MaxStack, body.LocalVariables, attributes: MethodBodyAttributes.InitLocals,
                debugName: obj.Name,
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

        enc.Call(GetObjectReference(_mainObj));

        // If main returns void, push 0
        if (_mainObj.Ty.ReturnTy.Kind == TypeKind.Void)
            enc.OpCode(ILOpCode.Ldc_i4_0);

        enc.OpCode(ILOpCode.Ret);

        GetBodyEncoder(_cxxPureMsilEntryObj).AddMethodBody(_cxxPureMsilEntry, _cxxPureMsilEntryMangledName, enc,
            maxStack: Math.Max(mainParamCount, 1), localVariablesSignature: default, attributes: MethodBodyAttributes.InitLocals,
            debugName: "__CxxPureMSILEntry");
    }

    private CoffSymbolHandle GetOrCreateNepForDefinition(Obj fn)
    {
        string bareName = _nameMangler.MangleFunctionBaseName(fn);
        if (_nepBareNameSymbols.TryGetValue(bareName, out CoffSymbolHandle bareSym))
            return bareSym;

        MemberReferenceHandle methodRef = GetObjectReference(fn);
        bareSym = EmitNepForMethod(
            methodRef,
            bareName,
            _nameMangler.MangleFunctionName(fn));
        _nepBareNameSymbols.Add(bareName, bareSym);

        // Relocation labels use the source name for static functions.
        if (fn.IsStatic)
            _nepBareNameSymbols.TryAdd(fn.Name, bareSym);
        return bareSym;
    }

    /// <summary>
    /// Emit NEP machinery for a single method: __mep@ slot, thunk, bare-name alias, ilfixup.
    /// </summary>
    private CoffSymbolHandle EmitNepForMethod(EntityHandle methodToken, string bareName, string mangledSuffix)
    {
        var bareSym = ClrIjw.EmitComdatNepMachinery(
            TargetMachine, PtrSize, SymPrefix,
            _coffHeader, _symtab,
            _comdatSections,
            methodToken, bareName, mangledSuffix);
        return bareSym;
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

    /// <summary>Write data bytes and register COFF data token symbols.</summary>
    private void EmitGlobalDataBytesAndToken(Obj g, FieldDefinitionHandle fieldDef)
    {
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
                // walk through array nesting to the element before checking.
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
                section.Content.Align(g.Align);
                offset = section.Content.Count;
            }

            byte[] data = (byte[])g.InitData.Clone();
            for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
            {
                if (rel.Addend != 0)
                    Util.WriteBuf(data, rel.Offset, rel.Addend, PtrSize);
            }
            section.Content.WriteBytes(data);

            var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, section, offset, out _, isExternal);
            _dataCoffSymbols[g.Name] = coffSym;
            _dataPlacement[g] = (section, offset);
        }
        else if (g.IsTentative && !g.IsStatic)
        {
            var coffSym = _symtab.AddCommonDataClrToken(g.Name, fieldDef, g.Ty.Size, out _);
            _dataCoffSymbols[g.Name] = coffSym;
        }
        else
        {
            int bssOffset = AllocateMergedBss(g);
            bool isExternal = !g.IsTentative && !g.IsStatic && !g.IsLocal;
            var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, _bssSection, bssOffset, out _, isExternal);
            _dataCoffSymbols[g.Name] = coffSym;
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
    private void EmitGlobalDataRelocations()
    {
        foreach (Obj g in _globalsWithRelocations)
        {
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

        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_md.GetRowCount(TableIndex.Field) + 1),
            MetadataTokens.MethodDefinitionHandle(_md.GetRowCount(TableIndex.MethodDef) + 1));
        _md.AddModule(0, _md.GetOrAddString(objName), _md.GetOrAddGuid(Guid.NewGuid()), default, default);

        EmitObjects(prog);

        EmitCxxPureMSILEntry();

        _aggregates.MaterializeAll();

        FinalizeReferencedFunctionSymbols();

        // Global data relocations — AFTER NEP so bare-name symbols exist
        EmitGlobalDataRelocations();

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
