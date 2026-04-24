using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class PinvokeForwardrefTest
{
    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Arm64)]
    public void Emit(Machine machine)
    {
        byte[] emitted = EmitObj(machine);
        string refDir = machine == Machine.I386 ? "x86" : "arm64";
        byte[] reference = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "reference", "pinvoke-forwardref", refDir, "pinvoke-forwardref.obj"));
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
        string callConvName = machine == Machine.I386 ? "CallConvStdcall" : "CallConvCdecl";
        var callConvRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString(callConvName));
        var decoratedNameAttrRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("DecoratedNameAttribute"));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        var moduleType = md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── TypeRef: Mine (forward-declared struct, resolution scope = null) ─
        // A null resolution scope means the type is expected to be defined by
        // another module at link time. This matches MSVC's behavior for
        // forward-declared structs.
        var mineTypeRef = md.AddTypeReference(default(ModuleReferenceHandle),
            default, md.GetOrAddString("Mine"));

        // ─── MethodDef: main ──────────────────────────────────────────────
        var methodSigBuilder = new BlobBuilder();
        new BlobEncoder(methodSigBuilder).MethodSignature()
            .Parameters(0, out var rtEnc, out var parEnc);
        rtEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(methodSigBuilder),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── MemberRef: MessageBoxW on <Module> ───────────────────────────
        // Signature: returns CMOD_OPT CallConvCdecl I4, params: Ptr ValueClass Mine, Ptr Void, Ptr Void, I4
        var msgBoxSigBuilder = new BlobBuilder();
        var msgBoxSigEnc = new BlobEncoder(msgBoxSigBuilder).MethodSignature();
        msgBoxSigEnc.Parameters(4, out var msgBoxRetEnc, out var msgBoxParEnc);

        // Return type: CMOD_OPT CallConvCdecl I4
        msgBoxRetEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        msgBoxRetEnc.Type().Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvRef));
        msgBoxRetEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.Int32);

        // Param 1: Ptr ValueClass Mine
        msgBoxParEnc.AddParameter().Type().Pointer().Type(mineTypeRef, isValueType: true);
        // Params 2-3: Ptr Void
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Void);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Void);
        // Param 4: I4
        msgBoxParEnc.AddParameter().Type().Int32();

        var messageBoxWRef = md.AddMemberReference(moduleType,
            md.GetOrAddString("MessageBoxW"), md.GetOrAddBlob(msgBoxSigBuilder));

        // ─── MemberRef: DecoratedNameAttribute::.ctor(String) ─────────────
        var decNameCtorSigBuilder = new BlobBuilder();
        new BlobEncoder(decNameCtorSigBuilder).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(1, out var decNameRetEnc, out var decNameParEnc);
        decNameRetEnc.Void();
        decNameParEnc.AddParameter().Type().String();

        var decNameCtorRef = md.AddMemberReference(decoratedNameAttrRef,
            md.GetOrAddString(".ctor"), md.GetOrAddBlob(decNameCtorSigBuilder));

        // ─── CustomAttribute: DecoratedNameAttribute on MessageBoxW ───────
        var customAttrValueBuilder = new BlobBuilder();
        customAttrValueBuilder.WriteUInt16(0x0001);
        string messageBoxWDecoratedName = machine == Machine.I386
            ? "?MessageBoxW@@$$J216YGHPAUMine@@PAX1H@Z"
            : "?MessageBoxW@@$$J0YAHPEAUMine@@PEAX1H@Z";
        customAttrValueBuilder.WriteSerializedString(messageBoxWDecoratedName);
        customAttrValueBuilder.WriteUInt16(0x0000);

        md.AddCustomAttribute(messageBoxWRef, decNameCtorRef,
            md.GetOrAddBlob(customAttrValueBuilder));

        // ─── StandaloneSig: locals (int32) ────────────────────────────────
        var localsSigBuilder = new BlobBuilder();
        new BlobEncoder(localsSigBuilder).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var localsSig = md.AddStandaloneSignature(md.GetOrAddBlob(localsSigBuilder));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("pinvoke-forwardref.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        symtab.AddExternalClrToken(messageBoxWDecoratedName, messageBoxWRef);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "pinvoke-forwardref.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "pinvoke-forwardref.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main ─────────────────────────────────────────────
        var encoder = new RelocatableInstructionEncoder(
            new BlobBuilder(),
            new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(),
            new CodeViewLineNumberBuilder());

        encoder.MarkLineNumber(cvFile, 9);
        encoder.OpCode(ILOpCode.Ldc_i4_0);
        encoder.OpCode(ILOpCode.Stloc_0);
        encoder.OpCode(ILOpCode.Ldc_i4_0);
        if (machine != Machine.I386) encoder.OpCode(ILOpCode.Conv_i8);
        encoder.OpCode(ILOpCode.Ldc_i4_0);
        if (machine != Machine.I386) encoder.OpCode(ILOpCode.Conv_i8);
        encoder.OpCode(ILOpCode.Ldc_i4_0);
        if (machine != Machine.I386) encoder.OpCode(ILOpCode.Conv_i8);
        encoder.OpCode(ILOpCode.Ldc_i4_0);
        encoder.Call(messageBoxWRef);
        encoder.OpCode(ILOpCode.Stloc_0);
        encoder.MarkLineNumber(cvFile, 10);
        encoder.OpCode(ILOpCode.Ldloc_0);
        encoder.OpCode(ILOpCode.Ret);

        bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", encoder,
            maxStack: 4, localVariablesSignature: localsSig, attributes: 0,
            debugName: "main");

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }
}

