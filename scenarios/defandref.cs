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
            var ilStreamBuilder = new BlobBuilder();
            var ilRelocBuilder = new BlobBuilder();
            var dataStreamBuilder = new BlobBuilder();
            var dataRelocBuilder = new BlobBuilder();
            var nepStreamBuilder = new BlobBuilder();
            var nepRelocBuilder = new BlobBuilder();
            var ilFixupStreamBuilder = new BlobBuilder();
            var ilFixupRelocBuilder = new BlobBuilder();

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
                ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

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
            EmitNepMachinery(
                machine, is32, ptrSize, symPrefix, coffHeader, symtab,
                dataStreamBuilder, dataRelocBuilder,
                nepStreamBuilder, nepRelocBuilder,
                ilFixupStreamBuilder, ilFixupRelocBuilder,
                methodToken: MetadataTokens.GetToken(arithMethod),
                bareName: "arith",
                mangledSuffix: "?arith@@$$J0YAHHH@Z");

            var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
                ilStreamBuilder, ilRelocBuilder,
                dataStream: dataStreamBuilder, dataRelocs: dataRelocBuilder,
                ilFixupStream: ilFixupStreamBuilder, ilFixupRelocs: ilFixupRelocBuilder,
                nepStream: nepStreamBuilder, nepRelocs: nepRelocBuilder);
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
            var ilStreamBuilder = new BlobBuilder();
            var ilRelocBuilder = new BlobBuilder();
            var dataStreamBuilder = new BlobBuilder();
            var dataRelocBuilder = new BlobBuilder();
            var nepStreamBuilder = new BlobBuilder();
            var nepRelocBuilder = new BlobBuilder();
            var ilFixupStreamBuilder = new BlobBuilder();
            var ilFixupRelocBuilder = new BlobBuilder();

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
                ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

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
            EmitNepMachinery(
                machine, is32, ptrSize, symPrefix, coffHeader, symtab,
                dataStreamBuilder, dataRelocBuilder,
                nepStreamBuilder, nepRelocBuilder,
                ilFixupStreamBuilder, ilFixupRelocBuilder,
                methodToken: MetadataTokens.GetToken(mainMethod),
                bareName: "main",
                mangledSuffix: "?main@@$$J0YAHXZ");

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

    /// <summary>
    /// Emits the minimal /clr IJW machinery for a single managed function: a
    /// <c>__mep@?fn</c> data slot stamped with a TOKEN reloc to the method's
    /// MethodDef CLR-token symbol, a single indirect-jump <c>.nep</c> thunk
    /// that targets the slot, a bare-name COFF alias for the thunk, and a
    /// single <c>.rdata$ilfixup</c> entry that tells the CLR loader to
    /// resolve the token in the slot into a from-unmanaged stub address.
    /// </summary>
    static void EmitNepMachinery(
        Machine machine, bool is32, int ptrSize, string symPrefix,
        CoffHeaderBuilder coffHeader, ManagedCoffSymbolTableBuilder symtab,
        BlobBuilder dataStream, BlobBuilder dataRelocs,
        BlobBuilder nepStream, BlobBuilder nepRelocs,
        BlobBuilder ilFixupStream, BlobBuilder ilFixupRelocs,
        int methodToken, string bareName, string mangledSuffix)
    {
        // (1) __mep@?fn slot in .data (zero-initialized; linker will stamp the
        //     MethodDef token via the TOKEN reloc below).
        int slotOffset = dataStream.Count;
        for (int i = 0; i < ptrSize; i++) dataStream.WriteByte(0);

        var mepDataSym = symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, LogicalSection.Data, slotOffset);

        var tokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataRelocs).AddTokenRelocation(slotOffset, tokenSym);

        // (2) Single indirect-jump thunk in .nep, targeting the __mep@?fn slot.
        //   x86  : FF 25 [imm32→slot]                                 (6  bytes, DIR32 reloc)
        //   x64  : FF 25 [rel32→slot]                                 (6  bytes, REL32 reloc)
        //   arm64: ADRP X9,[slot] / LDR X9,[X9,#off] / BR X9          (12 bytes, PAGEBASE_REL21 + PAGEOFFSET_12L)
        int thunkOffset = nepStream.Count;
        if (machine == Machine.Arm64)
        {
            nepStream.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
            nepRelocs.WriteInt32(thunkOffset + 0);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0004);                            // IMAGE_REL_ARM64_PAGEBASE_REL21
            nepRelocs.WriteInt32(thunkOffset + 4);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0007);                            // IMAGE_REL_ARM64_PAGEOFFSET_12L
        }
        else
        {
            nepStream.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepRelocs.WriteInt32(thunkOffset + 2);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(is32 ? (ushort)0x0006 : (ushort)0x0004); // I386 DIR32 / AMD64 REL32
        }

        // (3) Bare-name COFF alias for the thunk (e.g. `arith` / `_arith`).
        symtab.AddExternalDataSymbol(symPrefix + bareName, LogicalSection.Nep, thunkOffset);

        // (4) One 8-byte ILFixup entry pointing at the slot: { RVA, Count=1, Type }.
        int ilfixupOffset = ilFixupStream.Count;
        ilFixupStream.WriteInt32(0);                                  // RVA placeholder
        ilFixupStream.WriteInt16(1);                                  // Count
        ilFixupStream.WriteInt16(is32 ? (short)0x0009 : (short)0x000A); // *_BIT | FROM_UNMANAGED_RETAIN_APPDOMAIN
        new CoffRelocationEncoder(coffHeader, ilFixupRelocs).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);
    }
}
