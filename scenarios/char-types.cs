using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class CharTypesTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "char-types", refDir, "char-types.obj"));
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

        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

        // ─── TypeDef: <Module> ────────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: char_func(modopt(IsSignUnspecifiedByte) int8, int8, uint8) -> int32
        var cfSig = new BlobBuilder();
        var cfSigEnc = new BlobEncoder(cfSig).MethodSignature();
        cfSigEnc.Parameters(3, out var cfRetEnc, out var cfParEnc);
        cfRetEnc.Type().Int32();
        // Param a: modopt(IsSignUnspecifiedByte) int8 — plain C 'char'
        var p1 = cfParEnc.AddParameter().Type();
        p1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        p1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        p1.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        // Param b: int8 — 'signed char'
        cfParEnc.AddParameter().Type().SByte();
        // Param c: uint8 — 'unsigned char'
        cfParEnc.AddParameter().Type().Byte();

        var charFuncMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("char_func"), md.GetOrAddBlob(cfSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("c"), 3);

        // char_func locals: (int32)
        var cfLocalsSig = new BlobBuilder();
        new BlobEncoder(cfLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var cfLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cfLocalsSig));

        // ─── MethodDef #2: main() -> int32
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature().Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(4));

        // main locals: (int32, uint8, int8, modopt(IsSignUnspecifiedByte) int8)
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(4);
        mainLocalsEnc.AddVariable().Type().Int32();   // V_0: return temp
        mainLocalsEnc.AddVariable().Type().Byte();    // V_1: z (unsigned char)
        mainLocalsEnc.AddVariable().Type().SByte();   // V_2: y (signed char)
        // V_3: x (char) — modopt(IsSignUnspecifiedByte) int8
        var v3 = mainLocalsEnc.AddVariable().Type();
        v3.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        v3.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        v3.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("char-types.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("char-types.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "char-types.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── IL for char_func ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldarg_2);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(charFuncMethod, "?char_func@@$$J0YMHDCE@Z", enc,
                maxStack: 2, localVariablesSignature: cfLocalsSigHandle, attributes: 0,
                debugName: "char_func");
        }

        // ─── IL for main ──────────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 12);
            enc.LoadConstantI4(65);     // 'A'
            enc.OpCode(ILOpCode.Stloc_3);

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldc_i4_m1);
            enc.OpCode(ILOpCode.Stloc_2);

            enc.MarkLineNumber(cvFile, 14);
            enc.LoadConstantI4(0xFF);
            enc.OpCode(ILOpCode.Stloc_1);

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.Call(charFuncMethod);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var localSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(mainLocalsSigHandle), "x"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "y"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "z"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: localSlots);
        }

        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
