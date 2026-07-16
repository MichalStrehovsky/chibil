using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class StaticLocalTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "static-local", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "static-local.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "static-local", refDir, "static-local.obj"));
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

        // ─── TypeRef: CallConvCdecl (modopt on return types under /clr) ───
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeDef: <Module> ────────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── FieldDef: static local 'count' in counter() ──────────────────
        // MSVC emits this as a static field on <Module> with a hash-prefixed name.
        // ObjDumper normalizes the ?A0x<hash> prefix to ?A0x*, so any hash works.
        var fieldSig = new BlobBuilder();
        new BlobEncoder(fieldSig).Field().Type().Int32();

        var countField = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0x00000000.?count@?1??counter@@9@9"),
            md.GetOrAddBlob(fieldSig));
        md.AddFieldRelativeVirtualAddress(countField, 0);

        // ─── MethodDef #1: counter() -> int32 ─────────────────────────────
        var counterSig = new BlobBuilder();
        new BlobEncoder(counterSig).MethodSignature().Parameters(0, out var cRetEnc, out var cParEnc);
        ClrIjw.EncodeCdeclI4Return(cRetEnc, callConvCdeclRef);

        var counterMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("counter"), md.GetOrAddBlob(counterSig), 0,
            MetadataTokens.ParameterHandle(1));

        var counterLocalsSig = new BlobBuilder();
        new BlobEncoder(counterLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var counterLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(counterLocalsSig));

        // ─── MethodDef #2: main() -> int32 ────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature().Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(1));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("static-local.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var bssSection = new UninitializedCoffSectionBuilder(".bss", SectionCharacteristics.ContainsUninitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes) { Size = 4 };
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("static-local.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "static-local.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Pre-register the static-local field as a .bss-section symbol ─
        // `static int count` is uninitialized in C, so under /clr MSVC emits
        // it into a `.bss` section (SizeOfRawData=4, PointerToRawData=0 —
        // i.e., no file content; the loader zero-initializes the 4 bytes).
        // We add a CLR-token alias bound to the FieldDef alongside the
        // mangled COFF symbol so cross-module IL references resolve.
        symtab.AddDataClrToken(symPrefix + "?A0x00000000.?count@?1??counter@@9@9", countField, bssSection, 0, out _);

        // ─── IL for counter ───────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(countField);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stsfld);
            enc.Token(countField);

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(countField);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(counterMethod, "?counter@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: counterLocalsSigHandle, attributes: 0,
                debugName: "counter");
        }

        // ─── IL for main ──────────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001
            enc.Call(counterMethod);                 // IL_0002: call counter
            enc.OpCode(ILOpCode.Pop);               // IL_0007

            enc.MarkLineNumber(cvFile, 14);
            enc.Call(counterMethod);                 // IL_0008: call counter
            enc.OpCode(ILOpCode.Pop);               // IL_000D

            enc.MarkLineNumber(cvFile, 15);
            enc.Call(counterMethod);                 // IL_000E: call counter
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0013

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0014
            enc.OpCode(ILOpCode.Ret);               // IL_0015

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── IJW machinery for counter and main ───────────────────────────
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            counterMethod, "counter", "?counter@@$$J0YAHXZ");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            mainMethod, "main", "?main@@$$J0YAHXZ");

        var sections = new System.Collections.Generic.List<CoffSectionBuilder>();
        if (ilSection.Content.Count > 0) sections.Add(ilSection);
        if (dataSection.Content.Count > 0) sections.Add(dataSection);
        if (bssSection.Size > 0) sections.Add(bssSection);
        if (ilFixupSection.Content.Count > 0) sections.Add(ilFixupSection);
        if (nepSection.Content.Count > 0) sections.Add(nepSection);
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols, sections);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
