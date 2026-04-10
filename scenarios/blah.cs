// This will generate a blah.obj file that contains a single method.
// You can inspect the managed content of the OBJ with ildasm (the GUI doesn't work for obj files, but
// you can run it from the command line and specify /out= to disassemble).
//
// Run "link.exe /debug blah.obj /entry:MyMethod /subsystem:console" to generate an EXE file.
//
// There's also debug information. You can create a fake il.il file with a couple
// irrelevant lines in it and step through the fake file in a debugger while debugging the EXE.
//
// You can inspect the debug info with cvdump.exe from https://github.com/Microsoft/microsoft-pdb/tree/master/cvdump.
//

#:property Nullable=disable
#:property AllowUnsafeBlocks=true

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        var mdBuilder = new MetadataBuilder();

        var h = mdBuilder.GetOrAddString("Hello");

        mdBuilder.AddTypeDefinition(TypeAttributes.Class, default, mdBuilder.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        BlobBuilder sigBuilder = new BlobBuilder();
        BlobEncoder sigBlobEncoder = new BlobEncoder(sigBuilder);
        var sigEncoder = sigBlobEncoder.MethodSignature();
        sigEncoder.Parameters(0, out var rtEnc, out var parEnc);
        rtEnc.Type().Int32();

        var mdHandle = mdBuilder.AddMethodDefinition(
            MethodAttributes.Static | MethodAttributes.Public,
            MethodImplAttributes.Managed,
            h,
            mdBuilder.GetOrAddBlob(sigBuilder),
            0,
            MetadataTokens.ParameterHandle(1));

        mdBuilder.AddModule(0,
            mdBuilder.GetOrAddString("blah.dll"),
            mdBuilder.GetOrAddGuid(Guid.Empty),
            mdBuilder.GetOrAddGuid(Guid.Empty),
            mdBuilder.GetOrAddGuid(Guid.Empty));

        var coffHeaderBuilder = new CoffHeaderBuilder(Machine.I386, 0);

        var symtab = new ManagedCoffSymbolTableBuilder(ManagedCoffBuilder.ClrTextSectionNumber, ObjectFeatures.PureMsil);

        var codeviewSymbols = new CodeViewSymbolBuilder(coffHeaderBuilder);

        CodeViewFileHandle file1 = codeviewSymbols.GetOrAddFile("il.il");

        var instructionStreamBuilder = new BlobBuilder();
        var relocationStreamBuilder = new BlobBuilder();

        var instructionStreamEncoder = new RelocatableMethodBodyStreamEncoder(instructionStreamBuilder, relocationStreamBuilder, symtab, coffHeaderBuilder, codeviewSymbols);

        var encoder = new RelocatableInstructionEncoder(
            new BlobBuilder(),
            new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(),
            new CodeViewLineNumberBuilder());

        var lbl = encoder.DefineLabel();

        encoder.MarkLineNumber(file1, 1);
        encoder.LoadConstantI4(0);
        encoder.Branch(ILOpCode.Brfalse, lbl);
        encoder.MarkLineNumber(file1, 2);
        encoder.Call(mdHandle);
        encoder.OpCode(ILOpCode.Pop);
        encoder.MarkLineNumber(file1, 3);
        encoder.MarkLabel(lbl);
        encoder.LoadConstantI4(0xBABE);
        encoder.OpCode(ILOpCode.Ret);
        

        instructionStreamEncoder.AddMethodBody(mdHandle, "MyMethod", encoder);

        var root = new MetadataRootBuilder(mdBuilder);

        var peB = new ManagedCoffBuilder(coffHeaderBuilder, root, symtab, codeviewSymbols, instructionStreamBuilder, relocationStreamBuilder);

        var o = new BlobBuilder();

        peB.Serialize(o);

        using var fs = File.Create("blah.obj");
        o.WriteContentTo(fs);
    }
}
