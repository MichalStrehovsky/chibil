using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class InitTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        // Persist the emitted obj so the linker harness can pick it up.
        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "init", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "init.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "init", refDir, "init.obj"));
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

        // ─── TypeRefs (only what's referenced) ────────────────────────────
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("IsSignUnspecifiedByte"));
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));

        // ─── MemberRef: NativeCppClassAttribute::.ctor() ──────────────────
        var ctorSig = new BlobBuilder();
        new BlobEncoder(ctorSig).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(0, out var ctorRetEnc, out var _);
        ctorRetEnc.Void();
        var nativeCppCtorRef = md.AddMemberReference(nativeCppClassAttrRef,
            md.GetOrAddString(".ctor"), md.GetOrAddBlob(ctorSig));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── TypeDef #2: $ArrayType$$$BY06D — value type for "Hello!\0" (size 7) ─
        var arrayType6D = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY06D"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(3),  // no fields of its own
            MetadataTokens.MethodDefinitionHandle(2));
        md.AddTypeLayout(arrayType6D, 0, 7);
        md.AddCustomAttribute(arrayType6D, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: ?A0x*.unnamed-global-0 — the "Hello!\0" literal data ─
        // Type: ValueClass $ArrayType$$$BY06D (raw 7-byte value type held in .data)
        var literalSig = new BlobBuilder();
        new BlobEncoder(literalSig).Field().Type().Type(arrayType6D, isValueType: true);
        var fieldLiteral = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0x381663bd.unnamed-global-0"),
            md.GetOrAddBlob(literalSig));
        md.AddFieldRelativeVirtualAddress(fieldLiteral, 0);

        // ─── FieldDef #2: str — Ptr cmod_opt(IsSignUnspecifiedByte) I1 ───
        var strSig = new BlobBuilder();
        strSig.WriteByte(0x06);                                                                        // FIELD
        strSig.WriteByte((byte)SignatureTypeCode.Pointer);
        strSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        strSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        strSig.WriteByte((byte)SignatureTypeCode.SByte);
        var fieldStr = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("str"),
            md.GetOrAddBlob(strSig));
        md.AddFieldRelativeVirtualAddress(fieldStr, 0);

        // ─── MethodDef #1: main() -> cmod_opt(CallConvCdecl) int32 ───────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var _);
        var mainRetType = mainRetEnc.Type();
        mainRetType.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainRetType.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        mainRetType.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── StandaloneSig: locals = (int32) ────────────────────────────
        var localsSig = new BlobBuilder();
        new BlobEncoder(localsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var localsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(localsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("init.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();
        var dataStreamBuilder = new BlobBuilder();
        var dataRelocBuilder = new BlobBuilder();
        var ilFixupStreamBuilder = new BlobBuilder();
        var ilFixupRelocBuilder = new BlobBuilder();
        var nepStreamBuilder = new BlobBuilder();
        var nepRelocBuilder = new BlobBuilder();

        // ─── .data layout ────────────────────────────────────────────────
        //   +0x00  literal[8]  "Hello!\0\0"                                (7 bytes + 1 pad)
        //   +0x08  str         ADDR addend=0 → literal                    (ptrSize bytes)
        int literalOffset = 0;
        int strOffset = 8;

        dataStreamBuilder.WriteBytes(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x00, 0x00 });    // "Hello!\0" + pad
        if (is32)
            dataStreamBuilder.WriteInt32(0);
        else
            dataStreamBuilder.WriteInt64(0);

        // Pre-register data field COFF symbols BEFORE emitting IL.
        // MSVC uses $SG<N> for anonymous string literal COFF symbols (no underscore
        // prefix on x86 — they're compiler-internal, not C-decorated). The exact
        // <N> differs per arch (MSVC's counter is per-arch); ObjDumper normalizes
        // $SG\d+ → $SG* so any consistent number compares equal.
        symtab.AddDataClrToken("$SG7982",          fieldLiteral, LogicalSection.Data, literalOffset, out _);
        symtab.AddDataClrToken(symPrefix + "str",  fieldStr,     LogicalSection.Data, strOffset,     out _);

        var literalSymbol = symtab.AddDataSymbol("$SG7982", LogicalSection.Data, literalOffset);

        // ─── Emit .data relocations ──────────────────────────────────────
        // str → literal (ADDR addend=0)
        new CoffRelocationEncoder(coffHeader, dataRelocBuilder)
            .AddAddressRelocation(strOffset, literalSymbol);

        // ─── CodeView debug info ─────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("init.obj",
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "init.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main: return str[0] ─────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(fieldStr);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i1);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── IJW machinery for main (NEP thunk + __mep@ slot + ilfixup) ─
        EmitNepMachinery(
            machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder,
            nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            methodToken: MetadataTokens.GetToken(mainMethod),
            bareName: "main",
            mangledSuffix: "?main@@$$J0YAHXZ");

        // ─── Build COFF & Serialize ──────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder,
            dataStream: dataStreamBuilder, dataRelocs: dataRelocBuilder,
            ilFixupStream: ilFixupStreamBuilder, ilFixupRelocs: ilFixupRelocBuilder,
            nepStream: nepStreamBuilder, nepRelocs: nepRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
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
        int slotOffset = dataStream.Count;
        for (int i = 0; i < ptrSize; i++) dataStream.WriteByte(0);

        var mepDataSym = symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, LogicalSection.Data, slotOffset);

        var tokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataRelocs).AddTokenRelocation(slotOffset, tokenSym);

        int thunkOffset = nepStream.Count;
        if (machine == Machine.Arm64)
        {
            nepStream.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
            nepRelocs.WriteInt32(thunkOffset + 0);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0004);
            nepRelocs.WriteInt32(thunkOffset + 4);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0007);
        }
        else
        {
            nepStream.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepRelocs.WriteInt32(thunkOffset + 2);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(is32 ? (ushort)0x0006 : (ushort)0x0004);
        }

        symtab.AddExternalDataSymbol(symPrefix + bareName, LogicalSection.Nep, thunkOffset);

        int ilfixupOffset = ilFixupStream.Count;
        ilFixupStream.WriteInt32(0);
        ilFixupStream.WriteInt16(1);
        ilFixupStream.WriteInt16(is32 ? (short)0x0009 : (short)0x000A);
        new CoffRelocationEncoder(coffHeader, ilFixupRelocs).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);
    }
}
