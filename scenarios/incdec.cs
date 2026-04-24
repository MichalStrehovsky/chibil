using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class IncDecTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "incdec", refDir, "incdec.obj"));
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

        // ─── TypeDef: <Module> ────────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: post_inc(int32) -> int32 ───────────────────────
        var postIncSig = new BlobBuilder();
        new BlobEncoder(postIncSig).MethodSignature()
            .Parameters(1, out var postIncRetEnc, out var postIncParEnc);
        postIncRetEnc.Type().Int32();
        postIncParEnc.AddParameter().Type().Int32();

        var postIncMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("post_inc"),
            md.GetOrAddBlob(postIncSig), 0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);  // param 1

        // Locals for post_inc: 1 x int32
        var postIncLocalsSig = new BlobBuilder();
        new BlobEncoder(postIncLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var postIncLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(postIncLocalsSig));

        // ─── MethodDef #2: pre_inc(int32) -> int32 ────────────────────────
        var preIncSig = new BlobBuilder();
        new BlobEncoder(preIncSig).MethodSignature()
            .Parameters(1, out var preIncRetEnc, out var preIncParEnc);
        preIncRetEnc.Type().Int32();
        preIncParEnc.AddParameter().Type().Int32();

        var preIncMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("pre_inc"),
            md.GetOrAddBlob(preIncSig), 0,
            MetadataTokens.ParameterHandle(2));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);  // param 2

        // Locals for pre_inc: 1 x int32
        var preIncLocalsSig = new BlobBuilder();
        new BlobEncoder(preIncLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var preIncLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(preIncLocalsSig));

        // ─── MethodDef #3: compound_add(int32, int32) -> int32 ────────────
        var compAddSig = new BlobBuilder();
        new BlobEncoder(compAddSig).MethodSignature()
            .Parameters(2, out var compAddRetEnc, out var compAddParEnc);
        compAddRetEnc.Type().Int32();
        compAddParEnc.AddParameter().Type().Int32();
        compAddParEnc.AddParameter().Type().Int32();

        var compAddMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("compound_add"),
            md.GetOrAddBlob(compAddSig), 0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);  // param 3
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);  // param 4

        // Locals for compound_add: 1 x int32
        var compAddLocalsSig = new BlobBuilder();
        new BlobEncoder(compAddLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var compAddLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(compAddLocalsSig));

        // ─── MethodDef #4: compound_sub(int32, int32) -> int32 ────────────
        var compSubSig = new BlobBuilder();
        new BlobEncoder(compSubSig).MethodSignature()
            .Parameters(2, out var compSubRetEnc, out var compSubParEnc);
        compSubRetEnc.Type().Int32();
        compSubParEnc.AddParameter().Type().Int32();
        compSubParEnc.AddParameter().Type().Int32();

        var compSubMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("compound_sub"),
            md.GetOrAddBlob(compSubSig), 0,
            MetadataTokens.ParameterHandle(5));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);  // param 5
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);  // param 6

        // Locals for compound_sub: 1 x int32
        var compSubLocalsSig = new BlobBuilder();
        new BlobEncoder(compSubLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var compSubLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(compSubLocalsSig));

        // ─── MethodDef #5: compound_mul(int32, int32) -> int32 ────────────
        var compMulSig = new BlobBuilder();
        new BlobEncoder(compMulSig).MethodSignature()
            .Parameters(2, out var compMulRetEnc, out var compMulParEnc);
        compMulRetEnc.Type().Int32();
        compMulParEnc.AddParameter().Type().Int32();
        compMulParEnc.AddParameter().Type().Int32();

        var compMulMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("compound_mul"),
            md.GetOrAddBlob(compMulSig), 0,
            MetadataTokens.ParameterHandle(7));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);  // param 7
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);  // param 8

        // Locals for compound_mul: 1 x int32
        var compMulLocalsSig = new BlobBuilder();
        new BlobEncoder(compMulLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var compMulLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(compMulLocalsSig));

        // ─── MethodDef #6: compound_shl(int32, int32) -> int32 ────────────
        var compShlSig = new BlobBuilder();
        new BlobEncoder(compShlSig).MethodSignature()
            .Parameters(2, out var compShlRetEnc, out var compShlParEnc);
        compShlRetEnc.Type().Int32();
        compShlParEnc.AddParameter().Type().Int32();
        compShlParEnc.AddParameter().Type().Int32();

        var compShlMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("compound_shl"),
            md.GetOrAddBlob(compShlSig), 0,
            MetadataTokens.ParameterHandle(9));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);  // param 9
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);  // param 10

        // Locals for compound_shl: 1 x int32
        var compShlLocalsSig = new BlobBuilder();
        new BlobEncoder(compShlLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var compShlLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(compShlLocalsSig));

        // ─── MethodDef #7: ptr_post_inc(Ptr Ptr int32) -> void ────────────
        var ptrPostIncSig = new BlobBuilder();
        new BlobEncoder(ptrPostIncSig).MethodSignature()
            .Parameters(1, out var ptrPostIncRetEnc, out var ptrPostIncParEnc);
        ptrPostIncRetEnc.Void();
        ptrPostIncParEnc.AddParameter().Type().Pointer().Pointer().Int32();

        var ptrPostIncMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_post_inc"),
            md.GetOrAddBlob(ptrPostIncSig), 0,
            MetadataTokens.ParameterHandle(11));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("pp"), 1);  // param 11

        // Locals for ptr_post_inc: 1 x Ptr int32
        var ptrPostIncLocalsSig = new BlobBuilder();
        new BlobEncoder(ptrPostIncLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Pointer().Int32();
        var ptrPostIncLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(ptrPostIncLocalsSig));

        // ─── MethodDef #8: main() -> int32 ────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mainRetEnc, out var mainParEnc);
        mainRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(12));

        // Locals for main: int32, Ptr int32, int32
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Pointer().Int32();
        mainLocalsEnc.AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("incdec.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "incdec.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "incdec.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for post_inc ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0002
            enc.OpCode(ILOpCode.Add);             // IL_0003
            enc.StoreArgument(0);                 // IL_0004: starg.s V_0
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(postIncMethod, "?post_inc@@$$J0YMHH@Z", enc,
                maxStack: 2, localVariablesSignature: postIncLocalsSigHandle, attributes: 0,
                debugName: "post_inc");
        }

        // ─── Emit IL for pre_inc ──────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldc_i4_1);        // IL_0001
            enc.OpCode(ILOpCode.Add);             // IL_0002
            enc.StoreArgument(0);                 // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(preIncMethod, "?pre_inc@@$$J0YMHH@Z", enc,
                maxStack: 2, localVariablesSignature: preIncLocalsSigHandle, attributes: 0,
                debugName: "pre_inc");
        }

        // ─── Emit IL for compound_add ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Add);             // IL_0002
            enc.StoreArgument(0);                 // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(compAddMethod, "?compound_add@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: compAddLocalsSigHandle, attributes: 0,
                debugName: "compound_add");
        }

        // ─── Emit IL for compound_sub ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Sub);             // IL_0002
            enc.StoreArgument(0);                 // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(compSubMethod, "?compound_sub@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: compSubLocalsSigHandle, attributes: 0,
                debugName: "compound_sub");
        }

        // ─── Emit IL for compound_mul ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Mul);             // IL_0002
            enc.StoreArgument(0);                 // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(compMulMethod, "?compound_mul@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: compMulLocalsSigHandle, attributes: 0,
                debugName: "compound_mul");
        }

        // ─── Emit IL for compound_shl ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.OpCode(ILOpCode.Shl);             // IL_0002
            enc.StoreArgument(0);                 // IL_0003: starg.s V_0
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0005
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0006
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0007
            enc.OpCode(ILOpCode.Ret);             // IL_0008

            bodyEncoder.AddMethodBody(compShlMethod, "?compound_shl@@$$J0YMHHH@Z", enc,
                maxStack: 2, localVariablesSignature: compShlLocalsSigHandle, attributes: 0,
                debugName: "compound_shl");
        }

        // ─── Emit IL for ptr_post_inc ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Ldind_i4);    // x86: pointer is 4 bytes
            else
                enc.OpCode(ILOpCode.Ldind_i8);    // arm64: pointer is 8 bytes
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0002
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_0003
            enc.OpCode(ILOpCode.Ldc_i4_4);        // IL_0004
            if (machine != Machine.I386)
                enc.OpCode(ILOpCode.Conv_i8);     // arm64 only
            enc.OpCode(ILOpCode.Add);             // IL_0005 (x86) / IL_0006 (arm64)
            enc.OpCode(ILOpCode.Stloc_0);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.OpCode(ILOpCode.Ldloc_0);
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Stind_i4);    // x86
            else
                enc.OpCode(ILOpCode.Stind_i8);    // arm64
            enc.OpCode(ILOpCode.Ret);

            var ptrPostIncSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(ptrPostIncLocalsSigHandle), "p"),
            };

            bodyEncoder.AddMethodBody(ptrPostIncMethod, "?ptr_post_inc@@$$J0YMXPAPAH@Z", enc,
                maxStack: 2, localVariablesSignature: ptrPostIncLocalsSigHandle, attributes: 0,
                debugName: "ptr_post_inc", localSlots: ptrPostIncSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);         // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_5);        // IL_0002
            enc.OpCode(ILOpCode.Stloc_2);         // IL_0003
            enc.LoadLocalAddress(2);              // IL_0004: ldloca.s V_2
            enc.OpCode(ILOpCode.Stloc_1);         // IL_0006
            enc.LoadConstantI4(10);                // IL_0007: ldc.i4.s 10
            enc.Call(postIncMethod);               // IL_0009: call post_inc
            enc.LoadConstantI4(10);                // IL_000E: ldc.i4.s 10
            enc.Call(preIncMethod);                 // IL_0010: call pre_inc
            enc.OpCode(ILOpCode.Add);             // IL_0015
            enc.OpCode(ILOpCode.Ldc_i4_3);        // IL_0016
            enc.OpCode(ILOpCode.Ldc_i4_4);        // IL_0017
            enc.Call(compAddMethod);               // IL_0018: call compound_add
            enc.OpCode(ILOpCode.Add);             // IL_001D
            enc.OpCode(ILOpCode.Stloc_0);         // IL_001E
            enc.OpCode(ILOpCode.Ldloc_0);         // IL_001F
            enc.OpCode(ILOpCode.Ret);             // IL_0020

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "x"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "p"),
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
