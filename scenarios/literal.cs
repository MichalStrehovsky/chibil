// Emits literal.obj — equivalent to MSVC's output for:
//   int main() { char* c = "Hello"; return c[0]; }
//
// Run: dotnet run literal.cs
// Link: link.exe /entry:main /subsystem:console literal.obj

#:property Nullable=disable
#:property AllowUnsafeBlocks=true

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

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

        // ─── TypeRefs (only what's needed) ────────────────────────────────
        var valueTypeRef = md.AddTypeReference(mscorlibRef, md.GetOrAddString("System"), md.GetOrAddString("ValueType"));
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

        // ─── TypeDef #2: $ArrayType$$$BY05D (sequential, sealed, size=6) ──
        var arrayType5D = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY05D"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(3), // no fields of its own
            MetadataTokens.MethodDefinitionHandle(2)); // no methods

        md.AddTypeLayout(arrayType5D, 0, 6);
        md.AddCustomAttribute(arrayType5D, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── TypeDef #3: $ArrayType$$$BY06D (sequential, sealed, size=7) ──
        var arrayType6D = md.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.SequentialLayout | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            default,
            md.GetOrAddString("$ArrayType$$$BY06D"),
            valueTypeRef,
            MetadataTokens.FieldDefinitionHandle(3), // no fields of its own
            MetadataTokens.MethodDefinitionHandle(2)); // no methods

        md.AddTypeLayout(arrayType6D, 0, 7);
        md.AddCustomAttribute(arrayType6D, nativeCppCtorRef,
            md.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        // ─── FieldDef #1: ?A0x56407d0c.unnamed-global-0 ("Hello\0") ───────
        var field1SigBuilder = new BlobBuilder();
        new BlobEncoder(field1SigBuilder).Field().Type().Type(arrayType5D, isValueType: true);

        var field1 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0x56407d0c.unnamed-global-0"),
            md.GetOrAddBlob(field1SigBuilder));
        md.AddFieldRelativeVirtualAddress(field1, 0);

        // ─── FieldDef #2: ?A0x56407d0c.unnamed-global-1 ("World!\0") ──────
        var field2SigBuilder = new BlobBuilder();
        new BlobEncoder(field2SigBuilder).Field().Type().Type(arrayType6D, isValueType: true);

        var field2 = md.AddFieldDefinition(
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            md.GetOrAddString("?A0x56407d0c.unnamed-global-1"),
            md.GetOrAddBlob(field2SigBuilder));
        md.AddFieldRelativeVirtualAddress(field2, 0);

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

        // ─── StandaloneSig: locals (int32, int8 modopt(IsSignUnspecifiedByte)* d, int8 modopt(IsSignUnspecifiedByte)* c)
        var localsSigBuilder = new BlobBuilder();
        var localsSigEncoder = new BlobEncoder(localsSigBuilder).LocalVariableSignature(3);

        // Local 0: int32
        localsSigEncoder.AddVariable().Type().Int32();

        // Local 1: int8 modopt(IsSignUnspecifiedByte)* (d)
        var local1Enc = localsSigEncoder.AddVariable().Type();
        local1Enc.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        local1Enc.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        local1Enc.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        local1Enc.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        // Local 2: int8 modopt(IsSignUnspecifiedByte)* (c)
        var local2Enc = localsSigEncoder.AddVariable().Type();
        local2Enc.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        local2Enc.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        local2Enc.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isSignUnspecifiedByteRef));
        local2Enc.Builder.WriteByte((byte)SignatureTypeCode.SByte);

        var localsSig = md.AddStandaloneSignature(md.GetOrAddBlob(localsSigBuilder));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("literal.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(Machine.Arm64, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();
        var dataStreamBuilder = new BlobBuilder();

        // ─── .data section: "Hello\0" + padding + "World!\0" = 15 bytes ──
        dataStreamBuilder.WriteBytes(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00 }); // "Hello\0" at offset 0
        dataStreamBuilder.WriteBytes(new byte[] { 0x00, 0x00 }); // padding to 8-byte alignment
        dataStreamBuilder.WriteBytes(new byte[] { 0x57, 0x6F, 0x72, 0x6C, 0x64, 0x21, 0x00 }); // "World!\0" at offset 8

        // Create COFF symbols for both field data entries BEFORE emitting IL
        symtab.AddDataClrToken("$SG8557", field1, 2, 0, out _);
        symtab.AddDataClrToken("$SG8558", field2, 2, 8, out _);

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);

        // S_OBJNAME + S_COMPILE3
        string objPath = Path.GetFullPath("literal.obj");
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: CodeViewMachine.Arm64,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        // Source file with SHA-256 checksum
        string sourceFile = Path.GetFullPath("literal.c");
        byte[] sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for main ─────────────────────────────────────────────
        var encoder = new RelocatableInstructionEncoder(
            new BlobBuilder(),
            new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(),
            new CodeViewLineNumberBuilder());

        encoder.MarkLineNumber(cvFile, 5);
        encoder.OpCode(ILOpCode.Ldc_i4_0);       // IL_0000
        encoder.OpCode(ILOpCode.Stloc_0);         // IL_0001
        encoder.OpCode(ILOpCode.Ldsflda);          // IL_0002
        encoder.Token(field1);
        encoder.OpCode(ILOpCode.Stloc_2);         // IL_0007: c
        encoder.MarkLineNumber(cvFile, 6);
        encoder.OpCode(ILOpCode.Ldsflda);          // IL_0008
        encoder.Token(field2);
        encoder.OpCode(ILOpCode.Stloc_1);         // IL_000D: d
        encoder.MarkLineNumber(cvFile, 7);
        encoder.OpCode(ILOpCode.Ldloc_2);         // IL_000E
        encoder.OpCode(ILOpCode.Ldc_i4_1);        // IL_000F
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0010
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0011
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0012
        encoder.OpCode(ILOpCode.Mul);              // IL_0013
        encoder.OpCode(ILOpCode.Add);              // IL_0014
        encoder.OpCode(ILOpCode.Ldind_i1);         // IL_0015
        encoder.OpCode(ILOpCode.Ldloc_1);         // IL_0016
        encoder.OpCode(ILOpCode.Ldc_i4_1);        // IL_0017
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_0018
        encoder.OpCode(ILOpCode.Ldc_i4_0);        // IL_0019
        encoder.OpCode(ILOpCode.Conv_i8);          // IL_001A
        encoder.OpCode(ILOpCode.Mul);              // IL_001B
        encoder.OpCode(ILOpCode.Add);              // IL_001C
        encoder.OpCode(ILOpCode.Ldind_i1);         // IL_001D
        encoder.OpCode(ILOpCode.Add);              // IL_001E
        encoder.OpCode(ILOpCode.Stloc_0);         // IL_001F
        encoder.MarkLineNumber(cvFile, 8);
        encoder.OpCode(ILOpCode.Ldloc_0);         // IL_0020
        encoder.OpCode(ILOpCode.Ret);              // IL_0021

        // Local variable info for S_MANSLOT
        var localSlots = new[] {
            new CodeViewManSlot(1, MetadataTokens.GetToken(localsSig), "d"),
            new CodeViewManSlot(2, MetadataTokens.GetToken(localsSig), "c"),
        };

        bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", encoder,
            maxStack: 4, localVariablesSignature: localsSig, attributes: 0,
            localSlots: localSlots, debugName: "main");

        // ─── Build COFF ───────────────────────────────────────────────────
        var coffBuilder = new ManagedCoffBuilder(coffHeader, new MetadataRootBuilder(md), symtab, codeviewSymbols,
            ilStreamBuilder, ilRelocBuilder, dataStreamBuilder);

        // ─── Serialize ────────────────────────────────────────────────────
        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        using var fs = File.Create("literal.obj");
        output.WriteContentTo(fs);

        Console.WriteLine("literal.obj created");
    }
}
