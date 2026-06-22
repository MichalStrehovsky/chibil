using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class FuncptrArrayClrcallTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "funcptr-array_clrcall", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "funcptr-array_clrcall.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "funcptr-array_clrcall", refDir, "funcptr-array_clrcall.obj"));
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
        // No CallConvCdecl TypeRef — __clrcall uses DEFAULT calling convention.
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

        // ─── TypeDef #2: $ArrayType$$$BY01P6MHHH@Z ───────────────────────
        // Array of 2 __clrcall function pointers. P6M = __clrcall (was P6A for cdecl).
        int arrayTypeSize = machine == Machine.I386 ? 8 : 16;
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY01P6MHHH@Z"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),  // no __unep fields before this
            MetadataTokens.MethodDefinitionHandle(4)); // after add, sub_fn, main

        md.AddTypeLayout(arrayTypeDef, 0, (uint)arrayTypeSize);

        // CustomAttribute: NativeCppClassAttribute
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // No __unep@ fields — __clrcall functions use ldftn directly.

        // ─── StandaloneSignature for calli: DEFAULT I4(I4, I4) ────────────
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.Default);
        calliSig.WriteByte(0x02);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        var calliSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(calliSig));

        // ─── MethodDef #1: add ────────────────────────────────────────────
        var addSig = new BlobBuilder();
        new BlobEncoder(addSig).MethodSignature()
            .Parameters(2, out var addRetEnc, out var addParEnc);
        addRetEnc.Type().Int32();
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
        subRetEnc.Type().Int32();
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
        mainRetEnc.Type().Int32();

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
            md.GetOrAddString("funcptr-array_clrcall.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // No __unep@ data slots — __clrcall functions use ldftn directly.

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "funcptr-array_clrcall.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "funcptr-array_clrcall.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

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

            bodyEncoder.AddMethodBody(addMethod, "?add@@$$J0YMHHH@Z", enc,
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

            bodyEncoder.AddMethodBody(subMethod, "?sub_fn@@$$J0YMHHH@Z", enc,
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

            // ops[0] = add — use ldftn (not ldsfld __unep@) for __clrcall
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.LoadConstantI4(ptrSize);               // ldc.i4.4 / ldc.i4.8
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldftn);
            enc.Token(addMethod);
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
            enc.OpCode(ILOpCode.Ldftn);
            enc.Token(subMethod);
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

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 5, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for add and sub_fn only ───────────────────────
        // __clrcall main does NOT get a NEP thunk — the CLR resolves the
        // managed entry point directly from the metadata token.
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(addMethod), "add", "?add@@$$J0YMHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(subMethod), "sub_fn", "?sub_fn@@$$J0YMHHH@Z");

        // No __unep@ ADDR relocs — __clrcall functions don't have
        // unmanaged entry-point declaration fields.

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
