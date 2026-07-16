// Phase D — IL body emission. Registers typed reference-token symbols, emits
// each surviving method body, then finalizes decorated external names after
// local body symbols are known to be definitions.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Coff;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    // COFF symbol name (decorated name) per *output* MethodDef row.
    private string[] _outputMethodDecoratedNames;
    // Bare native name per output MethodDef row (used for UnmanagedExport NEP alias).
    private string[] _outputMethodBareNames;

    public void EmitMethodBodies(
        ManagedCoffSymbolTableBuilder symtab,
        CoffSectionWithContentBuilder ilSection,
        CoffHeaderBuilder coffHeader,
        PEReader peReader)
    {
        // Reference tokens are always undefined in this object. The linker
        // resolves local MemberRefs to their definitions just like references
        // imported from another object.
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            var inputH = MetadataTokens.MemberReferenceHandle(r);
            var outputH = TokenMap.MapMemberRef(inputH);
            var symbolType = _reader.GetMemberReference(inputH).GetKind() == MemberReferenceKind.Method
                ? CoffSymbolType.Function
                : CoffSymbolType.Null;
            symtab.GetOrAddUndefinedClrTokenSymbol(outputH, symbolType);
        }
        foreach (int fieldRow in _localFieldRefSourceRows)
        {
            symtab.GetOrAddUndefinedClrTokenSymbol(
                TokenMap.MapFieldReference(MetadataTokens.FieldDefinitionHandle(fieldRow)),
                CoffSymbolType.Null);
        }
        foreach (int methodRow in _localMethodRefSourceRows)
        {
            symtab.GetOrAddUndefinedClrTokenSymbol(
                TokenMap.MapMethodDefReference(MetadataTokens.MethodDefinitionHandle(methodRow)),
                CoffSymbolType.Function);
        }

        for (int inputRow = 1; inputRow < _methodInfo.Length; inputRow++)
        {
            if (_methodInfo[inputRow].Disposition != MethodDisposition.Regular) continue;
            var inputH = MetadataTokens.MethodDefinitionHandle(inputRow);
            var md = _reader.GetMethodDefinition(inputH);
            if (md.RelativeVirtualAddress == 0) continue; // abstract/runtime/no body

            EmitBody(symtab, ilSection, coffHeader, peReader, inputRow, md);
        }

        // Decorated names are added only after body symbols. For a regular
        // local method GetOrAdd observes the existing definition; only true
        // externs remain undefined.
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            var inputH = MetadataTokens.MemberReferenceHandle(r);
            var mr = _reader.GetMemberReference(inputH);
            if (mr.GetKind() != MemberReferenceKind.Method) continue;

            EntityHandle outParent = mr.Parent.Kind == HandleKind.MethodDefinition
                ? TokenMap.MapMethodDef((MethodDefinitionHandle)mr.Parent)
                : TokenMap.MapReference(mr.Parent);
            if (outParent.Kind != HandleKind.TypeDefinition ||
                MetadataTokens.GetRowNumber(outParent) != OutputModuleTypeDefRow)
                continue;

            string name = ComputeMemberRefSymbolName(inputH);
            if (name != null)
                symtab.AddUndefinedExternalSymbol(name, CoffSymbolType.Function);
        }
        foreach (int methodRow in _localMethodRefSourceRows)
        {
            var md = _reader.GetMethodDefinition(
                MetadataTokens.MethodDefinitionHandle(methodRow));
            if (_methodInfo[methodRow].Disposition == MethodDisposition.Regular &&
                md.RelativeVirtualAddress == 0)
                continue;
            symtab.AddUndefinedExternalSymbol(
                _methodReferenceDecoratedNames[methodRow],
                CoffSymbolType.Function);
        }
    }

    private void EmitBody(
        ManagedCoffSymbolTableBuilder symtab,
        CoffSectionWithContentBuilder ilSection,
        CoffHeaderBuilder coffHeader,
        PEReader peReader,
        int inputMethodRow,
        MethodDefinition md)
    {
        var body = peReader.GetMethodBody(md.RelativeVirtualAddress);

        var outputMethodH = TokenMap.MapMethodDef(MetadataTokens.MethodDefinitionHandle(inputMethodRow));
        int outRow = MetadataTokens.GetRowNumber(outputMethodH);
        string symName = _outputMethodDecoratedNames[outRow];

        // The body bytes (header + IL + any "more sections" EH table) are copied
        // verbatim — every token-operand slot is exactly 4 bytes, so branch and EH
        // offsets are unchanged. We only patch token slots via CLR-token relocations,
        // which the linker resolves by the token value.
        byte[] bytes = peReader.GetSectionData(md.RelativeVirtualAddress).GetReader().ReadBytes(body.Size);

        // Fat header (low 2 bits = 3) is 12 bytes and must be 4-aligned; its local-var
        // sig token lives at offset 8 and code size at offset 4. Tiny header (low 2
        // bits = 2) is 1 byte with the code size in the upper 6 bits.
        bool isFat = (bytes[0] & 3) == 3;
        int ilStart = isFat ? 12 : 1;
        int ilEnd = ilStart + (isFat ? System.BitConverter.ToInt32(bytes, 4) : bytes[0] >> 2);

        if (isFat)
            ilSection.Content.Align(4);

        int bodyOffset = ilSection.Content.Count;
        symtab.AddFunctionClrToken(ilSection, symName, outputMethodH, bodyOffset, out _);

        var relocEncoder = new ManagedCoffRelocationEncoder(coffHeader, ilSection.Relocations, symtab);

        // Remap the token in the 4-byte slot at `offset` and record a CLR-token
        // relocation so the linker resolves it. UserString (#US) tokens are table
        // 0x70; everything else is a metadata token.
        static void RemapToken(byte[] bytes, int offset, int baseOffset, TokenMap map, ManagedCoffRelocationEncoder reloc)
        {
            int token = System.BitConverter.ToInt32(bytes, offset);
            if (token == 0) return; // empty slot (e.g. fat header with no locals)
            new BlobWriter(bytes, offset, 4).WriteInt32(0);
            Handle mapped = (token >> 24) == 0x70
                ? map.MapUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF))
                : map.MapReference(MetadataTokens.EntityHandle(token));
            reloc.AddClrRelocation(baseOffset + offset, mapped);
        }

        if (isFat)
            RemapToken(bytes, 8, bodyOffset, TokenMap, relocEncoder);

        for (int pos = ilStart; pos < ilEnd;)
        {
            int b = bytes[pos++];
            ushort opcode = b == 0xFE ? (ushort)(0x100 | bytes[pos++]) : (ushort)b;

            byte entry = ILOpcodeHelper.GetEntry(opcode);
            switch (entry)
            {
                case ILOpcodeHelper.Invalid:
                    throw new BadImageFormatException($"Invalid IL opcode 0x{opcode:X4}.");
                case ILOpcodeHelper.VariableSize:
                    // The only variable-size opcode is `switch`: <uint32 n> <int32>{n}.
                    pos += 4 + 4 * System.BitConverter.ToInt32(bytes, pos);
                    break;
                case ILOpcodeHelper.Token:
                    RemapToken(bytes, pos, bodyOffset, TokenMap, relocEncoder);
                    pos += 4;
                    break;
                default:
                    pos += entry - (opcode > 0xFF ? 2 : 1); // skip fixed-size operand
                    break;
            }
        }

        // "More sections" exception table (only present with a fat header). A catch
        // clause's ClassToken slot is a metadata token; finally/fault/filter reuse
        // that slot for 0 or a filter offset, so only catch (flags 0) is remapped.
        if (isFat && (bytes[0] & 8) != 0)
        {
            int sect = (ilEnd + 3) & ~3;
            bool fatSect = (bytes[sect] & 0x40) != 0;
            int dataSize = fatSect ? System.BitConverter.ToInt32(bytes, sect) >> 8 : bytes[sect + 1];
            int clauseSize = fatSect ? 24 : 12;
            int classTokenOffset = fatSect ? 20 : 8;
            for (int clause = sect + 4; clause + clauseSize <= sect + dataSize; clause += clauseSize)
            {
                int flags = fatSect ? System.BitConverter.ToInt32(bytes, clause) : System.BitConverter.ToUInt16(bytes, clause);
                if (flags == 0) // catch: ClassToken is a metadata token
                    RemapToken(bytes, clause + classTokenOffset, bodyOffset, TokenMap, relocEncoder);
            }
        }

        ilSection.Content.WriteBytes(bytes);
    }

    /// <summary>
    /// Computes the COFF symbol name for a function body.
    /// Precedence: [DecoratedName] explicit value > MSVC auto-mangler > synthetic.
    /// </summary>
    private string ComputeFunctionSymbolName(int inputMethodRow)
    {
        string explicitName = _methodInfo[inputMethodRow].DecoratedName;
        if (explicitName != null)
            return ApplyX86UnderscoreRule(explicitName);

        var mh = MetadataTokens.MethodDefinitionHandle(inputMethodRow);
        try
        {
            return _mangler.MangleMethod(mh, _methodInjections[inputMethodRow]);
        }
        catch (NotSupportedException)
        {
            // Method has a signature we can't auto-mangle (e.g. managed
            // reference types). Synthesize a unique name. Such methods are
            // not externally linker-callable, but the COFF symbol still
            // needs *some* unique name to anchor the body.
            var md = _reader.GetMethodDefinition(mh);
            string name = _reader.GetString(md.Name);
            return $"$asm2obj.{name}.{inputMethodRow}";
        }
    }

    private string ComputeBareName(int inputMethodRow)
    {
        var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(inputMethodRow));
        return _reader.GetString(md.Name);
    }

    private string ComputeMemberRefSymbolName(MemberReferenceHandle inputH)
    {
        var mr = _reader.GetMemberReference(inputH);
        if (mr.GetKind() != MemberReferenceKind.Method) return null;

        // Check for [DecoratedName] on the MemberRef itself.
        // CustomAttributeHandleCollection isn't directly exposed on MemberRef
        // in older SRM; we iterate the CustomAttribute table.
        foreach (var caH in _reader.CustomAttributes)
        {
            var ca = _reader.GetCustomAttribute(caH);
            if (ca.Parent != (EntityHandle)inputH) continue;
            if (GetCustomAttributeTypeFullName(caH) != DecoratedNameAttrFullName) continue;
            _customAttrSkip[MetadataTokens.GetRowNumber(caH)] = true;
            var blobReader = _reader.GetBlobReader(ca.Value);
            ushort prolog = blobReader.ReadUInt16();
            if (prolog != 0x0001) continue;
            string s = blobReader.ReadSerializedString();
            return ApplyX86UnderscoreRule(s);
        }

        // No DecoratedName: auto-mangle.
        try
        {
            return _mangler.MangleMemberRef(inputH);
        }
        catch (NotSupportedException)
        {
            // Method involves managed reference types; can't auto-mangle.
            // Skip — the MemberRef will only be referenceable via its CLR
            // token in IL, not by external name. This is acceptable for
            // mscorlib methods called from managed IL.
            return null;
        }
    }

    /// <summary>
    /// On x86, link.exe expects plain C symbols (no <c>?</c> prefix) to start
    /// with an underscore. Auto-applies the prefix when DecoratedName-string
    /// doesn't already use C++ decoration.
    /// </summary>
    private string ApplyX86UnderscoreRule(string name)
    {
        if (!_is32) return name;
        if (name.StartsWith("?")) return name; // already decorated
        if (name.StartsWith("_")) return name; // already prefixed
        return "_" + name;
    }
}
