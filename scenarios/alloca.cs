using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using Xunit;

public class AllocaTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "alloca", refDir, "alloca.obj"));
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

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: sum_dynamic(int32) -> int32 ────────────────────
        var sumDynSig = new BlobBuilder();
        new BlobEncoder(sumDynSig).MethodSignature()
            .Parameters(1, out var sumDynRetEnc, out var sumDynParEnc);
        sumDynRetEnc.Type().Int32();
        sumDynParEnc.AddParameter().Type().Int32();

        var sumDynMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sum_dynamic"),
            md.GetOrAddBlob(sumDynSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 1);

        // Locals for sum_dynamic: int32(i), int32(sum), Ptr int32(arr), int32(retval)
        var sumDynLocalsSig = new BlobBuilder();
        var sumDynLocalsEnc = new BlobEncoder(sumDynLocalsSig).LocalVariableSignature(4);
        sumDynLocalsEnc.AddVariable().Type().Int32();
        sumDynLocalsEnc.AddVariable().Type().Int32();
        sumDynLocalsEnc.AddVariable().Type().Pointer().Int32();
        sumDynLocalsEnc.AddVariable().Type().Int32();
        var sumDynLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(sumDynLocalsSig));

        // ─── MethodDef #2: main() -> int32 ────────────────────────────────
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
            MetadataTokens.ParameterHandle(2));

        // Locals for main: int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("alloca.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "alloca.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sum_dynamic ──────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            var loop1Inc = enc.DefineLabel();
            var loop1Cond = enc.DefineLabel();
            var loop1End = enc.DefineLabel();
            var loop2Inc = enc.DefineLabel();
            var loop2Cond = enc.DefineLabel();
            var loop2End = enc.DefineLabel();

            // arr = (int*)_alloca(n * 4)
            enc.OpCode(ILOpCode.Ldarg_0);             // IL_0000
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0001
            enc.OpCode(ILOpCode.Mul);                  // IL_0002
            if (machine != Machine.I386)
                enc.OpCode(ILOpCode.Conv_u8);          // arm64: IL_0003
            enc.OpCode(ILOpCode.Localloc);             // IL_0003/0004
            enc.OpCode(ILOpCode.Stloc_2);             // IL_0005/0006

            // i = 0
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0006/0007
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0007/0008
            enc.Branch(ILOpCode.Br_s, loop1Cond);

            // loop1 increment
            enc.MarkLabel(loop1Inc);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            // loop1 condition
            enc.MarkLabel(loop1Cond);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.Branch(ILOpCode.Bge_s, loop1End);

            // loop1 body: arr[i] = i + 1
            enc.OpCode(ILOpCode.Ldloc_2);             // arr
            enc.OpCode(ILOpCode.Ldloc_0);             // i
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldloc_0);             // i
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stind_i4);
            enc.Branch(ILOpCode.Br_s, loop1Inc);

            // loop1 end — sum = 0, i = 0
            enc.MarkLabel(loop1End);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_1);             // sum = 0
            enc.OpCode(ILOpCode.Ldc_i4_0);
            enc.OpCode(ILOpCode.Stloc_0);             // i = 0
            enc.Branch(ILOpCode.Br_s, loop2Cond);

            // loop2 increment
            enc.MarkLabel(loop2Inc);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldc_i4_1);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_0);

            // loop2 condition
            enc.MarkLabel(loop2Cond);
            enc.OpCode(ILOpCode.Ldloc_0);
            enc.OpCode(ILOpCode.Ldarg_0);
            enc.Branch(ILOpCode.Bge_s, loop2End);

            // loop2 body: sum = sum + arr[i]
            enc.OpCode(ILOpCode.Ldloc_1);             // sum
            enc.OpCode(ILOpCode.Ldloc_2);             // arr
            enc.OpCode(ILOpCode.Ldloc_0);             // i
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Ldind_i4);
            enc.OpCode(ILOpCode.Add);
            enc.OpCode(ILOpCode.Stloc_1);
            enc.Branch(ILOpCode.Br_s, loop2Inc);

            // loop2 end — return sum
            enc.MarkLabel(loop2End);
            enc.OpCode(ILOpCode.Ldloc_1);
            enc.OpCode(ILOpCode.Stloc_3);             // retval
            enc.OpCode(ILOpCode.Ldloc_3);
            enc.OpCode(ILOpCode.Ret);

            var sumDynLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(sumDynLocalsSigHandle), "i"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(sumDynLocalsSigHandle), "arr"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(sumDynLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(sumDynMethod, "?sum_dynamic@@$$J0YMHH@Z", enc,
                maxStack: 4, localVariablesSignature: sumDynLocalsSigHandle, attributes: 0,
                debugName: "sum_dynamic", localSlots: sumDynLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_5);            // IL_0002
            enc.Call(sumDynMethod);                    // IL_0003: call sum_dynamic
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0008
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0009
            enc.OpCode(ILOpCode.Ret);                  // IL_000A

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
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
