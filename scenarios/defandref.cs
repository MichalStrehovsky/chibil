using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class DefAndRefTest
{
    [Theory]
    [InlineData(Machine.I386, "def")]
    [InlineData(Machine.I386, "ref")]
    [InlineData(Machine.Arm64, "def")]
    [InlineData(Machine.Arm64, "ref")]
    [InlineData(Machine.Amd64, "def")]
    [InlineData(Machine.Amd64, "ref")]
    public void Emit(Machine machine, string variant)
    {
        byte[] emitted = EmitObj(machine, variant);

        // Write the emitted obj to disk so external link-step harnesses can pick it up.
        // This runs unconditionally — the comparison below may fail (it is expected to,
        // because we intentionally skip the mixed-mode RVA fields and extra COFF
        // sections that MSVC's /clr output contains).
        string archDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "defandref", archDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, $"{variant}.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "defandref", archDir, $"{variant}.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine, string variant)
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

        // ─── TypeRef: CallConvCdecl (modopt on return type, /clr mixed mode) ─
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        var moduleType = md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // Helper: build a method signature "CMOD_OPT CallConvCdecl I4 (...)" with int32 params
        BlobBuilder BuildIntReturningSignature(int paramCount)
        {
            var sig = new BlobBuilder();
            new BlobEncoder(sig).MethodSignature()
                .Parameters(paramCount, out var retEnc, out var parEnc);
            retEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            retEnc.Type().Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
            retEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.Int32);
            for (int i = 0; i < paramCount; i++)
                parEnc.AddParameter().Type().Int32();
            return sig;
        }

        if (variant == "def")
        {
            // ─── MethodDef #1: arith(int, int) -> int ────────────────────
            var arithSig = BuildIntReturningSignature(2);
            var arithMethod = md.AddMethodDefinition(
                MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                md.GetOrAddString("arith"),
                md.GetOrAddBlob(arithSig),
                0,
                MetadataTokens.ParameterHandle(1));

            md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
            md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

            // Locals: 1 x int32
            var arithLocalsSig = new BlobBuilder();
            new BlobEncoder(arithLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
            var arithLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(arithLocalsSig));

            md.AddModule(0,
                md.GetOrAddString("def.obj"),
                md.GetOrAddGuid(Guid.NewGuid()),
                default, default);

            // ─── COFF structure ──────────────────────────────────────────
            var coffHeader = new CoffHeaderBuilder(machine, 0);
            var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
            var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
            var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
            var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
            var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

            // ─── CodeView ────────────────────────────────────────────────
            var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
            codeviewSymbols.AddObjNameAndCompile3("def.obj",
                language: CodeViewLanguage.C,
                machine: cvMachine,
                feMajor: 19, feMinor: 50, feBuild: 35730,
                beMajor: 19, beMinor: 50, beBuild: 35730,
                "Microsoft (R) Optimizing Compiler",
                compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

            string sourceFile = Path.Combine(AppContext.BaseDirectory, "defandref.c");
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
            CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

            var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
                ilSection, symtab, coffHeader, codeviewSymbols);

            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);   // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);   // IL_0001
            enc.OpCode(ILOpCode.Add);       // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);   // IL_0003
            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldloc_0);   // IL_0004
            enc.OpCode(ILOpCode.Ret);       // IL_0005

            // The /clr mixed-mode mangled name (cdecl 'A') — must match what
            // a ref.obj produced by either MSVC or our emitter uses to import this.
            bodyEncoder.AddMethodBody(arithMethod, "?arith@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: arithLocalsSigHandle, attributes: 0,
                debugName: "arith");

            // ─── IJW machinery for arith (NEP thunk + __mep@ slot + ilfixup) ─
            ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
                dataSection, nepSection, ilFixupSection,
                methodToken: MetadataTokens.GetToken(arithMethod),
                bareName: "arith",
                mangledSuffix: "?arith@@$$J0YAHHH@Z");

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
        else // variant == "ref"
        {
            // ─── MemberRef: arith on <Module> (the external call target) ──
            var arithMemberRefSig = BuildIntReturningSignature(2);
            var arithMemberRef = md.AddMemberReference(moduleType,
                md.GetOrAddString("arith"),
                md.GetOrAddBlob(arithMemberRefSig));

            // ─── MethodDef #1: main() -> int ─────────────────────────────
            var mainSig = BuildIntReturningSignature(0);
            var mainMethod = md.AddMethodDefinition(
                MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                md.GetOrAddString("main"),
                md.GetOrAddBlob(mainSig),
                0,
                MetadataTokens.ParameterHandle(1));

            var mainLocalsSig = new BlobBuilder();
            new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
            var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

            md.AddModule(0,
                md.GetOrAddString("ref.obj"),
                md.GetOrAddGuid(Guid.NewGuid()),
                default, default);

            // ─── COFF structure ──────────────────────────────────────────
            var coffHeader = new CoffHeaderBuilder(machine, 0);
            var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
            var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
            var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
            var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
            var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

            // Register the external arith symbol BEFORE emitting IL that calls it.
            symtab.AddExternalClrToken("?arith@@$$J0YAHHH@Z", arithMemberRef);

            // ─── CodeView ────────────────────────────────────────────────
            var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
            codeviewSymbols.AddObjNameAndCompile3("ref.obj",
                language: CodeViewLanguage.C,
                machine: cvMachine,
                feMajor: 19, feMinor: 50, feBuild: 35730,
                beMajor: 19, beMinor: 50, beBuild: 35730,
                "Microsoft (R) Optimizing Compiler",
                compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

            string sourceFile = Path.Combine(AppContext.BaseDirectory, "defandref.c");
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
            CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

            var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
                ilSection, symtab, coffHeader, codeviewSymbols);

            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_0);  // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);   // IL_0001
            enc.LoadConstantI4(10);          // IL_0002: ldc.i4.s 10
            enc.OpCode(ILOpCode.Ldc_i4_3);  // IL_0004
            enc.Call(arithMemberRef);        // IL_0005: call <Module>::arith
            enc.OpCode(ILOpCode.Stloc_0);   // IL_000A
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_0);   // IL_000B
            enc.OpCode(ILOpCode.Ret);       // IL_000C

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");

            // ─── IJW machinery for main (NEP thunk + __mep@ slot + ilfixup) ─
            ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
                dataSection, nepSection, ilFixupSection,
                methodToken: MetadataTokens.GetToken(mainMethod),
                bareName: "main",
                mangledSuffix: "?main@@$$J0YAHXZ");

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

}
