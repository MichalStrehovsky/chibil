using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class SwitchTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "switch", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "switch.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "switch", refDir, "switch.obj"));
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

        // ─── MethodDef #1: classify(int) -> int ───────────────────────────
        var classifySig = new BlobBuilder();
        new BlobEncoder(classifySig).MethodSignature()
            .Parameters(1, out var cRetEnc, out var cParEnc);
        ClrIjw.EncodeCdeclI4Return(cRetEnc, callConvCdeclRef);
        cParEnc.AddParameter().Type().Int32();

        var classifyMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("classify"), md.GetOrAddBlob(classifySig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // classify locals: 2 x int32 (x86), 3 x int32 (arm64/x64: extra temp)
        int classifyLocalCount = machine == Machine.I386 ? 2 : 3;
        var cLocalsSig = new BlobBuilder();
        var cLocalsEnc = new BlobEncoder(cLocalsSig).LocalVariableSignature(classifyLocalCount);
        for (int i = 0; i < classifyLocalCount; i++) cLocalsEnc.AddVariable().Type().Int32();
        var cLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cLocalsSig));

        // ─── MethodDef #2: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(2));

        // main locals: 1 x int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("switch.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();
        var dataStreamBuilder = new BlobBuilder();
        var dataRelocBuilder = new BlobBuilder();
        var nepStreamBuilder = new BlobBuilder();
        var nepRelocBuilder = new BlobBuilder();
        var ilFixupStreamBuilder = new BlobBuilder();
        var ilFixupRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("switch.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "switch.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for classify ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_default = enc.DefineLabel();
            var lbl_case0 = enc.DefineLabel();
            var lbl_case1 = enc.DefineLabel();
            var lbl_case2 = enc.DefineLabel();
            var lbl_end = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 7);

            if (machine == Machine.Arm64)
            {
                // ARM64: bounds-check before switch
                // ldarg.0, stloc.1, ldloc.1, ldc.i4.0, blt.s default, ldloc.1, ldc.i4.3, bgt.s default, ldloc.1, switch...
                enc.OpCode(ILOpCode.Ldarg_0);
                enc.OpCode(ILOpCode.Stloc_1);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_0);
                enc.Branch(ILOpCode.Blt_s, lbl_default);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_3);
                enc.Branch(ILOpCode.Bgt_s, lbl_default);
                enc.OpCode(ILOpCode.Ldloc_1);

                // switch (case0, case1, case2, case2)
                enc.Switch(lbl_case0, lbl_case1, lbl_case2, lbl_case2);

                enc.Branch(ILOpCode.Br_s, lbl_default);
            }
            else if (machine == Machine.Amd64)
            {
                // x64: if-else chain (no switch instruction)
                enc.OpCode(ILOpCode.Ldarg_0);
                enc.OpCode(ILOpCode.Stloc_1);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.Branch(ILOpCode.Brfalse_s, lbl_case0);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_1);
                enc.Branch(ILOpCode.Beq_s, lbl_case1);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_2);
                enc.Branch(ILOpCode.Beq_s, lbl_case2);
                enc.OpCode(ILOpCode.Ldloc_1);
                enc.OpCode(ILOpCode.Ldc_i4_3);
                enc.Branch(ILOpCode.Beq_s, lbl_case2);
                enc.Branch(ILOpCode.Br_s, lbl_default);
            }
            else
            {
                // x86: direct switch
                enc.OpCode(ILOpCode.Ldarg_0);

                // switch (case0, case1, case2, case2)
                enc.Switch(lbl_case0, lbl_case1, lbl_case2, lbl_case2);

                enc.Branch(ILOpCode.Br_s, lbl_default);
            }

            enc.MarkLabel(lbl_case0);
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadConstantI4(10);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 11);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case1);
            enc.MarkLineNumber(cvFile, 13);
            enc.LoadConstantI4(20);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 14);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_case2);
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadConstantI4(30);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 18);
            enc.Branch(ILOpCode.Br_s, lbl_end);

            enc.MarkLabel(lbl_default);
            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldc_i4_m1);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLabel(lbl_end);
            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldloc_0);
            if (machine != Machine.I386)
            {
                enc.OpCode(ILOpCode.Stloc_2);
                enc.MarkLineNumber(cvFile, 24);
                enc.OpCode(ILOpCode.Ldloc_2);
            }
            else
            {
                enc.OpCode(ILOpCode.Stloc_1);
                enc.MarkLineNumber(cvFile, 24);
                enc.OpCode(ILOpCode.Ldloc_1);
            }
            enc.OpCode(ILOpCode.Ret);

            int classifyMaxStack = machine == Machine.I386 ? 1 : 2;

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(cLocalsSigHandle), "result"),
            };

            bodyEncoder.AddMethodBody(classifyMethod, "?classify@@$$J0YAHH@Z", enc,
                maxStack: classifyMaxStack, localVariablesSignature: cLocalsSigHandle, attributes: 0,
                debugName: "classify", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 28);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0002
            enc.Call(classifyMethod);                // IL_0003
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_0008
            enc.Call(classifyMethod);                // IL_0009
            enc.OpCode(ILOpCode.Add);               // IL_000E
            enc.OpCode(ILOpCode.Ldc_i4_2);          // IL_000F
            enc.Call(classifyMethod);                // IL_0010
            enc.OpCode(ILOpCode.Add);               // IL_0015
            enc.OpCode(ILOpCode.Ldc_i4_5);          // IL_0016
            enc.Call(classifyMethod);                // IL_0017
            enc.OpCode(ILOpCode.Add);               // IL_001C
            enc.OpCode(ILOpCode.Stloc_0);           // IL_001D

            enc.MarkLineNumber(cvFile, 29);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_001E
            enc.OpCode(ILOpCode.Ret);               // IL_001F

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(classifyMethod), "classify", "?classify@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder,
            dataStream: dataStreamBuilder, dataRelocs: dataRelocBuilder,
            ilFixupStream: ilFixupStreamBuilder, ilFixupRelocs: ilFixupRelocBuilder,
            nepStream: nepStreamBuilder, nepRelocs: nepRelocBuilder);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
