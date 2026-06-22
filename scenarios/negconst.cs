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

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "negconst", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "negconst.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "negconst", refDir, "negconst.obj"));
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
        // int32()
        var intVoidSig = new BlobBuilder();
        new BlobEncoder(intVoidSig).MethodSignature()
            .Parameters(0, out var ivRet, out var ivPar);
        ClrIjw.EncodeCdeclI4Return(ivRet, callConvCdeclRef);
        var intVoidSigBlob = md.GetOrAddBlob(intVoidSig);

        // uint32()
        var uintVoidSig = new BlobBuilder();
        new BlobEncoder(uintVoidSig).MethodSignature()
            .Parameters(0, out var uvRet, out var uvPar);
        ClrIjw.WriteCdeclModOpt(uvRet, callConvCdeclRef).UInt32();

        // int64()
        var longVoidSig = new BlobBuilder();
        new BlobEncoder(longVoidSig).MethodSignature()
            .Parameters(0, out var lvRet, out var lvPar);
        ClrIjw.WriteCdeclModOpt(lvRet, callConvCdeclRef).Int64();

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
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("negconst.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "negconst.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for neg_one: ldc.i4.m1, stloc.0, ldloc.0, ret ──────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadConstantI4(-1);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(negOneMethod, "?neg_one@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: negOneLocalsSigHandle, attributes: 0,
                debugName: "neg_one");
        }

        // ─── Emit IL for int_min: ldc.i4 0x80000000, stloc.0, ldloc.0, ret
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 11);
            enc.LoadConstantI4(unchecked((int)0x80000000));
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(intMinMethod, "?int_min@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: intMinLocalsSigHandle, attributes: 0,
                debugName: "int_min");
        }

        // ─── Emit IL for uint_max: ldc.i4.m1, stloc.0, ldloc.0, ret ─────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 12);
            enc.LoadConstantI4(-1);  // 0xFFFFFFFF = ldc.i4.m1
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(uintMaxMethod, "?uint_max@@$$J0YAIXZ", enc,
                maxStack: 1, localVariablesSignature: uintMaxLocalsSigHandle, attributes: 0,
                debugName: "uint_max");
        }

        // ─── Emit IL for ll_max: ldc.i8 0x7FFFFFFFFFFFFFFF ───────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 13);
            enc.LoadConstantI8(0x7FFFFFFFFFFFFFFF);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(llMaxMethod, "?ll_max@@$$J0YA_JXZ", enc,
                maxStack: 1, localVariablesSignature: llMaxLocalsSigHandle, attributes: 0,
                debugName: "ll_max");
        }

        // ─── Emit IL for ll_min: ldc.i8 0x8000000000000000 ───────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 14);
            enc.LoadConstantI8(unchecked((long)0x8000000000000000));
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(llMinMethod, "?ll_min@@$$J0YA_JXZ", enc,
                maxStack: 1, localVariablesSignature: llMinLocalsSigHandle, attributes: 0,
                debugName: "ll_min");
        }

        // ─── Emit IL for small_neg: ldc.i4.s -42 ─────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 15);
            enc.LoadConstantI4(-42);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(smallNegMethod, "?small_neg@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: smallNegLocalsSigHandle, attributes: 0,
                debugName: "small_neg");
        }

        // ─── Emit IL for zero: ldc.i4.0 ──────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadConstantI4(0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(zeroMethod, "?zero@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: zeroLocalsSigHandle, attributes: 0,
                debugName: "zero");
        }

        // ─── Emit IL for small_pos: ldc.i4.8 ─────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadConstantI4(8);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(smallPosMethod, "?small_pos@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: smallPosLocalsSigHandle, attributes: 0,
                debugName: "small_pos");
        }

        // ─── Emit IL for medium_pos: ldc.i4.s 127 ────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 18);
            enc.LoadConstantI4(127);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(mediumPosMethod, "?medium_pos@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: mediumPosLocalsSigHandle, attributes: 0,
                debugName: "medium_pos");
        }

        // ─── Emit IL for large_pos: ldc.i4 0x3E8 ─────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
            enc.MarkLineNumber(cvFile, 19);
            enc.LoadConstantI4(0x3E8);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);
            bodyEncoder.AddMethodBody(largePosMethod, "?large_pos@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: largePosLocalsSigHandle, attributes: 0,
                debugName: "large_pos");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 23);
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
            enc.MarkLineNumber(cvFile, 25);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── IJW machinery for exported methods ───────────────────────────
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(negOneMethod), "neg_one", "?neg_one@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(intMinMethod), "int_min", "?int_min@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(uintMaxMethod), "uint_max", "?uint_max@@$$J0YAIXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llMaxMethod), "ll_max", "?ll_max@@$$J0YA_JXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(llMinMethod), "ll_min", "?ll_min@@$$J0YA_JXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(smallNegMethod), "small_neg", "?small_neg@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(zeroMethod), "zero", "?zero@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(smallPosMethod), "small_pos", "?small_pos@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(mediumPosMethod), "medium_pos", "?medium_pos@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(largePosMethod), "large_pos", "?large_pos@@$$J0YAHXZ");
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
