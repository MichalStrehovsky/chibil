using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class ConstParamTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "const-param", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "const-param.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "const-param", refDir, "const-param.obj"));
        string emittedDump = ObjDumper.DumpForComparison(emitted);
        string referenceDump = ObjDumper.DumpForComparison(reference);
        Assert.Equal(referenceDump, emittedDump);
    }

    static byte[] EmitObj(Machine machine)
    {
        bool is32 = machine == Machine.I386;
        int ptrSize = is32 ? 4 : 8;
        string symPrefix = is32 ? "_" : "";
        string e = is32 ? "" : "E";

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

        var callConvCdeclRef = md.AddTypeReference(mscorlibRef, md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("CallConvCdecl"));

        // ─── TypeRefs ─────────────────────────────────────────────────────
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));
        var isConstRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsConst"));
        var isVolatileRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsVolatile"));

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

        // ─── TypeDef #2: $ArrayType$$$BY02H ───────────────────────────────
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY02H"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(4));

        md.AddTypeLayout(arrayTypeDef, 0, 12);

        // CustomAttribute: NativeCppClassAttribute on $ArrayType$$$BY02H
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── MethodDef #1: sum_array(Ptr modopt(IsConst) int32, int32) -> int32 ──
        var sumArraySig = new BlobBuilder();
        new BlobEncoder(sumArraySig).MethodSignature()
            .Parameters(2, out var sumArrayRetEnc, out var sumArrayParEnc);
        ClrIjw.EncodeCdeclI4Return(sumArrayRetEnc, callConvCdeclRef);
        // Param 1: Ptr CMOD_OPT(IsConst) I4
        var sap1 = sumArrayParEnc.AddParameter().Type();
        sap1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        sap1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sap1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isConstRef));
        sap1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        // Param 2: int32
        sumArrayParEnc.AddParameter().Type().Int32();

        var sumArrayMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("sum_array"),
            md.GetOrAddBlob(sumArraySig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("arr"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("len"), 2);

        // Locals for sum_array: int32 (i), int32 (sum), int32 (return value)
        var sumArrayLocalsSig = new BlobBuilder();
        var sumArrayLocalsEnc = new BlobEncoder(sumArrayLocalsSig).LocalVariableSignature(3);
        sumArrayLocalsEnc.AddVariable().Type().Int32();
        sumArrayLocalsEnc.AddVariable().Type().Int32();
        sumArrayLocalsEnc.AddVariable().Type().Int32();
        var sumArrayLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(sumArrayLocalsSig));

        // ─── MethodDef #2: read_volatile(Ptr modreq(IsVolatile) int32) -> int32 ──
        var readVolSig = new BlobBuilder();
        new BlobEncoder(readVolSig).MethodSignature()
            .Parameters(1, out var readVolRetEnc, out var readVolParEnc);
        ClrIjw.EncodeCdeclI4Return(readVolRetEnc, callConvCdeclRef);
        // Param 1: Ptr CMOD_REQD(IsVolatile) I4
        var rvp1 = readVolParEnc.AddParameter().Type();
        rvp1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        rvp1.Builder.WriteByte((byte)SignatureTypeCode.RequiredModifier);
        rvp1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isVolatileRef));
        rvp1.Builder.WriteByte((byte)SignatureTypeCode.Int32);

        var readVolMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("read_volatile"),
            md.GetOrAddBlob(readVolSig),
            0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);

        // Locals for read_volatile: int32
        var readVolLocalsSig = new BlobBuilder();
        new BlobEncoder(readVolLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var readVolLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(readVolLocalsSig));

        // ─── MethodDef #3: main() -> int32 ────────────────────────────────
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
            MetadataTokens.ParameterHandle(4));

        // Locals for main: int32 (V_0), modreq(IsVolatile) int32 (V_1:v), ValueType $ArrayType$$$BY02H (V_2:arr)
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(3);
        mainLocalsEnc.AddVariable().Type().Int32();
        // V_1: modreq(IsVolatile) int32
        var mainLocV1 = mainLocalsEnc.AddVariable().Type();
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.RequiredModifier);
        mainLocV1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isVolatileRef));
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        // V_2: ValueType $ArrayType$$$BY02H
        mainLocalsEnc.AddVariable().Type().Type(arrayTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("const-param.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        var ilSection = new CoffSectionWithContentBuilder(".text$mn", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes);
        var dataSection = new CoffSectionWithContentBuilder(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes);
        var nepSection = new CoffSectionWithContentBuilder(".nep", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes);
        var ilFixupSection = new CoffSectionWithContentBuilder(".rdata$ilfixup", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        codeviewSymbols.AddObjNameAndCompile3("const-param.obj",
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "const-param.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilSection, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for sum_array ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var loopInc = enc.DefineLabel();
            var loopCond = enc.DefineLabel();
            var loopEnd = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 6);
            enc.LoadConstantI4(0);                 // IL_0000: ldc.i4.0
            enc.StoreLocal(1);                     // IL_0001: stloc.1 (sum = 0)

            enc.MarkLineNumber(cvFile, 8);
            enc.LoadConstantI4(0);                 // IL_0002: ldc.i4.0
            enc.StoreLocal(0);                     // IL_0003: stloc.0 (i = 0)
            enc.Branch(ILOpCode.Br_s, loopCond);   // IL_0004: br.s loopCond

            enc.MarkLabel(loopInc);                // IL_0006:
            enc.LoadLocal(0);                      // IL_0006: ldloc.0
            enc.LoadConstantI4(1);                 // IL_0007: ldc.i4.1
            enc.OpCode(ILOpCode.Add);              // IL_0008: add
            enc.StoreLocal(0);                     // IL_0009: stloc.0 (i++)

            enc.MarkLabel(loopCond);               // IL_000A:
            enc.LoadLocal(0);                      // IL_000A: ldloc.0
            enc.LoadArgument(1);                   // IL_000B: ldarg.1
            enc.Branch(ILOpCode.Bge_s, loopEnd);   // IL_000C: bge.s loopEnd

            // loop body: sum = sum + arr[i]
            enc.MarkLineNumber(cvFile, 9);
            enc.LoadLocal(1);                      // IL_000E: ldloc.1 (sum)
            enc.LoadArgument(0);                   // IL_000F: ldarg.0 (arr)
            enc.LoadLocal(0);                      // IL_0010: ldloc.0 (i)
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.LoadConstantI4(4);                 // ldc.i4.4
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);              // mul
            enc.OpCode(ILOpCode.Add);              // add
            enc.OpCode(ILOpCode.Ldind_i4);         // ldind.i4
            enc.OpCode(ILOpCode.Add);              // add
            enc.StoreLocal(1);                     // stloc.1
            enc.Branch(ILOpCode.Br_s, loopInc);    // br.s loopInc

            enc.MarkLabel(loopEnd);
            enc.MarkLineNumber(cvFile, 10);
            enc.LoadLocal(1);                      // ldloc.1
            enc.StoreLocal(2);                     // stloc.2 (return value)
            enc.MarkLineNumber(cvFile, 11);
            enc.LoadLocal(2);                      // ldloc.2
            enc.OpCode(ILOpCode.Ret);              // ret

            var sumArrayLocalSlots = new[] {
                new CodeViewManSlot(0, MetadataTokens.GetToken(sumArrayLocalsSigHandle), "i"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(sumArrayLocalsSigHandle), "sum"),
            };

            bodyEncoder.AddMethodBody(sumArrayMethod, $"?sum_array@@$$J0YAHP{e}BHH@Z", enc,
                maxStack: 4, localVariablesSignature: sumArrayLocalsSigHandle, attributes: 0,
                debugName: "sum_array", localSlots: sumArrayLocalSlots);
        }

        // ─── Emit IL for read_volatile ────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 15);
            enc.LoadArgument(0);                   // IL_0000: ldarg.0
            enc.OpCode(ILOpCode.Volatile);         // IL_0001: volatile.
            enc.OpCode(ILOpCode.Ldind_i4);         // IL_0003: ldind.i4
            enc.StoreLocal(0);                     // IL_0004: stloc.0
            enc.MarkLineNumber(cvFile, 16);
            enc.LoadLocal(0);                      // IL_0005: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_0006: ret

            bodyEncoder.AddMethodBody(readVolMethod, $"?read_volatile@@$$J0YAHP{e}CH@Z", enc,
                maxStack: 1, localVariablesSignature: readVolLocalsSigHandle, attributes: 0,
                debugName: "read_volatile");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldc_i4_0);         // IL_0000: ldc.i4.0
            enc.StoreLocal(0);                     // IL_0001: stloc.0

            // arr[0] = 10
            enc.LoadLocalAddress(2);               // IL_0002: ldloca.s V_2
            enc.LoadConstantI4(10);                // IL_0004: ldc.i4.s 10
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0006: stind.i4

            // arr[1] = 20
            enc.LoadLocalAddress(2);               // IL_0007: ldloca.s V_2
            enc.LoadConstantI4(4);                 // IL_0009: ldc.i4.4
            enc.OpCode(ILOpCode.Add);              // IL_000A: add
            enc.LoadConstantI4(20);                // IL_000B: ldc.i4.s 20
            enc.OpCode(ILOpCode.Stind_i4);         // IL_000D: stind.i4

            // arr[2] = 30
            enc.LoadLocalAddress(2);               // IL_000E: ldloca.s V_2
            enc.LoadConstantI4(8);                 // IL_0010: ldc.i4.8
            enc.OpCode(ILOpCode.Add);              // IL_0011: add
            enc.LoadConstantI4(30);                // IL_0012: ldc.i4.s 30
            enc.OpCode(ILOpCode.Stind_i4);         // IL_0014: stind.i4

            // volatile int v = 42
            enc.MarkLineNumber(cvFile, 21);
            enc.LoadConstantI4(42);                // IL_0015: ldc.i4.s 42
            enc.StoreLocal(1);                     // IL_0017: stloc.1

            // sum_array(arr, 3)
            enc.MarkLineNumber(cvFile, 22);
            enc.LoadLocalAddress(2);               // IL_0018: ldloca.s V_2
            enc.LoadConstantI4(3);                 // IL_001A: ldc.i4.3
            enc.Call(sumArrayMethod);              // IL_001B: call sum_array

            // read_volatile(&v)
            enc.LoadLocalAddress(1);               // IL_0020: ldloca.s V_1
            enc.Call(readVolMethod);               // IL_0022: call read_volatile

            // sum_array(...) + read_volatile(...)
            enc.OpCode(ILOpCode.Add);              // IL_0027: add
            enc.StoreLocal(0);                     // IL_0028: stloc.0

            enc.MarkLineNumber(cvFile, 23);
            enc.LoadLocal(0);                      // IL_0029: ldloc.0
            enc.OpCode(ILOpCode.Ret);              // IL_002A: ret

            var mainLocalSlots = new[] {
                new CodeViewManSlot(2, MetadataTokens.GetToken(mainLocalsSigHandle), "arr"),
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "v"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 2, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            sumArrayMethod, "sum_array", $"?sum_array@@$$J0YAHP{e}BHH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            readVolMethod, "read_volatile", $"?read_volatile@@$$J0YAHP{e}CH@Z");
        ClrIjw.EmitNepMachinery(machine, ptrSize, symPrefix, coffHeader, symtab,
            dataSection, nepSection, ilFixupSection,
            mainMethod, "main", "?main@@$$J0YAHXZ");

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var sections = new System.Collections.Generic.List<CoffSectionBuilder>();
        if (ilSection.Content.Count > 0) sections.Add(ilSection);
        if (dataSection.Content.Count > 0) sections.Add(dataSection);
        if (ilFixupSection.Content.Count > 0) sections.Add(ilFixupSection);
        if (nepSection.Content.Count > 0) sections.Add(nepSection);
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols, sections);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}
