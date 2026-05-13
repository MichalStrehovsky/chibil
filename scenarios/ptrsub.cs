using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class PtrsubTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    [InlineData(Machine.Amd64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";

        string emittedDir = Path.Combine(AppContext.BaseDirectory, "emitted", "ptrsub", refDir);
        Directory.CreateDirectory(emittedDir);
        File.WriteAllBytes(Path.Combine(emittedDir, "ptrsub.obj"), emitted);

        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "ptrsub", refDir, "ptrsub.obj"));
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
            md.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default, md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRef: CallConvCdecl (modopt on return types under /clr) ───
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"),
            md.GetOrAddString("CallConvCdecl"));

        // ─── TypeRefs ─────────────────────────────────────────────────────
        var valueTypeRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
        var nativeCppClassAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("NativeCppClassAttribute"));
        var isSignUnspecifiedByteRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsSignUnspecifiedByte"));

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

        // ─── TypeDef #2: $ArrayType$$$BY03H (Pack=0, Size=16) ─────────────
        var arrayTypeDef = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY03H"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(7));

        md.AddTypeLayout(arrayTypeDef, 0, 16);

        // CustomAttribute: NativeCppClassAttribute on $ArrayType$$$BY03H
        md.AddCustomAttribute(arrayTypeDef, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── MethodDef #1: ptr_subtract_int(Ptr int32, Ptr int32) -> int32 ─
        var psiSig = new BlobBuilder();
        new BlobEncoder(psiSig).MethodSignature()
            .Parameters(2, out var psiRetEnc, out var psiParEnc);
        ClrIjw.EncodeCdeclI4Return(psiRetEnc, callConvCdeclRef);
        psiParEnc.AddParameter().Type().Pointer().Int32();
        psiParEnc.AddParameter().Type().Pointer().Int32();

        var ptrSubIntMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_subtract_int"), md.GetOrAddBlob(psiSig), 0,
            MetadataTokens.ParameterHandle(1));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("q"), 2);

        var psiLocalsSig = new BlobBuilder();
        new BlobEncoder(psiLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var psiLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(psiLocalsSig));

        // ─── MethodDef #2: ptr_subtract_char(Ptr modopt(IsSignUnspecifiedByte) int8, ...) -> int32 ─
        var pscSig = new BlobBuilder();
        new BlobEncoder(pscSig).MethodSignature()
            .Parameters(2, out var pscRetEnc, out var pscParEnc);
        ClrIjw.EncodeCdeclI4Return(pscRetEnc, callConvCdeclRef);
        var pscP1 = pscParEnc.AddParameter().Type();
        pscP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        pscP1.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        pscP1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        pscP1.Builder.WriteByte((byte)SignatureTypeCode.SByte);
        var pscP2 = pscParEnc.AddParameter().Type();
        pscP2.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        pscP2.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        pscP2.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        pscP2.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var ptrSubCharMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_subtract_char"), md.GetOrAddBlob(pscSig), 0,
            MetadataTokens.ParameterHandle(3));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("q"), 2);

        var pscLocalsSig = new BlobBuilder();
        new BlobEncoder(pscLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var pscLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(pscLocalsSig));

        // ─── MethodDef #3: ptr_subtract_double(Ptr float64, Ptr float64) -> int64 ─
        var psdSig = new BlobBuilder();
        new BlobEncoder(psdSig).MethodSignature()
            .Parameters(2, out var psdRetEnc, out var psdParEnc);
        ClrIjw.WriteCdeclModOpt(psdRetEnc, callConvCdeclRef).Int64();
        psdParEnc.AddParameter().Type().Pointer().Double();
        psdParEnc.AddParameter().Type().Pointer().Double();

        var ptrSubDblMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_subtract_double"), md.GetOrAddBlob(psdSig), 0,
            MetadataTokens.ParameterHandle(5));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("q"), 2);

        var psdLocalsSig = new BlobBuilder();
        new BlobEncoder(psdLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int64();
        var psdLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(psdLocalsSig));

        // ─── MethodDef #4: ptr_less(Ptr int32, Ptr int32) -> int32 ────────
        var plSig = new BlobBuilder();
        new BlobEncoder(plSig).MethodSignature()
            .Parameters(2, out var plRetEnc, out var plParEnc);
        ClrIjw.EncodeCdeclI4Return(plRetEnc, callConvCdeclRef);
        plParEnc.AddParameter().Type().Pointer().Int32();
        plParEnc.AddParameter().Type().Pointer().Int32();

        var ptrLessMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_less"), md.GetOrAddBlob(plSig), 0,
            MetadataTokens.ParameterHandle(7));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("q"), 2);

        var plLocalsSig = new BlobBuilder();
        new BlobEncoder(plLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var plLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(plLocalsSig));

        // ─── MethodDef #5: ptr_equal(Ptr int32, Ptr int32) -> int32 ───────
        var peSig = new BlobBuilder();
        new BlobEncoder(peSig).MethodSignature()
            .Parameters(2, out var peRetEnc, out var peParEnc);
        ClrIjw.EncodeCdeclI4Return(peRetEnc, callConvCdeclRef);
        peParEnc.AddParameter().Type().Pointer().Int32();
        peParEnc.AddParameter().Type().Pointer().Int32();

        var ptrEqualMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("ptr_equal"), md.GetOrAddBlob(peSig), 0,
            MetadataTokens.ParameterHandle(9));
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("q"), 2);

        var peLocalsSig = new BlobBuilder();
        new BlobEncoder(peLocalsSig).LocalVariableSignature(1).AddVariable().Type().Int32();
        var peLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(peLocalsSig));

        // ─── MethodDef #6: main() -> int32 ────────────────────────────────
        var mainSig = new BlobBuilder();
        new BlobEncoder(mainSig).MethodSignature()
            .Parameters(0, out var mRetEnc, out var mParEnc);
        ClrIjw.EncodeCdeclI4Return(mRetEnc, callConvCdeclRef);

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"), md.GetOrAddBlob(mainSig), 0,
            MetadataTokens.ParameterHandle(11));

        // Locals for main: int32 (V_0), ValueType $ArrayType$$$BY03H (V_1)
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        mainLocalsEnc.AddVariable().Type().Type(arrayTypeDef, isValueType: true);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0, md.GetOrAddString("ptrsub.obj"), md.GetOrAddGuid(Guid.NewGuid()), default, default);

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
        codeviewSymbols.AddObjNameAndCompile3("ptrsub.obj",
            language: CodeViewLanguage.C, machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "ptrsub.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for ptr_subtract_int ─────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.OpCode(ILOpCode.Sub);              // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_2);        // IL_0003
            enc.OpCode(ILOpCode.Shr);              // IL_0004
            if (machine != Machine.I386)
                enc.OpCode(ILOpCode.Conv_i4);      // arm64: IL_0005
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0005 (x86) / IL_0006 (arm64)
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0006 / IL_0007
            enc.OpCode(ILOpCode.Ret);              // IL_0007 / IL_0008

            bodyEncoder.AddMethodBody(ptrSubIntMethod, $"?ptr_subtract_int@@$$J0YAHP{e}AH0@Z", enc,
                maxStack: 2, localVariablesSignature: psiLocalsSigHandle, attributes: 0,
                debugName: "ptr_subtract_int");
        }

        // ─── Emit IL for ptr_subtract_char ────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 12);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.OpCode(ILOpCode.Sub);              // IL_0002
            if (machine != Machine.I386)
                enc.OpCode(ILOpCode.Conv_i4);      // arm64: IL_0003
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0003 (x86) / IL_0004 (arm64)
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0004 / IL_0005
            enc.OpCode(ILOpCode.Ret);              // IL_0005 / IL_0006

            bodyEncoder.AddMethodBody(ptrSubCharMethod, $"?ptr_subtract_char@@$$J0YAHP{e}AD0@Z", enc,
                maxStack: 2, localVariablesSignature: pscLocalsSigHandle, attributes: 0,
                debugName: "ptr_subtract_char");
        }

        // ─── Emit IL for ptr_subtract_double ──────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 13);
            enc.OpCode(ILOpCode.Ldarg_0);          // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);          // IL_0001
            enc.OpCode(ILOpCode.Sub);              // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_3);        // IL_0003
            enc.OpCode(ILOpCode.Shr);              // IL_0004
            if (machine == Machine.I386)
                enc.OpCode(ILOpCode.Conv_i8);      // x86 only: IL_0005
            enc.OpCode(ILOpCode.Stloc_0);          // IL_0005 (arm64) / IL_0006 (x86)
            enc.OpCode(ILOpCode.Ldloc_0);          // IL_0006 / IL_0007
            enc.OpCode(ILOpCode.Ret);              // IL_0007 / IL_0008

            bodyEncoder.AddMethodBody(ptrSubDblMethod, $"?ptr_subtract_double@@$$J0YA_JP{e}AN0@Z", enc,
                maxStack: 2, localVariablesSignature: psdLocalsSigHandle, attributes: 0,
                debugName: "ptr_subtract_double");
        }

        // ─── Emit IL for ptr_less ─────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_zero = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 14);
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);              // IL_0001
            enc.Branch(ILOpCode.Bge_un_s, lbl_zero);  // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_1);            // IL_0004
            enc.Branch(ILOpCode.Br_s, lbl_done);      // IL_0005
            enc.MarkLabel(lbl_zero);                   // IL_0007
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0007
            enc.MarkLabel(lbl_done);                   // IL_0008
            enc.OpCode(ILOpCode.Stloc_0);              // IL_0008
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0009
            enc.OpCode(ILOpCode.Ret);                  // IL_000A

            bodyEncoder.AddMethodBody(ptrLessMethod, $"?ptr_less@@$$J0YAHP{e}AH0@Z", enc,
                maxStack: 2, localVariablesSignature: plLocalsSigHandle, attributes: 0,
                debugName: "ptr_less");
        }

        // ─── Emit IL for ptr_equal ────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            var lbl_zero = enc.DefineLabel();
            var lbl_done = enc.DefineLabel();

            enc.MarkLineNumber(cvFile, 15);
            enc.OpCode(ILOpCode.Ldarg_0);              // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);              // IL_0001
            enc.Branch(ILOpCode.Bne_un_s, lbl_zero);  // IL_0002
            enc.OpCode(ILOpCode.Ldc_i4_1);            // IL_0004
            enc.Branch(ILOpCode.Br_s, lbl_done);      // IL_0005
            enc.MarkLabel(lbl_zero);                   // IL_0007
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0007
            enc.MarkLabel(lbl_done);                   // IL_0008
            enc.OpCode(ILOpCode.Stloc_0);              // IL_0008
            enc.OpCode(ILOpCode.Ldloc_0);              // IL_0009
            enc.OpCode(ILOpCode.Ret);                  // IL_000A

            bodyEncoder.AddMethodBody(ptrEqualMethod, $"?ptr_equal@@$$J0YAHP{e}AH0@Z", enc,
                maxStack: 2, localVariablesSignature: peLocalsSigHandle, attributes: 0,
                debugName: "ptr_equal");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 20);
            enc.OpCode(ILOpCode.Ldc_i4_0);            // IL_0000
            enc.OpCode(ILOpCode.Stloc_0);             // IL_0001

            // ptr_subtract_int(&arr[3], &arr[0])
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_3);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.Call(ptrSubIntMethod);                 // call ptr_subtract_int

            // ptr_less(&arr[0], &arr[3])
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_0);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Ldc_i4_3);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Mul);
            enc.OpCode(ILOpCode.Add);
            enc.Call(ptrLessMethod);                   // call ptr_less
            enc.OpCode(ILOpCode.Add);                  // add

            // ptr_equal(&arr[1], &arr[1])
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);
            enc.LoadLocalAddress(1);                   // ldloca.s V_1
            enc.OpCode(ILOpCode.Ldc_i4_4);
            if (machine != Machine.I386) enc.OpCode(ILOpCode.Conv_i8);
            enc.OpCode(ILOpCode.Add);
            enc.Call(ptrEqualMethod);                  // call ptr_equal
            enc.OpCode(ILOpCode.Add);                  // add

            enc.OpCode(ILOpCode.Stloc_0);             // stloc.0
            enc.MarkLineNumber(cvFile, 23);
            enc.OpCode(ILOpCode.Ldloc_0);             // ldloc.0
            enc.OpCode(ILOpCode.Ret);                  // ret

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "arr"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YAHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
                debugName: "main", localSlots: mainLocalSlots);
        }

        // ─── IJW machinery for managed exports ────────────────────────────
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(ptrSubIntMethod), "ptr_subtract_int", $"?ptr_subtract_int@@$$J0YAHP{e}AH0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(ptrSubCharMethod), "ptr_subtract_char", $"?ptr_subtract_char@@$$J0YAHP{e}AD0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(ptrSubDblMethod), "ptr_subtract_double", $"?ptr_subtract_double@@$$J0YA_JP{e}AN0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(ptrLessMethod), "ptr_less", $"?ptr_less@@$$J0YAHP{e}AH0@Z");
        ClrIjw.EmitNepMachinery(machine, is32, ptrSize, symPrefix, coffHeader, symtab,
            dataStreamBuilder, dataRelocBuilder, nepStreamBuilder, nepRelocBuilder,
            ilFixupStreamBuilder, ilFixupRelocBuilder,
            MetadataTokens.GetToken(ptrEqualMethod), "ptr_equal", $"?ptr_equal@@$$J0YAHP{e}AH0@Z");
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
