using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class CompareTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "compare", refDir, "compare.obj"));
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

        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: compare(int, int) -> int ───────────────────────
        var compareSig = new BlobBuilder();
        new BlobEncoder(compareSig).MethodSignature()
            .Parameters(2, out var cRetEnc, out var cParEnc);
        cRetEnc.Type().Int32();
        cParEnc.AddParameter().Type().Int32();
        cParEnc.AddParameter().Type().Int32();

        var compareMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("compare"), md.GetOrAddBlob(compareSig), 0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var compareLocalsSig = new BlobBuilder();
        var compareLocalsEnc = new BlobEncoder(compareLocalsSig).LocalVariableSignature(7);
        for (int i = 0; i < 7; i++) compareLocalsEnc.AddVariable().Type().Int32();
        var compareLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(compareLocalsSig));

        // ─── MethodDef #2: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(3));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("compare.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("compare.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "compare.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for compare ──────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // eq: a == b  (bne.un.s skips to 0 if not equal)
            var lbl_eq0 = enc.DefineLabel();
            var lbl_eq_done = enc.DefineLabel();
            var lbl_ne0 = enc.DefineLabel();
            var lbl_ne_done = enc.DefineLabel();
            var lbl_lt0 = enc.DefineLabel();
            var lbl_lt_done = enc.DefineLabel();
            var lbl_le0 = enc.DefineLabel();
            var lbl_le_done = enc.DefineLabel();
            var lbl_gt0 = enc.DefineLabel();
            var lbl_gt_done = enc.DefineLabel();
            var lbl_ge0 = enc.DefineLabel();
            var lbl_ge_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.Branch(ILOpCode.Bne_un_s, lbl_eq0); // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0004
            enc.Branch(ILOpCode.Br_s, lbl_eq_done); // IL_0005
            enc.MarkLabel(lbl_eq0);                // IL_0007
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0007
            enc.MarkLabel(lbl_eq_done);            // IL_0008
            enc.StoreLocal(6);                     // IL_0008: stloc.s V_6

            // ne: a != b  (beq.s skips to 0 if equal)
            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_000A
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_000B
            enc.Branch(ILOpCode.Beq_s, lbl_ne0);  // IL_000C
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_000E
            enc.Branch(ILOpCode.Br_s, lbl_ne_done); // IL_000F
            enc.MarkLabel(lbl_ne0);                // IL_0011
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0011
            enc.MarkLabel(lbl_ne_done);            // IL_0012
            enc.StoreLocal(5);                     // IL_0012: stloc.s V_5

            // lt: a < b  (bge.s skips to 0 if a >= b)
            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0014
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0015
            enc.Branch(ILOpCode.Bge_s, lbl_lt0);  // IL_0016
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0018
            enc.Branch(ILOpCode.Br_s, lbl_lt_done); // IL_0019
            enc.MarkLabel(lbl_lt0);                // IL_001B
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_001B
            enc.MarkLabel(lbl_lt_done);            // IL_001C
            enc.StoreLocal(4);                     // IL_001C: stloc.s V_4

            // le: a <= b  (bgt.s skips to 0 if a > b)
            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_001E
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_001F
            enc.Branch(ILOpCode.Bgt_s, lbl_le0);  // IL_0020
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0022
            enc.Branch(ILOpCode.Br_s, lbl_le_done); // IL_0023
            enc.MarkLabel(lbl_le0);                // IL_0025
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0025
            enc.MarkLabel(lbl_le_done);            // IL_0026
            enc.StoreLocal(3);                     // IL_0026: stloc.3

            // gt: a > b  (ble.s skips to 0 if a <= b)
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0027
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0028
            enc.Branch(ILOpCode.Ble_s, lbl_gt0);  // IL_0029
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_002B
            enc.Branch(ILOpCode.Br_s, lbl_gt_done); // IL_002C
            enc.MarkLabel(lbl_gt0);                // IL_002E
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_002E
            enc.MarkLabel(lbl_gt_done);            // IL_002F
            enc.StoreLocal(2);                     // IL_002F: stloc.2

            // ge: a >= b  (blt.s skips to 0 if a < b)
            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0030
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0031
            enc.Branch(ILOpCode.Blt_s, lbl_ge0);  // IL_0032
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0034
            enc.Branch(ILOpCode.Br_s, lbl_ge_done); // IL_0035
            enc.MarkLabel(lbl_ge0);                // IL_0037
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0037
            enc.MarkLabel(lbl_ge_done);            // IL_0038
            enc.StoreLocal(1);                     // IL_0038: stloc.1

            enc.MarkLineNumber(cvFile, 12);
            enc.LoadLocal(6);                     // IL_0039
            enc.LoadLocal(5);                     // IL_003B
            enc.OpCode(ILOpCode.Add);             // IL_003D
            enc.LoadLocal(4);                     // IL_003E
            enc.OpCode(ILOpCode.Add);             // IL_0040
            enc.OpCode(ILOpCode.Ldloc_3);         // IL_0041
            enc.OpCode(ILOpCode.Add);             // IL_0042
            enc.OpCode(ILOpCode.Ldloc_2);         // IL_0043
            enc.OpCode(ILOpCode.Add);             // IL_0044
            enc.OpCode(ILOpCode.Ldloc_1);         // IL_0045
            enc.OpCode(ILOpCode.Add);             // IL_0046
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0047

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0048
            enc.OpCode(ILOpCode.Ret);             // IL_0049

            var localSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(compareLocalsSigHandle), "le"),
                new CodeViewManSlot(5, MetadataTokens.GetToken(compareLocalsSigHandle), "ne"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(compareLocalsSigHandle), "ge"),
                new CodeViewManSlot(6, MetadataTokens.GetToken(compareLocalsSigHandle), "eq"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(compareLocalsSigHandle), "lt"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(compareLocalsSigHandle), "gt"),
            };

            bodyEncoder.AddMethodBody(compareMethod, "?compare@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: compareLocalsSigHandle, attributes: 0,
                debugName: "compare", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.LoadConstantI4(10);
            enc.LoadConstantI4(20);
            enc.Call(compareMethod);

            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

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
