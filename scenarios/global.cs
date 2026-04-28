using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class GlobalTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "global", refDir, "global.obj"));
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

        // ─── TypeRefs ─────────────────────────────────────────────────────
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));
        var unsafeValueTypeAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("UnsafeValueTypeAttribute"));
        var fixedAddressAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("FixedAddressValueTypeAttribute"));
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

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

        // ─── TypeDef #2: <CppImplementationDetails>.g_array$$BY0A@H ───────
        // Sequential, sealed, BeforeFieldInit, size=16 (4 ints), Pack=0
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class |
            TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
            md.GetOrAddString("<CppImplementationDetails>"),
            md.GetOrAddString("g_array$$BY0A@H"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(6), // no fields of its own
            MetadataTokens.MethodDefinitionHandle(5)); // no methods — starts past __CxxPureMSILEntry

        md.AddTypeLayout(arrayTypeDef, 0, 16);
        // UnsafeValueTypeAttribute first, then NativeCppClassAttribute
        md.AddCustomAttribute(arrayTypeDef, unsafeVTCtorRef, defaultCtorAttrBlob);
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef, defaultCtorAttrBlob);

        // ─── Field signatures ─────────────────────────────────────────────
        // int32 field signature
        var int32FieldSig = new BlobBuilder();
        new BlobEncoder(int32FieldSig).Field().Type().Int32();
        var int32FieldSigBlob = md.GetOrAddBlob(int32FieldSig);

        // valuetype g_array$$BY0A@H field signature
        var arrayFieldSig = new BlobBuilder();
        new BlobEncoder(arrayFieldSig).Field().Type().Type(arrayTypeDef, isValueType: true);
        var arrayFieldSigBlob = md.GetOrAddBlob(arrayFieldSig);

        // FNPTR void() field signature
        var fnptrFieldSig = new BlobBuilder();
        fnptrFieldSig.WriteByte(0x06); // FIELD calling convention
        fnptrFieldSig.WriteByte(0x1B); // ELEMENT_TYPE_FNPTR
        fnptrFieldSig.WriteByte(0x00); // DEFAULT calling convention
        fnptrFieldSig.WriteByte(0x00); // 0 params
        fnptrFieldSig.WriteByte(0x01); // VOID return
        var fnptrFieldSigBlob = md.GetOrAddBlob(fnptrFieldSig);

        // ─── Field #1: g_initialized (int32, Assembly|Static) ─────────────
        var field_gInitialized = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static,
            md.GetOrAddString("g_initialized"),
            int32FieldSigBlob);
        md.AddCustomAttribute(field_gInitialized, fixedAddrCtorRef, defaultCtorAttrBlob);

        // ─── Field #2: ?A0xb6c09798.g_initialized$initializer$ (FNPTR, HasFieldRVA) ──
        var field_gInitializedInit = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0xb6c09798.g_initialized$initializer$"),
            fnptrFieldSigBlob);
        md.AddFieldRelativeVirtualAddress(field_gInitializedInit, 0);

        // ─── Field #3: g_array (valuetype, Assembly|Static) ───────────────
        var field_gArray = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static,
            md.GetOrAddString("g_array"),
            arrayFieldSigBlob);
        md.AddCustomAttribute(field_gArray, fixedAddrCtorRef, defaultCtorAttrBlob);

        // ─── Field #4: g_uninitialized (int32, Assembly|Static) ───────────
        var field_gUninitialized = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static,
            md.GetOrAddString("g_uninitialized"),
            int32FieldSigBlob);
        md.AddCustomAttribute(field_gUninitialized, fixedAddrCtorRef, defaultCtorAttrBlob);

        // ─── Field #5: ?A0xb6c09798.g_array$initializer$ (FNPTR, HasFieldRVA) ──
        var field_gArrayInit = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0xb6c09798.g_array$initializer$"),
            fnptrFieldSigBlob);
        md.AddFieldRelativeVirtualAddress(field_gArrayInit, 0);

        // ─── Method #1: ??__Eg_initialized (initializer) ──────────────────
        var initGInitializedSig = new BlobBuilder();
        new BlobEncoder(initGInitializedSig).MethodSignature()
            .Parameters(0, out var igRet, out var igPar);
        igRet.Void();

        var initGInitializedMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("?A0xb6c09798.??__Eg_initialized@@YMXXZ"),
            md.GetOrAddBlob(initGInitializedSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── Method #2: ??__Eg_array (initializer) ────────────────────────
        var initGArraySig = new BlobBuilder();
        new BlobEncoder(initGArraySig).MethodSignature()
            .Parameters(0, out var iaRet, out var iaPar);
        iaRet.Void();

        var initGArrayMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("?A0xb6c09798.??__Eg_array@@YMXXZ"),
            md.GetOrAddBlob(initGArraySig),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── Method #3: main ──────────────────────────────────────────────
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

        // ─── MethodDef #4: __CxxPureMSILEntry(int32, char**, char**) -> int32
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

        // ─── StandaloneSig: locals (int32, int32, int32) for main ─────────
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32(); // slot 0: i
        mainLocalsEnc.AddVariable().Type().Int32(); // slot 1: sum
        mainLocalsEnc.AddVariable().Type().Int32(); // slot 2: return
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── StandaloneSig: locals (int32) for __CxxPureMSILEntry ─────────
        var entryLocalsSig = new BlobBuilder();
        new BlobEncoder(entryLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var entryLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(entryLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("global.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── .CRTMA$XCC initializer list ──────────────────────────────────
        var initializerList = new InitializerListSectionBuilder(coffHeader, symtab);
        initializerList.AddInitializer(initGInitializedMethod);
        initializerList.AddInitializer(initGArrayMethod);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "global.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.Cpp,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35729,
            beMajor: 19, beMinor: 50, beBuild: 35729,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "global.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        // Create COFF builder first to get section numbers
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder,
            initializerList: initializerList);

        // Register field data symbols BEFORE emitting IL
        symtab.AddDataClrToken("g_initialized$initializer$", field_gInitializedInit, LogicalSection.Crtma, 0, out _);
        symtab.AddDataClrToken("g_array$initializer$", field_gArrayInit, LogicalSection.Crtma, 4, out _);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for ??__Eg_initialized (initializer) ─────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 5);
            enc.LoadConstantI4(42);                   // IL_0000: ldc.i4.s 42
            enc.OpCode(ILOpCode.Stsfld);              // IL_0002: stsfld g_initialized
            enc.Token(field_gInitialized);
            enc.OpCode(ILOpCode.Ret);                 // IL_0007: ret

            bodyEncoder.AddMethodBody(initGInitializedMethod, "???__Eg_initialized@@YMXXZ@?A0xb6c09798@@$$FYMXXZ", enc,
                maxStack: 1, debugName: "`dynamic initializer for 'g_initialized''");
        }

        // ─── Emit IL for ??__Eg_array (initializer) ───────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // g_array[0] = 1
            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldsflda);             // IL_0000
            enc.Token(field_gArray);
            enc.OpCode(ILOpCode.Ldc_i4_1);            // IL_0005
            enc.OpCode(ILOpCode.Stind_i4);            // IL_0006

            // g_array[1] = 2
            enc.OpCode(ILOpCode.Ldsflda);             // IL_0007
            enc.Token(field_gArray);
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_000C: offset +4
            enc.OpCode(ILOpCode.Add);                 // IL_000D
            enc.OpCode(ILOpCode.Ldc_i4_2);            // IL_000E
            enc.OpCode(ILOpCode.Stind_i4);            // IL_000F

            // g_array[2] = 3
            enc.OpCode(ILOpCode.Ldsflda);             // IL_0010
            enc.Token(field_gArray);
            enc.OpCode(ILOpCode.Ldc_i4_8);            // IL_0015: offset +8
            enc.OpCode(ILOpCode.Add);                 // IL_0016
            enc.OpCode(ILOpCode.Ldc_i4_3);            // IL_0017
            enc.OpCode(ILOpCode.Stind_i4);            // IL_0018

            // g_array[3] = 4
            enc.OpCode(ILOpCode.Ldsflda);             // IL_0019
            enc.Token(field_gArray);
            enc.LoadConstantI4(12);                    // IL_001E: ldc.i4.s 12
            enc.OpCode(ILOpCode.Add);                 // IL_0020
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0021
            enc.OpCode(ILOpCode.Stind_i4);            // IL_0022

            enc.OpCode(ILOpCode.Ret);                 // IL_0023

            bodyEncoder.AddMethodBody(initGArrayMethod, "???__Eg_array@@YMXXZ@?A0xb6c09798@@$$FYMXXZ", enc,
                maxStack: 2, debugName: "`dynamic initializer for 'g_array''");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_loopBody = enc.DefineLabel();     // IL_0019
            var lbl_loopTest = enc.DefineLabel();     // IL_001D
            var lbl_afterLoop = enc.DefineLabel();    // IL_0030 (x86) / IL_0032 (arm64)

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_2);             // IL_0001: return = 0

            enc.LoadConstantI4(10);                    // IL_0002: ldc.i4.s 10
            enc.OpCode(ILOpCode.Stsfld);               // IL_0004: stsfld g_uninitialized
            enc.Token(field_gUninitialized);

            enc.MarkLineNumber(cvFile, 12);
            enc.OpCode(ILOpCode.Ldsfld);               // IL_0009: ldsfld g_initialized
            enc.Token(field_gInitialized);
            enc.OpCode(ILOpCode.Ldsfld);               // IL_000E: ldsfld g_uninitialized
            enc.Token(field_gUninitialized);
            enc.OpCode(ILOpCode.Add);                  // IL_0013
            enc.OpCode(ILOpCode.Stloc_1);              // IL_0014: sum = g_initialized + g_uninitialized

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldc_i4_0);             // IL_0015
            enc.OpCode(ILOpCode.Stloc_0);              // IL_0016: i = 0
            enc.Branch(ILOpCode.Br_s, lbl_loopTest);   // IL_0017: goto loopTest

            enc.MarkLabel(lbl_loopBody);               // IL_0019
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0019
            enc.OpCode(ILOpCode.Ldc_i4_1);             // IL_001A
            enc.OpCode(ILOpCode.Add);                  // IL_001B
            enc.OpCode(ILOpCode.Stloc_0);              // IL_001C: i++

            enc.MarkLabel(lbl_loopTest);               // IL_001D
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_001D
            enc.OpCode(ILOpCode.Ldc_i4_4);             // IL_001E
            enc.Branch(ILOpCode.Bge_s, lbl_afterLoop);  // IL_001F: if i >= 4 goto afterLoop

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldloc_1);              // IL_0021
            enc.OpCode(ILOpCode.Ldsflda);              // IL_0022: ldsflda g_array
            enc.Token(field_gArray);
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0027
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);  // ARM64 only
            enc.OpCode(ILOpCode.Ldc_i4_4);             // ldc.i4.4
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);  // ARM64 only
            enc.OpCode(ILOpCode.Mul);                  // mul
            enc.OpCode(ILOpCode.Add);                  // add
            enc.OpCode(ILOpCode.Ldind_i4);             // ldind.i4
            enc.OpCode(ILOpCode.Add);                  // add
            enc.OpCode(ILOpCode.Stloc_1);              // stloc.1: sum += g_array[i]
            enc.Branch(ILOpCode.Br_s, lbl_loopBody);   // goto loopBody

            enc.MarkLabel(lbl_afterLoop);
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_1);              // ldloc.1
            enc.OpCode(ILOpCode.Stloc_2);              // stloc.2: return = sum
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldloc_2);              // ldloc.2
            enc.OpCode(ILOpCode.Ret);                  // ret

            var mainLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(mainLocalsSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$HYMHXZ", enc,
                maxStack: 4, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── Emit IL for __CxxPureMSILEntry ───────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 17);
            enc.Call(mainMethod);                      // IL_0000: call main (no args)
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
