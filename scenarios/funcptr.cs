using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class FuncptrTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "funcptr", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "funcptr.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "funcptr", refDir, "funcptr.obj"));
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

        // ─── FieldDef #1: __unep@?add — FNPTR [C] cmod_opt(CallConvCdecl) I4(I4, I4)
        // Under /clr, taking the address of a managed C function yields a
        // pointer to the native entry-point thunk; MSVC materializes this
        // through an `__unep@?fn` static field that the loader populates with
        // the from-unmanaged stub address at module load time. The IL in
        // `main` (`fp = add;` / `apply(sub_fn, ...)`) emits `ldsfld __unep@?fn`
        // rather than `ldftn fnMethod`.
        var unepAddSig = new BlobBuilder();
        unepAddSig.WriteByte(0x06); // FIELD
        unepAddSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        unepAddSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        unepAddSig.WriteByte(0x02); // 2 params
        unepAddSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        unepAddSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        unepAddSig.WriteByte((byte)SignatureTypeCode.Int32);
        unepAddSig.WriteByte((byte)SignatureTypeCode.Int32);
        unepAddSig.WriteByte((byte)SignatureTypeCode.Int32);

        var unepAddField = md.AddFieldDefinition(
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("__unep@?add@@$$J0YAHHH@Z"),
            md.GetOrAddBlob(unepAddSig));
        md.AddFieldRelativeVirtualAddress(unepAddField, 0);

        // ─── FieldDef #2: __unep@?sub_fn — same shape as __unep@?add ─────
        var unepSubSig = new BlobBuilder();
        unepSubSig.WriteByte(0x06); // FIELD
        unepSubSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        unepSubSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        unepSubSig.WriteByte(0x02); // 2 params
        unepSubSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        unepSubSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        unepSubSig.WriteByte((byte)SignatureTypeCode.Int32);
        unepSubSig.WriteByte((byte)SignatureTypeCode.Int32);
        unepSubSig.WriteByte((byte)SignatureTypeCode.Int32);

        var unepSubField = md.AddFieldDefinition(
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("__unep@?sub_fn@@$$J0YAHHH@Z"),
            md.GetOrAddBlob(unepSubSig));
        md.AddFieldRelativeVirtualAddress(unepSubField, 0);

        // ─── StandaloneSignature for calli: [C] cmod_opt(CallConvCdecl) I4(I4, I4)
        // Must be created before any method body that references it so its
        // token (StandAloneSig #1) is stable.
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        calliSig.WriteByte(0x02); // 2 params
        calliSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        calliSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        var calliSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(calliSig));

        // ─── MethodDef #1: add ────────────────────────────────────────────
        var addSig = new BlobBuilder();
        new BlobEncoder(addSig).MethodSignature()
            .Parameters(2, out var addRetEnc, out var addParEnc);
        ClrIjw.EncodeCdeclI4Return(addRetEnc, callConvCdeclRef);
        addParEnc.AddParameter().Type().Int32();
        addParEnc.AddParameter().Type().Int32();

        var addMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("add"),
            md.GetOrAddBlob(addSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for add: 1 x int32
        var addLocalsSig = new BlobBuilder();
        new BlobEncoder(addLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var addLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(addLocalsSig));

        // ─── MethodDef #2: sub_fn ─────────────────────────────────────────
        var subSig = new BlobBuilder();
        new BlobEncoder(subSig).MethodSignature()
            .Parameters(2, out var subRetEnc, out var subParEnc);
        ClrIjw.EncodeCdeclI4Return(subRetEnc, callConvCdeclRef);
        subParEnc.AddParameter().Type().Int32();
        subParEnc.AddParameter().Type().Int32();

        var subMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sub_fn"),
            md.GetOrAddBlob(subSig),
            0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // sub_fn shares the same local signature as add (1 x int32)
        // No need to create a new StandaloneSignature — reuse addLocalsSigHandle

        // ─── MethodDef #3: apply ──────────────────────────────────────────
        // Sig: [C] cmod_opt(CallConvCdecl) I4( FNPTR [C] cmod_opt(CallConvCdecl) I4(I4, I4), I4, I4 )
        var applySig = new BlobBuilder();
        new BlobEncoder(applySig).MethodSignature()
            .Parameters(3, out var applyRetEnc, out var applyParEnc);
        ClrIjw.EncodeCdeclI4Return(applyRetEnc, callConvCdeclRef);
        // Param 1: FnPtr cdecl modopt(CallConvCdecl) I4(I4, I4)
        var applyP1 = applyParEnc.AddParameter().Type();
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        applyP1.Builder.WriteByte((byte)SignatureCallingConvention.CDecl);
        applyP1.Builder.WriteByte(0x02); // 2 params
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        applyP1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.Int32); // return
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 1
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 2
        applyParEnc.AddParameter().Type().Int32();
        applyParEnc.AddParameter().Type().Int32();

        var applyMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("apply"),
            md.GetOrAddBlob(applySig),
            0,
            MetadataTokens.ParameterHandle(5));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("fn"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("y"), 3);

        // apply shares the same local signature as add (1 x int32)
        // No need to create a new StandaloneSignature — reuse addLocalsSigHandle

        // ─── MethodDef #4: main ───────────────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var mainParEnc);
        ClrIjw.EncodeCdeclI4Return(mainRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig),
            0,
            MetadataTokens.ParameterHandle(8));

        // Locals for main: 4 locals (int32, int32, int32, FnPtr [C] cmod_opt(CallConvCdecl) I4(I4, I4))
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(4);
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 0: return value
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 1: b
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 2: a
        // slot 3: fp — FnPtr cdecl modopt(CallConvCdecl) I4(I4, I4)
        var mainLocFp = mainLocalsEnc.AddVariable().Type();
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        mainLocFp.Builder.WriteByte((byte)SignatureCallingConvention.CDecl);
        mainLocFp.Builder.WriteByte(0x02); // 2 params
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        mainLocFp.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // return
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 1
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 2
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("funcptr.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
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

        // ─── __unep@?fn slots: allocate + AddDataClrToken BEFORE IL ─────
        // The IL writer creates an undefined clr-token COFF symbol on first
        // reference to a metadata token; `AddDataClrToken` throws if asked
        // to define a token that's already been registered as undefined.
        // We must therefore register the data slot here, before the IL
        // block runs `enc.Token(unepAddField)`. The ADDR reloc that binds
        // the slot to the bare-name NEP thunk is added below after the
        // NEP machinery creates those symbols.
        int unepAddOffset = dataStreamBuilder.Count;
        for (int i = 0; i < ptrSize; i++) dataStreamBuilder.WriteByte(0);
        symtab.AddDataClrToken("__unep@?add@@$$J0YAHHH@Z", unepAddField, LogicalSection.Data, unepAddOffset, out _);

        int unepSubOffset = dataStreamBuilder.Count;
        for (int i = 0; i < ptrSize; i++) dataStreamBuilder.WriteByte(0);
        symtab.AddDataClrToken("__unep@?sub_fn@@$$J0YAHHH@Z", unepSubField, LogicalSection.Data, unepSubOffset, out _);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "funcptr.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "funcptr.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for add ──────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 4);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Add);             // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0003
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0004
            enc.OpCode(ILOpCode.Ret);             // IL_0005

            bodyEncoder.AddMethodBody(addMethod, "?add@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: addLocalsSigHandle, attributes: 0,
                debugName: "add");
        }

        // ─── Emit IL for sub_fn ───────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 5);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Sub);             // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0003
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0004
            enc.OpCode(ILOpCode.Ret);             // IL_0005

            bodyEncoder.AddMethodBody(subMethod, "?sub_fn@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: addLocalsSigHandle, attributes: 0,
                debugName: "sub_fn");
        }

        // ─── Emit IL for apply ────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_2);         // IL_0001
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0002
            enc.CallIndirect(calliSigHandle);      // IL_0003: calli StandaloneSig(1)
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0008
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0009
            enc.OpCode(ILOpCode.Ret);             // IL_000A

            bodyEncoder.AddMethodBody(applyMethod, "?apply@@$$J0YAHP6AHHH@ZHH@Z", enc,
                maxStack: 3, localVariablesSignature: addLocalsSigHandle, attributes: 0,
                debugName: "apply");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001

            // fp = add;
            enc.OpCode(ILOpCode.Ldsfld);            // IL_0002
            enc.Token(unepAddField);
            enc.OpCode(ILOpCode.Stloc_3);         // IL_0008

            // int a = fp(10, 3);
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadConstantI4(10);                // IL_0009: ldc.i4.s 10
            enc.OpCode(ILOpCode.Ldc_i4_3);        // IL_000B
            enc.OpCode(ILOpCode.Ldloc_3);         // IL_000C
            enc.CallIndirect(calliSigHandle);      // IL_000D: calli StandaloneSig(1)
            enc.OpCode(ILOpCode.Stloc_2);         // IL_0012

            // int b = apply(sub_fn, 10, 3);
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldsfld);            // IL_0013
            enc.Token(unepSubField);
            enc.LoadConstantI4(10);                // IL_0019: ldc.i4.s 10
            enc.OpCode(ILOpCode.Ldc_i4_3);        // IL_001B
            enc.Call(applyMethod);                 // IL_001C: call apply
            enc.OpCode(ILOpCode.Stloc_1);         // IL_0021

            // return a + b;
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_2);         // IL_0022
            enc.OpCode(ILOpCode.Ldloc_1);         // IL_0023
            enc.OpCode(ILOpCode.Add);             // IL_0024
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0025

            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0026
            enc.OpCode(ILOpCode.Ret);             // IL_0027

            var mainLocalSlots = new[] {
                new CodeViewManSlot(3, MetadataTokens.GetToken(mainLocalsSigHandle), "fp"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "b"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "a"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for all 4 user functions ──────────────────────
        // Emit NEP thunks so the bare-name COFF symbols (`add`, `sub_fn`,
        // `apply`, `main`) exist before we ADDR-reloc the __unep slots
        // against them below.
        var addBareSym = ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(addMethod), "add", "?add@@$$J0YAHHH@Z");
        var subBareSym = ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(subMethod), "sub_fn", "?sub_fn@@$$J0YAHHH@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(applyMethod), "apply", "?apply@@$$J0YAHP6AHHH@ZHH@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

        // Stamp the pre-allocated __unep slots with ADDR relocs to the
        // matching bare-name NEP thunk symbols. The linker fills the slots
        // with the thunk addresses at link time so `ldsfld __unep@?fn`
        // yields a real native function pointer.
        new CoffRelocationEncoder(coffHeader, dataRelocBuilder).AddAddressRelocation(unepAddOffset, addBareSym);
        new CoffRelocationEncoder(coffHeader, dataRelocBuilder).AddAddressRelocation(unepSubOffset, subBareSym);

        // ─── Build COFF & Serialize ───────────────────────────────────────
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
