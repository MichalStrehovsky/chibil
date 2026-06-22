using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ControlTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "control", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "control.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "control", refDir, "control.obj"));
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

        // ─── int(int) signature (shared by sum_loop, count_while, count_do, use_goto) ──
        var intIntSig = new BlobBuilder();
        new BlobEncoder(intIntSig).MethodSignature()
            .Parameters(1, out var iiRetEnc, out var iiParEnc);
        ClrIjw.EncodeCdeclI4Return(iiRetEnc, callConvCdeclRef);
        iiParEnc.AddParameter().Type().Int32();
        var intIntSigBlob = md.GetOrAddBlob(intIntSig);

        // ─── MethodDef #1: sum_loop ───────────────────────────────────────
        var sumLoopMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sum_loop"), intIntSigBlob, 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 1);

        // sum_loop locals: 3 x int32 (i, sum, retval)
        var slLocalsSig = new BlobBuilder();
        var slLocalsEnc = new BlobEncoder(slLocalsSig).LocalVariableSignature(3);
        for (int i = 0; i < 3; i++) slLocalsEnc.AddVariable().Type().Int32();
        var slLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(slLocalsSig));

        // ─── MethodDef #2: count_while ────────────────────────────────────
        var countWhileMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("count_while"), intIntSigBlob, 0,
            MetadataTokens.ParameterHandle(2));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 1);

        // count_while locals: 2 x int32
        var cwLocalsSig = new BlobBuilder();
        var cwLocalsEnc = new BlobEncoder(cwLocalsSig).LocalVariableSignature(2);
        for (int i = 0; i < 2; i++) cwLocalsEnc.AddVariable().Type().Int32();
        var cwLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cwLocalsSig));

        // ─── MethodDef #3: count_do ───────────────────────────────────────
        var countDoMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("count_do"), intIntSigBlob, 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 1);

        // count_do uses same local sig as count_while (2 x int32)
        var cdLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cwLocalsSig));

        // ─── MethodDef #4: use_goto ───────────────────────────────────────
        var useGotoMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("use_goto"), intIntSigBlob, 0,
            MetadataTokens.ParameterHandle(4));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 1);

        // use_goto locals: 2 x int32
        var ugLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cwLocalsSig));

        // ─── MethodDef #5: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(5));

        // main locals: 1 x int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("control.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("control.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "control.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sum_loop ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_neg = enc.DefineLabel();       // IL_001A: else
            var lbl_loopTest = enc.DefineLabel();   // IL_000E: loop condition
            var lbl_loopBody = enc.DefineLabel();   // IL_000A: loop body
            var lbl_afterLoop = enc.DefineLabel();  // IL_0018: after loop
            var lbl_end = enc.DefineLabel();        // IL_001C: end

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0001: sum = 0

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0003
            enc.Branch(ILOpCode.Ble_s, lbl_neg);    // IL_0004: if n <= 0 goto neg

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0006
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0007: i = 0
            enc.Branch(ILOpCode.Br_s, lbl_loopTest); // IL_0008: goto loopTest

            enc.MarkLabel(lbl_loopBody);            // IL_000A
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000A
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_000B
            enc.OpCode(ILOpCode.Add);               // IL_000C
            enc.OpCode(ILOpCode.Stloc_0);           // IL_000D: i++

            enc.MarkLabel(lbl_loopTest);            // IL_000E
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000E
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_000F
            enc.Branch(ILOpCode.Bge_s, lbl_afterLoop); // IL_0010: if i >= n goto afterLoop

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0012
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0013
            enc.OpCode(ILOpCode.Add);               // IL_0014
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0015: sum += i
            enc.Branch(ILOpCode.Br_s, lbl_loopBody); // IL_0016: goto loopBody

            enc.MarkLabel(lbl_afterLoop);           // IL_0018
            enc.MarkLineNumber(cvFile, 12);
            enc.Branch(ILOpCode.Br_s, lbl_end);     // IL_0018: goto end

            enc.MarkLabel(lbl_neg);                 // IL_001A
            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_m1);         // IL_001A
            enc.OpCode(ILOpCode.Stloc_1);           // IL_001B: sum = -1

            enc.MarkLabel(lbl_end);                 // IL_001C
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_001C
            enc.OpCode(ILOpCode.Stloc_2);           // IL_001D
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_2);           // IL_001E
            enc.OpCode(ILOpCode.Ret);               // IL_001F

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(slLocalsSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(slLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(sumLoopMethod, "?sum_loop@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: slLocalsSigHandle, attributes: 0,
                debugName: "sum_loop", localSlots: localSlots);
        }

        // ─── Emit IL for count_while ──────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_cond = enc.DefineLabel();       // IL_0002
            var lbl_done = enc.DefineLabel();       // IL_0011

            enc.MarkLineNumber(cvFile, 22);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001: count = 0

            enc.MarkLabel(lbl_cond);
            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0003
            enc.Branch(ILOpCode.Ble_s, lbl_done);   // IL_0004: if n <= 0 done

            enc.MarkLineNumber(cvFile, 25);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0006
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_0007
            enc.OpCode(ILOpCode.Add);               // IL_0008
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0009: count++

            enc.MarkLineNumber(cvFile, 26);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_000A
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_000B
            enc.OpCode(ILOpCode.Sub);               // IL_000C
            enc.StoreArgument(0);                   // IL_000D: starg.s 0 (n--)

            enc.MarkLineNumber(cvFile, 27);
            enc.Branch(ILOpCode.Br_s, lbl_cond);    // IL_000F: goto cond

            enc.MarkLabel(lbl_done);
            enc.MarkLineNumber(cvFile, 28);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0011
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0012

            enc.MarkLineNumber(cvFile, 29);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0013
            enc.OpCode(ILOpCode.Ret);               // IL_0014

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(cwLocalsSigHandle), "count"),
            };

            bodyEncoder.AddMethodBody(countWhileMethod, "?count_while@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: cwLocalsSigHandle, attributes: 0,
                debugName: "count_while", localSlots: localSlots);
        }

        // ─── Emit IL for count_do ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_loop = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 33);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001: count = 0

            enc.MarkLabel(lbl_loop);
            enc.MarkLineNumber(cvFile, 36);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_0003
            enc.OpCode(ILOpCode.Add);               // IL_0004
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0005: count++

            enc.MarkLineNumber(cvFile, 37);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0006
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0007
            enc.Branch(ILOpCode.Blt_s, lbl_loop);   // IL_0008: if count < n goto loop

            enc.MarkLineNumber(cvFile, 38);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000A
            enc.OpCode(ILOpCode.Stloc_1);           // IL_000B

            enc.MarkLineNumber(cvFile, 39);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_000C
            enc.OpCode(ILOpCode.Ret);               // IL_000D

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(cdLocalsSigHandle), "count"),
            };

            bodyEncoder.AddMethodBody(countDoMethod, "?count_do@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: cdLocalsSigHandle, attributes: 0,
                debugName: "count_do", localSlots: localSlots);
        }

        // ─── Emit IL for use_goto ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_cond = enc.DefineLabel();       // IL_0002
            var lbl_loop = enc.DefineLabel();       // IL_0008
            var lbl_done = enc.DefineLabel();       // IL_0013

            enc.MarkLineNumber(cvFile, 43);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001: result = 0

            enc.MarkLabel(lbl_cond);
            enc.MarkLineNumber(cvFile, 45);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0003
            enc.Branch(ILOpCode.Bgt_s, lbl_loop);   // IL_0004: if n > 0 goto loop

            enc.MarkLineNumber(cvFile, 46);
            enc.Branch(ILOpCode.Br_s, lbl_done);    // IL_0006: goto done

            enc.MarkLabel(lbl_loop);
            enc.MarkLineNumber(cvFile, 47);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0008
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0009
            enc.OpCode(ILOpCode.Add);               // IL_000A
            enc.OpCode(ILOpCode.Stloc_0);           // IL_000B: result += n

            enc.MarkLineNumber(cvFile, 48);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_000C
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_000D
            enc.OpCode(ILOpCode.Sub);               // IL_000E
            enc.StoreArgument(0);                   // IL_000F: starg.s 0 (n--)

            enc.MarkLineNumber(cvFile, 49);
            enc.Branch(ILOpCode.Br_s, lbl_cond);    // IL_0011: goto cond

            enc.MarkLabel(lbl_done);
            enc.MarkLineNumber(cvFile, 51);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0013
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0014

            enc.MarkLineNumber(cvFile, 52);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0015
            enc.OpCode(ILOpCode.Ret);               // IL_0016

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(ugLocalsSigHandle), "result"),
            };

            bodyEncoder.AddMethodBody(useGotoMethod, "?use_goto@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: ugLocalsSigHandle, attributes: 0,
                debugName: "use_goto", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 56);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001

            enc.OpCode(ILOpCode.Ldc_i4_5);          // IL_0002
            enc.Call(sumLoopMethod);                 // IL_0003
            enc.OpCode(ILOpCode.Ldc_i4_3);          // IL_0008
            enc.Call(countWhileMethod);              // IL_0009
            enc.OpCode(ILOpCode.Add);               // IL_000E
            enc.OpCode(ILOpCode.Ldc_i4_4);          // IL_000F
            enc.Call(countDoMethod);                 // IL_0010
            enc.OpCode(ILOpCode.Add);               // IL_0015
            enc.OpCode(ILOpCode.Ldc_i4_3);          // IL_0016
            enc.Call(useGotoMethod);                 // IL_0017
            enc.OpCode(ILOpCode.Add);               // IL_001C
            enc.OpCode(ILOpCode.Stloc_0);           // IL_001D

            enc.MarkLineNumber(cvFile, 57);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_001E
            enc.OpCode(ILOpCode.Ret);               // IL_001F

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(sumLoopMethod), "sum_loop", "?sum_loop@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(countWhileMethod), "count_while", "?count_while@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(countDoMethod), "count_do", "?count_do@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(useGotoMethod), "use_goto", "?use_goto@@$$J0YAHH@Z");
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

