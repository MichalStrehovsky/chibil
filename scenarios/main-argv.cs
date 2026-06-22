using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class MainArgvTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "main-argv", refDir, "main-argv.obj"));
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

        // TypeRef for modopt(IsSignUnspecifiedByte) on char**
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

        // ─── TypeDef: <Module> ────────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: main(int32, Ptr Ptr modopt(IsSignUnspecifiedByte) int8) -> int32
        var mainSig = new BlobBuilder();
        var mainSigEnc = new BlobEncoder(mainSig).MethodSignature();
        mainSigEnc.Parameters(2, out var retEnc, out var parEnc);
        retEnc.Type().Int32();
        // Param 1: int32 (argc)
        parEnc.AddParameter().Type().Int32();
        // Param 2: Ptr Ptr modopt(IsSignUnspecifiedByte) int8 (argv / char**)
        var p2 = parEnc.AddParameter().Type();
        p2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        p2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        p2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        p2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        p2.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argc"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argv"), 2);

        // main locals: (int32)
        var localsSig = new BlobBuilder();
        new BlobEncoder(localsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var localsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(localsSig));

        // ─── MethodDef #2: __CxxPureMSILEntry(int32, Ptr Ptr modopt(..) int8, Ptr Ptr modopt(..) int8) -> int32
        var entrySig = new BlobBuilder();
        var entrySigEnc = new BlobEncoder(entrySig).MethodSignature();
        entrySigEnc.Parameters(3, out var eRetEnc, out var eParEnc);
        eRetEnc.Type().Int32();
        eParEnc.AddParameter().Type().Int32();
        // argv param (same char** type)
        var ep2 = eParEnc.AddParameter().Type();
        ep2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        ep2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        ep2.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        // envp param (same char** type)
        var ep3 = eParEnc.AddParameter().Type();
        ep3.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep3.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        ep3.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        ep3.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        ep3.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var entryMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("__CxxPureMSILEntry"), md.GetOrAddBlob(entrySig), 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argc"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("argv"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("envp"), 3);

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("main-argv.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("main-argv.obj",
            language: CodeViewLanguage.Cpp, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "main-argv.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_else = enc.DefineLabel();
            var lbl_end = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);            // IL_0001
            enc.OpCode(ILOpCode.Ldarg_0);            // IL_0002: argc
            enc.OpCode(ILOpCode.Ldc_i4_1);           // IL_0003
            enc.Branch(ILOpCode.Ble_s, lbl_else);    // IL_0004: if argc <= 1 goto else

            // argv[1][0]
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldarg_1);            // IL_0006: argv
            if (machine == Machine.I386)
            {
                enc.OpCode(ILOpCode.Ldc_i4_4);       // IL_0007: sizeof(char*) = 4
                enc.OpCode(ILOpCode.Add);             // IL_0008
                enc.OpCode(ILOpCode.Ldind_i4);        // IL_0009: load argv[1] (32-bit ptr)
                enc.OpCode(ILOpCode.Ldc_i4_1);       // IL_000A: sizeof(char) = 1
                enc.OpCode(ILOpCode.Ldc_i4_0);       // IL_000B: index 0
                enc.OpCode(ILOpCode.Mul);             // IL_000C
                enc.OpCode(ILOpCode.Add);             // IL_000D
            }
            else
            {
                enc.OpCode(ILOpCode.Ldc_i4_8);       // IL_0007: sizeof(char*) = 8
                enc.OpCode(ILOpCode.Conv_i8);         // IL_0008
                enc.OpCode(ILOpCode.Add);             // IL_0009
                enc.OpCode(ILOpCode.Ldind_i8);        // IL_000A: load argv[1] (64-bit ptr)
                enc.OpCode(ILOpCode.Ldc_i4_1);       // IL_000B: sizeof(char) = 1
                enc.OpCode(ILOpCode.Conv_i8);         // IL_000C
                enc.OpCode(ILOpCode.Ldc_i4_0);       // IL_000D: index 0
                enc.OpCode(ILOpCode.Conv_i8);         // IL_000E
                enc.OpCode(ILOpCode.Mul);             // IL_000F
                enc.OpCode(ILOpCode.Add);             // IL_0010
            }
            enc.OpCode(ILOpCode.Ldind_i1);            // load byte (argv[1][0])
            enc.OpCode(ILOpCode.Stloc_0);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_else);
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLabel(lbl_end);
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            string mainCoffName = machine == Machine.I386
                ? "?main@@$$HYMHHPAPAD@Z"
                : "?main@@$$HYMHHPEAPEAD@Z";
            bodyEncoder.AddMethodBody(mainMethod, mainCoffName, enc,
                maxStack: 3, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── Emit IL for __CxxPureMSILEntry ───────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldarg_0);             // IL_0000: argc
            enc.OpCode(ILOpCode.Ldarg_1);             // IL_0001: argv
            enc.Call(mainMethod);                      // IL_0002: call main
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0007
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0008
            enc.OpCode(ILOpCode.Ret);                 // IL_0009

            string entryCoffName = machine == Machine.I386
                ? "?__CxxPureMSILEntry@@$$J0YMHHPAPAD0@Z"
                : "?__CxxPureMSILEntry@@$$J0YMHHPEAPEAD0@Z";
            bodyEncoder.AddMethodBody(entryMethod, entryCoffName, enc,
                maxStack: 2, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "__CxxPureMSILEntry");
        }

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
