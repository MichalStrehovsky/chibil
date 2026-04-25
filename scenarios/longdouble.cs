using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using Xunit;

public class LongdoubleTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "longdouble", refDir, "longdouble.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        byte[] mscorlibHash = machine == Machine.I386
            ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
            : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
        CodeViewMachine cvMachine = machine == Machine.I386 ? CodeViewMachine.I386 : CodeViewMachine.Arm64;

        var md = new MetadataBuilder();

        // ─── AssemblyRef: mscorlib ────────────────────────────────────────
        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: IsLong ──────────────────────────────────────────────
        var isLongRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsLong"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: ld_add(modopt(IsLong) float64, modopt(IsLong) float64) -> modopt(IsLong) float64
        var ldAddSig = new BlobBuilder();
        {
            var enc = new BlobEncoder(ldAddSig).MethodSignature();
            enc.Parameters(2, out var retEnc, out var parEnc);
            var retType = retEnc.Type();
            retType.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            retType.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
            retType.Builder.WriteByte((byte)SignatureTypeCode.Double);
            var p1 = parEnc.AddParameter().Type();
            p1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            p1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
            p1.Builder.WriteByte((byte)SignatureTypeCode.Double);
            var p2 = parEnc.AddParameter().Type();
            p2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            p2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
            p2.Builder.WriteByte((byte)SignatureTypeCode.Double);
        }

        var ldAddMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ld_add"), md.GetOrAddBlob(ldAddSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for ld_add: modopt(IsLong) float64
        var ldAddLocalsSig = new BlobBuilder();
        var ldAddLocalsEnc = new BlobEncoder(ldAddLocalsSig).LocalVariableSignature(1);
        var ldAddLocV0 = ldAddLocalsEnc.AddVariable().Type();
        ldAddLocV0.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        ldAddLocV0.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
        ldAddLocV0.Builder.WriteByte((byte)SignatureTypeCode.Double);
        var ldAddLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ldAddLocalsSig));

        // ─── MethodDef #2: main() -> int32 ────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRet, out var mainPar);
        mainRet.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(3));

        // Locals for main: int32, modopt(IsLong) float64, modopt(IsLong) float64, modopt(IsLong) float64
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(4);
        mainLocalsEnc.AddVariable().Type().Int32();     // V_0
        // V_1: modopt(IsLong) float64 (z)
        var mainLocV1 = mainLocalsEnc.AddVariable().Type();
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainLocV1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.Double);
        // V_2: modopt(IsLong) float64 (y)
        var mainLocV2 = mainLocalsEnc.AddVariable().Type();
        mainLocV2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainLocV2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
        mainLocV2.Builder.WriteByte((byte)SignatureTypeCode.Double);
        // V_3: modopt(IsLong) float64 (x)
        var mainLocV3 = mainLocalsEnc.AddVariable().Type();
        mainLocV3.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainLocV3.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isLongRef));
        mainLocV3.Builder.WriteByte((byte)SignatureTypeCode.Double);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("longdouble.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("longdouble.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for ld_add ───────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(ldAddMethod, "?ld_add@@$$J0YMOOO@Z", enc,
                maxStack: 2, localVariablesSignature: ldAddLocalsSigHandle, attributes: 0,
                debugName: "ld_add");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);               // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);                 // IL_0001
            enc.LoadConstantR8(3.14);                     // IL_0002: ldc.r8 3.14
            enc.StoreLocal(3);                            // IL_000B: stloc.3 (x)
            enc.LoadConstantR8(2.0);                      // IL_000C: ldc.r8 2.0
            enc.OpCode(ILOpCode.Stloc_2);                 // IL_0015: stloc.2 (y)
            enc.OpCode(ILOpCode.Ldloc_3);                 // IL_0016
            enc.OpCode(ILOpCode.Ldloc_2);                 // IL_0017
            enc.Call(ldAddMethod);                         // IL_0018: call ld_add
            enc.OpCode(ILOpCode.Stloc_1);                 // IL_001D: stloc.1 (z)
            enc.OpCode(ILOpCode.Ldloc_1);                 // IL_001E
            enc.OpCode(ILOpCode.Conv_i4);                 // IL_001F
            enc.OpCode(ILOpCode.Stloc_0);                 // IL_0020
            enc.OpCode(ILOpCode.Ldloc_0);                 // IL_0021
            enc.OpCode(ILOpCode.Ret);                      // IL_0022

            var mainLocalSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(mainLocalsSigHandle), "x"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "y"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "z"),
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
