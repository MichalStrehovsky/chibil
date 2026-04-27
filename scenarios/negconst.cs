using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class NegconstTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "negconst", refDir, "negconst.obj"));
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
        // int32()
        var intVoidSig = new BlobBuilder();
        new BlobEncoder(intVoidSig).MethodSignature()
            .Parameters(0, out var ivRet, out var ivPar);
        ivRet.Type().Int32();
        var intVoidSigBlob = md.GetOrAddBlob(intVoidSig);

        // uint32()
        var uintVoidSig = new BlobBuilder();
        new BlobEncoder(uintVoidSig).MethodSignature()
            .Parameters(0, out var uvRet, out var uvPar);
        uvRet.Type().UInt32();

        // int64()
        var longVoidSig = new BlobBuilder();
        new BlobEncoder(longVoidSig).MethodSignature()
            .Parameters(0, out var lvRet, out var lvPar);
        lvRet.Type().Int64();

        // Locals: (int32)
        var intLocalsSig = new BlobBuilder();
        new BlobEncoder(intLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var intLocalsSigBlob = md.GetOrAddBlob(intLocalsSig);

        // Locals: (uint32)
        var uintLocalsSig = new BlobBuilder();
        new BlobEncoder(uintLocalsSig).LocalVariableSignature(1).AddVariable().Type().UInt32();

        // Locals: (int64)
        var longLocalsSig = new BlobBuilder();
        new BlobEncoder(longLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();

        // ─── MethodDef #1: neg_one ────────────────────────────────────────
        var negOneMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("neg_one"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var negOneLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #2: int_min ────────────────────────────────────────
        var intMinMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("int_min"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var intMinLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #3: uint_max ──────────────────────────────────────
        var uintMaxMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("uint_max"), md.GetOrAddBlob(uintVoidSig), 0,
            MetadataTokens.ParameterHandle(1));
        var uintMaxLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(uintLocalsSig));

        // ─── MethodDef #4: ll_max ─────────────────────────────────────────
        var llMaxMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_max"), md.GetOrAddBlob(longVoidSig), 0,
            MetadataTokens.ParameterHandle(1));
        var llMaxLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(longLocalsSig));

        // ─── MethodDef #5: ll_min ─────────────────────────────────────────
        var llMinMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ll_min"), md.GetOrAddBlob(longVoidSig), 0,
            MetadataTokens.ParameterHandle(1));
        var llMinLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(longLocalsSig));

        // ─── MethodDef #6: small_neg ──────────────────────────────────────
        var smallNegMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("small_neg"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var smallNegLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #7: zero ───────────────────────────────────────────
        var zeroMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("zero"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var zeroLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #8: small_pos ──────────────────────────────────────
        var smallPosMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("small_pos"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var smallPosLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #9: medium_pos ─────────────────────────────────────
        var mediumPosMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("medium_pos"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var mediumPosLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #10: large_pos ─────────────────────────────────────
        var largePosMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("large_pos"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var largePosLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── MethodDef #11: main ──────────────────────────────────────────
        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), intVoidSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        var mainLocalsSigHandle = md.AddStandaloneSignature(intLocalsSigBlob);

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("negconst.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("negconst.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "negconst.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for neg_one: ldc.i4.m1, stloc.0, ldloc.0, ret ──────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(-1);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(negOneMethod, "?neg_one@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: negOneLocalsSigHandle, attributes: 0,
                debugName: "neg_one");
        }

        // ─── Emit IL for int_min: ldc.i4 0x80000000, stloc.0, ldloc.0, ret
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(unchecked((int)0x80000000));
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(intMinMethod, "?int_min@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: intMinLocalsSigHandle, attributes: 0,
                debugName: "int_min");
        }

        // ─── Emit IL for uint_max: ldc.i4.m1, stloc.0, ldloc.0, ret ─────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(-1);  // 0xFFFFFFFF = ldc.i4.m1
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(uintMaxMethod, "?uint_max@@$$J0YMIXZ", enc,
                maxStack: 1, localVariablesSignature: uintMaxLocalsSigHandle, attributes: 0,
                debugName: "uint_max");
        }

        // ─── Emit IL for ll_max: ldc.i8 0x7FFFFFFFFFFFFFFF ───────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI8(0x7FFFFFFFFFFFFFFF);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(llMaxMethod, "?ll_max@@$$J0YM_JXZ", enc,
                maxStack: 1, localVariablesSignature: llMaxLocalsSigHandle, attributes: 0,
                debugName: "ll_max");
        }

        // ─── Emit IL for ll_min: ldc.i8 0x8000000000000000 ───────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI8(unchecked((long)0x8000000000000000));
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(llMinMethod, "?ll_min@@$$J0YM_JXZ", enc,
                maxStack: 1, localVariablesSignature: llMinLocalsSigHandle, attributes: 0,
                debugName: "ll_min");
        }

        // ─── Emit IL for small_neg: ldc.i4.s -42 ─────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(-42);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(smallNegMethod, "?small_neg@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: smallNegLocalsSigHandle, attributes: 0,
                debugName: "small_neg");
        }

        // ─── Emit IL for zero: ldc.i4.0 ──────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(zeroMethod, "?zero@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: zeroLocalsSigHandle, attributes: 0,
                debugName: "zero");
        }

        // ─── Emit IL for small_pos: ldc.i4.8 ─────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(8);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(smallPosMethod, "?small_pos@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: smallPosLocalsSigHandle, attributes: 0,
                debugName: "small_pos");
        }

        // ─── Emit IL for medium_pos: ldc.i4.s 127 ────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(127);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(mediumPosMethod, "?medium_pos@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: mediumPosLocalsSigHandle, attributes: 0,
                debugName: "medium_pos");
        }

        // ─── Emit IL for large_pos: ldc.i4 0x3E8 ─────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.LoadConstantI4(0x3E8);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(largePosMethod, "?large_pos@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: largePosLocalsSigHandle, attributes: 0,
                debugName: "large_pos");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Call(negOneMethod);
            enc.Call(intMinMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(uintMaxMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(smallNegMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(zeroMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(smallPosMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(mediumPosMethod);
            enc.OpCode(ILOpCode.Add);
            enc.Call(largePosMethod);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
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
