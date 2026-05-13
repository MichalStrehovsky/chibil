using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ReturnStructTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "return-struct", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "return-struct.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "return-struct", refDir, "return-struct.obj"));
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
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("CallConvCdecl"));
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

        // ─── TypeDef #2: _Point (sequential, sealed, size=8) ──────────────
        var pointTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Point"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3)); // no methods on this type

        md.AddTypeLayout(pointTypeDef, 0, 8);

        // CustomAttribute: NativeCppClassAttribute on _Point
        md.AddCustomAttribute(pointTypeDef, nativeCppCtorRef,
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

        // ─── MethodDef #1: make_point ─────────────────────────────────────
        // Signature: valuetype _Point (int32, int32)
        var makePointSig = new BlobBuilder();
        var makePointSigEnc = new BlobEncoder(makePointSig).MethodSignature();
        makePointSigEnc.Parameters(2, out var makePointRetEnc, out var makePointParEnc);
        ClrIjw.WriteCdeclModOpt(makePointRetEnc, callConvCdeclRef).Type(pointTypeDef, isValueType: true);
        makePointParEnc.AddParameter().Type().Int32();
        makePointParEnc.AddParameter().Type().Int32();

        var makePointMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("make_point"),
            md.GetOrAddBlob(makePointSig),
            0,
            MetadataTokens.ParameterHandle(1));

        // Parameters: x, y
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("y"), 2);

        // Locals for make_point: valuetype _Point, valuetype _Point
        var makePointLocalsSig = new BlobBuilder();
        var makePointLocalsEnc = new BlobEncoder(makePointLocalsSig).LocalVariableSignature(2);
        makePointLocalsEnc.AddVariable().Type().Type(pointTypeDef, isValueType: true);
        makePointLocalsEnc.AddVariable().Type().Type(pointTypeDef, isValueType: true);
        var makePointLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(makePointLocalsSig));

        // ─── MethodDef #2: main ───────────────────────────────────────────
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
            MetadataTokens.ParameterHandle(3)); // after x, y

        // Locals for main: int32, valuetype _Point
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(pointTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("return-struct.obj"),
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

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "return-struct.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "return-struct.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder= new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for make_point ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 9);
            enc.LoadLocalAddress(1);               // IL_0000: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0002: ldarg.0
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0003: stind.i4
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadLocalAddress(1);               // IL_0004: ldloca.s V_1
            enc.LoadConstantI4(4);                 // IL_0006: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_0007: add
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0008: ldarg.1
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0009: stind.i4
            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldloc_1);          // IL_000A: ldloc.1
            enc.OpCode(ILOpCode.Stloc_0);          // IL_000B: stloc.0
            enc.MarkLineNumber(cvFile, 12);
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_000C: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_000D: ret

            var makePointLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(makePointLocalsSigHandle), "p"),
            };

            bodyEncoder.AddMethodBody(makePointMethod, "?make_point@@$$J0YA?AU_Point@@HH@Z", enc,
                maxStack: 2, localVariablesSignature: makePointLocalsSigHandle, attributes: 0,
                debugName: "make_point", localSlots: makePointLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000: ldc.i4.0
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001: stloc.0
            enc.LoadConstantI4(10);                // IL_0002: ldc.i4.s 10
            enc.LoadConstantI4(20);                // IL_0004: ldc.i4.s 20
            enc.Call(makePointMethod);             // IL_0006: call make_point
            enc.OpCode(ILOpCode.Stloc_1);         // IL_000B: stloc.1
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocalAddress(1);               // IL_000C: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_000E: ldind.i4
            enc.LoadLocalAddress(1);               // IL_000F: ldloca.s V_1
            enc.LoadConstantI4(4);                 // IL_0011: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_0012: add
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0013: ldind.i4
            enc.OpCode(ILOpCode.Add);              // IL_0014: add
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0015: stloc.0
            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0016: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_0017: ret

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "p"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for make_point and main ────────────────────────
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(makePointMethod), "make_point", "?make_point@@$$J0YA?AU_Point@@HH@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(mainMethod), "main", "?main@@$$J0YAHXZ");

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
