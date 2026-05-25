// Phase E — NEP thunk emission for UnmanagedExport methods.

using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    public void EmitNepThunks(
        Machine machine,
        CoffHeaderBuilder coffHeader,
        ManagedCoffSymbolTableBuilder symtab,
        BlobBuilder dataStream, BlobBuilder dataRelocs,
        BlobBuilder nepStream, BlobBuilder nepRelocs,
        BlobBuilder ilFixupStream, BlobBuilder ilFixupRelocs)
    {
        for (int inputRow = 1; inputRow < _methodInfo.Length; inputRow++)
        {
            if (_methodInfo[inputRow].Disposition != MethodDisposition.Regular) continue;
            if (!_methodInfo[inputRow].UnmanagedExport) continue;

            var inputH = MetadataTokens.MethodDefinitionHandle(inputRow);
            var outputH = TokenMap.MapMethodDef(inputH);
            int outRow = MetadataTokens.GetRowNumber(outputH);
            int methodToken = MetadataTokens.GetToken(outputH);

            string mangled = _outputMethodDecoratedNames[outRow];
            string bareName = _outputMethodBareNames[outRow];

            ClrIjw.EmitNepMachinery(
                machine, _is32, _ptrSize, _symPrefix,
                coffHeader, symtab,
                dataStream, dataRelocs,
                nepStream, nepRelocs,
                ilFixupStream, ilFixupRelocs,
                methodToken, bareName, mangled);
        }
    }
}
