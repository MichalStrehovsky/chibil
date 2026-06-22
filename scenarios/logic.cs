using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class LogicTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "logic", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "logic.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "logic", refDir, "logic.obj"));
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

        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: logic(int, int) -> cmod_opt(CallConvCdecl) int ──
        var logicSig = new BlobBuilder();
        new BlobEncoder(logicSig).MethodSignature()
            .Parameters(2, out var lRetEnc, out var lParEnc);
        ClrIjw.EncodeCdeclI4Return(lRetEnc, callConvCdeclRef);
        lParEnc.AddParameter().Type().Int32();
        lParEnc.AddParameter().Type().Int32();

        var logicMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("logic"), md.GetOrAddBlob(logicSig), 0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // 6 locals for logic
        var logicLocalsSig = new BlobBuilder();
        var logicLocalsEnc = new BlobEncoder(logicLocalsSig).LocalVariableSignature(6);
        for (int i = 0; i < 6; i++) logicLocalsEnc.AddVariable().Type().Int32();
        var logicLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(logicLocalsSig));

        // ─── MethodDef #2: main() -> cmod_opt(CallConvCdecl) int ───────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(3));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("logic.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("logic.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "logic.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for logic ────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // land: a && b
            var lbl_land_false = enc.DefineLabel();
            var lbl_land_done = enc.DefineLabel();
            // lor: a || b
            var lbl_lor_true = enc.DefineLabel();
            var lbl_lor_done = enc.DefineLabel();
            // lnot: !a
            var lbl_lnot0 = enc.DefineLabel();
            var lbl_lnot_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.Branch(ILOpCode.Brfalse_s, lbl_land_false); // IL_0001
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0003
            enc.Branch(ILOpCode.Brfalse_s, lbl_land_false); // IL_0004
            enc.OpCode(ILOpCode.Ldc_i4_1);         // IL_0006
            enc.OpCode(ILOpCode.Stloc_1);          // IL_0007
            enc.Branch(ILOpCode.Br_s, lbl_land_done); // IL_0008
            enc.MarkLabel(lbl_land_false);          // IL_000A
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_000A
            enc.OpCode(ILOpCode.Stloc_1);          // IL_000B
            enc.MarkLabel(lbl_land_done);           // IL_000C
            enc.OpCode(ILOpCode.Ldloc_1);          // IL_000C
            enc.StoreLocal(5);                     // IL_000D: stloc.s V_5

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_000F
            enc.Branch(ILOpCode.Brtrue_s, lbl_lor_true); // IL_0010
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0012
            enc.Branch(ILOpCode.Brtrue_s, lbl_lor_true); // IL_0013
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_0015
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0016
            enc.Branch(ILOpCode.Br_s, lbl_lor_done); // IL_0017
            enc.MarkLabel(lbl_lor_true);            // IL_0019
            enc.OpCode(ILOpCode.Ldc_i4_1);         // IL_0019
            enc.OpCode(ILOpCode.Stloc_0);          // IL_001A
            enc.MarkLabel(lbl_lor_done);            // IL_001B
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_001B
            enc.StoreLocal(4);                     // IL_001C: stloc.s V_4

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_001E
            enc.Branch(ILOpCode.Brtrue_s, lbl_lnot0); // IL_001F
            enc.OpCode(ILOpCode.Ldc_i4_1);         // IL_0021
            enc.Branch(ILOpCode.Br_s, lbl_lnot_done); // IL_0022
            enc.MarkLabel(lbl_lnot0);               // IL_0024
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_0024
            enc.MarkLabel(lbl_lnot_done);           // IL_0025
            enc.OpCode(ILOpCode.Stloc_3);          // IL_0025

            enc.MarkLineNumber(cvFile, 9);
            enc.LoadLocal(5);                      // IL_0026
            enc.LoadLocal(4);                      // IL_0028
            enc.OpCode(ILOpCode.Add);              // IL_002A
            enc.OpCode(ILOpCode.Ldloc_3);          // IL_002B
            enc.OpCode(ILOpCode.Add);              // IL_002C
            enc.OpCode(ILOpCode.Stloc_2);          // IL_002D

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldloc_2);          // IL_002E
            enc.OpCode(ILOpCode.Ret);              // IL_002F

            var localSlots = new[] {
                new CodeViewManSlot(4, MetadataTokens.GetToken(logicLocalsSigHandle), "lor"),
                new CodeViewManSlot(5, MetadataTokens.GetToken(logicLocalsSigHandle), "land"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(logicLocalsSigHandle), "lnot"),
            };

            bodyEncoder.AddMethodBody(logicMethod, "?logic@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: logicLocalsSigHandle, attributes: 0,
                debugName: "logic", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.Call(logicMethod);

            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(logicMethod), "logic", "?logic@@$$J0YAHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

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
