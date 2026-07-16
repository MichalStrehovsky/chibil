using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ConditionalTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "conditional", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "conditional.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "conditional", refDir, "conditional.obj"));
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

        // ─── MethodDef #1: abs_val(int) -> int ────────────────────────────
        var absValSig = new BlobBuilder();
        new BlobEncoder(absValSig).MethodSignature()
            .Parameters(1, out var avRetEnc, out var avParEnc);
        ClrIjw.EncodeCdeclI4Return(avRetEnc, callConvCdeclRef);
        avParEnc.AddParameter().Type().Int32();

        var absValMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("abs_val"), md.GetOrAddBlob(absValSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // abs_val locals: 1 x int32
        var avLocalsSig = new BlobBuilder();
        new BlobEncoder(avLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var avLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(avLocalsSig));

        // ─── MethodDef #2: comma_test(int, int) -> int ────────────────────
        var commaSig = new BlobBuilder();
        new BlobEncoder(commaSig).MethodSignature()
            .Parameters(2, out var ctRetEnc, out var ctParEnc);
        ClrIjw.EncodeCdeclI4Return(ctRetEnc, callConvCdeclRef);
        ctParEnc.AddParameter().Type().Int32();
        ctParEnc.AddParameter().Type().Int32();

        var commaMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("comma_test"), md.GetOrAddBlob(commaSig), 0,
            MetadataTokens.ParameterHandle(2));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // comma_test locals: 2 x int32
        var ctLocalsSig = new BlobBuilder();
        var ctLocalsEnc = new BlobEncoder(ctLocalsSig).LocalVariableSignature(2);
        for (int i = 0; i < 2; i++) ctLocalsEnc.AddVariable().Type().Int32();
        var ctLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ctLocalsSig));

        // ─── MethodDef #3: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(4));

        // main locals: 1 x int32 — reuse abs_val's local sig
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(avLocalsSig));

        md.AddModule(0, md.GetOrAddString("conditional.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("conditional.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "conditional.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for abs_val ──────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_neg = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0001
            enc.Branch(ILOpCode.Blt_s, lbl_neg);    // IL_0002: if x < 0 goto neg
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0004
            enc.Branch(ILOpCode.Br_s, lbl_done);    // IL_0005: goto done
            enc.MarkLabel(lbl_neg);                 // IL_0007
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0007
            enc.OpCode(ILOpCode.Neg);               // IL_0008
            enc.MarkLabel(lbl_done);                // IL_0009
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0009

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000A
            enc.OpCode(ILOpCode.Ret);               // IL_000B

            bodyEncoder.AddMethodBody(absValMethod, "?abs_val@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: avLocalsSigHandle, attributes: 0,
                debugName: "abs_val");
        }

        // ─── Emit IL for comma_test ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 12);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_0001
            enc.OpCode(ILOpCode.Add);               // IL_0002
            enc.StoreArgument(0);                   // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_1);           // IL_0005
            enc.OpCode(ILOpCode.Ldc_i4_2);          // IL_0006
            enc.OpCode(ILOpCode.Add);               // IL_0007
            enc.StoreArgument(1);                   // IL_0008: starg.s V_1
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_000A
            enc.OpCode(ILOpCode.Ldarg_1);           // IL_000B
            enc.OpCode(ILOpCode.Add);               // IL_000C
            enc.OpCode(ILOpCode.Stloc_1);           // IL_000D

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_000E
            enc.OpCode(ILOpCode.Stloc_0);           // IL_000F

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0010
            enc.OpCode(ILOpCode.Ret);               // IL_0011

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(ctLocalsSigHandle), "x"),
            };

            bodyEncoder.AddMethodBody(commaMethod, "?comma_test@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: ctLocalsSigHandle, attributes: 0,
                debugName: "comma_test", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001
            enc.LoadConstantI4(-5);                 // IL_0002: ldc.i4.s -5
            enc.Call(absValMethod);                  // IL_0004: call abs_val
            enc.LoadConstantI4(10);                 // IL_0009: ldc.i4.s 10
            enc.LoadConstantI4(20);                 // IL_000B: ldc.i4.s 20
            enc.Call(commaMethod);                  // IL_000D: call comma_test
            enc.OpCode(ILOpCode.Add);               // IL_0012
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0013

            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0014
            enc.OpCode(ILOpCode.Ret);               // IL_0015

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            absValMethod, "abs_val", "?abs_val@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            commaMethod, "comma_test", "?comma_test@@$$J0YAHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            mainMethod, "main", "?main@@$$J0YAHXZ");

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
