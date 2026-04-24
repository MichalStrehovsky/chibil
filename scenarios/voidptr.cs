using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class VoidPtrTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "voidptr", refDir, "voidptr.obj"));
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
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: IsSignUnspecifiedByte ────────────────────────────────
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: identity — Ptr void(Ptr void) ──────────────────
        var idSig = new BlobBuilder();
        var idSigEnc = new BlobEncoder(idSig).MethodSignature();
        idSigEnc.Parameters(1, out var idRetEnc, out var idParEnc);
        var idRetType = idRetEnc.Type();
        idRetType.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        idRetType.Builder.WriteByte((byte)SignatureTypeCode.Void);
        var idP1 = idParEnc.AddParameter().Type();
        idP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        idP1.Builder.WriteByte((byte)SignatureTypeCode.Void);

        var identityMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("identity"), md.GetOrAddBlob(idSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);

        // identity locals: (Ptr void)
        var idLocSig = new BlobBuilder();
        var idLocEnc = new BlobEncoder(idLocSig).LocalVariableSignature(1);
        var idLocV0 = idLocEnc.AddVariable().Type();
        idLocV0.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        idLocV0.Builder.WriteByte((byte)SignatureTypeCode.Void);
        var idLocSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(idLocSig));

        // ─── MethodDef #2: deref_via_cast — int32(Ptr void) ───────────────
        var dvcSig = new BlobBuilder();
        var dvcSigEnc = new BlobEncoder(dvcSig).MethodSignature();
        dvcSigEnc.Parameters(1, out var dvcRetEnc, out var dvcParEnc);
        dvcRetEnc.Type().Int32();
        var dvcP1 = dvcParEnc.AddParameter().Type();
        dvcP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        dvcP1.Builder.WriteByte((byte)SignatureTypeCode.Void);

        var derefViaCastMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("deref_via_cast"), md.GetOrAddBlob(dvcSig), 0,
            MetadataTokens.ParameterHandle(2));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);

        // deref_via_cast locals: (int32)
        var dvcLocSig = new BlobBuilder();
        new BlobEncoder(dvcLocSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var dvcLocSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(dvcLocSig));

        // ─── MethodDef #3: write_via_cast — void(Ptr void, int32) ─────────
        var wvcSig = new BlobBuilder();
        var wvcSigEnc = new BlobEncoder(wvcSig).MethodSignature();
        wvcSigEnc.Parameters(2, out var wvcRetEnc, out var wvcParEnc);
        wvcRetEnc.Void();
        var wvcP1 = wvcParEnc.AddParameter().Type();
        wvcP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        wvcP1.Builder.WriteByte((byte)SignatureTypeCode.Void);
        wvcParEnc.AddParameter().Type().Int32();

        var writeViaCastMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("write_via_cast"), md.GetOrAddBlob(wvcSig), 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("val"), 2);
        // write_via_cast has NO locals

        // ─── MethodDef #4: copy_bytes — void(Ptr void, Ptr void, int32) ───
        var cbSig = new BlobBuilder();
        var cbSigEnc = new BlobEncoder(cbSig).MethodSignature();
        cbSigEnc.Parameters(3, out var cbRetEnc, out var cbParEnc);
        cbRetEnc.Void();
        var cbP1 = cbParEnc.AddParameter().Type();
        cbP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        cbP1.Builder.WriteByte((byte)SignatureTypeCode.Void);
        var cbP2 = cbParEnc.AddParameter().Type();
        cbP2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        cbP2.Builder.WriteByte((byte)SignatureTypeCode.Void);
        cbParEnc.AddParameter().Type().Int32();

        var copyBytesMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("copy_bytes"), md.GetOrAddBlob(cbSig), 0,
            MetadataTokens.ParameterHandle(5));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("dst"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("src"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("n"), 3);

        // copy_bytes locals: (int32, Ptr modopt(IsSignUnspecifiedByte) int8, Ptr modopt(IsSignUnspecifiedByte) int8)
        var cbLocSig = new BlobBuilder();
        var cbLocEnc = new BlobEncoder(cbLocSig).LocalVariableSignature(3);
        cbLocEnc.AddVariable().Type().Int32();
        var cbLocV1 = cbLocEnc.AddVariable().Type();
        cbLocV1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        cbLocV1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        cbLocV1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        cbLocV1.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var cbLocV2 = cbLocEnc.AddVariable().Type();
        cbLocV2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        cbLocV2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        cbLocV2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        cbLocV2.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var cbLocSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cbLocSig));

        // ─── MethodDef #5: main — int32() ─────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(8));

        // main locals: (int32, int32, int32)
        var mainLocSig = new BlobBuilder();
        var mainLocEnc = new BlobEncoder(mainLocSig).LocalVariableSignature(3);
        mainLocEnc.AddVariable().Type().Int32();
        mainLocEnc.AddVariable().Type().Int32();
        mainLocEnc.AddVariable().Type().Int32();
        var mainLocSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("voidptr.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("voidptr.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "voidptr.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for identity ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0001
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0002
            enc.OpCode(ILOpCode.Ret);              // IL_0003

            bodyEncoder.AddMethodBody(identityMethod, "?identity@@$$J0YMPAXPAX@Z", enc,
                maxStack: 1, localVariablesSignature: idLocSigHandle, attributes: 0,
                debugName: "identity");
        }

        // ─── Emit IL for deref_via_cast ───────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0001
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0002
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0003
            enc.OpCode(ILOpCode.Ret);              // IL_0004

            bodyEncoder.AddMethodBody(derefViaCastMethod, "?deref_via_cast@@$$J0YMHPAX@Z", enc,
                maxStack: 1, localVariablesSignature: dvcLocSigHandle, attributes: 0,
                debugName: "deref_via_cast");
        }

        // ─── Emit IL for write_via_cast ───────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0002
            enc.OpCode(ILOpCode.Ret);              // IL_0003

            bodyEncoder.AddMethodBody(writeViaCastMethod, "?write_via_cast@@$$J0YMXPAXH@Z", enc,
                maxStack: 2, localVariablesSignature: default, attributes: 0,
                debugName: "write_via_cast");
        }

        // ─── Emit IL for copy_bytes ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_inc = enc.DefineLabel();
            var lbl_cond = enc.DefineLabel();
            var lbl_end = enc.DefineLabel();

            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0000
            enc.OpCode(ILOpCode.Stloc_2);              // IL_0001: d = dst
            enc.OpCode(ILOpCode.Ldarg_1);              // IL_0002
            enc.OpCode(ILOpCode.Stloc_1);              // IL_0003: s = src
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0004
            enc.OpCode(ILOpCode.Stloc_0);              // IL_0005: i = 0
            enc.Branch(ILOpCode.Br_s, lbl_cond);      // IL_0006: br.s lbl_cond

            enc.MarkLabel(lbl_inc);                    // IL_0008
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0008: i
            enc.OpCode(ILOpCode.Ldc_i4_1);            // IL_0009
            enc.OpCode(ILOpCode.Add);                  // IL_000A
            enc.OpCode(ILOpCode.Stloc_0);              // IL_000B: i++

            enc.MarkLabel(lbl_cond);                   // IL_000C
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_000C: i
            enc.OpCode(ILOpCode.Ldarg_2);              // IL_000D: n
            enc.Branch(ILOpCode.Bge_s, lbl_end);      // IL_000E: bge.s lbl_end

            // d[i] = s[i]
            enc.OpCode(ILOpCode.Ldloc_2);              // IL_0010: d
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0011: i
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);                  // add
            enc.OpCode(ILOpCode.Ldloc_1);              // s
            enc.OpCode(ILOpCode.Ldloc_0);              // i
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);                  // add
            enc.OpCode(ILOpCode.Ldind_i1);             // ldind.i1
            enc.OpCode(ILOpCode.Stind_i1);             // stind.i1
            enc.Branch(ILOpCode.Br_s, lbl_inc);       // br.s lbl_inc

            enc.MarkLabel(lbl_end);                    // IL_001A (x86) / IL_001C (arm64)
            enc.OpCode(ILOpCode.Ret);                  // ret

            var cbLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(cbLocSigHandle), "d"),
                new CodeViewManSlot(0, MetadataTokens.GetToken(cbLocSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(cbLocSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(copyBytesMethod, "?copy_bytes@@$$J0YMXPAX0H@Z", enc,
                maxStack: 3, localVariablesSignature: cbLocSigHandle, attributes: 0,
                debugName: "copy_bytes", localSlots: cbLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001
            enc.LoadConstantI4(42);                    // IL_0002: ldc.i4.s 42
            enc.OpCode(ILOpCode.Stloc_2);             // IL_0004: x = 42
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0005
            enc.OpCode(ILOpCode.Stloc_1);             // IL_0006: y = 0
            enc.LoadLocalAddress(1);                   // IL_0007: ldloca.s V_1 (&y)
            enc.LoadLocalAddress(2);                   // IL_0009: ldloca.s V_2 (&x)
            enc.Call(derefViaCastMethod);              // IL_000B: call deref_via_cast
            enc.Call(writeViaCastMethod);              // IL_0010: call write_via_cast
            enc.OpCode(ILOpCode.Ldloc_1);             // IL_0015
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0016
            enc.OpCode(ILOpCode.Ldloc_0);             // IL_0017
            enc.OpCode(ILOpCode.Ret);                  // IL_0018

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocSigHandle), "x"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocSigHandle), "y"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocSigHandle, attributes: 0,
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
