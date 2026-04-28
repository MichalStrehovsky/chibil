using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class FloatTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "float", refDir, "float.obj"));
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

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: float_arith(float32, float32) -> float32 ──────
        var floatArithSig = new BlobBuilder();
        new BlobEncoder(floatArithSig).MethodSignature()
            .Parameters(2, out var faRetEnc, out var faParEnc);
        faRetEnc.Type().Single();
        faParEnc.AddParameter().Type().Single();
        faParEnc.AddParameter().Type().Single();

        var floatArithMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("float_arith"),
            md.GetOrAddBlob(floatArithSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for float_arith: 5 x float32
        var faLocalsSig = new BlobBuilder();
        var faLocalsEnc = new BlobEncoder(faLocalsSig).LocalVariableSignature(5);
        for (int i = 0; i < 5; i++) faLocalsEnc.AddVariable().Type().Single();
        var faLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(faLocalsSig));

        // ─── MethodDef #2: double_arith(float64, float64) -> float64 ─────
        var doubleArithSig = new BlobBuilder();
        new BlobEncoder(doubleArithSig).MethodSignature()
            .Parameters(2, out var daRetEnc, out var daParEnc);
        daRetEnc.Type().Double();
        daParEnc.AddParameter().Type().Double();
        daParEnc.AddParameter().Type().Double();

        var doubleArithMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("double_arith"),
            md.GetOrAddBlob(doubleArithSig),
            0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for double_arith: 5 x float64
        var daLocalsSig = new BlobBuilder();
        var daLocalsEnc = new BlobEncoder(daLocalsSig).LocalVariableSignature(5);
        for (int i = 0; i < 5; i++) daLocalsEnc.AddVariable().Type().Double();
        var daLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(daLocalsSig));

        // ─── MethodDef #3: float_compare(float32, float32) -> int32 ──────
        var floatCmpSig = new BlobBuilder();
        new BlobEncoder(floatCmpSig).MethodSignature()
            .Parameters(2, out var fcRetEnc, out var fcParEnc);
        fcRetEnc.Type().Int32();
        fcParEnc.AddParameter().Type().Single();
        fcParEnc.AddParameter().Type().Single();

        var floatCmpMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("float_compare"),
            md.GetOrAddBlob(floatCmpSig),
            0,
            MetadataTokens.ParameterHandle(5));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for float_compare: 4 x int32
        var fcLocalsSig = new BlobBuilder();
        var fcLocalsEnc = new BlobEncoder(fcLocalsSig).LocalVariableSignature(4);
        for (int i = 0; i < 4; i++) fcLocalsEnc.AddVariable().Type().Int32();
        var fcLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(fcLocalsSig));

        // ─── MethodDef #4: main() -> int32 ───────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var mainParEnc);
        mainRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig),
            0,
            MetadataTokens.ParameterHandle(7));

        // Locals for main: int32, int32, float64, float32
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(4);
        mainLocalsEnc.AddVariable().Type().Int32();    // slot 0
        mainLocalsEnc.AddVariable().Type().Int32();    // slot 1 (c)
        mainLocalsEnc.AddVariable().Type().Double();   // slot 2 (d)
        mainLocalsEnc.AddVariable().Type().Single();   // slot 3 (f)
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("float.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "float.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "float.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for float_arith ──────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // sum = a + b
            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Conv_r4);
            enc.StoreLocal(4);                     // stloc.s V_4 (sum)

            // diff = a - b
            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Sub);
            enc.OpCode(ILOpCode.Conv_r4);
            enc.OpCode(ILOpCode.Stloc_3);          // stloc.3 (diff)

            // prod = a * b
            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Conv_r4);
            enc.OpCode(ILOpCode.Stloc_2);          // stloc.2 (prod)

            // quot = a / b
            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Div);
            enc.OpCode(ILOpCode.Conv_r4);
            enc.OpCode(ILOpCode.Stloc_1);          // stloc.1 (quot)

            // return sum + diff + prod + quot
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadLocal(4);                       // ldloc.s V_4
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldloc_3);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_2);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Conv_r4);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var faLocalSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(faLocalsSigHandle), "diff"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(faLocalsSigHandle), "quot"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(faLocalsSigHandle), "prod"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(faLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(floatArithMethod, "?float_arith@@$$J0YMMMM@Z", enc,
                maxStack: 2, localVariablesSignature: faLocalsSigHandle, attributes: 0,
                debugName: "float_arith", localSlots: faLocalSlots);
        }

        // ─── Emit IL for double_arith ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // sum = a + b
            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Add);
            enc.StoreLocal(4);                     // stloc.s V_4 (sum)

            // diff = a - b
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Sub);
            enc.OpCode(ILOpCode.Stloc_3);          // stloc.3 (diff)

            // prod = a * b
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Stloc_2);          // stloc.2 (prod)

            // quot = a / b
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Div);
            enc.OpCode(ILOpCode.Stloc_1);          // stloc.1 (quot)

            // return sum + diff + prod + quot
            enc.MarkLineNumber(cvFile, 19);
            enc.LoadLocal(4);                       // ldloc.s V_4
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var daLocalSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(daLocalsSigHandle), "diff"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(daLocalsSigHandle), "quot"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(daLocalsSigHandle), "prod"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(daLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(doubleArithMethod, "?double_arith@@$$J0YMNNN@Z", enc,
                maxStack: 2, localVariablesSignature: daLocalsSigHandle, attributes: 0,
                debugName: "double_arith", localSlots: daLocalSlots);
        }

        // ─── Emit IL for float_compare ────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_eq0 = enc.DefineLabel();
            var lbl_eq_done = enc.DefineLabel();
            var lbl_lt0 = enc.DefineLabel();
            var lbl_lt_done = enc.DefineLabel();
            var lbl_le0 = enc.DefineLabel();
            var lbl_le_done = enc.DefineLabel();

            // eq = (a == b)
            enc.MarkLineNumber(cvFile, 24);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.Branch(ILOpCode.Bne_un_s, lbl_eq0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_eq_done);
            enc.MarkLabel(lbl_eq0);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_eq_done);
            enc.OpCode(ILOpCode.Stloc_3);          // stloc.3 (eq)

            // lt = (a < b)
            enc.MarkLineNumber(cvFile, 25);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.Branch(ILOpCode.Bge_un_s, lbl_lt0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_lt_done);
            enc.MarkLabel(lbl_lt0);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_lt_done);
            enc.OpCode(ILOpCode.Stloc_2);          // stloc.2 (lt)

            // le = (a <= b)
            enc.MarkLineNumber(cvFile, 26);
            enc.OpCode(ILOpCode.Ldarg_0);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.OpCode(ILOpCode.Ldarg_1);
            if (machine != Machine.Arm64) enc.OpCode(ILOpCode.Conv_r8);
            enc.Branch(ILOpCode.Bgt_un_s, lbl_le0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_le_done);
            enc.MarkLabel(lbl_le0);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_le_done);
            enc.OpCode(ILOpCode.Stloc_1);          // stloc.1 (le)

            // return eq + lt + le
            enc.MarkLineNumber(cvFile, 27);
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 28);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var fcLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(fcLocalsSigHandle), "le"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(fcLocalsSigHandle), "eq"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(fcLocalsSigHandle), "lt"),
            };

            bodyEncoder.AddMethodBody(floatCmpMethod, "?float_compare@@$$J0YMHMM@Z", enc,
                maxStack: 2, localVariablesSignature: fcLocalsSigHandle, attributes: 0,
                debugName: "float_compare", localSlots: fcLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 32);
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0001

            // float f = float_arith(3.5f, 1.5f)
            enc.LoadConstantR4(3.5f);               // IL_0002: ldc.r4 3.5
            enc.LoadConstantR4(1.5f);               // IL_0007: ldc.r4 1.5
            enc.Call(floatArithMethod);              // IL_000C: call float_arith
            enc.OpCode(ILOpCode.Conv_r4);           // IL_0011: conv.r4
            enc.OpCode(ILOpCode.Stloc_3);          // IL_0012: stloc.3 (f)

            // double d = double_arith(3.5, 1.5)
            enc.MarkLineNumber(cvFile, 33);
            enc.LoadConstantR8(3.5);                // IL_0013: ldc.r8 3.5
            enc.LoadConstantR8(1.5);                // IL_001C: ldc.r8 1.5
            enc.Call(doubleArithMethod);             // IL_0025: call double_arith
            enc.OpCode(ILOpCode.Stloc_2);          // IL_002A: stloc.2 (d)

            // int c = float_compare(1.0f, 2.0f)
            enc.MarkLineNumber(cvFile, 34);
            enc.LoadConstantR4(1.0f);               // IL_002B: ldc.r4 1
            enc.LoadConstantR4(2.0f);               // IL_0030: ldc.r4 2
            enc.Call(floatCmpMethod);                // IL_0035: call float_compare
            enc.OpCode(ILOpCode.Stloc_1);          // IL_003A: stloc.1 (c)

            // return (int)f + (int)d + c
            enc.MarkLineNumber(cvFile, 35);
            enc.OpCode(ILOpCode.Ldloc_3);          // IL_003B: ldloc.3
            enc.OpCode(ILOpCode.Conv_r8);           // IL_003C: conv.r8
            enc.OpCode(ILOpCode.Conv_i4);           // IL_003D: conv.i4
            enc.OpCode(ILOpCode.Ldloc_2);          // IL_003E: ldloc.2
            enc.OpCode(ILOpCode.Conv_i4);           // IL_003F: conv.i4
            enc.OpCode(ILOpCode.Add);               // IL_0040
            enc.OpCode(ILOpCode.Ldloc_1);          // IL_0041: ldloc.1
            enc.OpCode(ILOpCode.Add);               // IL_0042
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0043

            enc.MarkLineNumber(cvFile, 36);
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0044
            enc.OpCode(ILOpCode.Ret);               // IL_0045

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "d"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "c"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(mainLocalsSigHandle), "f"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
