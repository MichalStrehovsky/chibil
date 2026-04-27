using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class LocalArrayTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "local-array", refDir, "local-array.obj"));
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

        // ─── TypeDef #2: $ArrayType$$$BY04H ───────────────────────────────
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY04H"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3));

        md.AddTypeLayout(arrayTypeDef, 0, 20);

        // CustomAttribute: NativeCppClassAttribute on $ArrayType$$$BY04H
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── MethodDef #1: array_sum ──────────────────────────────────────
        // Signature: int32(int32*, int32)
        var arraySumSig = new BlobBuilder();
        var arraySumSigEnc = new BlobEncoder(arraySumSig).MethodSignature();
        arraySumSigEnc.Parameters(2, out var arraySumRetEnc, out var arraySumParEnc);
        arraySumRetEnc.Type().Int32();
        arraySumParEnc.AddParameter().Type().Pointer().Int32();
        arraySumParEnc.AddParameter().Type().Int32();

        var arraySumMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("array_sum"),
            md.GetOrAddBlob(arraySumSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // Parameters
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("arr"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("len"), 2);

        // Locals for array_sum: int32 (i), int32 (sum), int32 (return value)
        var arraySumLocalsSig = new BlobBuilder();
        var arraySumLocalsEnc = new BlobEncoder(arraySumLocalsSig).LocalVariableSignature(3);
        arraySumLocalsEnc.AddVariable().Type().Int32();
        arraySumLocalsEnc.AddVariable().Type().Int32();
        arraySumLocalsEnc.AddVariable().Type().Int32();
        var arraySumLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(arraySumLocalsSig));

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
            MetadataTokens.ParameterHandle(3));

        // Locals for main: int32, int32, Ptr int32, int32, valuetype $ArrayType$$$BY04H
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(5);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Pointer().Int32();
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(arrayTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("local-array.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "local-array.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "local-array.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for array_sum ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var loopInc = enc.DefineLabel();
            var loopCond = enc.DefineLabel();
            var loopEnd = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 6);
            enc.LoadConstantI4(0);
            enc.StoreLocal(1);                     // sum = 0

            enc.MarkLineNumber(cvFile, 8);
            enc.LoadConstantI4(0);
            enc.StoreLocal(0);                     // i = 0
            enc.Branch(ILOpCode.Br_s, loopCond);

            enc.MarkLabel(loopInc);
            enc.LoadLocal(0);
            enc.LoadConstantI4(1);
            enc.OpCode(ILOpCode.Add);
            enc.StoreLocal(0);                     // i = i + 1

            enc.MarkLabel(loopCond);
            enc.LoadLocal(0);
            enc.LoadArgument(1);
            enc.Branch(ILOpCode.Bge_s, loopEnd);   // if i >= len goto end

            // loop body: sum = sum + arr[i]
            enc.MarkLineNumber(cvFile, 9);
            enc.LoadLocal(1);
            enc.LoadArgument(0);
            enc.LoadLocal(0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.LoadConstantI4(4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i4);
            enc.OpCode(ILOpCode.Add);
            enc.StoreLocal(1);
            enc.Branch(ILOpCode.Br_s, loopInc);

            enc.MarkLabel(loopEnd);
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadLocal(1);
            enc.StoreLocal(2);                     // return value
            enc.MarkLineNumber(cvFile, 11);
            enc.LoadLocal(2);
            enc.OpCode(ILOpCode.Ret);

            var arraySumLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(arraySumLocalsSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(arraySumLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(arraySumMethod, "?array_sum@@$$J0YMHPAHH@Z", enc,
                maxStack: 4, localVariablesSignature: arraySumLocalsSigHandle, attributes: 0,
                debugName: "array_sum", localSlots: arraySumLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001: return value init

            // arr[0] = 10
            enc.LoadLocalAddress(4);               // IL_0002: ldloca.s V_4
            enc.LoadConstantI4(10);                // IL_0004: ldc.i4.s 10
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0006

            // arr[1] = 20
            enc.LoadLocalAddress(4);               // IL_0007
            enc.LoadConstantI4(4);                 // IL_0009: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_000A
            enc.LoadConstantI4(20);                // IL_000B: ldc.i4.s 20
            enc.OpCode(ILOpCode.Stind_i4);         // IL_000D

            // arr[2] = 30
            enc.LoadLocalAddress(4);               // IL_000E
            enc.LoadConstantI4(8);                 // IL_0010: ldc.i4.8
            enc.OpCode(ILOpCode.Add);              // IL_0011
            enc.LoadConstantI4(30);                // IL_0012: ldc.i4.s 30
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0014

            // arr[3] = 40
            enc.LoadLocalAddress(4);               // IL_0015
            enc.LoadConstantI4(12);                // IL_0017: ldc.i4.s 12
            enc.OpCode(ILOpCode.Add);              // IL_0019
            enc.LoadConstantI4(40);                // IL_001A: ldc.i4.s 40
            enc.OpCode(ILOpCode.Stind_i4);         // IL_001C

            // arr[4] = 50
            enc.LoadLocalAddress(4);               // IL_001D
            enc.LoadConstantI4(16);                // IL_001F: ldc.i4.s 16
            enc.OpCode(ILOpCode.Add);              // IL_0021
            enc.LoadConstantI4(50);                // IL_0022: ldc.i4.s 50
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0024

            // int sum = array_sum(arr, 5)
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadLocalAddress(4);               // IL_0025
            enc.LoadConstantI4(5);                 // IL_0027: ldc.i4.5
            enc.Call(arraySumMethod);               // IL_0028: call array_sum
            enc.OpCode(ILOpCode.Stloc_3);         // IL_002D

            // int* p = arr + 2
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocalAddress(4);               // IL_002E
            enc.LoadConstantI4(8);                 // IL_0030: ldc.i4.8
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);              // IL_0031 (x86) / IL_0032 (arm64)
            enc.OpCode(ILOpCode.Stloc_2);         // stloc.2 (p)

            // int val = *p
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_2);
            enc.OpCode(ILOpCode.Ldind_i4);
            enc.OpCode(ILOpCode.Stloc_1);         // stloc.1 (val)

            // return sum + val
            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ret);

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "p"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "val"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(mainLocalsSigHandle), "arr"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(mainLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
