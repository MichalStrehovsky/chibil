using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class BitwiseTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "bitwise", refDir, "bitwise.obj"));
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

        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            md.GetOrAddBlob(mscorlibHash));

        md.AddTypeDefinition(
            TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: bitwise(int, int) -> int ───────────────────────
        var bitwiseSig = new BlobBuilder();
        new BlobEncoder(bitwiseSig).MethodSignature()
            .Parameters(2, out var bRetEnc, out var bParEnc);
        bRetEnc.Type().Int32();
        bParEnc.AddParameter().Type().Int32();
        bParEnc.AddParameter().Type().Int32();

        var bitwiseMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("bitwise"),
            md.GetOrAddBlob(bitwiseSig), 0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        var bitwiseLocalsSig = new BlobBuilder();
        var bitwiseLocalsEnc = new BlobEncoder(bitwiseLocalsSig).LocalVariableSignature(7);
        for (int i = 0; i < 7; i++) bitwiseLocalsEnc.AddVariable().Type().Int32();
        var bitwiseLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(bitwiseLocalsSig));

        // ─── MethodDef #2: main() -> int ──────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(3));

        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        md.AddModule(0, md.GetOrAddString("bitwise.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("bitwise.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "bitwise.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for bitwise ──────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.And);             // IL_0002
            enc.StoreLocal(6);                    // IL_0003: stloc.s V_6

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0006
            enc.OpCode(ILOpCode.Or);              // IL_0007
            enc.StoreLocal(5);                    // IL_0008: stloc.s V_5

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_000A
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_000B
            enc.OpCode(ILOpCode.Xor);             // IL_000C
            enc.StoreLocal(4);                    // IL_000D: stloc.s V_4

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_000F
            enc.OpCode(ILOpCode.Not);             // IL_0010
            enc.StoreLocal(3);                    // IL_0011: stloc.3

            enc.MarkLineNumber(cvFile, 10);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0012
            enc.OpCode(ILOpCode.Ldc_i4_2);        // IL_0013
            enc.OpCode(ILOpCode.Shl);             // IL_0014
            enc.StoreLocal(2);                    // IL_0015: stloc.2

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0016
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0017
            enc.OpCode(ILOpCode.Shr);             // IL_0018
            enc.StoreLocal(1);                    // IL_0019: stloc.1

            enc.MarkLineNumber(cvFile, 12);
            enc.LoadLocal(6);                     // IL_001A
            enc.LoadLocal(5);                     // IL_001C
            enc.OpCode(ILOpCode.Add);             // IL_001E
            enc.LoadLocal(4);                     // IL_001F
            enc.OpCode(ILOpCode.Add);             // IL_0021
            enc.OpCode(ILOpCode.Ldloc_3);         // IL_0022
            enc.OpCode(ILOpCode.Add);             // IL_0023
            enc.OpCode(ILOpCode.Ldloc_2);         // IL_0024
            enc.OpCode(ILOpCode.Add);             // IL_0025
            enc.OpCode(ILOpCode.Ldloc_1);         // IL_0026
            enc.OpCode(ILOpCode.Add);             // IL_0027
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0028

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0029
            enc.OpCode(ILOpCode.Ret);             // IL_002A

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "shr"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "shl"),
                new CodeViewManSlot(5, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "bor"),
                new CodeViewManSlot(6, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "band"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "bnot"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(bitwiseLocalsSigHandle), "bxor"),
            };

            bodyEncoder.AddMethodBody(bitwiseMethod, "?bitwise@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: bitwiseLocalsSigHandle, attributes: 0,
                debugName: "bitwise", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 17);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001
            enc.LoadConstantI4(85);                // IL_0002: ldc.i4.s 85
            enc.LoadConstantI4(51);                // IL_0004: ldc.i4.s 51
            enc.Call(bitwiseMethod);               // IL_0006: call bitwise

            enc.MarkLineNumber(cvFile, 18);
            enc.OpCode(ILOpCode.Stloc_0);         // IL_000B
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_000C
            enc.OpCode(ILOpCode.Ret);             // IL_000D

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main");
        }

        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);
        return output.ToArray();
    }
}
