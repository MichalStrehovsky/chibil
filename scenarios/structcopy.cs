using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class StructCopyTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "structcopy", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "structcopy.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "structcopy", refDir, "structcopy.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        bool is32 = machine == Machine.I386;
        int ptrSize = is32 ? 4 : 8;
        string symPrefix = is32 ? "_" : "";
        string e = is32 ? "" : "E";  // MSVC __ptr64 modifier in 64-bit mangled names

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

        // ─── TypeDef #2: Small (sequential, sealed, size=8) ───────────────
        var smallTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("Small"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(6));

        md.AddTypeLayout(smallTypeDef, 0, 8);

        md.AddCustomAttribute(smallTypeDef, nativeCppCtorRef,
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

        // ─── TypeDef #3: Big (sequential, sealed, size=64) ────────────────
        int bigFirstField = machine == Machine.I386 ? 1 : 2;
        var bigTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("Big"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(bigFirstField),
            MetadataTokens.MethodDefinitionHandle(6));

        md.AddTypeLayout(bigTypeDef, 0, 64);

        md.AddCustomAttribute(bigTypeDef, nativeCppCtorRef,
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

        // ─── MethodDef #1: copy_small ─────────────────────────────────────
        // Sig: void(Ptr ValueType Small, Ptr ValueType Small)
        var copySmallSig = new BlobBuilder();
        var copySmallSigEnc = new BlobEncoder(copySmallSig).MethodSignature();
        copySmallSigEnc.Parameters(2, out var csRetEnc, out var csParEnc);
        ClrIjw.EncodeCdeclVoidReturn(csRetEnc, callConvCdeclRef);
        csParEnc.AddParameter().Type().Pointer().Type(smallTypeDef, isValueType: true);
        csParEnc.AddParameter().Type().Pointer().Type(smallTypeDef, isValueType: true);

        var copySmallMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("copy_small"),
            md.GetOrAddBlob(copySmallSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("dst"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("src"), 2);

        // ─── MethodDef #2: copy_big ───────────────────────────────────────
        // Sig: void(Ptr ValueType Big, Ptr ValueType Big)
        var copyBigSig = new BlobBuilder();
        var copyBigSigEnc = new BlobEncoder(copyBigSig).MethodSignature();
        copyBigSigEnc.Parameters(2, out var cbRetEnc, out var cbParEnc);
        ClrIjw.EncodeCdeclVoidReturn(cbRetEnc, callConvCdeclRef);
        cbParEnc.AddParameter().Type().Pointer().Type(bigTypeDef, isValueType: true);
        cbParEnc.AddParameter().Type().Pointer().Type(bigTypeDef, isValueType: true);

        var copyBigMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("copy_big"),
            md.GetOrAddBlob(copyBigSig),
            0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("dst"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("src"), 2);

        // ─── MethodDef #3: make_small ─────────────────────────────────────
        // Sig: ValueType Small(int32, int32)
        var makeSmallSig = new BlobBuilder();
        var makeSmallSigEnc = new BlobEncoder(makeSmallSig).MethodSignature();
        makeSmallSigEnc.Parameters(2, out var msRetEnc, out var msParEnc);
        ClrIjw.WriteCdeclModOpt(msRetEnc, callConvCdeclRef).Type(smallTypeDef, isValueType: true);
        msParEnc.AddParameter().Type().Int32();
        msParEnc.AddParameter().Type().Int32();

        var makeSmallMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("make_small"),
            md.GetOrAddBlob(makeSmallSig),
            0,
            MetadataTokens.ParameterHandle(5));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("a"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("b"), 2);

        // Locals for make_small: ValueType Small, ValueType Small
        var makeSmallLocalsSig = new BlobBuilder();
        var makeSmallLocalsEnc = new BlobEncoder(makeSmallLocalsSig).LocalVariableSignature(2);
        makeSmallLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        makeSmallLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        var makeSmallLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(makeSmallLocalsSig));

        // ─── MethodDef #4: assign_local ───────────────────────────────────
        // Sig: void()
        var assignLocalSig = new BlobBuilder();
        new BlobEncoder(assignLocalSig).MethodSignature()
            .Parameters(0, out var alRetEnc, out var alParEnc);
        ClrIjw.EncodeCdeclVoidReturn(alRetEnc, callConvCdeclRef);

        var assignLocalMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("assign_local"),
            md.GetOrAddBlob(assignLocalSig),
            0,
            MetadataTokens.ParameterHandle(7));

        // Locals for assign_local: ValueType Small, ValueType Small
        var assignLocalLocalsSig = new BlobBuilder();
        var assignLocalLocalsEnc = new BlobEncoder(assignLocalLocalsSig).LocalVariableSignature(2);
        assignLocalLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        assignLocalLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        var assignLocalLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(assignLocalLocalsSig));

        // ─── MethodDef #5: main ───────────────────────────────────────────
        // Sig: int32()
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
            MetadataTokens.ParameterHandle(7));

        // Locals for main: int32, ValueType Small, ValueType Small
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        mainLocalsEnc.AddVariable().Type().Type(smallTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("structcopy.obj"),
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
        string objPath = "structcopy.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "structcopy.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for copy_small ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.LoadConstantI4(8);                 // IL_0002: ldc.i4.8
            if (machine != Machine.I386)
            {
                enc.OpCode(ILOpCode.Unaligned);    // unaligned. prefix
                enc.CodeBuilder.WriteByte(4);      // alignment = 4
            }
            enc.OpCode(ILOpCode.Cpblk);            // cpblk
            enc.OpCode(ILOpCode.Ret);              // ret

            bodyEncoder.AddMethodBody(copySmallMethod, $"?copy_small@@$$J0YAXP{e}AUSmall@@0@Z", enc,
                maxStack: 3, localVariablesSignature: default, attributes: 0,
                debugName: "copy_small");
        }

        // ─── Emit IL for copy_big ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 12);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.LoadConstantI4(64);                // IL_0002: ldc.i4.s 64
            if (machine != Machine.I386)
            {
                enc.OpCode(ILOpCode.Unaligned);    // unaligned. prefix
                enc.CodeBuilder.WriteByte(4);      // alignment = 4
            }
            enc.OpCode(ILOpCode.Cpblk);            // cpblk
            enc.OpCode(ILOpCode.Ret);              // ret

            bodyEncoder.AddMethodBody(copyBigMethod, $"?copy_big@@$$J0YAXP{e}AUBig@@0@Z", enc,
                maxStack: 3, localVariablesSignature: default, attributes: 0,
                debugName: "copy_big");
        }

        // ─── Emit IL for make_small ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocalAddress(1);               // IL_0000: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0002: ldarg.0
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0003: stind.i4
            enc.MarkLineNumber(cvFile, 18);
            enc.LoadLocalAddress(1);               // IL_0004: ldloca.s V_1
            enc.LoadConstantI4(4);                 // IL_0006: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_0007: add
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0008: ldarg.1
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0009: stind.i4
            enc.MarkLineNumber(cvFile, 19);
            enc.OpCode(ILOpCode.Ldloc_1);          // IL_000A: ldloc.1
            enc.OpCode(ILOpCode.Stloc_0);          // IL_000B: stloc.0
            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_000C: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_000D: ret

            var makeSmallLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(makeSmallLocalsSigHandle), "s"),
            };

            bodyEncoder.AddMethodBody(makeSmallMethod, "?make_small@@$$J0YA?AUSmall@@HH@Z", enc,
                maxStack: 2, localVariablesSignature: makeSmallLocalsSigHandle, attributes: 0,
                debugName: "make_small", localSlots: makeSmallLocalSlots);
        }

        // ─── Emit IL for assign_local ─────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 26);
            enc.LoadLocalAddress(0);               // IL_0000: ldloca.s V_0
            enc.LoadConstantI4(1);                 // IL_0002: ldc.i4.1
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0003: stind.i4
            enc.MarkLineNumber(cvFile, 27);
            enc.LoadLocalAddress(0);               // IL_0004: ldloca.s V_0
            enc.LoadConstantI4(4);                 // IL_0006: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_0007: add
            enc.LoadConstantI4(2);                 // IL_0008: ldc.i4.2
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0009: stind.i4
            enc.MarkLineNumber(cvFile, 28);
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_000A: ldloc.0
            enc.OpCode(ILOpCode.Stloc_1);          // IL_000B: stloc.1
            enc.MarkLineNumber(cvFile, 29);
            enc.OpCode(ILOpCode.Ret);              // IL_000C: ret

            var assignLocalLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(assignLocalLocalsSigHandle), "b"),
                new CodeViewManSlot(0, MetadataTokens.GetToken(assignLocalLocalsSigHandle), "a"),
            };

            bodyEncoder.AddMethodBody(assignLocalMethod, "?assign_local@@$$J0YAXXZ", enc,
                maxStack: 2, localVariablesSignature: assignLocalLocalsSigHandle, attributes: 0,
                debugName: "assign_local", localSlots: assignLocalLocalSlots);
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 35);
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_0000: ldc.i4.0
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0001: stloc.0
            enc.LoadConstantI4(10);                // IL_0002: ldc.i4.s 10
            enc.LoadConstantI4(20);                // IL_0004: ldc.i4.s 20
            enc.Call(makeSmallMethod);             // IL_0006: call make_small
            enc.OpCode(ILOpCode.Stloc_2);          // IL_000B: stloc.2
            enc.MarkLineNumber(cvFile, 36);
            enc.LoadLocalAddress(1);               // IL_000C: ldloca.s V_1
            enc.LoadLocalAddress(2);               // IL_000E: ldloca.s V_2
            enc.Call(copySmallMethod);             // IL_0010: call copy_small
            enc.MarkLineNumber(cvFile, 37);
            enc.LoadLocalAddress(1);               // IL_0015: ldloca.s V_1
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0017: ldind.i4
            enc.LoadLocalAddress(1);               // IL_0018: ldloca.s V_1
            enc.LoadConstantI4(4);                 // IL_001A: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_001B: add
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_001C: ldind.i4
            enc.OpCode(ILOpCode.Add);              // IL_001D: add
            enc.OpCode(ILOpCode.Stloc_0);          // IL_001E: stloc.0
            enc.MarkLineNumber(cvFile, 38);
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_001F: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_0020: ret

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "s1"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "s2"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for exported methods ───────────────────────────
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(copySmallMethod), "copy_small", $"?copy_small@@$$J0YAXP{e}AUSmall@@0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(copyBigMethod), "copy_big", $"?copy_big@@$$J0YAXP{e}AUBig@@0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(makeSmallMethod), "make_small", "?make_small@@$$J0YA?AUSmall@@HH@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(assignLocalMethod), "assign_local", "?assign_local@@$$J0YAXXZ");
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
