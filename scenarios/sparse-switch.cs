using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class SparseSwitchTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "sparse-switch", refDir, "sparse-switch.obj"));
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

        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: sparse_switch(int) -> int ──────────────────────
        var sparseSig = new BlobBuilder();
        new BlobEncoder(sparseSig).MethodSignature()
            .Parameters(1, out var sRetEnc, out var sParEnc);
        sRetEnc.Type().Int32();
        sParEnc.AddParameter().Type().Int32();

        var sparseMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sparse_switch"), md.GetOrAddBlob(sparseSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // sparse_switch locals: 2 x int32 on both architectures
        var sLocalsSig = new BlobBuilder();
        var sLocalsEnc = new BlobEncoder(sLocalsSig).LocalVariableSignature(2);
        for (int i = 0; i < 2; i++) sLocalsEnc.AddVariable().Type().Int32();
        var sLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(sLocalsSig));

        // ─── MethodDef #2: dense_switch(int) -> int ───────────────────────
        var denseSig = new BlobBuilder();
        new BlobEncoder(denseSig).MethodSignature()
            .Parameters(1, out var dRetEnc, out var dParEnc);
        dRetEnc.Type().Int32();
        dParEnc.AddParameter().Type().Int32();

        var denseMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("dense_switch"), md.GetOrAddBlob(denseSig), 0,
            MetadataTokens.ParameterHandle(2));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // dense_switch locals: 1 x int32 on x86/x64, 2 x int32 on arm64
        int denseLocalCount = machine == Machine.Arm64 ? 2 : 1;
        var dLocalsSig = new BlobBuilder();
        var dLocalsEnc = new BlobEncoder(dLocalsSig).LocalVariableSignature(denseLocalCount);
        for (int i = 0; i < denseLocalCount; i++) dLocalsEnc.AddVariable().Type().Int32();
        var dLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(dLocalsSig));

        // ─── MethodDef #3: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(3));

        // main locals: 1 x int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("sparse-switch.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("sparse-switch.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "sparse-switch.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sparse_switch ────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_case1 = enc.DefineLabel();
            var lbl_case100 = enc.DefineLabel();
            var lbl_case1000 = enc.DefineLabel();
            var lbl_case10000 = enc.DefineLabel();
            var lbl_default = enc.DefineLabel();
            var lbl_end = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 12);
            if (machine == Machine.I386)
            {
                // x86 binary tree: bgt.s for initial pivot at 1000
                var lbl_right = enc.DefineLabel();

                enc.OpCode(ILOpCode.Ldarg_0);               // IL_0000
                enc.OpCode(ILOpCode.Stloc_1);               // IL_0001
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0002
                enc.LoadConstantI4(1000);                    // IL_0003: ldc.i4 0x3E8
                enc.Branch(ILOpCode.Bgt_s, lbl_right);      // IL_0008: bgt.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_000A
                enc.LoadConstantI4(1000);                    // IL_000B: ldc.i4 0x3E8
                enc.Branch(ILOpCode.Beq_s, lbl_case1000);   // IL_0010: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0012
                enc.OpCode(ILOpCode.Ldc_i4_1);              // IL_0013
                enc.Branch(ILOpCode.Beq_s, lbl_case1);      // IL_0014: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0016
                enc.LoadConstantI4(100);                     // IL_0017: ldc.i4.s 100
                enc.Branch(ILOpCode.Beq_s, lbl_case100);    // IL_0019: beq.s
                enc.Branch(ILOpCode.Br_s, lbl_default);     // IL_001B: br.s

                enc.MarkLabel(lbl_right);                    // IL_001D
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_001D
                enc.LoadConstantI4(10000);                   // IL_001E: ldc.i4 0x2710
                enc.Branch(ILOpCode.Beq_s, lbl_case10000);  // IL_0023: beq.s
                enc.Branch(ILOpCode.Br_s, lbl_default);     // IL_0025: br.s
            }
            else if (machine == Machine.Amd64)
            {
                // x64: linear if-else chain
                enc.OpCode(ILOpCode.Ldarg_0);               // IL_0000
                enc.OpCode(ILOpCode.Stloc_1);               // IL_0001
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0002
                enc.OpCode(ILOpCode.Ldc_i4_1);              // IL_0003
                enc.Branch(ILOpCode.Beq_s, lbl_case1);      // IL_0004: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0006
                enc.LoadConstantI4(100);                     // IL_0007: ldc.i4.s 100
                enc.Branch(ILOpCode.Beq_s, lbl_case100);    // IL_0009: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_000B
                enc.LoadConstantI4(1000);                    // IL_000C: ldc.i4 0x3E8
                enc.Branch(ILOpCode.Beq_s, lbl_case1000);   // IL_0011: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0013
                enc.LoadConstantI4(10000);                   // IL_0014: ldc.i4 0x2710
                enc.Branch(ILOpCode.Beq_s, lbl_case10000);  // IL_0019: beq.s
                enc.Branch(ILOpCode.Br_s, lbl_default);     // IL_001B: br.s
            }
            else
            {
                // arm64 binary tree: blt.s/bgt.s/br.s for initial pivot at 1000
                var lbl_left = enc.DefineLabel();
                var lbl_right = enc.DefineLabel();

                enc.OpCode(ILOpCode.Ldarg_0);               // IL_0000
                enc.OpCode(ILOpCode.Stloc_1);               // IL_0001
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0002
                enc.LoadConstantI4(1000);                    // IL_0003: ldc.i4 0x3E8
                enc.Branch(ILOpCode.Blt_s, lbl_left);       // IL_0008: blt.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_000A
                enc.LoadConstantI4(1000);                    // IL_000B: ldc.i4 0x3E8
                enc.Branch(ILOpCode.Bgt_s, lbl_right);      // IL_0010: bgt.s
                enc.Branch(ILOpCode.Br_s, lbl_case1000);    // IL_0012: br.s

                enc.MarkLabel(lbl_left);                     // IL_0014
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0014
                enc.OpCode(ILOpCode.Ldc_i4_1);              // IL_0015
                enc.Branch(ILOpCode.Beq_s, lbl_case1);      // IL_0016: beq.s
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_0018
                enc.LoadConstantI4(100);                     // IL_0019: ldc.i4.s 100
                enc.Branch(ILOpCode.Beq_s, lbl_case100);    // IL_001B: beq.s
                enc.Branch(ILOpCode.Br_s, lbl_default);     // IL_001D: br.s

                enc.MarkLabel(lbl_right);                    // IL_001F
                enc.OpCode(ILOpCode.Ldloc_1);               // IL_001F
                enc.LoadConstantI4(10000);                   // IL_0020: ldc.i4 0x2710
                enc.Branch(ILOpCode.Beq_s, lbl_case10000);  // IL_0025: beq.s
                enc.Branch(ILOpCode.Br_s, lbl_default);     // IL_0027: br.s
            }

            enc.MarkLabel(lbl_case1);                        // x86:IL_0027 arm64:IL_0029
            enc.MarkLineNumber(cvFile, 13);
            enc.LoadConstantI4(10);                          // ldc.i4.s 10
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case100);                      // x86:IL_002C arm64:IL_002E
            enc.MarkLineNumber(cvFile, 14);
            enc.LoadConstantI4(20);                          // ldc.i4.s 20
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case1000);                     // x86:IL_0031 arm64:IL_0033
            enc.MarkLineNumber(cvFile, 15);
            enc.LoadConstantI4(30);                          // ldc.i4.s 30
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case10000);                    // x86:IL_0036 arm64:IL_0038
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadConstantI4(40);                          // ldc.i4.s 40
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_default);                      // x86:IL_003B arm64:IL_003D
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldc_i4_m1);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLabel(lbl_end);                          // x86:IL_003D arm64:IL_003F
            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(sparseMethod, "?sparse_switch@@$$J0YMHH@Z", enc,
                maxStack: 2, localVariablesSignature: sLocalsSigHandle, attributes: 0,
                debugName: "sparse_switch");
        }

        // ─── Emit IL for dense_switch ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_default = enc.DefineLabel();
            var lbl_end = enc.DefineLabel();
            var lbl_case0 = enc.DefineLabel();
            var lbl_case1 = enc.DefineLabel();
            var lbl_case2 = enc.DefineLabel();
            var lbl_case3 = enc.DefineLabel();
            var lbl_case4 = enc.DefineLabel();
            var lbl_case5 = enc.DefineLabel();
            var lbl_case6 = enc.DefineLabel();
            var lbl_case7 = enc.DefineLabel();
            var lbl_case8 = enc.DefineLabel();
            var lbl_case9 = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 23);
            if (machine == Machine.Arm64)
            {
                // ARM64: bounds-check before switch
                enc.OpCode(ILOpCode.Ldarg_0);
                enc.OpCode(ILOpCode.Stloc_1);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_0);
                enc.Branch(ILOpCode.Blt_s, lbl_default);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.LoadConstantI4(9);
                enc.Branch(ILOpCode.Bgt_s, lbl_default);
                enc.OpCode(ILOpCode.Ldloc_1);
            }
            else
            {
                enc.OpCode(ILOpCode.Ldarg_0);
            }

            enc.Switch(lbl_case0, lbl_case1, lbl_case2, lbl_case3, lbl_case4,
                       lbl_case5, lbl_case6, lbl_case7, lbl_case8, lbl_case9);

            enc.Branch(ILOpCode.Br_s, lbl_default);

            enc.MarkLabel(lbl_case0);
            enc.MarkLineNumber(cvFile, 24);
            enc.LoadConstantI4(100);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case1);
            enc.MarkLineNumber(cvFile, 25);
            enc.LoadConstantI4(101);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case2);
            enc.MarkLineNumber(cvFile, 26);
            enc.LoadConstantI4(102);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case3);
            enc.MarkLineNumber(cvFile, 27);
            enc.LoadConstantI4(103);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case4);
            enc.MarkLineNumber(cvFile, 28);
            enc.LoadConstantI4(104);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case5);
            enc.MarkLineNumber(cvFile, 29);
            enc.LoadConstantI4(105);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case6);
            enc.MarkLineNumber(cvFile, 30);
            enc.LoadConstantI4(106);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case7);
            enc.MarkLineNumber(cvFile, 31);
            enc.LoadConstantI4(107);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case8);
            enc.MarkLineNumber(cvFile, 32);
            enc.LoadConstantI4(108);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case9);
            enc.MarkLineNumber(cvFile, 33);
            enc.LoadConstantI4(109);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_default);
            enc.MarkLineNumber(cvFile, 34);
            enc.OpCode(ILOpCode.Ldc_i4_m1);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLabel(lbl_end);
            enc.MarkLineNumber(cvFile, 36);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            int denseMaxStack = machine == Machine.Arm64 ? 2 : 1;

            bodyEncoder.AddMethodBody(denseMethod, "?dense_switch@@$$J0YMHH@Z", enc,
                maxStack: denseMaxStack, localVariablesSignature: dLocalsSigHandle, attributes: 0,
                debugName: "dense_switch");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 40);
            enc.OpCode(ILOpCode.Ldc_i4_0);              // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);               // IL_0001
            enc.LoadConstantI4(100);                     // IL_0002: ldc.i4.s 100
            enc.Call(sparseMethod);                       // IL_0004: call sparse_switch
            enc.OpCode(ILOpCode.Ldc_i4_5);              // IL_0009: ldc.i4.5
            enc.Call(denseMethod);                        // IL_000A: call dense_switch
            enc.OpCode(ILOpCode.Add);                    // IL_000F: add
            enc.OpCode(ILOpCode.Stloc_0);               // IL_0010
            enc.MarkLineNumber(cvFile, 41);
            enc.OpCode(ILOpCode.Ldloc_0);               // IL_0011
            enc.OpCode(ILOpCode.Ret);                    // IL_0012

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
