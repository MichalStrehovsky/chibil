using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ReturnLargeStructTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "return-large-struct", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "return-large-struct.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "return-large-struct", refDir, "return-large-struct.obj"));
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

        // ─── TypeDef #2: _Large (sequential, sealed, size=20) ─────────────
        var largeTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Large"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3));

        md.AddTypeLayout(largeTypeDef, 0, 20);

        // CustomAttribute: NativeCppClassAttribute
        md.AddCustomAttribute(largeTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // Field: <alignment member> — ARM64 only (int32)
        if (machine != Machine.I386)
        {
            var alignFieldSig = new BlobBuilder();
            new BlobEncoder(alignFieldSig).Field().Type().Int32();
            md.AddFieldDefinition(
                FieldAttributes.Private,
                md.GetOrAddString("<alignment member>"),
                md.GetOrAddBlob(alignFieldSig));
        }

        // ─── MethodDef #1: make_large(int32) -> ValueType _Large ──────────
        var makeLargeSig = new BlobBuilder();
        var makeLargeSigEnc = new BlobEncoder(makeLargeSig).MethodSignature();
        makeLargeSigEnc.Parameters(1, out var makeLargeRetEnc, out var makeLargeParEnc);
        ClrIjw.WriteCdeclModOpt(makeLargeRetEnc, callConvCdeclRef).Type(largeTypeDef, isValueType: true);
        makeLargeParEnc.AddParameter().Type().Int32();

        var makeLargeMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("make_large"),
            md.GetOrAddBlob(makeLargeSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("v"), 1);

        // Locals for make_large: ValueType _Large, ValueType _Large
        var makeLargeLocalsSig = new BlobBuilder();
        var makeLargeLocalsEnc = new BlobEncoder(makeLargeLocalsSig).LocalVariableSignature(2);
        makeLargeLocalsEnc.AddVariable().Type().Type(largeTypeDef, isValueType: true);
        makeLargeLocalsEnc.AddVariable().Type().Type(largeTypeDef, isValueType: true);
        var makeLargeLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(makeLargeLocalsSig));

        // ─── MethodDef #2: main() -> int32 ────────────────────────────────
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
            MetadataTokens.ParameterHandle(2));

        // Locals for main: int32, ValueType _Large
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(largeTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("return-large-struct.obj"),
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
        string objPath = "return-large-struct.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "return-large-struct.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for make_large ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            // s.a = v (offset 0)
            enc.MarkLineNumber(cvFile, 11);
            enc.LoadLocalAddress(1);                   // IL_0000: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0002
            enc.OpCode(ILOpCode.Stind_i4);             // IL_0003

            // s.b = v + 1 (offset 4)
            enc.MarkLineNumber(cvFile, 12);
            enc.LoadLocalAddress(1);                   // IL_0004
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0006
            enc.OpCode(ILOpCode.Add);                  // IL_0007
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0008
            enc.OpCode(ILOpCode.Ldc_i4_1);            // IL_0009
            enc.OpCode(ILOpCode.Add);                  // IL_000A
            enc.OpCode(ILOpCode.Stind_i4);             // IL_000B

            // s.c = v + 2 (offset 8)
            enc.MarkLineNumber(cvFile, 13);
            enc.LoadLocalAddress(1);                   // IL_000C
            enc.OpCode(ILOpCode.Ldc_i4_8);            // IL_000E
            enc.OpCode(ILOpCode.Add);                  // IL_000F
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0010
            enc.OpCode(ILOpCode.Ldc_i4_2);            // IL_0011
            enc.OpCode(ILOpCode.Add);                  // IL_0012
            enc.OpCode(ILOpCode.Stind_i4);             // IL_0013

            // s.d = v + 3 (offset 12)
            enc.MarkLineNumber(cvFile, 14);
            enc.LoadLocalAddress(1);                   // IL_0014
            enc.LoadConstantI4(12);                    // IL_0016: ldc.i4.s 12
            enc.OpCode(ILOpCode.Add);                  // IL_0018
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0019
            enc.OpCode(ILOpCode.Ldc_i4_3);            // IL_001A
            enc.OpCode(ILOpCode.Add);                  // IL_001B
            enc.OpCode(ILOpCode.Stind_i4);             // IL_001C

            // s.e = v + 4 (offset 16)
            enc.MarkLineNumber(cvFile, 15);
            enc.LoadLocalAddress(1);                   // IL_001D
            enc.LoadConstantI4(16);                    // IL_001F: ldc.i4.s 16
            enc.OpCode(ILOpCode.Add);                  // IL_0021
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0022
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0023
            enc.OpCode(ILOpCode.Add);                  // IL_0024
            enc.OpCode(ILOpCode.Stind_i4);             // IL_0025

            // return s
            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_1);             // IL_0026
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0027
            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0028
            enc.OpCode(ILOpCode.Ret);                  // IL_0029

            var makeLargeLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(makeLargeLocalsSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(makeLargeMethod, "?make_large@@$$J0YA?AU_Large@@H@Z", enc,
                maxStack: 2, localVariablesSignature: makeLargeLocalsSigHandle, attributes: 0,
                debugName: "make_large", localSlots: makeLargeLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 21);
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001

            // Large s = make_large(10)
            enc.LoadConstantI4(10);                    // IL_0002: ldc.i4.s 10
            enc.Call(makeLargeMethod);                 // IL_0004: call make_large
            enc.OpCode(ILOpCode.Stloc_1);             // IL_0009

            // return s.a + s.b + s.c + s.d + s.e
            enc.MarkLineNumber(cvFile, 22);
            enc.LoadLocalAddress(1);                   // IL_000A: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);             // IL_000C: s.a
            enc.LoadLocalAddress(1);                   // IL_000D
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_000F
            enc.OpCode(ILOpCode.Add);                  // IL_0010
            enc.OpCode(ILOpCode.Ldind_i4);             // IL_0011: s.b
            enc.OpCode(ILOpCode.Add);                  // IL_0012

            enc.LoadLocalAddress(1);                   // IL_0013
            enc.OpCode(ILOpCode.Ldc_i4_8);            // IL_0015
            enc.OpCode(ILOpCode.Add);                  // IL_0016
            enc.OpCode(ILOpCode.Ldind_i4);             // IL_0017: s.c
            enc.OpCode(ILOpCode.Add);                  // IL_0018

            enc.LoadLocalAddress(1);                   // IL_0019
            enc.LoadConstantI4(12);                    // IL_001B: ldc.i4.s 12
            enc.OpCode(ILOpCode.Add);                  // IL_001D
            enc.OpCode(ILOpCode.Ldind_i4);             // IL_001E: s.d
            enc.OpCode(ILOpCode.Add);                  // IL_001F

            enc.LoadLocalAddress(1);                   // IL_0020
            enc.LoadConstantI4(16);                    // IL_0022: ldc.i4.s 16
            enc.OpCode(ILOpCode.Add);                  // IL_0024
            enc.OpCode(ILOpCode.Ldind_i4);             // IL_0025: s.e
            enc.OpCode(ILOpCode.Add);                  // IL_0026

            enc.OpCode(ILOpCode.Stloc_0);             // IL_0027
            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0028
            enc.OpCode(ILOpCode.Ret);                  // IL_0029

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for make_large and main ────────────────────────
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(makeLargeMethod), "make_large", "?make_large@@$$J0YA?AU_Large@@H@Z");
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
