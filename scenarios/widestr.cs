using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class WidestrTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "widestr", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "widestr.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "widestr", refDir, "widestr.obj"));
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

        var valueTypeRef = md.AddTypeReference(mscorlibRef, md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));

        var ctorSigBuilder = new BlobBuilder();
        new BlobEncoder(ctorSigBuilder).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(0, out var ctorRetEnc, out var ctorParEnc);
        ctorRetEnc.Void();
        var nativeCppCtorRef = md.AddMemberReference(nativeCppClassAttrRef,
            md.GetOrAddString(".ctor"), md.GetOrAddBlob(ctorSigBuilder));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── TypeDef #2: $ArrayType$$$BY02G (sequential, sealed, size=6) ──
        // Wide string L"Hi" = {0x0048, 0x0069, 0x0000} = 3 uint16 elements = 6 bytes
        // 'G' = unsigned short / wchar_t element type
        var arrayType = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default, md.GetOrAddString("$ArrayType$$$BY02G"), valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(2));

        md.AddTypeLayout(arrayType, 0, 6);
        md.AddCustomAttribute(arrayType, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: ?A0x00000000.unnamed-global-0 (L"Hi\0") ────────
        var field1SigBuilder = new BlobBuilder();
        new BlobEncoder(field1SigBuilder).Field().Type().Type(arrayType, isValueType: true);

        var field1 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0x00000000.unnamed-global-0"),
            md.GetOrAddBlob(field1SigBuilder));
        md.AddFieldRelativeVirtualAddress(field1, 0);

        // ─── MethodDef: main ──────────────────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature().Parameters(0, out var rtEnc, out var parEnc);
        ClrIjw.EncodeCdeclI4Return(rtEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(1));

        // Locals: (Ptr uint16, int32) — w, return temp
        var localsSig = new BlobBuilder();
        var localsEnc = new BlobEncoder(localsSig).LocalVariableSignature(2);
        localsEnc.AddVariable().Type().Pointer().UInt16(); // V_0: w (unsigned short*)
        localsEnc.AddVariable().Type().Int32();             // V_1: return temp
        var localsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(localsSig));

        md.AddModule(0, md.GetOrAddString("widestr.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);
        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // .data: L"Hi" = 48 00 69 00 00 00 (UTF-16LE)
        dataSection.Content.WriteBytes(new byte[] { 0x48, 0x00, 0x69, 0x00, 0x00, 0x00 });

        // ObjDumper normalizes `$SG<digits>` → `$SG*` so any digit-form name works.
        symtab.AddDataClrToken("$SG12345", field1, dataSection, 0, out _);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("widestr.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "widestr.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── IL for main ──────────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_1);

            enc.OpCode(ILOpCode.Ldsflda);
            enc.Token(field1);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.LoadConstantI4(2);   // sizeof(unsigned short)
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_u2);   // w[0]

            enc.OpCode(ILOpCode.Ldloc_0);
            enc.LoadConstantI4(2);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_u2);   // w[1]

            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_1);

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Ret);

            var localSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(localsSigHandle), "w"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 4, localVariablesSignature: localsSigHandle, attributes: 0,
                localSlots: localSlots, debugName: "main");
        }

        // ─── IJW machinery for main ──────────────────────────────────────
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
