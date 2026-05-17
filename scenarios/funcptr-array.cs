using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class FuncptrArrayTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "funcptr-array", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "funcptr-array.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "funcptr-array", refDir, "funcptr-array.obj"));
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

        // ─── TypeRefs ─────────────────────────────────────────────────────
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));

        // ─── MemberRef: NativeCppClassAttribute::.ctor() ──────────────────
        var ctorSigBuilder = new BlobBuilder();
        new BlobEncoder(ctorSigBuilder).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(0, out var ctorRetEnc, out var ctorParEnc);
        ctorRetEnc.Void();
        var nativeCppCtorRef = md.AddMemberReference(nativeCppClassAttrRef,
            md.GetOrAddString(".ctor"), md.GetOrAddBlob(ctorSigBuilder));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── TypeDef #2: $ArrayType$$$BY01P6AHHH@Z ───────────────────────
        // Array of 2 function pointers. Size = 8 on x86 (2*4), 16 on arm64 (2*8).
        // Mangling: P6A = cdecl function pointer (was P6M for /clr:pure).
        int arrayTypeSize = machine == Machine.I386 ? 8 : 16;
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY01P6AHHH@Z"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(3),  // first 2 are __unep fields
            MetadataTokens.MethodDefinitionHandle(4)); // after add, sub_fn, main

        md.AddTypeLayout(arrayTypeDef, 0, (uint)arrayTypeSize);

        // CustomAttribute: NativeCppClassAttribute
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: __unep@?add — FNPTR [C] cmod_opt(CallConvCdecl) I4(I4, I4)
        // /clr taking the address of add yields the address of its NEP thunk;
        // MSVC materializes this through an `__unep@?fn` static field that
        // the linker stamps with the thunk address via ADDR reloc. The IL
        // in `main` loads these via `ldsfld __unep@?fn`.
        var unepAddSig = new BlobBuilder();
        unepAddSig.WriteByte(0x06); // FIELD
        unepAddSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        unepAddSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        unepAddSig.WriteByte(0x02);
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

        // ─── FieldDef #2: __unep@?sub_fn — same shape ────────────────────
        var unepSubSig = new BlobBuilder();
        unepSubSig.WriteByte(0x06); // FIELD
        unepSubSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        unepSubSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        unepSubSig.WriteByte(0x02);
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
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        calliSig.WriteByte(0x02);
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

        // ─── MethodDef #3: main ───────────────────────────────────────────
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
            MetadataTokens.ParameterHandle(5));

        // Locals for main: int32, ValueType $ArrayType$$$BY01P6MHHH@Z
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(arrayTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("funcptr-array.obj"),
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
        // reference to a metadata token; registering the data slot here
        // keeps `ldsfld unepAddField` bound to a defined symbol. The ADDR
        // reloc that binds the slot to the bare-name NEP thunk is added
        // below after the NEP machinery creates those symbols.
        int unepAddOffset = dataStreamBuilder.Count;
        for (int i = 0; i < ptrSize; i++) dataStreamBuilder.WriteByte(0);
        symtab.AddDataClrToken("__unep@?add@@$$J0YAHHH@Z", unepAddField, LogicalSection.Data, unepAddOffset, out _);

        int unepSubOffset = dataStreamBuilder.Count;
        for (int i = 0; i < ptrSize; i++) dataStreamBuilder.WriteByte(0);
        symtab.AddDataClrToken("__unep@?sub_fn@@$$J0YAHHH@Z", unepSubField, LogicalSection.Data, unepSubOffset, out _);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "funcptr-array.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "funcptr-array.c");
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
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

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
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldarg_1);
            enc.OpCode(ILOpCode.Sub);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            bodyEncoder.AddMethodBody(subMethod, "?sub_fn@@$$J0YAHHH@Z", enc,
                maxStack: 2, localVariablesSignature: addLocalsSigHandle, attributes: 0,
                debugName: "sub_fn");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // IL_0000-0001: init return value
            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);

            // ops[0] = add
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.LoadConstantI4(ptrSize);               // ldc.i4.4 / ldc.i4.8
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(unepAddField);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Stind_i4);
            else
                enc.OpCode(ILOpCode.Stind_i8);

            // ops[1] = sub_fn
            enc.MarkLineNumber(cvFile, 11);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.LoadConstantI4(ptrSize);
            if (machine != Machine.I386)
            {
                // arm64: no multiply — just add 8
            }
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(unepSubField);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Stind_i4);
            else
                enc.OpCode(ILOpCode.Stind_i8);

            // ops[0](10, 3)
            enc.MarkLineNumber(cvFile, 12);
            enc.LoadConstantI4(10);
            enc.OpCode(ILOpCode.Ldc_i4_3);
            enc.LoadLocalAddress(1);
            enc.LoadConstantI4(ptrSize);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Ldind_i4);
            else
                enc.OpCode(ILOpCode.Ldind_i8);
            enc.CallIndirect(calliSigHandle);

            // ops[1](10, 3)
            enc.LoadConstantI4(10);
            enc.OpCode(ILOpCode.Ldc_i4_3);
            enc.LoadLocalAddress(1);
            enc.LoadConstantI4(ptrSize);
            if (machine != Machine.I386)
            {
                // arm64: just add 8 — no multiply
            }
            enc.OpCode(ILOpCode.Add);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Ldind_i4);
            else
                enc.OpCode(ILOpCode.Ldind_i8);
            enc.CallIndirect(calliSigHandle);

            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "ops"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 5, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for all 3 user functions ──────────────────────
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
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

        // Stamp the pre-allocated __unep slots with ADDR relocs to the
        // bare-name NEP thunk symbols (link-time fill).
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
