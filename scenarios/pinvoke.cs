// Emits pinvoke.obj — equivalent to MSVC's output for:
//   int __stdcall MessageBoxW(void* a, void* b, void* c, int d);
//   int main() { return MessageBoxW(0, 0, 0, 0); }
//
// Run: dotnet run pinvoke.cs
// Link: link.exe /entry:main /subsystem:console /libpath:... user32.lib pinvoke.obj

#:property Nullable=disable
#:property AllowUnsafeBlocks=true

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        var md = new MetadataBuilder();

        // ─── AssemblyRef: mscorlib ────────────────────────────────────────
        var mscorlibRef = md.AddAssemblyReference(
            md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            md.GetOrAddBlob(new byte[] {
                0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC,
                0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49,
                0x13, 0xC1, 0x99, 0xCE }));

        // ─── TypeRefs (only what's actually referenced) ───────────────────
        var callConvCdeclRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("CallConvCdecl"));
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

        // ─── MethodDef: main ──────────────────────────────────────────────
        var methodSigBuilder = new BlobBuilder();
        new BlobEncoder(methodSigBuilder).MethodSignature()
            .Parameters(0, out var rtEnc, out var parEnc);
        rtEnc.Type().Int32();

        var mainMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008 /* UnmanagedExport */,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("main"),
            md.GetOrAddBlob(methodSigBuilder),
            0,
            MetadataTokens.ParameterHandle(1));

        // ─── MemberRef #1: MessageBoxW on <Module> ────────────────────────
        // Signature: default, returns CMOD_OPT CallConvCdecl I4, params: Ptr Void × 3, I4
        var msgBoxSigBuilder = new BlobBuilder();
        var msgBoxSigEnc = new BlobEncoder(msgBoxSigBuilder).MethodSignature();
        msgBoxSigEnc.Parameters(4, out var msgBoxRetEnc, out var msgBoxParEnc);

        // Return type: CMOD_OPT CallConvCdecl I4
        msgBoxRetEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        msgBoxRetEnc.Type().Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        msgBoxRetEnc.Type().Builder.WriteByte((byte)SignatureTypeCode.Int32);

        // 4 parameters: Ptr Void, Ptr Void, Ptr Void, I4
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Void);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Void);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        msgBoxParEnc.AddParameter().Type().Builder.WriteByte((byte)SignatureTypeCode.Void);
        msgBoxParEnc.AddParameter().Type().Int32();

        var messageBoxWRef = md.AddMemberReference(moduleType,
            md.GetOrAddString("MessageBoxW"), md.GetOrAddBlob(msgBoxSigBuilder));

        // ─── MemberRef #2: DecoratedNameAttribute::.ctor(String) ──────────
        var decNameCtorSigBuilder = new BlobBuilder();
        new BlobEncoder(decNameCtorSigBuilder).MethodSignature(SignatureCallingConvention.Default, 0, true)
            .Parameters(1, out var decNameRetEnc, out var decNameParEnc);
        decNameRetEnc.Void();
        decNameParEnc.AddParameter().Type().String();

        var decNameCtorRef = md.AddMemberReference(decoratedNameAttrRef,
            md.GetOrAddString(".ctor"), md.GetOrAddBlob(decNameCtorSigBuilder));

        // ─── CustomAttribute: DecoratedNameAttribute on MessageBoxW ───────
        var customAttrValueBuilder = new BlobBuilder();
        customAttrValueBuilder.WriteUInt16(0x0001); // prolog
        string decoratedName = "?MessageBoxW@@$$J0YAHPEAX00H@Z";
        customAttrValueBuilder.WriteSerializedString(decoratedName);
        customAttrValueBuilder.WriteUInt16(0x0000); // no named args

        md.AddCustomAttribute(messageBoxWRef, decNameCtorRef,
            md.GetOrAddBlob(customAttrValueBuilder));

        // ─── StandaloneSig: locals (int32) ────────────────────────────────
        var localsSigBuilder = new BlobBuilder();
        new BlobEncoder(localsSigBuilder).LocalVariableSignature(1)
            .AddVariable().Type().Int32();

        var localsSig = md.AddStandaloneSignature(md.GetOrAddBlob(localsSigBuilder));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("pinvoke.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(Machine.Arm64, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // Add external COFF symbol for MessageBoxW BEFORE emitting IL
        symtab.AddExternalClrToken("?MessageBoxW@@$$J0YAHPEAX00H@Z", messageBoxWRef);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, null);

        // ─── Emit IL for main ─────────────────────────────────────────────
        var encoder = new RelocatableInstructionEncoder(
            new BlobBuilder(),
            new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(),
            null);

        encoder.OpCode(ILOpCode.Ldc_i4_0);       // IL_0000
        encoder.OpCode(ILOpCode.Stloc_0);         // IL_0001
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0002
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0003
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0004
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0005
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0006
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0007
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0008
        encoder.Call(messageBoxWRef);              // IL_0009
        encoder.OpCode(ILOpCode.Stloc_0);         // IL_000E
        encoder.OpCode(ILOpCode.Ldloc_0);         // IL_000F
        encoder.OpCode(ILOpCode.Ret);              // IL_0010

        bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", encoder,
            maxStack: 4, localVariablesSignature: localsSig, attributes: 0);

        // ─── Build COFF & Serialize ───────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, null,
            ilStreamBuilder, ilRelocBuilder);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        using var fs = File.Create("pinvoke.obj");
        output.WriteContentTo(fs);

        Console.WriteLine("pinvoke.obj created");
    }
}
