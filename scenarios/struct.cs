using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class StructTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "struct", refDir, "struct.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
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

        // ─── TypeDef #2: _MyStruct (sequential, sealed, size=12) ──────────
        var myStructTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_MyStruct"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3)); // no methods on this type

        md.AddTypeLayout(myStructTypeDef, 0, 12);

        // CustomAttribute: NativeCppClassAttribute on _MyStruct
        md.AddCustomAttribute(myStructTypeDef, nativeCppCtorRef,
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

        // ─── MethodDef #1: sum_struct ─────────────────────────────────────
        // Signature: int32 sum_struct(valuetype _MyStruct*)
        var sumStructSig = new BlobBuilder();
        var sumStructSigEnc = new BlobEncoder(sumStructSig).MethodSignature();
        sumStructSigEnc.Parameters(1, out var sumRetEnc, out var sumParEnc);
        sumRetEnc.Type().Int32();
        // Param: Ptr ValueClass _MyStruct
        sumParEnc.AddParameter().Type().Pointer().Type(myStructTypeDef, isValueType: true);

        var sumStructMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sum_struct"),
            md.GetOrAddBlob(sumStructSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // Parameter: pS
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("pS"), 1);

        // Locals for sum_struct: int32
        var sumLocalsSig = new BlobBuilder();
        new BlobEncoder(sumLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var sumLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(sumLocalsSig));

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
            MetadataTokens.ParameterHandle(2)); // after pS

        // Locals for main: int32 s, int32 V_1, int32 j, int32 i, ValueClass _MyStruct V_4, ValueClass _MyStruct m
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(6);
        mainLocalsEnc.AddVariable().Type().Int32(); // s
        mainLocalsEnc.AddVariable().Type().Int32(); // V_1
        mainLocalsEnc.AddVariable().Type().Int32(); // j
        mainLocalsEnc.AddVariable().Type().Int32(); // i
        mainLocalsEnc.AddVariable().Type().Type(myStructTypeDef, isValueType: true); // V_4
        mainLocalsEnc.AddVariable().Type().Type(myStructTypeDef, isValueType: true); // m
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("struct.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "struct.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "struct.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sum_struct ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0001
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0002
            enc.LoadConstantI4(4);                 // IL_0003
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);              // IL_0005
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0006
            enc.OpCode(ILOpCode.Add);              // IL_0007
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0008
            enc.LoadConstantI4(8);                 // IL_0009
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);              // IL_000B
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_000C
            enc.OpCode(ILOpCode.Add);              // IL_000D
            enc.OpCode(ILOpCode.Stloc_0);         // IL_000E
            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_000F
            enc.OpCode(ILOpCode.Ret);              // IL_0010

            bodyEncoder.AddMethodBody(sumStructMethod, "?sum_struct@@$$J0YMHPEAU_MyStruct@@@Z", enc,
                maxStack: 3, localVariablesSignature: sumLocalsSigHandle, attributes: 0,
                debugName: "sum_struct");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_1);         // IL_0001: V_1 (return temp)
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0002
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0003: s = 0

            // { MyStruct m = { 10, 20, 30 };
            enc.MarkLineNumber(cvFile, 18);
            enc.LoadLocalAddress(5);               // IL_0004: ldloca.s m
            enc.LoadConstantI4(10);                // IL_0006: ldc.i4.s 10
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0008
            enc.LoadLocalAddress(5);               // IL_0009
            enc.LoadConstantI4(4);                 // IL_000B: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_000C
            enc.LoadConstantI4(20);                // IL_000D: ldc.i4.s 20
            enc.OpCode(ILOpCode.Stind_i4);         // IL_000F
            enc.LoadLocalAddress(5);               // IL_0010
            enc.LoadConstantI4(8);                 // IL_0012: ldc.i4.8
            enc.OpCode(ILOpCode.Add);              // IL_0013
            enc.LoadConstantI4(30);                // IL_0014: ldc.i4.s 30
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0016

            // int i = sum_struct(&m);
            enc.MarkLineNumber(cvFile, 19);
            enc.LoadLocalAddress(5);               // IL_0017
            enc.Call(sumStructMethod);              // IL_0019
            enc.OpCode(ILOpCode.Stloc_3);         // IL_001E: i

            // s += i;
            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_001F
            enc.OpCode(ILOpCode.Ldloc_3);         // IL_0020
            enc.OpCode(ILOpCode.Add);              // IL_0021
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0022

            // { MyStruct m = { 20, 30, 40 };
            enc.MarkLineNumber(cvFile, 24);
            enc.LoadLocalAddress(4);               // IL_0023: ldloca.s V_4
            enc.LoadConstantI4(20);                // IL_0025
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0027
            enc.LoadLocalAddress(4);               // IL_0028
            enc.LoadConstantI4(4);                 // IL_002A
            enc.OpCode(ILOpCode.Add);              // IL_002B
            enc.LoadConstantI4(30);                // IL_002C
            enc.OpCode(ILOpCode.Stind_i4);         // IL_002E
            enc.LoadLocalAddress(4);               // IL_002F
            enc.LoadConstantI4(8);                 // IL_0031
            enc.OpCode(ILOpCode.Add);              // IL_0032
            enc.LoadConstantI4(40);                // IL_0033
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0035

            // int j = sum_struct(&m);
            enc.MarkLineNumber(cvFile, 25);
            enc.LoadLocalAddress(4);               // IL_0036
            enc.Call(sumStructMethod);              // IL_0038
            enc.OpCode(ILOpCode.Stloc_2);         // IL_003D: j

            // s += j;
            enc.MarkLineNumber(cvFile, 26);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_003E
            enc.OpCode(ILOpCode.Ldloc_2);         // IL_003F
            enc.OpCode(ILOpCode.Add);              // IL_0040
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0041

            // return s;
            enc.MarkLineNumber(cvFile, 29);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0042
            enc.OpCode(ILOpCode.Stloc_1);         // IL_0043
            enc.MarkLineNumber(cvFile, 30);
            enc.OpCode(ILOpCode.Ldloc_1);         // IL_0044
            enc.OpCode(ILOpCode.Ret);              // IL_0045

            // Function-level locals (s)
            var mainLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(mainLocalsSigHandle), "s"),
            };

            // Nested block scopes
            int localTypeToken = MetadataTokens.GetToken(mainLocalsSigHandle);
            // Need a separate token for valuetype locals — MSVC uses different StandaloneSig tokens
            // For simplicity, use the same token (won't affect ILDASM output)
            var mainScopes = new[] {
                // First { } block: IL_0004 to IL_0023, length = 0x1F
                new CodeViewLocalScope {
                    CodeOffset = 0x04, CodeLength = 0x1F,
                    Slots = { 
                        new CodeViewManSlot(3, localTypeToken, "i"),
                        new CodeViewManSlot(5, localTypeToken, "m"),
                    }
                },
                // Second { } block: IL_0023 to IL_0042, length = 0x1F
                new CodeViewLocalScope {
                    CodeOffset = 0x23, CodeLength = 0x1F,
                    Slots = {
                        new CodeViewManSlot(2, localTypeToken, "j"),
                        new CodeViewManSlot(4, localTypeToken, "m"),
                    }
                },
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots, localScopes: mainScopes);
        }

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}

