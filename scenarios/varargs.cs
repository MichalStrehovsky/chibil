using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class VarargsTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "varargs", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "varargs.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "varargs", refDir, "varargs.obj"));
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
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: CallConvCdecl (modopt on return types under /clr) ───
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        var moduleType = md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MemberRef: sum on <Module> (VARARG call site) ────────────────
        // Call-site signature: VARARG int32(int32, SENTINEL, int32, int32, int32)
        var sumSig = new BlobBuilder();
        sumSig.WriteByte(0x05);                    // VARARG calling convention
        sumSig.WriteCompressedInteger(4);           // 4 params total
        sumSig.WriteByte((byte)SignatureTypeCode.Int32); // return type: int32
        sumSig.WriteByte((byte)SignatureTypeCode.Int32); // param 1: int32 (count - fixed)
        sumSig.WriteByte(0x41);                    // SENTINEL
        sumSig.WriteByte((byte)SignatureTypeCode.Int32); // param 2: int32
        sumSig.WriteByte((byte)SignatureTypeCode.Int32); // param 3: int32
        sumSig.WriteByte((byte)SignatureTypeCode.Int32); // param 4: int32

        var sumRef = md.AddMemberReference(moduleType,
            md.GetOrAddString("sum"), md.GetOrAddBlob(sumSig));

        // ─── MethodDef #1: main() -> cmod_opt(CallConvCdecl) int32 ──────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRet, out var mainPar);
        ClrIjw.EncodeCdeclI4Return(mainRet, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(1));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("varargs.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

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

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("varargs.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "varargs.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldc_i4_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001
            enc.LoadConstantI4(3);                    // IL_0002: ldc.i4.3
            enc.LoadConstantI4(10);                   // IL_0003: ldc.i4.s 10
            enc.LoadConstantI4(20);                   // IL_0005: ldc.i4.s 20
            enc.LoadConstantI4(30);                   // IL_0007: ldc.i4.s 30
            enc.Call(sumRef);                          // IL_0009: call sum
            enc.OpCode(ILOpCode.Stloc_0);             // IL_000E
            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_000F
            enc.OpCode(ILOpCode.Ret);                 // IL_0010

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 4, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        // ─── External symbol for sum ──────────────────────────────────────
        symtab.AddExternalClrToken("?sum@@$$J0YAHHZZ", sumRef);

        // ─── IJW machinery for main ──────────────────────────────────────
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
