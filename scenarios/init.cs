using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class InitTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "init", refDir, "init.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        byte[] mscorlibHash = machine == Machine.I386
            ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
            : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
        CodeViewMachine cvMachine = machine == Machine.I386 ? CodeViewMachine.I386 : machine == Machine.Arm64 ? CodeViewMachine.Arm64 : CodeViewMachine.Amd64;

        var md = new MetadataBuilder();

        // ─── AssemblyRef: mscorlib ────────────────────────────────────────
        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRefs (only what's referenced) ────────────────────────────
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));
        var unsafeValueTypeAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("UnsafeValueTypeAttribute"));
        var isConstRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsConst"));
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));
        var fixedAddressAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("FixedAddressValueTypeAttribute"));

        // ─── MemberRefs for custom attribute constructors ─────────────────
        var voidCtorSig = new BlobBuilder();
        new BlobEncoder(voidCtorSig).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(0, out var voidCtorRet, out var voidCtorPar);
        voidCtorRet.Void();
        var voidCtorBlob = md.GetOrAddBlob(voidCtorSig);

        var nativeCppCtorRef = md.AddMemberReference(nativeCppClassAttrRef, md.GetOrAddString(".ctor"), voidCtorBlob);
        var unsafeVTCtorRef = md.AddMemberReference(unsafeValueTypeAttrRef, md.GetOrAddString(".ctor"), voidCtorBlob);
        var fixedAddrCtorRef = md.AddMemberReference(fixedAddressAttrRef, md.GetOrAddString(".ctor"), voidCtorBlob);

        var defaultCtorAttrBlob = md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 });

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── TypeDef #2: <CppImplementationDetails>.$ArrayType$$$BY06$$CBD
        // Sequential, sealed, size=7 (for "Hello!\0"), NativeCppClassAttribute + UnsafeValueTypeAttribute
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class |
            TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
            md.GetOrAddString("<CppImplementationDetails>"),
            md.GetOrAddString("$ArrayType$$$BY06$$CBD"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(4), // no fields of its own
            MetadataTokens.MethodDefinitionHandle(5)); // no methods

        md.AddTypeLayout(arrayTypeDef, 0, 7);
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef, defaultCtorAttrBlob);
        md.AddCustomAttribute(arrayTypeDef, unsafeVTCtorRef, defaultCtorAttrBlob);

        // ─── Field #1: ?A0xb6c09798.unnamed-global-0 (the string literal data)
        // Type: CMOD_OPT IsConst ValueClass $ArrayType$$$BY06$$CBD
        var field1SigBuilder = new BlobBuilder();
        var field1SigEnc = new BlobEncoder(field1SigBuilder).Field().Type();
        field1SigEnc.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        field1SigEnc.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isConstRef));
        field1SigEnc.Type(arrayTypeDef, isValueType: true);

        var field1 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0xb6c09798.unnamed-global-0"),
            md.GetOrAddBlob(field1SigBuilder));
        md.AddFieldRelativeVirtualAddress(field1, 0);

        // ─── Field #2: str (the global pointer variable)
        // Type: Ptr CMOD_OPT IsSignUnspecifiedByte I1
        var field2SigBuilder = new BlobBuilder();
        var field2SigEnc = new BlobEncoder(field2SigBuilder).Field().Type();
        field2SigEnc.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        field2SigEnc.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        field2SigEnc.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        field2SigEnc.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var field2 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static,
            md.GetOrAddString("str"),
            md.GetOrAddBlob(field2SigBuilder));
        md.AddCustomAttribute(field2, fixedAddrCtorRef, defaultCtorAttrBlob);

        // ─── Field #3: ?A0xb6c09798.str$initializer$ (function pointer for CRTMA)
        // Type: FNPTR void()
        var field3SigBuilder = new BlobBuilder();
        field3SigBuilder.WriteByte(0x06); // FIELD calling convention
        field3SigBuilder.WriteByte(0x1B); // ELEMENT_TYPE_FNPTR
        field3SigBuilder.WriteByte(0x00); // DEFAULT calling convention, 0 generic params
        field3SigBuilder.WriteByte(0x00); // 0 params
        field3SigBuilder.WriteByte(0x01); // VOID return

        var field3 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0xb6c09798.str$initializer$"),
            md.GetOrAddBlob(field3SigBuilder));
        md.AddFieldRelativeVirtualAddress(field3, 0);

        // ─── Method #1: ??__Estr (initializer — sets str = &"Hello!")
        var estrSig = new BlobBuilder();
        new BlobEncoder(estrSig).MethodSignature()
            .Parameters(0, out var estrRet, out var estrPar);
        estrRet.Void();

        var estrMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("?A0xb6c09798.??__Estr@@YMXXZ"),
            md.GetOrAddBlob(estrSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── Method #2: main ──────────────────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRet, out var mainPar);
        mainRet.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── MethodDef #3: __CxxPureMSILEntry(int32, char**, char**) -> int32
        var entrySig = new BlobBuilder();
        var entrySigEnc = new BlobEncoder(entrySig).MethodSignature();
        entrySigEnc.Parameters(3, out var eRetEnc, out var eParEnc);
        eRetEnc.Type().Int32();
        eParEnc.AddParameter().Type().Int32();
        var ep2 = eParEnc.AddParameter().Type();
        ep2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        ep2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        ep2.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var ep3 = eParEnc.AddParameter().Type();
        ep3.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep3.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep3.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        ep3.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        ep3.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var entryMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("__CxxPureMSILEntry"),
            md.GetOrAddBlob(entrySig),
            0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argc"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argv"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("envp"), 3);

        // ─── StandaloneSig: locals (int32) for main ───────────────────────
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── StandaloneSig: locals (int32) for __CxxPureMSILEntry ─────────
        var entryLocalsSig = new BlobBuilder();
        new BlobEncoder(entryLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var entryLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(entryLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("init.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();
        var rdataBuilder = new BlobBuilder();

        // ─── .rdata section: "Hello!\0" (7 bytes) ─────────────────────────
        rdataBuilder.WriteBytes(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x00 });

        // ─── .CRTMA$XCC initializer list ──────────────────────────────────
        var initializerList = new InitializerListSectionBuilder(coffHeader, symtab);
        initializerList.AddInitializer(estrMethod);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "init.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            // MSVC C frontend rejects this source under /clr:pure, so it is compiled
            // with /TP (C++ mode). The debug info language must match.
            language: CodeViewLanguage.Cpp,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35729,
            beMajor: 19, beMinor: 50, beBuild: 35729,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "init.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        // Create COFF builder first to get section numbers
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder, rdataStream: rdataBuilder,
            initializerList: initializerList);

        // Register field data symbols BEFORE emitting IL
        int rdataSectionNum = coffBuilder.RDataSectionNumber;
        int crtmaSectionNum = coffBuilder.CrtmaSectionNumber;
        symtab.AddDataClrToken("$SG_literal", field1, rdataSectionNum, 0, out _);
        symtab.AddDataClrToken("str$initializer$", field3, crtmaSectionNum, 0, out _);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for ??__Estr (initializer) ──────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // ldsflda unnamed-global-0 / stsfld str / ret
            enc.MarkLineNumber(cvFile, 5);
            enc.OpCode(ILOpCode.Ldsflda);
            enc.Token(field1);
            enc.OpCode(ILOpCode.Stsfld);
            enc.Token(field2);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(estrMethod, "???__Estr@@YMXXZ@?A0xb6c09798@@$$FYMXXZ", enc,
                maxStack: 1, debugName: "`dynamic initializer for 'str''");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(field2);
            enc.LoadConstantI4(1);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i1);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$HYMHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── Emit IL for __CxxPureMSILEntry ──────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 10);
            enc.Call(mainMethod);                      // IL_0000: call main
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0005
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0006
            enc.OpCode(ILOpCode.Ret);                 // IL_0007

            string entryCoffName = machine == Machine.I386
                ? "?__CxxPureMSILEntry@@$$J0YMHHPAPAD0@Z"
                : "?__CxxPureMSILEntry@@$$J0YMHHPEAPEAD0@Z";
            bodyEncoder.AddMethodBody(entryMethod, entryCoffName, enc,
                maxStack: 1, localVariablesSignature: entryLocalsSigHandle, attributes: 0,
                debugName: "__CxxPureMSILEntry");
        }

        // ─── Serialize ────────────────────────────────────────────────────
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}

