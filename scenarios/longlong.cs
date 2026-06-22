using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class LonglongTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "longlong", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "longlong.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "longlong", refDir, "longlong.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        bool is32 = machine == Machine.I386;
        int ptrSize = is32 ? 4 : 8;
        string symPrefix = is32 ? "_" : "";

        byte[] mscorlibHash = machine == Machine.I386
            ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
            : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
        CodeViewMachine cvMachine = machine == Machine.I386 ? CodeViewMachine.I386 : machine == Machine.Arm64 ? CodeViewMachine.Arm64 : CodeViewMachine.Amd64;

        var md = new MetadataBuilder();

        // ─── AssemblyRef: mscorlib ────────────────────────────────────────
        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: CallConvCdecl (modopt on return types under /clr) ───
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── Shared signatures ────────────────────────────────────────────
        // int64(int64, int64)
        var llllSig = new BlobBuilder();
        new BlobEncoder(llllSig).MethodSignature()
            .Parameters(2, out var llllRet, out var llllPar);
        ClrIjw.WriteCdeclModOpt(llllRet, callConvCdeclRef).Int64();
        llllPar.AddParameter().Type().Int64();
        llllPar.AddParameter().Type().Int64();
        var llllSigBlob = md.GetOrAddBlob(llllSig);

        // int64(int64, int32)
        var lliSig = new BlobBuilder();
        new BlobEncoder(lliSig).MethodSignature()
            .Parameters(2, out var lliRet, out var lliPar);
        ClrIjw.WriteCdeclModOpt(lliRet, callConvCdeclRef).Int64();
        lliPar.AddParameter().Type().Int64();
        lliPar.AddParameter().Type().Int32();
        var lliSigBlob = md.GetOrAddBlob(lliSig);

        // uint64(uint64, int32)
        var uluiSig = new BlobBuilder();
        new BlobEncoder(uluiSig).MethodSignature()
            .Parameters(2, out var uluiRet, out var uluiPar);
        ClrIjw.WriteCdeclModOpt(uluiRet, callConvCdeclRef).UInt64();
        uluiPar.AddParameter().Type().UInt64();
        uluiPar.AddParameter().Type().Int32();
        var uluiSigBlob = md.GetOrAddBlob(uluiSig);

        // int32(int64, int64)
        var illSig = new BlobBuilder();
        new BlobEncoder(illSig).MethodSignature()
            .Parameters(2, out var illRet, out var illPar);
        ClrIjw.EncodeCdeclI4Return(illRet, callConvCdeclRef);
        illPar.AddParameter().Type().Int64();
        illPar.AddParameter().Type().Int64();

        // int64(int32)
        var liSig = new BlobBuilder();
        new BlobEncoder(liSig).MethodSignature()
            .Parameters(1, out var liRet, out var liPar);
        ClrIjw.WriteCdeclModOpt(liRet, callConvCdeclRef).Int64();
        liPar.AddParameter().Type().Int32();

        // int32(int64)
        var ilSig = new BlobBuilder();
        new BlobEncoder(ilSig).MethodSignature()
            .Parameters(1, out var ilRet, out var ilPar);
        ClrIjw.EncodeCdeclI4Return(ilRet, callConvCdeclRef);
        ilPar.AddParameter().Type().Int64();

        // int32()
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRet, out var mainPar);
        ClrIjw.EncodeCdeclI4Return(mainRet, callConvCdeclRef);

        // ─── MethodDef #1: ll_add ─────────────────────────────────────────
        var llAddMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_add"), llllSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var llAddLocalsSig = new BlobBuilder();
        new BlobEncoder(llAddLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var llAddLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llAddLocalsSig));

        // ─── MethodDef #2: ll_mul ─────────────────────────────────────────
        var llMulMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_mul"), llllSigBlob, 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var llMulLocalsSig = new BlobBuilder();
        new BlobEncoder(llMulLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var llMulLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llMulLocalsSig));

        // ─── MethodDef #3: ll_div ─────────────────────────────────────────
        var llDivMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_div"), llllSigBlob, 0,
            MetadataTokens.ParameterHandle(5));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var llDivLocalsSig = new BlobBuilder();
        new BlobEncoder(llDivLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var llDivLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llDivLocalsSig));

        // ─── MethodDef #4: ll_shl ─────────────────────────────────────────
        var llShlMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_shl"), lliSigBlob, 0,
            MetadataTokens.ParameterHandle(7));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 2);

        var llShlLocalsSig = new BlobBuilder();
        new BlobEncoder(llShlLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var llShlLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llShlLocalsSig));

        // ─── MethodDef #5: ll_shr ─────────────────────────────────────────
        var llShrMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_shr"), lliSigBlob, 0,
            MetadataTokens.ParameterHandle(9));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 2);

        var llShrLocalsSig = new BlobBuilder();
        new BlobEncoder(llShrLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var llShrLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llShrLocalsSig));

        // ─── MethodDef #6: ull_shr ────────────────────────────────────────
        var ullShrMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ull_shr"), uluiSigBlob, 0,
            MetadataTokens.ParameterHandle(11));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 2);

        var ullShrLocalsSig = new BlobBuilder();
        new BlobEncoder(ullShrLocalsSig).LocalVariableSignature(1).AddVariable().Type().UInt64();
        var ullShrLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ullShrLocalsSig));

        // ─── MethodDef #7: ll_compare ─────────────────────────────────────
        var llCompareMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_compare"), md.GetOrAddBlob(illSig), 0,
            MetadataTokens.ParameterHandle(13));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var llCompareLocalsSig = new BlobBuilder();
        new BlobEncoder(llCompareLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var llCompareLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llCompareLocalsSig));

        // ─── MethodDef #8: int_to_ll ──────────────────────────────────────
        var intToLlMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("int_to_ll"), md.GetOrAddBlob(liSig), 0,
            MetadataTokens.ParameterHandle(15));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        var intToLlLocalsSig = new BlobBuilder();
        new BlobEncoder(intToLlLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var intToLlLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(intToLlLocalsSig));

        // ─── MethodDef #9: ll_to_int ──────────────────────────────────────
        var llToIntMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_to_int"), md.GetOrAddBlob(ilSig), 0,
            MetadataTokens.ParameterHandle(16));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        var llToIntLocalsSig = new BlobBuilder();
        new BlobEncoder(llToIntLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var llToIntLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(llToIntLocalsSig));

        // ─── MethodDef #10: main ──────────────────────────────────────────
        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(17));

        // main locals: int32, int64, int64
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32();    // V_0
        mainLocalsEnc.AddVariable().Type().Int64();    // V_1: b
        mainLocalsEnc.AddVariable().Type().Int64();    // V_2: a
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("longlong.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("longlong.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "longlong.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Helper: emit simple 6-byte body (ldarg.0, ldarg.1, op, stloc.0, ldloc.0, ret) ──
        void EmitSimpleBinOp(MethodDefinitionHandle method, string coffName, ILOpCode op,
            StandaloneSignatureHandle localsSigHandle, string debugName, int sourceLineNumber)
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, sourceLineNumber);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(op);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(method, coffName, enc,
                maxStack: 2, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: debugName);
        }

        EmitSimpleBinOp(llAddMethod, "?ll_add@@$$J0YA_J_J0@Z", ILOpCode.Add, llAddLocalsSigHandle, "ll_add", 10);
        EmitSimpleBinOp(llMulMethod, "?ll_mul@@$$J0YA_J_J0@Z", ILOpCode.Mul, llMulLocalsSigHandle, "ll_mul", 11);
        EmitSimpleBinOp(llDivMethod, "?ll_div@@$$J0YA_J_J0@Z", ILOpCode.Div, llDivLocalsSigHandle, "ll_div", 12);
        EmitSimpleBinOp(llShlMethod, "?ll_shl@@$$J0YA_J_JH@Z", ILOpCode.Shl, llShlLocalsSigHandle, "ll_shl", 13);
        EmitSimpleBinOp(llShrMethod, "?ll_shr@@$$J0YA_J_JH@Z", ILOpCode.Shr, llShrLocalsSigHandle, "ll_shr", 14);
        EmitSimpleBinOp(ullShrMethod, "?ull_shr@@$$J0YA_K_KH@Z", ILOpCode.Shr_un, ullShrLocalsSigHandle, "ull_shr", 15);

        // ─── Emit IL for ll_compare ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_false = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.Branch(ILOpCode.Bge_s, lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_done);
            enc.MarkLabel(lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_done);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(llCompareMethod, "?ll_compare@@$$J0YAH_J0@Z", enc,
                maxStack: 2, localVariablesSignature: llCompareLocalsSigHandle, attributes: 0,
                debugName: "ll_compare");
        }

        // ─── Emit IL for int_to_ll ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(intToLlMethod, "?int_to_ll@@$$J0YA_JH@Z", enc,
                maxStack: 1, localVariablesSignature: intToLlLocalsSigHandle, attributes: 0,
                debugName: "int_to_ll");
        }

        // ─── Emit IL for ll_to_int ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Conv_i4);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(llToIntMethod, "?ll_to_int@@$$J0YAH_J@Z", enc,
                maxStack: 1, localVariablesSignature: llToIntLocalsSigHandle, attributes: 0,
                debugName: "ll_to_int");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 22);
            enc.OpCode(ILOpCode.Ldc_i4_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001
            enc.LoadConstantI4(0xF4240);              // IL_0002: ldc.i4 1000000
            enc.OpCode(ILOpCode.Conv_i8);             // IL_0007
            enc.OpCode(ILOpCode.Stloc_2);             // IL_0008: a
            enc.MarkLineNumber(cvFile, 23);
            enc.LoadConstantI4(0x1E8480);             // IL_0009: ldc.i4 2000000
            enc.OpCode(ILOpCode.Conv_i8);             // IL_000E
            enc.OpCode(ILOpCode.Stloc_1);             // IL_000F: b
            enc.MarkLineNumber(cvFile, 24);
            enc.OpCode(ILOpCode.Ldloc_2);             // IL_0010
            enc.OpCode(ILOpCode.Ldloc_1);             // IL_0011
            enc.Call(llAddMethod);                     // IL_0012: call ll_add
            enc.Call(llToIntMethod);                   // IL_0017: call ll_to_int
            enc.LoadConstantI8(unchecked((long)0xFFFFFFFFFFFFFFFF)); // IL_001C: ldc.i8
            enc.LoadConstantI4(1);                    // IL_0025: ldc.i4.1
            enc.Call(ullShrMethod);                    // IL_0026: call ull_shr
            enc.OpCode(ILOpCode.Conv_i4);             // IL_002B
            enc.OpCode(ILOpCode.Add);                 // IL_002C
            enc.OpCode(ILOpCode.Stloc_0);             // IL_002D
            enc.MarkLineNumber(cvFile, 25);
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_002E
            enc.OpCode(ILOpCode.Ret);                 // IL_002F

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "b"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "a"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for exported methods ───────────────────────────
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llAddMethod), "ll_add", "?ll_add@@$$J0YA_J_J0@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llMulMethod), "ll_mul", "?ll_mul@@$$J0YA_J_J0@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llDivMethod), "ll_div", "?ll_div@@$$J0YA_J_J0@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llShlMethod), "ll_shl", "?ll_shl@@$$J0YA_J_JH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llShrMethod), "ll_shr", "?ll_shr@@$$J0YA_J_JH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(ullShrMethod), "ull_shr", "?ull_shr@@$$J0YA_K_KH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llCompareMethod), "ll_compare", "?ll_compare@@$$J0YAH_J0@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(intToLlMethod), "int_to_ll", "?int_to_ll@@$$J0YA_JH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llToIntMethod), "ll_to_int", "?ll_to_int@@$$J0YAH_J@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var sections = new System.Collections.Generic.List<CoffSectionBuilder>();
        if (ilSection.Content.Count > 0) sections.Add(ilSection);
        if (dataSection.Content.Count > 0) sections.Add(dataSection);
        if (ilFixupSection.Content.Count > 0) sections.Add(ilFixupSection);
        if (nepSection.Content.Count > 0) sections.Add(nepSection);
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols, sections);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
