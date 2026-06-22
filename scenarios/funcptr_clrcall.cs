using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class FuncptrClrcallTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "funcptr_clrcall", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "funcptr_clrcall.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "funcptr_clrcall", refDir, "funcptr_clrcall.obj"));
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

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        // No CallConvCdecl TypeRef — __clrcall uses DEFAULT calling convention.
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // No __unep@ fields — __clrcall functions use ldftn directly, not
        // ldsfld through a loader-populated unmanaged entry-point slot.

        // ─── StandaloneSignature for calli: DEFAULT I4(I4, I4) ────────────
        // __clrcall uses the managed DEFAULT calling convention, not CDecl.
        var calliSig = new BlobBuilder();
        calliSig.WriteByte((byte)SignatureCallingConvention.Default);
        calliSig.WriteByte(0x02); // 2 params
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        calliSig.WriteByte((byte)SignatureTypeCode.Int32);
        var calliSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(calliSig));

        // ─── MethodDef #1: add ────────────────────────────────────────────
        // __clrcall: no modopt(CallConvCdecl) on return type.
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

        // ─── MethodDef #3: apply ──────────────────────────────────────────
        // Sig: DEFAULT I4( FNPTR DEFAULT I4(I4, I4), I4, I4 )
        var applySig = new BlobBuilder();
        new BlobEncoder(applySig).MethodSignature()
            .Parameters(3, out var applyRetEnc, out var applyParEnc);
        applyRetEnc.Type().Int32();
        // Param 1: FnPtr DEFAULT I4(I4, I4) — __clrcall function pointer
        var applyP1 = applyParEnc.AddParameter().Type();
        applyP1.Builder.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        applyP1.Builder.WriteByte((byte)SignatureCallingConvention.Default);
        applyP1.Builder.WriteByte(0x02); // 2 params
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

        // ─── MethodDef #4: main ───────────────────────────────────────────
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
            MetadataTokens.ParameterHandle(8));

        // Locals for main: 4 locals (int32, int32, int32, FnPtr DEFAULT I4(I4, I4))
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(4);
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 0: return value
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 1: b
        mainLocalsEnc.AddVariable().Type().Int32();   // slot 2: a
        // slot 3: fp — FnPtr DEFAULT I4(I4, I4)
        var mainLocFp = mainLocalsEnc.AddVariable().Type();
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.FunctionPointer);
        mainLocFp.Builder.WriteByte((byte)SignatureCallingConvention.Default);
        mainLocFp.Builder.WriteByte(0x02); // 2 params
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // return
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 1
        mainLocFp.Builder.WriteByte((byte)SignatureTypeCode.Int32); // param 2
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("funcptr_clrcall.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // No __unep@ data slots needed — __clrcall functions are managed-only.

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "funcptr_clrcall.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "funcptr_clrcall.c");
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
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Add);             // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0003
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0004
            enc.OpCode(ILOpCode.Ret);             // IL_0005

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
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Sub);             // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0003
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0004
            enc.OpCode(ILOpCode.Ret);             // IL_0005

            bodyEncoder.AddMethodBody(subMethod, "?sub_fn@@$$J0YMHHH@Z", enc,
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

            bodyEncoder.AddMethodBody(applyMethod, "?apply@@$$J0YMHP6MHHH@ZHH@Z", enc,
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

            // fp = add; — use ldftn (not ldsfld __unep@) for __clrcall
            enc.OpCode(ILOpCode.Ldftn);            // IL_0002: ldftn add
            enc.Token(addMethod);
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
            enc.OpCode(ILOpCode.Ldftn);            // IL_0013: ldftn sub_fn
            enc.Token(subMethod);
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

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for add, sub_fn, apply ─────────────────────
        // __clrcall functions still get NEP thunks so the bare-name COFF
        // symbols exist for linking. However, main does NOT get a NEP
        // thunk under __clrcall — the CLR resolves the managed entry
        // point directly from the metadata token.
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(addMethod), "add", "?add@@$$J0YMHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(subMethod), "sub_fn", "?sub_fn@@$$J0YMHHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            MetadataTokens.GetToken(applyMethod), "apply", "?apply@@$$J0YMHP6MHHH@ZHH@Z");

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
