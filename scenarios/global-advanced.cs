using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class GlobalAdvancedTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);

        // Persist the emitted obj so the linker harness can pick it up.
        string archDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "global-advanced", archDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "global-advanced.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "global-advanced", archDir, "global-advanced.obj"));
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

        // ─── TypeDef #2: $ArrayType$$$BY06D (sequential, sealed, size=7) ──
        var arrayType6D = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY06D"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(4),  // no fields of its own (e, hello, m come first)
            MetadataTokens.MethodDefinitionHandle(3));
        md.AddTypeLayout(arrayType6D, 0, 7);
        md.AddCustomAttribute(arrayType6D, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: e — Ptr cmod_opt(IsSignUnspecifiedByte) I1 ───
        var eSig = new BlobBuilder();
        eSig.WriteByte(0x06);                                                                          // FIELD
        eSig.WriteByte((byte)SignatureTypeCode.Pointer);
        eSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        eSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        eSig.WriteByte((byte)SignatureTypeCode.SByte);

        var fieldE = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("e"),
            md.GetOrAddBlob(eSig));
        md.AddFieldRelativeVirtualAddress(fieldE, 0);

        // ─── FieldDef #2: hello — ValueClass $ArrayType$$$BY06D ────────
        var helloSig = new BlobBuilder();
        new BlobEncoder(helloSig).Field().Type().Type(arrayType6D, isValueType: true);
        var fieldHello = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("hello"),
            md.GetOrAddBlob(helloSig));
        md.AddFieldRelativeVirtualAddress(fieldHello, 0);

        // ─── FieldDef #3: m — FNPTR [C] cmod_opt(CallConvCdecl) I4() ───
        var mSig = new BlobBuilder();
        mSig.WriteByte(0x06);                                                                          // FIELD
        mSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        mSig.WriteByte((byte)SignatureCallingConvention.CDecl);                                        // C calling convention
        mSig.WriteByte(0x00);                                                                          // 0 params
        mSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        mSig.WriteByte((byte)SignatureTypeCode.Int32);

        var fieldM = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("m"),
            md.GetOrAddBlob(mSig));
        md.AddFieldRelativeVirtualAddress(fieldM, 0);

        // Note: __mep@?get and __mep@?main are emitted below as COFF data symbols
        // only — NOT as FieldDefs in metadata. This matches MSVC's layout (on x86
        // these live in COMDAT .data sections without a metadata representation;
        // on x64 only __m2mep@? gets a FieldDef while __mep@? is COFF-only). Tying
        // them to FieldDefs would add ildasm-visible boilerplate that MSVC omits.

        // ─── MethodDef #1: get() -> int (UnmanagedExport, cdecl-cmod-opt return) ─
        var getSigBlob = new BlobBuilder();
        new BlobEncoder(getSigBlob).MethodSignature()
            .Parameters(0, out var getRetEnc, out var _);
        var getRetType = getRetEnc.Type();
        getRetType.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        getRetType.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        getRetType.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        var getMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("get"),
            md.GetOrAddBlob(getSigBlob),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── MethodDef #2: main() -> int (same return shape) ────────────
        var mainSigBlob = new BlobBuilder();
        new BlobEncoder(mainSigBlob).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var _);
        var mainRetType = mainRetEnc.Type();
        mainRetType.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainRetType.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        mainRetType.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSigBlob),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── StandaloneSig #1: calli signature — C-cc + cmod_opt(CallConvCdecl) I4() ─
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.CDecl);                                    // C calling convention
        calliSig.WriteByte(0x00);                                                                      // 0 params
        calliSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        calliSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        var calliSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(calliSig));

        // ─── StandaloneSig #2: locals = (int32) — shared by get and main ─
        var localsSig = new BlobBuilder();
        new BlobEncoder(localsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var localsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(localsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("global-advanced.obj"),
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
        //   +0x00            hello[8]   "Hello!\0\0"               (8 bytes value)
        //   +0x08            e          addend=1 → hello           (ptrSize bytes)
        //   +0x08+p          m          addend=0 → bare-name get   (ptrSize bytes)
        //   +0x08+2p         __mep@?get  vtable-fixup slot          (ptrSize bytes)
        //   +0x08+3p         __mep@?main vtable-fixup slot          (ptrSize bytes)
        int helloOffset = 0;
        int eOffset = 8;
        int mOffset = 8 + ptrSize;
        int mepGetOffset = 8 + 2 * ptrSize;
        int mepMainOffset = 8 + 3 * ptrSize;

        dataStreamBuilder.WriteBytes(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x00, 0x00 });    // hello
        if (is32)
        {
            dataStreamBuilder.WriteInt32(1);   // e: addend 1
            dataStreamBuilder.WriteInt32(0);   // m: addend 0
            dataStreamBuilder.WriteInt32(0);   // __mep@?get  (linker stamps MethodDef token via TOKEN reloc)
            dataStreamBuilder.WriteInt32(0);   // __mep@?main (linker stamps MethodDef token via TOKEN reloc)
        }
        else
        {
            dataStreamBuilder.WriteInt64(1);
            dataStreamBuilder.WriteInt64(0);
            dataStreamBuilder.WriteInt64(0);
            dataStreamBuilder.WriteInt64(0);
        }

        // Pre-register data field COFF symbols BEFORE emitting IL that references them.
        symtab.AddDataClrToken(symPrefix + "hello", fieldHello, LogicalSection.Data, helloOffset, out _);
        symtab.AddDataClrToken(symPrefix + "e",     fieldE,     LogicalSection.Data, eOffset,     out _);
        symtab.AddDataClrToken(symPrefix + "m",     fieldM,     LogicalSection.Data, mOffset,     out _);

        // __mep@?get and __mep@?main are COFF-only data symbols (no FieldDef) at the
        // .data slots that the .nep thunks indirect through. The CLR fills these
        // slots with from-unmanaged stub addresses at load time, driven by the
        // ilfixup entries below.
        var mepGetDataSym  = symtab.AddDataSymbol("__mep@?get@@$$J0YAHXZ",  LogicalSection.Data, mepGetOffset);
        var mepMainDataSym = symtab.AddDataSymbol("__mep@?main@@$$J0YAHXZ", LogicalSection.Data, mepMainOffset);

        // ─── .nep layout ─────────────────────────────────────────────────
        // One indirect-jump thunk per method (no double-thunk-avoidance optimization).
        //   x86  : FF 25 [imm32→__mep@?fn]                                  (6  bytes, DIR32 reloc)
        //   x64  : FF 25 [rel32→__mep@?fn]                                  (6  bytes, REL32 reloc)
        //   arm64: ADRP X9,[__mep@?fn] / LDR X9,[X9,#off] / BR X9           (12 bytes, PAGEBASE_REL21 + PAGEOFFSET_12L)
        int thunkSize = machine == Machine.Arm64 ? 12 : 6;
        int getThunkOffset = 0;
        int mainThunkOffset = thunkSize;

        void EmitThunk(int thunkOff, CoffSymbolHandle mepDataSym)
        {
            if (machine == Machine.Arm64)
            {
                // ADRP X9, page-of-mep      ; encoded: 09 00 00 90 (placeholder; linker patches)
                // LDR X9, [X9, #pageoff]    ; encoded: 29 01 40 F9 (placeholder)
                // BR X9                     ; encoded: 20 01 1F D6
                nepStreamBuilder.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
                // IMAGE_REL_ARM64_PAGEBASE_REL21 = 0x0004 (at thunkOff+0)
                nepRelocBuilder.WriteInt32(thunkOff + 0);
                nepRelocBuilder.WriteInt32(mepDataSym._value);
                nepRelocBuilder.WriteUInt16(0x0004);
                // IMAGE_REL_ARM64_PAGEOFFSET_12L = 0x0006 (at thunkOff+4)
                nepRelocBuilder.WriteInt32(thunkOff + 4);
                nepRelocBuilder.WriteInt32(mepDataSym._value);
                nepRelocBuilder.WriteUInt16(0x0006);
            }
            else
            {
                // FF 25 [4-byte operand placeholder] — the linker fills the operand via the reloc.
                nepStreamBuilder.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
                nepRelocBuilder.WriteInt32(thunkOff + 2);
                nepRelocBuilder.WriteInt32(mepDataSym._value);
                if (is32)
                    nepRelocBuilder.WriteUInt16(0x0006);  // IMAGE_REL_I386_DIR32
                else
                    nepRelocBuilder.WriteUInt16(0x0004);  // IMAGE_REL_AMD64_REL32
            }
        }

        EmitThunk(getThunkOffset,  mepGetDataSym);
        EmitThunk(mainThunkOffset, mepMainDataSym);

        // Bare-name COFF symbol aliases for the NEP thunks. MSVC emits these as the
        // C-mangled names (`_get`/`get` etc.), External-linkage, so that native callers
        // (and unmanaged calli through `m`) bind to the thunk rather than the managed
        // IL body. We emit them as Static aliases pointing at the thunk in .nep.
        var getNepSymbol  = symtab.AddDataSymbol(symPrefix + "get",  LogicalSection.Nep, getThunkOffset);
        var mainNepSymbol = symtab.AddDataSymbol(symPrefix + "main", LogicalSection.Nep, mainThunkOffset);

        // ─── Emit .data relocations ──────────────────────────────────────
        // e   →  hello (ADDR with addend=1 already in slot data)
        // m   →  bare-name get (ADDR; resolves to NEP thunk at link time)
        var helloSymbol = symtab.AddDataSymbol(symPrefix + "hello", LogicalSection.Data, helloOffset);
        var dataRelocs = new CoffRelocationEncoder(coffHeader, dataRelocBuilder);
        dataRelocs.AddAddressRelocation(eOffset, helloSymbol);
        dataRelocs.AddAddressRelocation(mOffset, getNepSymbol);

        // ─── CodeView debug info ─────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("global-advanced.obj",
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "global-advanced.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for get ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.LoadConstantI4(42);            // IL_0000: ldc.i4.s 42
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(getMethod, "?get@@$$J0YAHXZ", enc,
                maxStack: 1, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "get");
        }

        // ─── Emit IL for main ────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(fieldM);
            enc.OpCode(ILOpCode.Calli);
            enc.Token(calliSigHandle);

            enc.OpCode(ILOpCode.Ldsflda);
            enc.Token(fieldHello);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i1);
            enc.OpCode(ILOpCode.Add);

            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(fieldE);
            enc.OpCode(ILOpCode.Ldind_i1);
            enc.OpCode(ILOpCode.Add);

            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 4, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── Stamp MethodDef tokens into __mep@?fn data slots via TOKEN relocs ─
        // (after AddMethodBody, the "06000001"/"06000002" CLR-token symbols exist.)
        int getMethodToken  = MetadataTokens.GetToken(getMethod);
        int mainMethodToken = MetadataTokens.GetToken(mainMethod);
        var getTokenSym  = symtab.GetOrAddUndefinedClrTokenSymbol(getMethodToken.ToString("X8"));
        var mainTokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(mainMethodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataRelocBuilder).AddTokenRelocation(mepGetOffset,  getTokenSym);
        new CoffRelocationEncoder(coffHeader, dataRelocBuilder).AddTokenRelocation(mepMainOffset, mainTokenSym);

        // ─── .rdata$ilfixup entries ──────────────────────────────────────
        // One 8-byte entry per __mep@?fn slot, telling the CLR to read the MethodDef
        // token stored in the slot and replace it with a from-unmanaged stub address
        // at load time. The NEP thunk's indirect JMP then lands on that stub.
        short ilFixupType = (short)(is32 ? 0x0009 : 0x000A);

        ilFixupStreamBuilder.WriteInt32(0);                       // RVA placeholder (linker patches via reloc)
        ilFixupStreamBuilder.WriteInt16(1);                       // Count
        ilFixupStreamBuilder.WriteInt16(ilFixupType);
        new CoffRelocationEncoder(coffHeader, ilFixupRelocBuilder).AddImageRelativeRelocation(0, mepGetDataSym);

        ilFixupStreamBuilder.WriteInt32(0);
        ilFixupStreamBuilder.WriteInt16(1);
        ilFixupStreamBuilder.WriteInt16(ilFixupType);
        new CoffRelocationEncoder(coffHeader, ilFixupRelocBuilder).AddImageRelativeRelocation(8, mepMainDataSym);

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
}
