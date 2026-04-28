using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class UnsignedTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "unsigned", refDir, "unsigned.obj"));
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
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── Shared signatures ────────────────────────────────────────────
        // uint32(uint32, uint32)
        var uuuSig = new BlobBuilder();
        new BlobEncoder(uuuSig).MethodSignature()
            .Parameters(2, out var uuuRet, out var uuuPar);
        uuuRet.Type().UInt32();
        uuuPar.AddParameter().Type().UInt32();
        uuuPar.AddParameter().Type().UInt32();
        var uuuSigBlob = md.GetOrAddBlob(uuuSig);

        // uint32(uint32, int32)
        var uuiSig = new BlobBuilder();
        new BlobEncoder(uuiSig).MethodSignature()
            .Parameters(2, out var uuiRet, out var uuiPar);
        uuiRet.Type().UInt32();
        uuiPar.AddParameter().Type().UInt32();
        uuiPar.AddParameter().Type().Int32();
        var uuiSigBlob = md.GetOrAddBlob(uuiSig);

        // int32(uint32, uint32)
        var iuuSig = new BlobBuilder();
        new BlobEncoder(iuuSig).MethodSignature()
            .Parameters(2, out var iuuRet, out var iuuPar);
        iuuRet.Type().Int32();
        iuuPar.AddParameter().Type().UInt32();
        iuuPar.AddParameter().Type().UInt32();
        var iuuSigBlob = md.GetOrAddBlob(iuuSig);

        // int32()
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRet, out var mainPar);
        mainRet.Type().Int32();

        // ─── MethodDef #1: udiv ───────────────────────────────────────────
        var udivMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("udiv"), uuuSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var udivLocalsSig = new BlobBuilder();
        new BlobEncoder(udivLocalsSig).LocalVariableSignature(1).AddVariable().Type().UInt32();
        var udivLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(udivLocalsSig));

        // ─── MethodDef #2: umod ───────────────────────────────────────────
        var umodMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("umod"), uuuSigBlob, 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var umodLocalsSig = new BlobBuilder();
        new BlobEncoder(umodLocalsSig).LocalVariableSignature(1).AddVariable().Type().UInt32();
        var umodLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(umodLocalsSig));

        // ─── MethodDef #3: ushr ───────────────────────────────────────────
        var ushrMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ushr"), uuiSigBlob, 0,
            MetadataTokens.ParameterHandle(5));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 2);

        var ushrLocalsSig = new BlobBuilder();
        new BlobEncoder(ushrLocalsSig).LocalVariableSignature(1).AddVariable().Type().UInt32();
        var ushrLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ushrLocalsSig));

        // ─── MethodDef #4: ult ────────────────────────────────────────────
        var ultMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ult"), iuuSigBlob, 0,
            MetadataTokens.ParameterHandle(7));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var ultLocalsSig = new BlobBuilder();
        new BlobEncoder(ultLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var ultLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ultLocalsSig));

        // ─── MethodDef #5: ule ────────────────────────────────────────────
        var uleMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ule"), iuuSigBlob, 0,
            MetadataTokens.ParameterHandle(9));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var uleLocalsSig = new BlobBuilder();
        new BlobEncoder(uleLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var uleLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(uleLocalsSig));

        // ─── MethodDef #6: ugt ────────────────────────────────────────────
        var ugtMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ugt"), iuuSigBlob, 0,
            MetadataTokens.ParameterHandle(11));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var ugtLocalsSig = new BlobBuilder();
        new BlobEncoder(ugtLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var ugtLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ugtLocalsSig));

        // ─── MethodDef #7: uge ────────────────────────────────────────────
        var ugeMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("uge"), iuuSigBlob, 0,
            MetadataTokens.ParameterHandle(13));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var ugeLocalsSig = new BlobBuilder();
        new BlobEncoder(ugeLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var ugeLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ugeLocalsSig));

        // ─── MethodDef #8: main ───────────────────────────────────────────
        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(15));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("unsigned.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("unsigned.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "unsigned.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for udiv ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Div_un);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(udivMethod, "?udiv@@$$J0YMIII@Z", enc,
                maxStack: 2, localVariablesSignature: udivLocalsSigHandle, attributes: 0,
                debugName: "udiv");
        }

        // ─── Emit IL for umod ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Rem_un);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(umodMethod, "?umod@@$$J0YMIII@Z", enc,
                maxStack: 2, localVariablesSignature: umodLocalsSigHandle, attributes: 0,
                debugName: "umod");
        }

        // ─── Emit IL for ushr ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Shr_un);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(ushrMethod, "?ushr@@$$J0YMIIH@Z", enc,
                maxStack: 2, localVariablesSignature: ushrLocalsSigHandle, attributes: 0,
                debugName: "ushr");
        }

        // ─── Emit IL for ult ──────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_false = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.Branch(ILOpCode.Bge_un_s, lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_done);
            enc.MarkLabel(lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_done);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(ultMethod, "?ult@@$$J0YMHII@Z", enc,
                maxStack: 2, localVariablesSignature: ultLocalsSigHandle, attributes: 0,
                debugName: "ult");
        }

        // ─── Emit IL for ule ──────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_false = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.Branch(ILOpCode.Bgt_un_s, lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_done);
            enc.MarkLabel(lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_done);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(uleMethod, "?ule@@$$J0YMHII@Z", enc,
                maxStack: 2, localVariablesSignature: uleLocalsSigHandle, attributes: 0,
                debugName: "ule");
        }

        // ─── Emit IL for ugt ──────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_false = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.Branch(ILOpCode.Ble_un_s, lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_done);
            enc.MarkLabel(lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_done);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(ugtMethod, "?ugt@@$$J0YMHII@Z", enc,
                maxStack: 2, localVariablesSignature: ugtLocalsSigHandle, attributes: 0,
                debugName: "ugt");
        }

        // ─── Emit IL for uge ──────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_false = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.Branch(ILOpCode.Blt_un_s, lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.Branch(ILOpCode.Br_s, lbl_done);
            enc.MarkLabel(lbl_false);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.MarkLabel(lbl_done);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(ugeMethod, "?uge@@$$J0YMHII@Z", enc,
                maxStack: 2, localVariablesSignature: ugeLocalsSigHandle, attributes: 0,
                debugName: "uge");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.LoadConstantI4(100);              // ldc.i4.s 100
            enc.LoadConstantI4(7);                // ldc.i4.7
            enc.Call(udivMethod);                  // call udiv
            enc.LoadConstantI4(100);              // ldc.i4.s 100
            enc.LoadConstantI4(7);                // ldc.i4.7
            enc.Call(umodMethod);                  // call umod
            enc.OpCode(ILOpCode.Add);
            enc.LoadConstantI4(-1);               // ldc.i4.m1 (0xFFFFFFFF)
            enc.LoadConstantI4(1);                // ldc.i4.1
            enc.Call(ushrMethod);                  // call ushr
            enc.OpCode(ILOpCode.Add);
            enc.LoadConstantI4(3);                // ldc.i4.3
            enc.LoadConstantI4(5);                // ldc.i4.5
            enc.Call(ultMethod);                   // call ult
            enc.OpCode(ILOpCode.Add);
            enc.LoadConstantI4(5);                // ldc.i4.5
            enc.LoadConstantI4(3);                // ldc.i4.3
            enc.Call(ugeMethod);                   // call uge
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
