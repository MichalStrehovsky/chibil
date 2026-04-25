using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using Xunit;

public class CompoundLiteralTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "compound-literal", refDir, "compound-literal.obj"));
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

        // ─── TypeDef #2: _Point (sequential, sealed, size=8) ──────────────
        var pointTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("_Point"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3));

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

        // ─── MethodDef #1: sum_point(Ptr ValueType _Point) -> int32 ──────
        var sumPointSig = new BlobBuilder();
        new BlobEncoder(sumPointSig).MethodSignature()
            .Parameters(1, out var sumPointRetEnc, out var sumPointParEnc);
        sumPointRetEnc.Type().Int32();
        sumPointParEnc.AddParameter().Type().Pointer().Type(pointTypeDef, isValueType: true);

        var sumPointMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sum_point"),
            md.GetOrAddBlob(sumPointSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);

        // Locals for sum_point: int32
        var sumPointLocalsSig = new BlobBuilder();
        new BlobEncoder(sumPointLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var sumPointLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(sumPointLocalsSig));

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

        // Locals for main: int32, ValueType _Point, ValueType _Point
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(pointTypeDef, isValueType: true);
        mainLocalsEnc.AddVariable().Type().Type(pointTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("compound-literal.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "compound-literal.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sum_point ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0000
            enc.OpCode(ILOpCode.Ldind_i4);              // IL_0001: p->x
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_4);             // IL_0003
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);                   // IL_0004/0005
            enc.OpCode(ILOpCode.Ldind_i4);              // IL_0005/0006: p->y
            enc.OpCode(ILOpCode.Add);                   // IL_0006/0007
            enc.OpCode(ILOpCode.Stloc_0);              // IL_0007/0008
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0008/0009
            enc.OpCode(ILOpCode.Ret);                   // IL_0009/000A

            bodyEncoder.AddMethodBody(sumPointMethod, "?sum_point@@$$J0YMHPAU_Point@@@Z", enc,
                maxStack: 2, localVariablesSignature: sumPointLocalsSigHandle, attributes: 0,
                debugName: "sum_point");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001

            // Compound literal: p.x = 10
            enc.LoadLocalAddress(1);                   // IL_0002: ldloca.s V_1
            enc.LoadConstantI4(10);                    // IL_0004: ldc.i4.s 10
            enc.OpCode(ILOpCode.Stind_i4);             // IL_0006

            // p.y = 20
            enc.LoadLocalAddress(1);                   // IL_0007: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);            // IL_0009
            enc.OpCode(ILOpCode.Add);                  // IL_000A
            enc.LoadConstantI4(20);                    // IL_000B: ldc.i4.s 20
            enc.OpCode(ILOpCode.Stind_i4);             // IL_000D

            // Copy compound literal to p (V_2)
            enc.OpCode(ILOpCode.Ldloc_1);             // IL_000E
            enc.OpCode(ILOpCode.Stloc_2);             // IL_000F

            // return sum_point(&p)
            enc.LoadLocalAddress(2);                   // IL_0010: ldloca.s V_2
            enc.Call(sumPointMethod);                  // IL_0012: call sum_point
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0017
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0018
            enc.OpCode(ILOpCode.Ret);                  // IL_0019

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "p"),
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
