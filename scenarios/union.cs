using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class UnionTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "union", refDir, "union.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        byte[] mscorlibHash = machine == Machine.I386
            ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
            : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
        CodeViewMachine cvMachine = machine == Machine.I386 ? CodeViewMachine.I386 : CodeViewMachine.Arm64;

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

        // ─── TypeDef #2: _Number (explicit layout, sealed, size=4) ────────
        var numberTypeDef = md.AddTypeDefinition(
            TypeAttributes.ExplicitLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Number"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3)); // no methods on this type

        md.AddTypeLayout(numberTypeDef, 0, 4);

        // CustomAttribute: NativeCppClassAttribute on _Number
        md.AddCustomAttribute(numberTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // Field: <alignment member> (private int32) — ARM64 only
        if (machine != Machine.I386)
        {
            var alignFieldSig = new BlobBuilder();
            new BlobEncoder(alignFieldSig).Field().Type().Int32();
            md.AddFieldDefinition(
                FieldAttributes.Private,
                md.GetOrAddString("<alignment member>"),
                md.GetOrAddBlob(alignFieldSig));
        }

        // ─── MethodDef #1: union_test ─────────────────────────────────────
        // Signature: int32()
        var unionTestSig = new BlobBuilder();
        new BlobEncoder(unionTestSig).MethodSignature()
            .Parameters(0, out var utRetEnc, out var utParEnc);
        utRetEnc.Type().Int32();

        var unionTestMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("union_test"),
            md.GetOrAddBlob(unionTestSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // Locals for union_test: int32, int32, float32, valuetype _Number, valuetype _Number
        var unionTestLocalsSig = new BlobBuilder();
        var unionTestLocalsEnc = new BlobEncoder(unionTestLocalsSig).LocalVariableSignature(5);
        unionTestLocalsEnc.AddVariable().Type().Int32();                                    // slot 0
        unionTestLocalsEnc.AddVariable().Type().Int32();                                    // slot 1
        unionTestLocalsEnc.AddVariable().Type().Single();                                   // slot 2: float32
        unionTestLocalsEnc.AddVariable().Type().Type(numberTypeDef, isValueType: true);     // slot 3: _Number
        unionTestLocalsEnc.AddVariable().Type().Type(numberTypeDef, isValueType: true);     // slot 4: _Number
        var unionTestLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(unionTestLocalsSig));

        // ─── MethodDef #2: main ───────────────────────────────────────────
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
            MetadataTokens.ParameterHandle(1)); // no parameters

        // Locals for main: int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("union.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "union.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "union.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for union_test ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // Number n; n.i = 0x41200000;
            enc.MarkLineNumber(cvFile, 13);
            enc.LoadLocalAddress(4);                                        // IL_0000: ldloca.s V_4
            enc.LoadConstantI4(unchecked((int)0x41200000));                 // IL_0002: ldc.i4 0x41200000
            enc.OpCode(ILOpCode.Stind_i4);                                 // IL_0007: stind.i4

            // float f = n.f;
            enc.MarkLineNumber(cvFile, 14);
            enc.LoadLocalAddress(4);                                        // IL_0008: ldloca.s V_4
            enc.OpCode(ILOpCode.Ldind_r4);                                  // IL_000A: ldind.r4
            enc.OpCode(ILOpCode.Stloc_2);                                   // IL_000B: stloc.2

            // Number m; m.f = 3.14f;
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadLocalAddress(3);                                        // IL_000C: ldloca.s V_3
            enc.LoadConstantR4(3.14f);                                      // IL_000E: ldc.r4 3.14
            enc.OpCode(ILOpCode.Stind_r4);                                  // IL_0013: stind.r4

            // int i = m.i;
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocalAddress(3);                                        // IL_0014: ldloca.s V_3
            enc.OpCode(ILOpCode.Ldind_i4);                                  // IL_0016: ldind.i4
            enc.OpCode(ILOpCode.Stloc_1);                                   // IL_0017: stloc.1

            // return i + (int)f;
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_1);                                   // IL_0018: ldloc.1
            enc.OpCode(ILOpCode.Ldloc_2);                                   // IL_0019: ldloc.2
            enc.OpCode(ILOpCode.Conv_r8);                                   // IL_001A: conv.r8
            enc.OpCode(ILOpCode.Conv_i4);                                   // IL_001B: conv.i4
            enc.OpCode(ILOpCode.Add);                                       // IL_001C: add
            enc.OpCode(ILOpCode.Stloc_0);                                   // IL_001D: stloc.0
            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_0);                                   // IL_001E: ldloc.0
            enc.OpCode(ILOpCode.Ret);                                       // IL_001F: ret

            var unionTestLocalSlots = new[] {
                new CodeViewManSlot(4, MetadataTokens.GetToken(unionTestLocalsSigHandle), "n"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(unionTestLocalsSigHandle), "i"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(unionTestLocalsSigHandle), "f"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(unionTestLocalsSigHandle), "m"),
            };

            bodyEncoder.AddMethodBody(unionTestMethod, "?union_test@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: unionTestLocalsSigHandle, attributes: 0,
                debugName: "union_test", localSlots: unionTestLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldc_i4_0);                                  // IL_0000: ldc.i4.0
            enc.OpCode(ILOpCode.Stloc_0);                                   // IL_0001: stloc.0
            enc.Call(unionTestMethod);                                       // IL_0002: call union_test
            enc.OpCode(ILOpCode.Stloc_0);                                   // IL_0007: stloc.0
            enc.OpCode(ILOpCode.Ldloc_0);                                   // IL_0008: ldloc.0
            enc.OpCode(ILOpCode.Ret);                                       // IL_0009: ret

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 1, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
