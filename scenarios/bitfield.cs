using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class BitfieldTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "bitfield", refDir, "bitfield.obj"));
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

        // ─── TypeDef #2: _Flags (sequential, sealed, size=4) ──────────────
        var flagsTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Flags"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3)); // no methods on this type

        md.AddTypeLayout(flagsTypeDef, 0, 4);

        // CustomAttribute: NativeCppClassAttribute on _Flags
        md.AddCustomAttribute(flagsTypeDef, nativeCppCtorRef,
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

        // ─── MethodDef #1: bitfield_test ──────────────────────────────────
        // Signature: int32 bitfield_test()
        var bitfieldTestSig = new BlobBuilder();
        new BlobEncoder(bitfieldTestSig).MethodSignature()
            .Parameters(0, out var btRetEnc, out var btParEnc);
        btRetEnc.Type().Int32();

        var bitfieldTestMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("bitfield_test"),
            md.GetOrAddBlob(bitfieldTestSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // Locals for bitfield_test: int32 (slot 0), valuetype _Flags (slot 1)
        var btLocalsSig = new BlobBuilder();
        var btLocalsEnc = new BlobEncoder(btLocalsSig).LocalVariableSignature(2);
        btLocalsEnc.AddVariable().Type().Int32();
        btLocalsEnc.AddVariable().Type().Type(flagsTypeDef, isValueType: true);
        var btLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(btLocalsSig));

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
            MetadataTokens.ParameterHandle(1));

        // Locals for main: int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("bitfield.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "bitfield.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "bitfield.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for bitfield_test ────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // f.a = 5;
            enc.MarkLineNumber(cvFile, 15);
            enc.LoadLocalAddress(1);               // IL_0000: ldloca.s V_1
            enc.LoadLocalAddress(1);               // IL_0002: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0004
            enc.LoadConstantI4(-8);                // IL_0005: ldc.i4.s -8
            enc.OpCode(ILOpCode.And);              // IL_0007
            enc.OpCode(ILOpCode.Ldc_i4_5);        // IL_0008
            enc.OpCode(ILOpCode.Or);               // IL_0009
            enc.OpCode(ILOpCode.Stind_i4);         // IL_000A

            // f.b = 17;
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadLocalAddress(1);               // IL_000B: ldloca.s V_1
            enc.LoadLocalAddress(1);               // IL_000D: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_000F
            enc.LoadConstantI4(unchecked((int)0xFFFFFF07)); // IL_0010: ldc.i4 0xFFFFFF07
            enc.OpCode(ILOpCode.And);              // IL_0015
            enc.LoadConstantI4(unchecked((int)0x88)); // IL_0016: ldc.i4 0x88
            enc.OpCode(ILOpCode.Or);               // IL_001B
            enc.OpCode(ILOpCode.Stind_i4);         // IL_001C

            // f.c = 200;
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocalAddress(1);               // IL_001D: ldloca.s V_1
            enc.LoadLocalAddress(1);               // IL_001F: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0021
            enc.LoadConstantI4(unchecked((int)0xFFFF00FF)); // IL_0022: ldc.i4 0xFFFF00FF
            enc.OpCode(ILOpCode.And);              // IL_0027
            enc.LoadConstantI4(unchecked((int)0xC800)); // IL_0028: ldc.i4 0xC800
            enc.OpCode(ILOpCode.Or);               // IL_002D
            enc.OpCode(ILOpCode.Stind_i4);         // IL_002E

            // f.d = 1000;
            enc.MarkLineNumber(cvFile, 18);
            enc.LoadLocalAddress(1);               // IL_002F: ldloca.s V_1
            enc.LoadLocalAddress(1);               // IL_0031: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0033
            enc.LoadConstantI4(unchecked((int)0xFFFF)); // IL_0034: ldc.i4 0xFFFF
            enc.OpCode(ILOpCode.And);              // IL_0039
            enc.LoadConstantI4(unchecked((int)0x3E80000)); // IL_003A: ldc.i4 0x3E80000
            enc.OpCode(ILOpCode.Or);               // IL_003F
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0040

            // return f.a + f.b + f.c + f.d;
            enc.MarkLineNumber(cvFile, 19);
            enc.LoadLocalAddress(1);               // IL_0041: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0043
            enc.OpCode(ILOpCode.Ldc_i4_7);        // IL_0044
            enc.OpCode(ILOpCode.And);              // IL_0045
            enc.LoadLocalAddress(1);               // IL_0046: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0048
            enc.OpCode(ILOpCode.Ldc_i4_3);        // IL_0049
            enc.OpCode(ILOpCode.Shr_un);           // IL_004A
            enc.LoadConstantI4(31);                // IL_004B: ldc.i4.s 31
            enc.OpCode(ILOpCode.And);              // IL_004D
            enc.OpCode(ILOpCode.Add);              // IL_004E
            enc.LoadLocalAddress(1);               // IL_004F: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0051
            enc.OpCode(ILOpCode.Ldc_i4_8);        // IL_0052
            enc.OpCode(ILOpCode.Shr_un);           // IL_0053
            enc.LoadConstantI4(unchecked((int)0xFF)); // IL_0054: ldc.i4 0xFF
            enc.OpCode(ILOpCode.And);              // IL_0059
            enc.OpCode(ILOpCode.Add);              // IL_005A
            enc.LoadLocalAddress(1);               // IL_005B: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_005D
            enc.LoadConstantI4(16);                // IL_005E: ldc.i4.s 16
            enc.OpCode(ILOpCode.Shr_un);           // IL_0060
            enc.LoadConstantI4(unchecked((int)0xFFFF)); // IL_0061: ldc.i4 0xFFFF
            enc.OpCode(ILOpCode.And);              // IL_0066
            enc.OpCode(ILOpCode.Add);              // IL_0067
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0068
            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0069
            enc.OpCode(ILOpCode.Ret);              // IL_006A

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(btLocalsSigHandle), "f"),
            };

            bodyEncoder.AddMethodBody(bitfieldTestMethod, "?bitfield_test@@$$J0YMHXZ", enc,
                maxStack: 3, localVariablesSignature: btLocalsSigHandle, attributes: 0,
                debugName: "bitfield_test", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 24);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001
            enc.Call(bitfieldTestMethod);           // IL_0002: call bitfield_test
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0008
            enc.OpCode(ILOpCode.Ret);              // IL_0009

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
