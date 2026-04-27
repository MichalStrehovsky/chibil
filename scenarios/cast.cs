using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class CastTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "cast", refDir, "cast.obj"));
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
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: IsSignUnspecifiedByte ───────────────────────────────
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(TypeAttributes.Class, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: cast_widen(modopt(IsSignUnspecifiedByte) int8, int16) -> int32 ──
        var cwSig = new BlobBuilder();
        new BlobEncoder(cwSig).MethodSignature()
            .Parameters(2, out var cwRetEnc, out var cwParEnc);
        cwRetEnc.Type().Int32();
        // Param 1: modopt(IsSignUnspecifiedByte) int8 (C 'char')
        var cwP1 = cwParEnc.AddParameter().Type();
        cwP1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        cwP1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        cwP1.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        // Param 2: int16 (C 'short')
        cwParEnc.AddParameter().Type().Int16();

        var castWidenMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("cast_widen"), md.GetOrAddBlob(cwSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("c"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("s"), 2);

        // cast_widen locals: int32 (V_0), int64 (V_1:ll), int32 (V_2:i)
        var cwLocalsSig = new BlobBuilder();
        var cwLocalsEnc = new BlobEncoder(cwLocalsSig).LocalVariableSignature(3);
        cwLocalsEnc.AddVariable().Type().Int32();   // V_0: result
        cwLocalsEnc.AddVariable().Type().Int64();   // V_1: ll
        cwLocalsEnc.AddVariable().Type().Int32();   // V_2: i
        var cwLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cwLocalsSig));

        // ─── MethodDef #2: cast_narrow(int32) -> int32 ────────────────────
        var cnSig = new BlobBuilder();
        new BlobEncoder(cnSig).MethodSignature()
            .Parameters(1, out var cnRetEnc, out var cnParEnc);
        cnRetEnc.Type().Int32();
        cnParEnc.AddParameter().Type().Int32();

        var castNarrowMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("cast_narrow"), md.GetOrAddBlob(cnSig), 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("i"), 1);

        // cast_narrow locals: int32 (V_0), int16 (V_1:s), modopt(IsSignUnspecifiedByte) int8 (V_2:c)
        var cnLocalsSig = new BlobBuilder();
        var cnLocalsEnc = new BlobEncoder(cnLocalsSig).LocalVariableSignature(3);
        cnLocalsEnc.AddVariable().Type().Int32();   // V_0: result
        cnLocalsEnc.AddVariable().Type().Int16();   // V_1: s
        // V_2: modopt(IsSignUnspecifiedByte) int8 (C 'char')
        var cnLocV2 = cnLocalsEnc.AddVariable().Type();
        cnLocV2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        cnLocV2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        cnLocV2.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var cnLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cnLocalsSig));

        // ─── MethodDef #3: cast_unsigned(uint32) -> int32 ──────────────────
        var cuSig = new BlobBuilder();
        new BlobEncoder(cuSig).MethodSignature()
            .Parameters(1, out var cuRetEnc, out var cuParEnc);
        cuRetEnc.Type().Int32();
        cuParEnc.AddParameter().Type().UInt32();

        var castUnsignedMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("cast_unsigned"), md.GetOrAddBlob(cuSig), 0,
            MetadataTokens.ParameterHandle(4));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("u"), 1);

        // cast_unsigned locals: int32 (V_0), uint64 (V_1:ull), int32 (V_2:s)
        var cuLocalsSig = new BlobBuilder();
        var cuLocalsEnc = new BlobEncoder(cuLocalsSig).LocalVariableSignature(3);
        cuLocalsEnc.AddVariable().Type().Int32();    // V_0
        cuLocalsEnc.AddVariable().Type().UInt64();   // V_1: ull
        cuLocalsEnc.AddVariable().Type().Int32();    // V_2: s
        var cuLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cuLocalsSig));

        // ─── MethodDef #4: cast_float(int32, float32, float64) -> int32 ───
        var cfSig = new BlobBuilder();
        new BlobEncoder(cfSig).MethodSignature()
            .Parameters(3, out var cfRetEnc, out var cfParEnc);
        cfRetEnc.Type().Int32();
        cfParEnc.AddParameter().Type().Int32();
        cfParEnc.AddParameter().Type().Single();
        cfParEnc.AddParameter().Type().Double();

        var castFloatMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("cast_float"), md.GetOrAddBlob(cfSig), 0,
            MetadataTokens.ParameterHandle(5));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("i"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("f"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("d"), 3);

        // cast_float locals: 7
        var cfLocalsSig = new BlobBuilder();
        var cfLocalsEnc = new BlobEncoder(cfLocalsSig).LocalVariableSignature(7);
        cfLocalsEnc.AddVariable().Type().Int32();     // V_0: result
        cfLocalsEnc.AddVariable().Type().Double();    // V_1: fd
        cfLocalsEnc.AddVariable().Type().Single();    // V_2: df
        cfLocalsEnc.AddVariable().Type().Int32();     // V_3: fromd
        cfLocalsEnc.AddVariable().Type().Int32();     // V_4: fromf
        cfLocalsEnc.AddVariable().Type().Double();    // V_5: di
        cfLocalsEnc.AddVariable().Type().Single();    // V_6: fi
        var cfLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cfLocalsSig));

        // ─── MethodDef #5: cast_bool(int32) -> int32 ─────────────────────
        var cbSig = new BlobBuilder();
        new BlobEncoder(cbSig).MethodSignature()
            .Parameters(1, out var cbRetEnc, out var cbParEnc);
        cbRetEnc.Type().Int32();
        cbParEnc.AddParameter().Type().Int32();

        var castBoolMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("cast_bool"), md.GetOrAddBlob(cbSig), 0,
            MetadataTokens.ParameterHandle(8));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("x"), 1);

        // cast_bool locals: int32 (V_0), bool (V_1:b)
        var cbLocalsSig = new BlobBuilder();
        var cbLocalsEnc = new BlobEncoder(cbLocalsSig).LocalVariableSignature(2);
        cbLocalsEnc.AddVariable().Type().Int32();    // V_0: result
        cbLocalsEnc.AddVariable().Type().Boolean();  // V_1: b
        var cbLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(cbLocalsSig));

        // ─── MethodDef #6: main() -> int32 ────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        mRetEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(9));

        // main locals: 1 x int32
        var mainLocalsSig = new BlobBuilder();
        new BlobEncoder(mainLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("cast.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);
        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("cast.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "cast.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for cast_widen ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 6);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_2);           // IL_0001: i = c

            enc.MarkLineNumber(cvFile, 7);
            enc.OpCode(ILOpCode.Ldarg_1);           // IL_0002
            enc.OpCode(ILOpCode.Conv_i8);           // IL_0003
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0004: ll = (long long)s

            enc.MarkLineNumber(cvFile, 8);
            enc.OpCode(ILOpCode.Ldloc_2);           // IL_0005
            enc.OpCode(ILOpCode.Conv_i8);           // IL_0006
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0007
            enc.OpCode(ILOpCode.Add);               // IL_0008
            enc.OpCode(ILOpCode.Conv_i4);           // IL_0009
            enc.OpCode(ILOpCode.Stloc_0);           // IL_000A

            enc.MarkLineNumber(cvFile, 9);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000B
            enc.OpCode(ILOpCode.Ret);               // IL_000C

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(cwLocalsSigHandle), "ll"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(cwLocalsSigHandle), "i"),
            };

            bodyEncoder.AddMethodBody(castWidenMethod, "?cast_widen@@$$J0YMHDF@Z", enc,
                maxStack: 2, localVariablesSignature: cwLocalsSigHandle, attributes: 0,
                debugName: "cast_widen", localSlots: localSlots);
        }

        // ─── Emit IL for cast_narrow ──────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_2);           // IL_0001: c = i

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0002
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0003: s = i

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldloc_2);           // IL_0004
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0005
            enc.OpCode(ILOpCode.Add);               // IL_0006
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0007

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_0008
            enc.OpCode(ILOpCode.Ret);               // IL_0009

            var localSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(cnLocalsSigHandle), "c"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(cnLocalsSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(castNarrowMethod, "?cast_narrow@@$$J0YMHH@Z", enc,
                maxStack: 2, localVariablesSignature: cnLocalsSigHandle, attributes: 0,
                debugName: "cast_narrow", localSlots: localSlots);
        }

        // ─── Emit IL for cast_unsigned ────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Stloc_2);           // IL_0001: s = u

            enc.MarkLineNumber(cvFile, 21);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0002
            enc.OpCode(ILOpCode.Conv_u8);           // IL_0003
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0004: ull = (ull)u

            enc.MarkLineNumber(cvFile, 22);
            enc.OpCode(ILOpCode.Ldloc_2);           // IL_0005
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0006
            enc.OpCode(ILOpCode.Conv_i4);           // IL_0007
            enc.OpCode(ILOpCode.Add);               // IL_0008
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0009

            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000A
            enc.OpCode(ILOpCode.Ret);               // IL_000B

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(cuLocalsSigHandle), "ull"),
                new CodeViewManSlot(2, MetadataTokens.GetToken(cuLocalsSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(castUnsignedMethod, "?cast_unsigned@@$$J0YMHI@Z", enc,
                maxStack: 2, localVariablesSignature: cuLocalsSigHandle, attributes: 0,
                debugName: "cast_unsigned", localSlots: localSlots);
        }

        // ─── Emit IL for cast_float ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 27);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.OpCode(ILOpCode.Conv_r4);           // IL_0001
            enc.StoreLocal(6);                      // IL_0002: stloc.s V_6 (fi)

            enc.MarkLineNumber(cvFile, 28);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0004
            enc.OpCode(ILOpCode.Conv_r8);           // IL_0005
            enc.StoreLocal(5);                      // IL_0006: stloc.s V_5 (di)

            enc.MarkLineNumber(cvFile, 29);
            enc.OpCode(ILOpCode.Ldarg_1);           // IL_0008
            enc.OpCode(ILOpCode.Conv_r8);           // IL_0009
            enc.OpCode(ILOpCode.Conv_i4);           // IL_000A
            enc.StoreLocal(4);                      // IL_000B: stloc.s V_4 (fromf)

            enc.MarkLineNumber(cvFile, 30);
            enc.OpCode(ILOpCode.Ldarg_2);           // IL_000D
            enc.OpCode(ILOpCode.Conv_i4);           // IL_000E
            enc.OpCode(ILOpCode.Stloc_3);           // IL_000F: stloc.3 (fromd)

            enc.MarkLineNumber(cvFile, 31);
            enc.OpCode(ILOpCode.Ldarg_2);           // IL_0010
            enc.OpCode(ILOpCode.Conv_r4);           // IL_0011
            enc.OpCode(ILOpCode.Stloc_2);           // IL_0012: stloc.2 (df)

            enc.MarkLineNumber(cvFile, 32);
            enc.OpCode(ILOpCode.Ldarg_1);           // IL_0013
            enc.OpCode(ILOpCode.Conv_r8);           // IL_0014
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0015: stloc.1 (fd)

            enc.MarkLineNumber(cvFile, 33);
            enc.LoadLocal(4);                       // IL_0016: ldloc.s V_4
            enc.OpCode(ILOpCode.Ldloc_3);           // IL_0018
            enc.OpCode(ILOpCode.Add);               // IL_0019
            enc.LoadLocal(6);                       // IL_001A: ldloc.s V_6
            enc.OpCode(ILOpCode.Conv_r8);           // IL_001C
            enc.OpCode(ILOpCode.Conv_i4);           // IL_001D
            enc.OpCode(ILOpCode.Add);               // IL_001E
            enc.LoadLocal(5);                       // IL_001F: ldloc.s V_5
            enc.OpCode(ILOpCode.Conv_i4);           // IL_0021
            enc.OpCode(ILOpCode.Add);               // IL_0022
            enc.OpCode(ILOpCode.Ldloc_2);           // IL_0023
            enc.OpCode(ILOpCode.Conv_r8);           // IL_0024
            enc.OpCode(ILOpCode.Conv_i4);           // IL_0025
            enc.OpCode(ILOpCode.Add);               // IL_0026
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0027
            enc.OpCode(ILOpCode.Conv_i4);           // IL_0028
            enc.OpCode(ILOpCode.Add);               // IL_0029
            enc.OpCode(ILOpCode.Stloc_0);           // IL_002A

            enc.MarkLineNumber(cvFile, 34);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_002B
            enc.OpCode(ILOpCode.Ret);               // IL_002C

            var localSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(cfLocalsSigHandle), "df"),
                new CodeViewManSlot(6, MetadataTokens.GetToken(cfLocalsSigHandle), "fi"),
                new CodeViewManSlot(4, MetadataTokens.GetToken(cfLocalsSigHandle), "fromf"),
                new CodeViewManSlot(5, MetadataTokens.GetToken(cfLocalsSigHandle), "di"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(cfLocalsSigHandle), "fd"),
                new CodeViewManSlot(3, MetadataTokens.GetToken(cfLocalsSigHandle), "fromd"),
            };

            bodyEncoder.AddMethodBody(castFloatMethod, "?cast_float@@$$J0YMHHMN@Z", enc,
                maxStack: 2, localVariablesSignature: cfLocalsSigHandle, attributes: 0,
                debugName: "cast_float", localSlots: localSlots);
        }

        // ─── Emit IL for cast_bool ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_true = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 38);
            enc.OpCode(ILOpCode.Ldarg_0);           // IL_0000
            enc.Branch(ILOpCode.Brtrue_s, lbl_true); // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0003
            enc.Branch(ILOpCode.Br_s, lbl_done);    // IL_0004
            enc.MarkLabel(lbl_true);                // IL_0006
            enc.OpCode(ILOpCode.Ldc_i4_1);          // IL_0006
            enc.MarkLabel(lbl_done);                // IL_0007
            enc.OpCode(ILOpCode.Stloc_1);           // IL_0007

            enc.MarkLineNumber(cvFile, 39);
            enc.OpCode(ILOpCode.Ldloc_1);           // IL_0008
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0009

            enc.MarkLineNumber(cvFile, 40);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_000A
            enc.OpCode(ILOpCode.Ret);               // IL_000B

            var localSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(cbLocalsSigHandle), "b"),
            };

            bodyEncoder.AddMethodBody(castBoolMethod, "?cast_bool@@$$J0YMHH@Z", enc,
                maxStack: 1, localVariablesSignature: cbLocalsSigHandle, attributes: 0,
                debugName: "cast_bool", localSlots: localSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 44);
            enc.OpCode(ILOpCode.Ldc_i4_0);          // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);           // IL_0001

            enc.LoadConstantI4(65);                 // IL_0002: ldc.i4.s 65
            enc.LoadConstantI4(100);                // IL_0004: ldc.i4.s 100
            enc.Call(castWidenMethod);               // IL_0006: call cast_widen

            enc.LoadConstantI4(0x12345);            // IL_000B: ldc.i4 0x12345
            enc.Call(castNarrowMethod);              // IL_0010: call cast_narrow
            enc.OpCode(ILOpCode.Add);               // IL_0015

            enc.LoadConstantI4(42);                 // IL_0016: ldc.i4.s 42
            enc.Call(castUnsignedMethod);            // IL_0018: call cast_unsigned
            enc.OpCode(ILOpCode.Add);               // IL_001D

            enc.LoadConstantI4(10);                 // IL_001E: ldc.i4.s 10
            enc.LoadConstantR4(3.5f);               // IL_0020: ldc.r4 3.5
            enc.LoadConstantR8(7.25);               // IL_0025: ldc.r8 7.25
            enc.Call(castFloatMethod);               // IL_002E: call cast_float
            enc.OpCode(ILOpCode.Add);               // IL_0033

            enc.LoadConstantI4(42);                 // IL_0034: ldc.i4.s 42
            enc.Call(castBoolMethod);                // IL_0036: call cast_bool
            enc.OpCode(ILOpCode.Add);               // IL_003B
            enc.OpCode(ILOpCode.Stloc_0);           // IL_003C

            enc.MarkLineNumber(cvFile, 45);
            enc.OpCode(ILOpCode.Ldloc_0);           // IL_003D
            enc.OpCode(ILOpCode.Ret);               // IL_003E

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 4, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
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
