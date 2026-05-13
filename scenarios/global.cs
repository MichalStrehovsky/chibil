using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class GlobalTest
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
        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "global", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "global.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "global", refDir, "global.obj"));
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

        // ─── TypeDef #2: $ArrayType$$$BY03H — value type for int[4] (size 16) ─
        var arrayType3H = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY03H"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(4),  // no fields of its own
            MetadataTokens.MethodDefinitionHandle(2));
        md.AddTypeLayout(arrayType3H, 0, 16);
        md.AddCustomAttribute(arrayType3H, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: g_initialized — int32 = 42 ────────────────────
        var gInitSig = new BlobBuilder();
        new BlobEncoder(gInitSig).Field().Type().Int32();
        var fieldGInit = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("g_initialized"),
            md.GetOrAddBlob(gInitSig));
        md.AddFieldRelativeVirtualAddress(fieldGInit, 0);

        // ─── FieldDef #2: g_array — ValueClass $ArrayType$$$BY03H = {1,2,3,4}
        var gArraySig = new BlobBuilder();
        new BlobEncoder(gArraySig).Field().Type().Type(arrayType3H, isValueType: true);
        var fieldGArray = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("g_array"),
            md.GetOrAddBlob(gArraySig));
        md.AddFieldRelativeVirtualAddress(fieldGArray, 0);

        // ─── FieldDef #3: g_uninitialized — int32 (common symbol) ────────
        var gUninitSig = new BlobBuilder();
        new BlobEncoder(gUninitSig).Field().Type().Int32();
        var fieldGUninit = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("g_uninitialized"),
            md.GetOrAddBlob(gUninitSig));
        md.AddFieldRelativeVirtualAddress(fieldGUninit, 0);

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

        // ─── StandaloneSig: locals = (int32, int32, int32) ─ i / sum / return
        var localsSig = new BlobBuilder();
        var localsSigEnc = new BlobEncoder(localsSig).LocalVariableSignature(3);
        localsSigEnc.AddVariable().Type().Int32();
        localsSigEnc.AddVariable().Type().Int32();
        localsSigEnc.AddVariable().Type().Int32();
        var localsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(localsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("global.obj"),
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
        //   +0x00  g_initialized   = 42                         (4 bytes; padded to 8 on 64-bit)
        //   +0x08  g_array         = {1, 2, 3, 4}               (16 bytes)
        // g_uninitialized is a common symbol (Sect=0, Value=size) — the linker
        // allocates space at link time, no .data bytes here.
        int gInitOffset = 0;
        int gArrayOffset = 8;

        dataStreamBuilder.WriteInt32(42);                              // g_initialized = 42
        dataStreamBuilder.WriteInt32(0);                               // pad to 8 bytes (g_array is 8-byte aligned)
        dataStreamBuilder.WriteInt32(1);                               // g_array[0]
        dataStreamBuilder.WriteInt32(2);                               // g_array[1]
        dataStreamBuilder.WriteInt32(3);                               // g_array[2]
        dataStreamBuilder.WriteInt32(4);                               // g_array[3]

        // Pre-register data field COFF symbols BEFORE emitting IL.
        symtab.AddDataClrToken(symPrefix + "g_initialized", fieldGInit,  LogicalSection.Data, gInitOffset,  out _);
        symtab.AddDataClrToken(symPrefix + "g_array",       fieldGArray, LogicalSection.Data, gArrayOffset, out _);
        // g_uninitialized: common symbol, 4-byte int
        symtab.AddCommonDataClrToken(symPrefix + "g_uninitialized", fieldGUninit, 4, out _);

        // ─── CodeView debug info ─────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("global.obj",
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "global.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main ────────────────────────────────────────────
        // C: g_uninitialized = 10; sum = g_initialized + g_uninitialized;
        //    for (i = 0; i < 4; i++) sum += g_array[i]; return sum;
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_loopBody  = enc.DefineLabel();
            var lbl_loopTest  = enc.DefineLabel();
            var lbl_afterLoop = enc.DefineLabel();

            // Note on line markers: MSVC's /clr line table puts the implicit "return = 0"
            // prologue under the FIRST statement's line (line 10), so the line-10 marker
            // covers IL 0x00..0x08 (prologue + `g_uninitialized = 10`). Subsequent line
            // markers are placed AFTER each statement's IL completes.
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_2);                               // return = 0 (prologue)
            enc.LoadConstantI4(10);
            enc.OpCode(ILOpCode.Stsfld);
            enc.Token(fieldGUninit);                                    // g_uninitialized = 10

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(fieldGInit);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(fieldGUninit);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_1);                               // sum = g_initialized + g_uninitialized

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);                               // i = 0
            enc.Branch(ILOpCode.Br_s, lbl_loopTest);

            enc.MarkLabel(lbl_loopBody);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);                               // i++

            enc.MarkLabel(lbl_loopTest);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldc_i4_4);
            enc.Branch(ILOpCode.Bge_s, lbl_afterLoop);

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Ldsflda);
            enc.Token(fieldGArray);
            enc.OpCode(ILOpCode.Ldloc_0);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (!is32) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i4);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_1);                               // sum += g_array[i]
            enc.Branch(ILOpCode.Br_s, lbl_loopBody);

            enc.MarkLabel(lbl_afterLoop);
            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Stloc_2);                               // return = sum
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Ret);

            var mainLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(localsSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(localsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 4, localVariablesSignature: localsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
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

        var mepDataSym = symtab.AddDataSymbol("__mep@" + mangledSuffix, LogicalSection.Data, slotOffset);

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
            nepRelocs.WriteUInt16(0x0006);
        }
        else
        {
            nepStream.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepRelocs.WriteInt32(thunkOffset + 2);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(is32 ? (ushort)0x0006 : (ushort)0x0004);
        }

        symtab.AddDataSymbol(symPrefix + bareName, LogicalSection.Nep, thunkOffset);

        int ilfixupOffset = ilFixupStream.Count;
        ilFixupStream.WriteInt32(0);
        ilFixupStream.WriteInt16(1);
        ilFixupStream.WriteInt16(is32 ? (short)0x0009 : (short)0x000A);
        new CoffRelocationEncoder(coffHeader, ilFixupRelocs).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);
    }
}
