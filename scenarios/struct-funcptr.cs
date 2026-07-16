using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class StructFuncptrTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "struct-funcptr", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "struct-funcptr.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "struct-funcptr", refDir, "struct-funcptr.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        bool is32 = machine == Machine.I386;
        int ptrSize = is32 ? 4 : 8;
        string symPrefix = is32 ? "_" : "";
        string e = is32 ? "" : "E";

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

        // ─── TypeDef #2: _Handler ─────────────────────────────────────────
        // x86: size=8 (4 bytes fnptr + 4 bytes int), arm64: size=16 (8 bytes fnptr + alignment + 4 bytes int + padding)
        int handlerSize = machine == Machine.I386 ? 8 : 16;
        var handlerTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Handler"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(2), // __unep@?double_it is the 1st FieldDef
            MetadataTokens.MethodDefinitionHandle(4)); // after double_it, invoke, main

        md.AddTypeLayout(handlerTypeDef, 0, (uint)handlerSize);

        // CustomAttribute: NativeCppClassAttribute
        md.AddCustomAttribute(handlerTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: __unep@?double_it — FNPTR [C] cmod_opt(CallConvCdecl) I4(I4)
        // Same /clr IJW pattern as funcptr/funcptr-array: the address of
        // `double_it` (taken via `h.callback = double_it`) flows through
        // an `__unep@?double_it` slot the linker stamps with the NEP thunk
        // address. main loads via `ldsfld __unep@?double_it`. Declared on
        // `<Module>` before _Handler so its FieldDef token is 04000001.
        var unepDoubleSig = new BlobBuilder();
        unepDoubleSig.WriteByte(0x06); // FIELD
        unepDoubleSig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        unepDoubleSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        unepDoubleSig.WriteByte(0x01); // 1 param
        unepDoubleSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        unepDoubleSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        unepDoubleSig.WriteByte((byte)SignatureTypeCode.Int32); // return
        unepDoubleSig.WriteByte((byte)SignatureTypeCode.Int32); // param 1
        var unepDoubleField = md.AddFieldDefinition(
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("__unep@?double_it@@$$J0YAHH@Z"),
            md.GetOrAddBlob(unepDoubleSig));
        md.AddFieldRelativeVirtualAddress(unepDoubleField, 0);

        // Field: <alignment member> (private int64) — ARM64 only
        if (machine != Machine.I386)
        {
            var alignFieldSig = new BlobBuilder();
            new BlobEncoder(alignFieldSig).Field().Type().Int64();
            md.AddFieldDefinition(
                FieldAttributes.Private,
                md.GetOrAddString("<alignment member>"),
                md.GetOrAddBlob(alignFieldSig));
        }

        // ─── StandaloneSignature for calli: [C] cmod_opt(CallConvCdecl) I4(I4)
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.CDecl);
        calliSig.WriteByte(0x01);
        calliSig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        calliSig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        var calliSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(calliSig));

        // ─── MethodDef #1: double_it(int32) -> cmod_opt(CallConvCdecl) int32
        var doubleSig = new BlobBuilder();
        new BlobEncoder(doubleSig).MethodSignature()
            .Parameters(1, out var doubleRetEnc, out var doubleParEnc);
        ClrIjw.EncodeCdeclI4Return(doubleRetEnc, callConvCdeclRef);
        doubleParEnc.AddParameter().Type().Int32();

        var doubleMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("double_it"),
            md.GetOrAddBlob(doubleSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // Locals for double_it: 1 x int32
        var doubleLocalsSig = new BlobBuilder();
        new BlobEncoder(doubleLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var doubleLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(doubleLocalsSig));

        // ─── MethodDef #2: invoke(Ptr ValueType _Handler) -> int32 ────────
        var invokeSig = new BlobBuilder();
        new BlobEncoder(invokeSig).MethodSignature()
            .Parameters(1, out var invokeRetEnc, out var invokeParEnc);
        ClrIjw.EncodeCdeclI4Return(invokeRetEnc, callConvCdeclRef);
        invokeParEnc.AddParameter().Type().Pointer().Type(handlerTypeDef, isValueType: true);

        var invokeMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("invoke"),
            md.GetOrAddBlob(invokeSig),
            0,
            MetadataTokens.ParameterHandle(2));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("h"), 1);

        // Locals for invoke: 1 x int32
        // Reuse doubleLocalsSigHandle

        // ─── MethodDef #3: main() -> cmod_opt(CallConvCdecl) int32 ──────
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
            MetadataTokens.ParameterHandle(3));

        // Locals for main: int32, ValueType _Handler
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(handlerTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("struct-funcptr.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // ─── __unep@?double_it slot: allocate + AddDataClrToken BEFORE IL ─
        int unepDoubleOffset = dataSection.Content.Count;
        for (int i = 0; i < ptrSize; i++) dataSection.Content.WriteByte(0);
        symtab.AddDataClrToken("__unep@?double_it@@$$J0YAHH@Z", unepDoubleField, dataSection, unepDoubleOffset, out _);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "struct-funcptr.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "struct-funcptr.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for double_it ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);             // IL_0000
            enc.OpCode(ILOpCode.Ldc_i4_2);            // IL_0001
            enc.OpCode(ILOpCode.Mul);                  // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0003
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0004
            enc.OpCode(ILOpCode.Ret);                  // IL_0005

            bodyEncoder.AddMethodBody(doubleMethod, "?double_it@@$$J0YAHH@Z", enc,
                maxStack: 2, localVariablesSignature: doubleLocalsSigHandle, attributes: 0,
                debugName: "double_it");
        }

        // ─── Emit IL for invoke ───────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            if (machine == Machine.I386)
            {
                // x86: h->value at offset 4, h->callback at offset 0
                enc.MarkLineNumber(cvFile, 13);
                enc.OpCode(ILOpCode.Ldarg_0);             // IL_0000
                enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0001
                enc.OpCode(ILOpCode.Add);                  // IL_0002
                enc.OpCode(ILOpCode.Ldind_i4);             // IL_0003: h->value
                enc.OpCode(ILOpCode.Ldarg_0);             // IL_0004
                enc.OpCode(ILOpCode.Ldind_i4);             // IL_0005: h->callback (i4 on x86)
                enc.CallIndirect(calliSigHandle);          // IL_0006: calli
                enc.OpCode(ILOpCode.Stloc_0);             // IL_000B
                enc.MarkLineNumber(cvFile, 14);
                enc.OpCode(ILOpCode.Ldloc_0);             // IL_000C
                enc.OpCode(ILOpCode.Ret);                  // IL_000D
            }
            else
            {
                // arm64: h->value at offset 8, h->callback at offset 0
                enc.MarkLineNumber(cvFile, 13);
                enc.OpCode(ILOpCode.Ldarg_0);             // IL_0000
                enc.LoadConstantI4(8);                     // IL_0001: ldc.i4.8
                enc.OpCode(ILOpCode.Conv_i8);              // IL_0002
                enc.OpCode(ILOpCode.Add);                  // IL_0003
                enc.OpCode(ILOpCode.Ldind_i4);             // IL_0004: h->value
                enc.OpCode(ILOpCode.Ldarg_0);             // IL_0005
                enc.OpCode(ILOpCode.Ldind_i8);             // IL_0006: h->callback (i8 on arm64)
                enc.CallIndirect(calliSigHandle);          // IL_0007: calli
                enc.OpCode(ILOpCode.Stloc_0);             // IL_000C
                enc.MarkLineNumber(cvFile, 14);
                enc.OpCode(ILOpCode.Ldloc_0);             // IL_000D
                enc.OpCode(ILOpCode.Ret);                  // IL_000E
            }

            bodyEncoder.AddMethodBody(invokeMethod, $"?invoke@@$$J0YAHP{e}AU_Handler@@@Z", enc,
                maxStack: 2, localVariablesSignature: doubleLocalsSigHandle, attributes: 0,
                debugName: "invoke");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001

            // h.callback = double_it  (at offset 0)
            enc.LoadLocalAddress(1);                   // IL_0002: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldsfld);
            enc.Token(unepDoubleField);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Stind_i4);         // IL_000A: stind.i4
            else
                enc.OpCode(ILOpCode.Stind_i8);         // arm64: stind.i8

            // h.value = 21  (at offset 4 on x86, 8 on arm64)
            enc.MarkLineNumber(cvFile, 20);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            int valueOffset = machine == Machine.I386 ? 4 : 8;
            enc.LoadConstantI4(valueOffset);
            enc.OpCode(ILOpCode.Add);
            enc.LoadConstantI4(21);                    // ldc.i4.s 21
            enc.OpCode(ILOpCode.Stind_i4);

            // return invoke(&h)
            enc.MarkLineNumber(cvFile, 21);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.Call(invokeMethod);                    // call invoke
            enc.OpCode(ILOpCode.Stloc_0);
            enc.MarkLineNumber(cvFile, 22);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "h"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for all 3 user functions ──────────────────────
        var doubleBareSym = ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            doubleMethod, "double_it", "?double_it@@$$J0YAHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            invokeMethod, "invoke", $"?invoke@@$$J0YAHP{e}AU_Handler@@@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            mainMethod, "main", "?main@@$$J0YAHXZ");

        // Stamp the __unep slot with an ADDR reloc to the bare-name NEP thunk.
        new CoffRelocationEncoder(coffHeader, dataSection.Relocations).AddAddressRelocation(unepDoubleOffset, doubleBareSym);

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
