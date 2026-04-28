using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

public class AtomicTest
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
            Path.Combine(AppContext.BaseDirectory, "reference", "atomic", refDir, "atomic.obj"));
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
            md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            md.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }),
            default,
            md.GetOrAddBlob(mscorlibHash));

        // ─── TypeRefs ─────────────────────────────────────────────────────
        var interlockedRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Threading"), md.GetOrAddString("Interlocked"));
        var isVolatileRef = md.AddTypeReference(mscorlibRef,
            md.GetOrAddString("System.Runtime.CompilerServices"), md.GetOrAddString("IsVolatile"));

        // ─── MemberRef: Interlocked::Exchange(ref int32, int32) -> int32 ──
        var exchangeSig = new BlobBuilder();
        new BlobEncoder(exchangeSig).MethodSignature()
            .Parameters(2, out var exchRetEnc, out var exchParEnc);
        exchRetEnc.Type().Int32();
        var ep1 = exchParEnc.AddParameter().Type();
        ep1.Builder.WriteByte((byte)SignatureTypeCode.ByReference);
        ep1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        exchParEnc.AddParameter().Type().Int32();

        var exchangeRef = md.AddMemberReference(interlockedRef,
            md.GetOrAddString("Exchange"), md.GetOrAddBlob(exchangeSig));

        // ─── MemberRef: Interlocked::CompareExchange(ref int32, int32, int32) -> int32 ──
        var cmpExchSig = new BlobBuilder();
        new BlobEncoder(cmpExchSig).MethodSignature()
            .Parameters(3, out var cmpRetEnc, out var cmpParEnc);
        cmpRetEnc.Type().Int32();
        var cp1 = cmpParEnc.AddParameter().Type();
        cp1.Builder.WriteByte((byte)SignatureTypeCode.ByReference);
        cp1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        cmpParEnc.AddParameter().Type().Int32();
        cmpParEnc.AddParameter().Type().Int32();

        var compareExchangeRef = md.AddMemberReference(interlockedRef,
            md.GetOrAddString("CompareExchange"), md.GetOrAddBlob(cmpExchSig));

        // ─── TypeDef #1: <Module> ─────────────────────────────────────────
        md.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // ─── MethodDef #1: atomic_xchg(volatile int32*, int32) -> int32 ───
        var xchgSig = new BlobBuilder();
        new BlobEncoder(xchgSig).MethodSignature()
            .Parameters(2, out var xchgRetEnc, out var xchgParEnc);
        xchgRetEnc.Type().Int32();
        // Param 1: Ptr CMOD_REQD(IsVolatile) I4
        var xp1 = xchgParEnc.AddParameter().Type();
        xp1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        xp1.Builder.WriteByte((byte)SignatureTypeCode.RequiredModifier);
        xp1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isVolatileRef));
        xp1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        // Param 2: int32
        xchgParEnc.AddParameter().Type().Int32();

        var xchgMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("atomic_xchg"),
            md.GetOrAddBlob(xchgSig),
            0,
            MetadataTokens.ParameterHandle(1));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("val"), 2);

        // Locals for atomic_xchg: 1 x int32
        var xchgLocalsSig = new BlobBuilder();
        new BlobEncoder(xchgLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var xchgLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(xchgLocalsSig));

        // ─── MethodDef #2: atomic_cas(volatile int32*, int32, int32) -> int32 ──
        var casSig = new BlobBuilder();
        new BlobEncoder(casSig).MethodSignature()
            .Parameters(3, out var casRetEnc, out var casParEnc);
        casRetEnc.Type().Int32();
        // Param 1: Ptr CMOD_REQD(IsVolatile) I4
        var casP1 = casParEnc.AddParameter().Type();
        casP1.Builder.WriteByte((byte)SignatureTypeCode.Pointer);
        casP1.Builder.WriteByte((byte)SignatureTypeCode.RequiredModifier);
        casP1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isVolatileRef));
        casP1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        // Param 2 and 3: int32
        casParEnc.AddParameter().Type().Int32();
        casParEnc.AddParameter().Type().Int32();

        var casMethod = md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static | (MethodAttributes)0x0008,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            md.GetOrAddString("atomic_cas"),
            md.GetOrAddBlob(casSig),
            0,
            MetadataTokens.ParameterHandle(3));

        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("p"), 1);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("expected"), 2);
        md.AddParameter(ParameterAttributes.None, md.GetOrAddString("desired"), 3);

        // Locals for atomic_cas: 1 x int32 (same shape as xchg)
        var casLocalsSig = new BlobBuilder();
        new BlobEncoder(casLocalsSig).LocalVariableSignature(1)
            .AddVariable().Type().Int32();
        var casLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(casLocalsSig));

        // ─── MethodDef #3: main() -> int32 ────────────────────────────────
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
            MetadataTokens.ParameterHandle(6));

        // Locals for main: int32 (V_0), modreq(IsVolatile) int32 (V_1:v)
        var mainLocalsSig = new BlobBuilder();
        var mainLocalsEnc = new BlobEncoder(mainLocalsSig).LocalVariableSignature(2);
        mainLocalsEnc.AddVariable().Type().Int32();
        // V_1: modreq(IsVolatile) int32
        var mainLocV1 = mainLocalsEnc.AddVariable().Type();
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.RequiredModifier);
        mainLocV1.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(isVolatileRef));
        mainLocV1.Builder.WriteByte((byte)SignatureTypeCode.Int32);
        var mainLocalsSigHandle = md.AddStandaloneSignature(md.GetOrAddBlob(mainLocalsSig));

        // ─── Module ───────────────────────────────────────────────────────
        md.AddModule(0,
            md.GetOrAddString("atomic.obj"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        // ─── COFF structure ───────────────────────────────────────────────
        var coffHeader = new CoffHeaderBuilder(machine, 0);
        var symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.PureMsil);

        var ilStreamBuilder = new BlobBuilder();
        var ilRelocBuilder = new BlobBuilder();

        // ─── CodeView debug info ──────────────────────────────────────────
        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeader);
        string objPath = "atomic.obj";
        codeviewSymbols.AddObjNameAndCompile3(objPath,
            language: CodeViewLanguage.C,
            machine: cvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35728,
            beMajor: 19, beMinor: 50, beBuild: 35728,
            "Microsoft (R) Optimizing Compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        string sourceFile = Path.Combine(AppContext.BaseDirectory, "atomic.c");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
        CodeViewFileHandle cvFile = codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);

        var bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            ilStreamBuilder, ilRelocBuilder, symtab, coffHeader, codeviewSymbols);

        // ─── Emit IL for atomic_xchg ──────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 11);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0001
            enc.Call(exchangeRef);                // IL_0002: call Interlocked::Exchange
            enc.StoreLocal(0);                    // IL_0007
            enc.MarkLineNumber(cvFile, 12);
            enc.LoadLocal(0);                     // IL_0008
            enc.OpCode(ILOpCode.Ret);             // IL_0009

            bodyEncoder.AddMethodBody(xchgMethod, "?atomic_xchg@@$$J0YMHPCHH@Z", enc,
                maxStack: 2, localVariablesSignature: xchgLocalsSigHandle, attributes: 0,
                debugName: "atomic_xchg");
        }

        // ─── Emit IL for atomic_cas ───────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 16);
            enc.OpCode(ILOpCode.Ldarg_0);         // IL_0000
            enc.OpCode(ILOpCode.Ldarg_2);         // IL_0001: desired
            enc.OpCode(ILOpCode.Ldarg_1);         // IL_0002: expected
            enc.Call(compareExchangeRef);          // IL_0003: call Interlocked::CompareExchange
            enc.StoreLocal(0);                    // IL_0008
            enc.MarkLineNumber(cvFile, 17);
            enc.LoadLocal(0);                     // IL_0009
            enc.OpCode(ILOpCode.Ret);             // IL_000A

            bodyEncoder.AddMethodBody(casMethod, "?atomic_cas@@$$J0YMHPCHHH@Z", enc,
                maxStack: 3, localVariablesSignature: casLocalsSigHandle, attributes: 0,
                debugName: "atomic_cas");
        }

        // ─── Emit IL for main ─────────────────────────────────────────────
        {
            var enc = new RelocatableInstructionEncoder(
                new BlobBuilder(), new MethodRelocationBuilder(),
                new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

            enc.MarkLineNumber(cvFile, 21);
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0000
            enc.StoreLocal(0);                    // IL_0001
            enc.OpCode(ILOpCode.Ldc_i4_0);        // IL_0002
            enc.StoreLocal(1);                    // IL_0003
            enc.MarkLineNumber(cvFile, 22);
            enc.LoadLocalAddress(1);              // IL_0004: ldloca.s V_1
            enc.LoadConstantI4(42);               // IL_0006: ldc.i4.s 42
            enc.Call(xchgMethod);                 // IL_0008: call atomic_xchg
            enc.OpCode(ILOpCode.Pop);             // IL_000D
            enc.MarkLineNumber(cvFile, 23);
            enc.LoadLocalAddress(1);              // IL_000E: ldloca.s V_1
            enc.LoadConstantI4(42);               // IL_0010: ldc.i4.s 42
            enc.LoadConstantI4(100);              // IL_0012: ldc.i4.s 100
            enc.Call(casMethod);                  // IL_0014: call atomic_cas
            enc.OpCode(ILOpCode.Pop);             // IL_0019
            enc.MarkLineNumber(cvFile, 24);
            enc.LoadLocal(1);                     // IL_001A: ldloc.1
            enc.StoreLocal(0);                    // IL_001B
            enc.MarkLineNumber(cvFile, 25);
            enc.LoadLocal(0);                     // IL_001C: ldloc.0
            enc.OpCode(ILOpCode.Ret);             // IL_001D

            var mainLocalSlots = new[] {
                new CodeViewManSlot(1, MetadataTokens.GetToken(mainLocalsSigHandle), "v"),
            };

            bodyEncoder.AddMethodBody(mainMethod, "?main@@$$J0YMHXZ", enc,
                maxStack: 3, localVariablesSignature: mainLocalsSigHandle, attributes: 0,
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
