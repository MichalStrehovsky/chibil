using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ArithTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "arith", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "arith.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "arith", refDir, "arith.obj"));
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
            md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: CallConvCdecl (modopt on return types under /clr) ───
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: arith(int, int) -> cmod_opt(CallConvCdecl) int ─
        var arithSig = new BlobBuilder();
        new BlobEncoder(arithSig).MethodSignature()
            .Parameters(2, out var arithRetEnc, out var arithParEnc);
        ClrIjw.EncodeCdeclI4Return(arithRetEnc, callConvCdeclRef);
        arithParEnc.AddParameter().Type().Int32();
        arithParEnc.AddParameter().Type().Int32();

        var arithMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("arith"),
            md.GetOrAddBlob(arithSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for arith: 7 x int32
        var arithLocalsSig = new BlobBuilder();
        var arithLocalsEnc = new BlobEncoder(arithLocalsSig).LocalVariableSignature(7);
        for (int i = 0; i < 7; i++)
            arithLocalsEnc.AddVariable().Type().Int32();
        var arithLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(arithLocalsSig));

        // ─── MethodDef #2: main() -> cmod_opt(CallConvCdecl) int ──────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var _);
        ClrIjw.EncodeCdeclI4Return(mainRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig),
            0,
            MetadataTokens.ParameterHandle(3));

        // Locals for main: 1 x int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("arith.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("arith.obj",
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "arith.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for arith ────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Add);
            enc.StoreLocal(6);

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Sub);
            enc.StoreLocal(5);

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Mul);
            enc.StoreLocal(4);

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Div);
            enc.StoreLocal(3);

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Rem);
            enc.StoreLocal(2);

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Neg);
            enc.StoreLocal(1);

            enc.MarkLineNumber(cvFile, 12);
            enc.LoadLocal(6);
            enc.LoadLocal(5);
            enc.OpCode(ILOpCode.Add);
            enc.LoadLocal(4);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var arithLocalSlots = new[] {
                new CodeViewManSlot(5, MetadataTokens.GetToken(arithLocalsSigHandle), "diff"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(arithLocalsSigHandle), "quot"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(arithLocalsSigHandle), "prod"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(arithLocalsSigHandle), "neg"),
                new CodeViewManSlot(6, MetadataTokens.GetToken(arithLocalsSigHandle), "sum"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(arithLocalsSigHandle), "rem"),
            };

            bodyEncoder.AddMethodBody(arithMethod, "?arith@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: arithLocalsSigHandle, attributes: 0,
                debugName: "arith", localSlots: arithLocalSlots);
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
            enc.OpCode(ILOpCode.Ldc_i4_3);
            enc.Call(arithMethod);

            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── IJW machinery for arith and main ────────────────────────────
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            arithMethod, "arith", "?arith@@$$J0YAHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            mainMethod, "main", "?main@@$$J0YAHXZ");

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
