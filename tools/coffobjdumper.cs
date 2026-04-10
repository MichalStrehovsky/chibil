// COFF Object IL Dumper
//
// Reads a managed COFF .obj file and dumps method bodies with decoded IL instructions.
// In COFF .obj files, method RVAs in metadata are 0 — the actual IL is in the .text$mn
// section and tokens in IL operands are 0 (resolved via COFF relocations).
//
// This tool:
//   1. Parses the COFF header, section headers, symbol table, and relocations
//   2. Finds .text$mn (IL method bodies) and .cormeta (metadata)
//   3. Applies CLR token relocations to recover actual token values in IL
//   4. Parses method bodies sequentially and disassembles IL
//
// Usage: dotnet run coffobjdumper.cs <path-to-obj-file>

#:property Nullable=disable
#:property AllowUnsafeBlocks=true

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

// ─── COFF structures ──────────────────────────────────────────────────────────

struct CoffFileHeader
{
    public ushort Machine;
    public ushort NumberOfSections;
    public uint TimeDateStamp;
    public uint PointerToSymbolTable;
    public uint NumberOfSymbols;
    public ushort SizeOfOptionalHeader;
    public ushort Characteristics;

    public static CoffFileHeader Read(ReadOnlySpan<byte> data)
    {
        return new CoffFileHeader
        {
            Machine = BinaryPrimitives.ReadUInt16LittleEndian(data),
            NumberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            TimeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            PointerToSymbolTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            NumberOfSymbols = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            Characteristics = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
        };
    }
}

struct CoffSectionHeader
{
    public string Name;
    public uint VirtualSize;
    public uint VirtualAddress;
    public uint SizeOfRawData;
    public uint PointerToRawData;
    public uint PointerToRelocations;
    public uint PointerToLineNumbers;
    public ushort NumberOfRelocations;
    public ushort NumberOfLineNumbers;
    public uint Characteristics;

    public static CoffSectionHeader Read(ReadOnlySpan<byte> data)
    {
        string name = Encoding.UTF8.GetString(data[..8]).TrimEnd('\0');
        return new CoffSectionHeader
        {
            Name = name,
            VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            PointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            PointerToRelocations = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            PointerToLineNumbers = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            NumberOfRelocations = BinaryPrimitives.ReadUInt16LittleEndian(data[32..]),
            NumberOfLineNumbers = BinaryPrimitives.ReadUInt16LittleEndian(data[34..]),
            Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
        };
    }
}

struct CoffRelocation
{
    public uint VirtualAddress; // offset within section
    public uint SymbolTableIndex;
    public ushort Type;

    public static CoffRelocation Read(ReadOnlySpan<byte> data)
    {
        return new CoffRelocation
        {
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data),
            SymbolTableIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            Type = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
        };
    }
}

struct CoffSymbol
{
    public string Name;
    public uint Value;
    public short SectionNumber;
    public ushort Type;
    public byte StorageClass;
    public byte NumberOfAuxSymbols;
}

// ─── COFF File Parser ─────────────────────────────────────────────────────────

class CoffFile
{
    public CoffFileHeader Header;
    public CoffSectionHeader[] Sections;
    public CoffSymbol[] Symbols;
    public byte[] FileData;

    const int CoffHeaderSize = 20;
    const int SectionHeaderSize = 40;
    const int SymbolSize = 18;
    const int RelocationSize = 10;

    const ushort IMAGE_REL_I386_TOKEN = 0x000C;
    const ushort IMAGE_REL_AMD64_TOKEN = 0x000D;
    const byte IMAGE_SYM_CLASS_CLR_TOKEN = 107;

    public static CoffFile Parse(byte[] data)
    {
        var coff = new CoffFile { FileData = data };
        coff.Header = CoffFileHeader.Read(data);

        // Parse section headers (immediately after COFF header + optional header)
        int sectionOffset = CoffHeaderSize + coff.Header.SizeOfOptionalHeader;
        coff.Sections = new CoffSectionHeader[coff.Header.NumberOfSections];
        for (int i = 0; i < coff.Header.NumberOfSections; i++)
        {
            coff.Sections[i] = CoffSectionHeader.Read(data.AsSpan(sectionOffset + i * SectionHeaderSize));
        }

        // Parse symbol table
        if (coff.Header.PointerToSymbolTable > 0 && coff.Header.NumberOfSymbols > 0)
        {
            coff.Symbols = ParseSymbols(data, (int)coff.Header.PointerToSymbolTable, (int)coff.Header.NumberOfSymbols);
        }
        else
        {
            coff.Symbols = Array.Empty<CoffSymbol>();
        }

        return coff;
    }

    static CoffSymbol[] ParseSymbols(byte[] data, int symTabOffset, int count)
    {
        // String table immediately follows the symbol table
        int stringTableOffset = symTabOffset + count * SymbolSize;
        uint stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(stringTableOffset));

        var symbols = new CoffSymbol[count];
        int offset = symTabOffset;

        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(offset, SymbolSize);

            // Name: first 4 bytes zero → use string table offset in next 4 bytes
            string name;
            uint nameCheck = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (nameCheck == 0)
            {
                uint strOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                int strStart = stringTableOffset + (int)strOffset;
                int strEnd = Array.IndexOf(data, (byte)0, strStart);
                name = Encoding.UTF8.GetString(data, strStart, strEnd - strStart);
            }
            else
            {
                name = Encoding.UTF8.GetString(data, offset, 8).TrimEnd('\0');
            }

            symbols[i] = new CoffSymbol
            {
                Name = name,
                Value = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                SectionNumber = BinaryPrimitives.ReadInt16LittleEndian(span[12..]),
                Type = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]),
                StorageClass = span[16],
                NumberOfAuxSymbols = span[17],
            };

            offset += SymbolSize;

            // Skip aux symbols
            int auxCount = symbols[i].NumberOfAuxSymbols;
            if (auxCount > 0)
            {
                for (int a = 0; a < auxCount && (i + 1) < count; a++)
                {
                    i++;
                    symbols[i] = new CoffSymbol { Name = $"<aux>", NumberOfAuxSymbols = 0 };
                    offset += SymbolSize;
                }
            }
        }

        return symbols;
    }

    public CoffSectionHeader? FindSection(string name)
    {
        foreach (var s in Sections)
            if (s.Name == name) return s;
        return null;
    }

    public ReadOnlySpan<byte> GetSectionData(CoffSectionHeader section)
    {
        return FileData.AsSpan((int)section.PointerToRawData, (int)section.SizeOfRawData);
    }

    public CoffRelocation[] GetRelocations(CoffSectionHeader section)
    {
        if (section.NumberOfRelocations == 0)
            return Array.Empty<CoffRelocation>();

        var relocs = new CoffRelocation[section.NumberOfRelocations];
        int offset = (int)section.PointerToRelocations;
        for (int i = 0; i < relocs.Length; i++)
        {
            relocs[i] = CoffRelocation.Read(FileData.AsSpan(offset + i * RelocationSize));
        }
        return relocs;
    }

    /// <summary>
    /// Builds a map of offset-within-section → resolved token value,
    /// by examining relocations whose symbols have storage class CLR_TOKEN (107)
    /// and names that are hex token strings. Architecture-independent: identifies
    /// token relocs by symbol class rather than relocation type code.
    /// </summary>
    public Dictionary<int, int> BuildTokenRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, int>();
        var relocs = GetRelocations(section);

        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length)
                continue;

            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass == IMAGE_SYM_CLASS_CLR_TOKEN &&
                sym.Name.Length == 8 &&
                int.TryParse(sym.Name, System.Globalization.NumberStyles.HexNumber, null, out int token))
            {
                map[(int)r.VirtualAddress] = token;
            }
        }

        return map;
    }

    /// <summary>
    /// Builds a map of offset-within-section → (symbol name, symbol section number)
    /// for non-token relocations. Used to resolve section/secrel references in debug data.
    /// </summary>
    public Dictionary<int, (string Name, short Section)> BuildSymbolRelocationMap(CoffSectionHeader section)
    {
        var map = new Dictionary<int, (string, short)>();
        var relocs = GetRelocations(section);

        foreach (var r in relocs)
        {
            if (r.SymbolTableIndex >= (uint)Symbols.Length)
                continue;

            var sym = Symbols[r.SymbolTableIndex];
            if (sym.StorageClass != IMAGE_SYM_CLASS_CLR_TOKEN)
            {
                map[(int)r.VirtualAddress] = (sym.Name, sym.SectionNumber);
            }
        }

        return map;
    }

    /// <summary>
    /// Apply token relocations to a copy of section data, returning patched bytes
    /// where CLR token operand slots are filled in with actual token values.
    /// </summary>
    public byte[] GetPatchedSectionData(CoffSectionHeader section)
    {
        var data = GetSectionData(section).ToArray();
        var tokenMap = BuildTokenRelocationMap(section);

        foreach (var (offset, token) in tokenMap)
        {
            if (offset + 4 <= data.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), token);
            }
        }

        return data;
    }
}

// ─── Main Program ─────────────────────────────────────────────────────────────

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run coffobjdumper.cs <path-to-obj-file>");
            return 1;
        }

        string objPath = args[0];
        if (!File.Exists(objPath))
        {
            Console.Error.WriteLine($"File not found: {objPath}");
            return 1;
        }

        byte[] fileData = File.ReadAllBytes(objPath);
        var coff = CoffFile.Parse(fileData);

        Console.WriteLine($"File: {objPath}");
        Console.WriteLine($"Machine: 0x{coff.Header.Machine:X4}");
        Console.WriteLine($"Sections: {coff.Header.NumberOfSections}");
        Console.WriteLine($"Symbols: {coff.Header.NumberOfSymbols}");
        Console.WriteLine();

        for (int i = 0; i < coff.Sections.Length; i++)
        {
            var sh = coff.Sections[i];
            Console.WriteLine($"  Section[{i}]: {sh.Name,-12} RawPtr=0x{sh.PointerToRawData:X4}  RawSize={sh.SizeOfRawData,5}  Relocs={sh.NumberOfRelocations}");
        }
        Console.WriteLine();

        // Print symbol table
        Console.WriteLine("Symbol Table:");
        for (int i = 0; i < coff.Symbols.Length; i++)
        {
            var sym = coff.Symbols[i];
            if (sym.Name == "<aux>") continue;
            Console.WriteLine($"  [{i,3}] {sym.Name,-20} Value=0x{sym.Value:X4}  Sect={sym.SectionNumber,2}  Type=0x{sym.Type:X4}  Class={sym.StorageClass,3}  Aux={sym.NumberOfAuxSymbols}");
        }
        Console.WriteLine();

        // Find .cormeta section for metadata
        var cormetaSection = coff.FindSection(".cormeta");
        if (cormetaSection == null)
        {
            Console.Error.WriteLine("No .cormeta section found — not a managed COFF object file.");
            return 1;
        }

        // Find .text$mn section for IL method bodies
        var textSection = coff.FindSection(".text$mn");
        if (textSection == null)
        {
            Console.Error.WriteLine("No .text$mn section found.");
            return 1;
        }

        // Read metadata
        var metadataBytes = coff.GetSectionData(cormetaSection.Value).ToArray();
        MetadataReader reader;
        unsafe
        {
            fixed (byte* ptr = metadataBytes)
            {
                reader = new MetadataReader(ptr, metadataBytes.Length);
            }
        }

        // Get patched IL data (token relocations applied)
        byte[] ilData = coff.GetPatchedSectionData(textSection.Value);

        Console.WriteLine($".text$mn section: {ilData.Length} bytes (with {coff.BuildTokenRelocationMap(textSection.Value).Count} token relocations applied)");
        Console.WriteLine();

        // Parse method bodies sequentially from .text$mn
        // Methods in metadata have RVA=0 in .obj files; bodies are sequential in .text$mn
        int ilOffset = 0;
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            int token = MetadataTokens.GetToken(methodHandle);

            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (not IL — ImplAttributes: {method.ImplAttributes})");
                Console.WriteLine();
                continue;
            }

            if (method.Attributes.HasFlag(MethodAttributes.Abstract))
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (abstract — no body)");
                Console.WriteLine();
                continue;
            }

            if (ilOffset >= ilData.Length)
            {
                Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                Console.WriteLine($"  (no more IL data in .text$mn)");
                Console.WriteLine();
                continue;
            }

            // Parse method body at current offset
            unsafe
            {
                fixed (byte* ptr = ilData)
                {
                    var bodyReader = new BlobReader(ptr + ilOffset, ilData.Length - ilOffset);
                    MethodBodyBlock body = MethodBodyBlock.Create(bodyReader);

                    Console.WriteLine($"=== Method: {name} ({token:X8}) ===");
                    Console.WriteLine($"  Offset in .text$mn: 0x{ilOffset:X4}");
                    Console.WriteLine($"  MaxStack: {body.MaxStack}");
                    Console.WriteLine($"  LocalsInit: {body.LocalVariablesInitialized}");
                    Console.WriteLine($"  LocalSignature: {(body.LocalSignature.IsNil ? "(none)" : $"0x{MetadataTokens.GetToken(body.LocalSignature):X8}")}");

                    byte[] ilBytes = body.GetILBytes();
                    Console.WriteLine($"  CodeSize: {ilBytes.Length}");
                    Console.WriteLine();

                    DumpIL(reader, ilBytes);

                    if (body.ExceptionRegions.Length > 0)
                    {
                        Console.WriteLine();
                        DumpExceptionRegions(reader, body);
                    }

                    Console.WriteLine();

                    // Advance past this method body (align to 4 for fat headers)
                    ilOffset += body.Size;
                    // Fat method bodies start aligned to 4, but the next body after
                    // a tiny body doesn't need alignment.  The emitter aligns before fat headers.
                }
            }
        }

        // ─── Debug Information ────────────────────────────────────────────────
        DumpDebugInfo(coff, reader);

        return 0;
    }

    static void DumpIL(MetadataReader reader, byte[] ilBytes)
    {
        int offset = 0;
        while (offset < ilBytes.Length)
        {
            int instrStart = offset;
            ILOpCode opCode = ReadOpCode(ilBytes, ref offset);
            string operandStr = ReadOperand(reader, ilBytes, ref offset, opCode);

            if (operandStr.Length > 0)
                Console.WriteLine($"  IL_{instrStart:X4}: {FormatOpCode(opCode),-16} {operandStr}");
            else
                Console.WriteLine($"  IL_{instrStart:X4}: {FormatOpCode(opCode)}");
        }
    }

    static ILOpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte b = il[offset++];
        if (b == 0xFE)
            return (ILOpCode)(0xFE00 | il[offset++]);
        return (ILOpCode)b;
    }

    static int ReadInt32(byte[] il, ref int offset)
    {
        int val = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset));
        offset += 4;
        return val;
    }

    static long ReadInt64(byte[] il, ref int offset)
    {
        long val = BinaryPrimitives.ReadInt64LittleEndian(il.AsSpan(offset));
        offset += 8;
        return val;
    }

    static short ReadInt16(byte[] il, ref int offset)
    {
        short val = BinaryPrimitives.ReadInt16LittleEndian(il.AsSpan(offset));
        offset += 2;
        return val;
    }

    static float ReadSingle(byte[] il, ref int offset)
    {
        float val = BitConverter.ToSingle(il, offset);
        offset += 4;
        return val;
    }

    static double ReadDouble(byte[] il, ref int offset)
    {
        double val = BitConverter.ToDouble(il, offset);
        offset += 8;
        return val;
    }

    static string FormatOpCode(ILOpCode opCode)
    {
        return opCode.ToString().ToLowerInvariant().Replace('_', '.');
    }

    static string ReadOperand(MetadataReader reader, byte[] il, ref int offset, ILOpCode opCode)
    {
        switch (opCode)
        {
            // InlineNone
            case ILOpCode.Nop:
            case ILOpCode.Break:
            case ILOpCode.Ldarg_0:
            case ILOpCode.Ldarg_1:
            case ILOpCode.Ldarg_2:
            case ILOpCode.Ldarg_3:
            case ILOpCode.Ldloc_0:
            case ILOpCode.Ldloc_1:
            case ILOpCode.Ldloc_2:
            case ILOpCode.Ldloc_3:
            case ILOpCode.Stloc_0:
            case ILOpCode.Stloc_1:
            case ILOpCode.Stloc_2:
            case ILOpCode.Stloc_3:
            case ILOpCode.Ldnull:
            case ILOpCode.Ldc_i4_m1:
            case ILOpCode.Ldc_i4_0:
            case ILOpCode.Ldc_i4_1:
            case ILOpCode.Ldc_i4_2:
            case ILOpCode.Ldc_i4_3:
            case ILOpCode.Ldc_i4_4:
            case ILOpCode.Ldc_i4_5:
            case ILOpCode.Ldc_i4_6:
            case ILOpCode.Ldc_i4_7:
            case ILOpCode.Ldc_i4_8:
            case ILOpCode.Dup:
            case ILOpCode.Pop:
            case ILOpCode.Ret:
            case ILOpCode.Ldind_i1:
            case ILOpCode.Ldind_u1:
            case ILOpCode.Ldind_i2:
            case ILOpCode.Ldind_u2:
            case ILOpCode.Ldind_i4:
            case ILOpCode.Ldind_u4:
            case ILOpCode.Ldind_i8:
            case ILOpCode.Ldind_i:
            case ILOpCode.Ldind_r4:
            case ILOpCode.Ldind_r8:
            case ILOpCode.Ldind_ref:
            case ILOpCode.Stind_ref:
            case ILOpCode.Stind_i1:
            case ILOpCode.Stind_i2:
            case ILOpCode.Stind_i4:
            case ILOpCode.Stind_i8:
            case ILOpCode.Stind_r4:
            case ILOpCode.Stind_r8:
            case ILOpCode.Add:
            case ILOpCode.Sub:
            case ILOpCode.Mul:
            case ILOpCode.Div:
            case ILOpCode.Div_un:
            case ILOpCode.Rem:
            case ILOpCode.Rem_un:
            case ILOpCode.And:
            case ILOpCode.Or:
            case ILOpCode.Xor:
            case ILOpCode.Shl:
            case ILOpCode.Shr:
            case ILOpCode.Shr_un:
            case ILOpCode.Neg:
            case ILOpCode.Not:
            case ILOpCode.Conv_i1:
            case ILOpCode.Conv_i2:
            case ILOpCode.Conv_i4:
            case ILOpCode.Conv_i8:
            case ILOpCode.Conv_r4:
            case ILOpCode.Conv_r8:
            case ILOpCode.Conv_u4:
            case ILOpCode.Conv_u8:
            case ILOpCode.Conv_r_un:
            case ILOpCode.Throw:
            case ILOpCode.Conv_ovf_i1_un:
            case ILOpCode.Conv_ovf_i2_un:
            case ILOpCode.Conv_ovf_i4_un:
            case ILOpCode.Conv_ovf_i8_un:
            case ILOpCode.Conv_ovf_u1_un:
            case ILOpCode.Conv_ovf_u2_un:
            case ILOpCode.Conv_ovf_u4_un:
            case ILOpCode.Conv_ovf_u8_un:
            case ILOpCode.Conv_ovf_i_un:
            case ILOpCode.Conv_ovf_u_un:
            case ILOpCode.Ldlen:
            case ILOpCode.Ldelem_i1:
            case ILOpCode.Ldelem_u1:
            case ILOpCode.Ldelem_i2:
            case ILOpCode.Ldelem_u2:
            case ILOpCode.Ldelem_i4:
            case ILOpCode.Ldelem_u4:
            case ILOpCode.Ldelem_i8:
            case ILOpCode.Ldelem_i:
            case ILOpCode.Ldelem_r4:
            case ILOpCode.Ldelem_r8:
            case ILOpCode.Ldelem_ref:
            case ILOpCode.Stelem_i:
            case ILOpCode.Stelem_i1:
            case ILOpCode.Stelem_i2:
            case ILOpCode.Stelem_i4:
            case ILOpCode.Stelem_i8:
            case ILOpCode.Stelem_r4:
            case ILOpCode.Stelem_r8:
            case ILOpCode.Stelem_ref:
            case ILOpCode.Conv_ovf_i1:
            case ILOpCode.Conv_ovf_u1:
            case ILOpCode.Conv_ovf_i2:
            case ILOpCode.Conv_ovf_u2:
            case ILOpCode.Conv_ovf_i4:
            case ILOpCode.Conv_ovf_u4:
            case ILOpCode.Conv_ovf_i8:
            case ILOpCode.Conv_ovf_u8:
            case ILOpCode.Conv_u2:
            case ILOpCode.Conv_u1:
            case ILOpCode.Conv_i:
            case ILOpCode.Conv_ovf_i:
            case ILOpCode.Conv_ovf_u:
            case ILOpCode.Add_ovf:
            case ILOpCode.Add_ovf_un:
            case ILOpCode.Mul_ovf:
            case ILOpCode.Mul_ovf_un:
            case ILOpCode.Sub_ovf:
            case ILOpCode.Sub_ovf_un:
            case ILOpCode.Endfinally:
            case ILOpCode.Stind_i:
            case ILOpCode.Conv_u:
            case ILOpCode.Arglist:
            case ILOpCode.Ceq:
            case ILOpCode.Cgt:
            case ILOpCode.Cgt_un:
            case ILOpCode.Clt:
            case ILOpCode.Clt_un:
            case ILOpCode.Localloc:
            case ILOpCode.Endfilter:
            case ILOpCode.Volatile:
            case ILOpCode.Tail:
            case ILOpCode.Cpblk:
            case ILOpCode.Initblk:
            case ILOpCode.Rethrow:
            case ILOpCode.Refanytype:
            case ILOpCode.Readonly:
                return "";

            // ShortInlineBrTarget (1-byte signed)
            case ILOpCode.Br_s:
            case ILOpCode.Brfalse_s:
            case ILOpCode.Brtrue_s:
            case ILOpCode.Beq_s:
            case ILOpCode.Bge_s:
            case ILOpCode.Bgt_s:
            case ILOpCode.Ble_s:
            case ILOpCode.Blt_s:
            case ILOpCode.Bne_un_s:
            case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble_un_s:
            case ILOpCode.Blt_un_s:
            case ILOpCode.Leave_s:
            {
                int delta = (sbyte)il[offset++];
                int target = offset + delta;
                return $"IL_{target:X4}";
            }

            // InlineBrTarget (4-byte signed)
            case ILOpCode.Br:
            case ILOpCode.Brfalse:
            case ILOpCode.Brtrue:
            case ILOpCode.Beq:
            case ILOpCode.Bge:
            case ILOpCode.Bgt:
            case ILOpCode.Ble:
            case ILOpCode.Blt:
            case ILOpCode.Bne_un:
            case ILOpCode.Bge_un:
            case ILOpCode.Bgt_un:
            case ILOpCode.Ble_un:
            case ILOpCode.Blt_un:
            case ILOpCode.Leave:
            {
                int delta = ReadInt32(il, ref offset);
                int target = offset + delta;
                return $"IL_{target:X4}";
            }

            // ShortInlineI
            case ILOpCode.Ldc_i4_s:
                return $"{(sbyte)il[offset++]}";
            case ILOpCode.Unaligned:
                return $"{il[offset++]}";

            // InlineI
            case ILOpCode.Ldc_i4:
            {
                int val = ReadInt32(il, ref offset);
                return $"0x{val:X}";
            }

            // InlineI8
            case ILOpCode.Ldc_i8:
            {
                long val = ReadInt64(il, ref offset);
                return $"0x{val:X}";
            }

            // ShortInlineR
            case ILOpCode.Ldc_r4:
            {
                float val = ReadSingle(il, ref offset);
                return $"{val}";
            }

            // InlineR
            case ILOpCode.Ldc_r8:
            {
                double val = ReadDouble(il, ref offset);
                return $"{val}";
            }

            // ShortInlineVar (1-byte index)
            case ILOpCode.Ldloc_s:
            case ILOpCode.Stloc_s:
            case ILOpCode.Ldloca_s:
            case ILOpCode.Ldarg_s:
            case ILOpCode.Starg_s:
            case ILOpCode.Ldarga_s:
                return $"V_{il[offset++]}";

            // InlineVar (2-byte index)
            case ILOpCode.Ldloc:
            case ILOpCode.Stloc:
            case ILOpCode.Ldloca:
            case ILOpCode.Ldarg:
            case ILOpCode.Starg:
            case ILOpCode.Ldarga:
            {
                short idx = ReadInt16(il, ref offset);
                return $"V_{idx}";
            }

            // InlineMethod / InlineField / InlineType / InlineTok (4-byte token)
            case ILOpCode.Call:
            case ILOpCode.Callvirt:
            case ILOpCode.Newobj:
            case ILOpCode.Ldftn:
            case ILOpCode.Ldvirtftn:
            case ILOpCode.Jmp:
            case ILOpCode.Ldfld:
            case ILOpCode.Ldflda:
            case ILOpCode.Stfld:
            case ILOpCode.Ldsfld:
            case ILOpCode.Ldsflda:
            case ILOpCode.Stsfld:
            case ILOpCode.Castclass:
            case ILOpCode.Isinst:
            case ILOpCode.Newarr:
            case ILOpCode.Box:
            case ILOpCode.Unbox:
            case ILOpCode.Unbox_any:
            case ILOpCode.Ldobj:
            case ILOpCode.Stobj:
            case ILOpCode.Initobj:
            case ILOpCode.Cpobj:
            case ILOpCode.Sizeof:
            case ILOpCode.Ldelem:
            case ILOpCode.Stelem:
            case ILOpCode.Mkrefany:
            case ILOpCode.Refanyval:
            case ILOpCode.Constrained:
            case ILOpCode.Ldtoken:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            // InlineString
            case ILOpCode.Ldstr:
            {
                int token = ReadInt32(il, ref offset);
                return FormatStringToken(reader, token);
            }

            // InlineSig
            case ILOpCode.Calli:
            {
                int token = ReadInt32(il, ref offset);
                return FormatToken(reader, token);
            }

            // InlineSwitch
            case ILOpCode.Switch:
            {
                uint count = (uint)ReadInt32(il, ref offset);
                int baseOffset = offset + (int)(count * 4);
                var targets = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    int delta = ReadInt32(il, ref offset);
                    targets[i] = $"IL_{baseOffset + delta:X4}";
                }
                return $"({string.Join(", ", targets)})";
            }

            default:
                return $"<unknown opcode 0x{(ushort)opCode:X}>";
        }
    }

    static string FormatToken(MetadataReader reader, int token)
    {
        string resolved = ResolveToken(reader, token);
        if (resolved != null)
            return $"{resolved} /* {token:X8} */";
        return $"/* {token:X8} */";
    }

    static string FormatStringToken(MetadataReader reader, int token)
    {
        try
        {
            var handle = MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
            string value = reader.GetUserString(handle);
            return $"\"{Escape(value)}\" /* {token:X8} */";
        }
        catch
        {
            return $"/* {token:X8} */";
        }
    }

    static string Escape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    static string ResolveToken(MetadataReader reader, int token)
    {
        int tableIndex = token >> 24;
        int rowId = token & 0x00FFFFFF;

        if (rowId == 0)
            return null;

        try
        {
            switch (tableIndex)
            {
                case 0x01: // TypeRef
                {
                    var handle = MetadataTokens.TypeReferenceHandle(rowId);
                    var typeRef = reader.GetTypeReference(handle);
                    string ns = reader.GetString(typeRef.Namespace);
                    string name = reader.GetString(typeRef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x02: // TypeDef
                {
                    var handle = MetadataTokens.TypeDefinitionHandle(rowId);
                    var typeDef = reader.GetTypeDefinition(handle);
                    string ns = reader.GetString(typeDef.Namespace);
                    string name = reader.GetString(typeDef.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case 0x04: // FieldDef
                {
                    var handle = MetadataTokens.FieldDefinitionHandle(rowId);
                    var fieldDef = reader.GetFieldDefinition(handle);
                    string name = reader.GetString(fieldDef.Name);
                    var declaringType = fieldDef.GetDeclaringType();
                    if (!declaringType.IsNil)
                    {
                        var typeDef = reader.GetTypeDefinition(declaringType);
                        string typeName = reader.GetString(typeDef.Name);
                        return $"{typeName}::{name}";
                    }
                    return name;
                }
                case 0x06: // MethodDef
                {
                    var handle = MetadataTokens.MethodDefinitionHandle(rowId);
                    var methodDef = reader.GetMethodDefinition(handle);
                    string name = reader.GetString(methodDef.Name);
                    var declaringType = methodDef.GetDeclaringType();
                    if (!declaringType.IsNil)
                    {
                        var typeDef = reader.GetTypeDefinition(declaringType);
                        string typeName = reader.GetString(typeDef.Name);
                        return $"{typeName}::{name}";
                    }
                    return name;
                }
                case 0x0A: // MemberRef
                {
                    var handle = MetadataTokens.MemberReferenceHandle(rowId);
                    var memberRef = reader.GetMemberReference(handle);
                    string name = reader.GetString(memberRef.Name);
                    var parent = memberRef.Parent;
                    string parentName = ResolveToken(reader, MetadataTokens.GetToken(parent));
                    if (parentName != null)
                        return $"{parentName}::{name}";
                    return name;
                }
                case 0x11: // StandaloneSig
                {
                    return $"StandaloneSig({rowId})";
                }
                case 0x1B: // TypeSpec
                {
                    return $"TypeSpec({rowId})";
                }
                case 0x2B: // MethodSpec
                {
                    var handle = MetadataTokens.MethodSpecificationHandle(rowId);
                    var methodSpec = reader.GetMethodSpecification(handle);
                    string methodName = ResolveToken(reader, MetadataTokens.GetToken(methodSpec.Method));
                    return methodName ?? $"MethodSpec({rowId})";
                }
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    static void DumpExceptionRegions(MetadataReader reader, MethodBodyBlock body)
    {
        Console.WriteLine("  Exception Handlers:");
        for (int i = 0; i < body.ExceptionRegions.Length; i++)
        {
            var region = body.ExceptionRegions[i];
            Console.Write($"    [{i}] {region.Kind}: ");
            Console.Write($"Try IL_{region.TryOffset:X4}..IL_{region.TryOffset + region.TryLength:X4} ");
            Console.Write($"Handler IL_{region.HandlerOffset:X4}..IL_{region.HandlerOffset + region.HandlerLength:X4}");

            if (region.Kind == ExceptionRegionKind.Catch)
            {
                int catchToken = MetadataTokens.GetToken(region.CatchType);
                string catchName = ResolveToken(reader, catchToken);
                Console.Write($" Catch={catchName ?? $"0x{catchToken:X8}"}");
            }
            else if (region.Kind == ExceptionRegionKind.Filter)
            {
                Console.Write($" Filter=IL_{region.FilterOffset:X4}");
            }

            Console.WriteLine();
        }
    }

    // ─── Debug Information Dumper ──────────────────────────────────────────────

    static void DumpDebugInfo(CoffFile coff, MetadataReader mdReader)
    {
        // .debug$S — symbols, lines, string table, file checksums
        for (int si = 0; si < coff.Sections.Length; si++)
        {
            var section = coff.Sections[si];
            if (section.Name == ".debug$S" && section.SizeOfRawData > 4)
            {
                Console.WriteLine("========================================");
                Console.WriteLine($"=== Debug Symbols (.debug$S, section {si}) ===");
                Console.WriteLine("========================================");
                Console.WriteLine();
                DumpDebugS(coff, section, mdReader);
            }
        }

        // .debug$T — type records
        for (int si = 0; si < coff.Sections.Length; si++)
        {
            var section = coff.Sections[si];
            if (section.Name == ".debug$T" && section.SizeOfRawData > 4)
            {
                Console.WriteLine("========================================");
                Console.WriteLine($"=== Debug Types (.debug$T, section {si}) ===");
                Console.WriteLine("========================================");
                Console.WriteLine();
                DumpDebugT(coff, section);
            }
        }
    }

    // ─── .debug$S parsing ─────────────────────────────────────────────────────

    static void DumpDebugS(CoffFile coff, CoffSectionHeader section, MetadataReader mdReader)
    {
        byte[] data = coff.GetSectionData(section).ToArray();

        // Apply token relocations to the debug data
        var tokenMap = coff.BuildTokenRelocationMap(section);
        foreach (var (offset, token) in tokenMap)
        {
            if (offset + 4 <= data.Length)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), token);
        }

        // Build symbol relocation map for section/secrel fixups
        var symRelocMap = coff.BuildSymbolRelocationMap(section);

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != 4)
        {
            Console.WriteLine($"  Unknown .debug$S version: {version}");
            return;
        }

        // First pass: parse string table and file checksums (needed by line numbers)
        var stringTable = new Dictionary<int, string>();
        var fileChecksums = new List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)>();

        int pos = 4;
        while (pos + 8 <= data.Length)
        {
            uint subType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            int subSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            if (subSize < 0 || pos + 8 + subSize > data.Length) break;

            var subData = data.AsSpan(pos + 8, subSize);

            if (subType == 0xF3) // STRING TABLE
                ParseStringTable(subData, stringTable);
            else if (subType == 0xF4) // FILE CHECKSUMS
                ParseFileChecksums(subData, fileChecksums);

            pos += 8 + subSize;
            pos = (pos + 3) & ~3; // align to 4
        }

        // Second pass: dump everything
        pos = 4;
        while (pos + 8 <= data.Length)
        {
            uint subType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            int subSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            if (subSize < 0 || pos + 8 + subSize > data.Length) break;

            int subStart = pos + 8; // absolute offset within section data
            var subData = data.AsSpan(subStart, subSize);

            switch (subType)
            {
                case 0xF1:
                    Console.WriteLine("*** SYMBOLS");
                    DumpSymbolRecords(subData, subStart, symRelocMap, mdReader);
                    Console.WriteLine();
                    break;
                case 0xF2:
                    DumpLineNumbers(subData, subStart, symRelocMap, stringTable, fileChecksums);
                    Console.WriteLine();
                    break;
                case 0xF3:
                    Console.WriteLine("*** STRINGTABLE");
                    foreach (var (off, s) in stringTable)
                        Console.WriteLine($"  [{off:X4}] {s}");
                    Console.WriteLine();
                    break;
                case 0xF4:
                    Console.WriteLine("*** FILECHKSUMS");
                    Console.WriteLine($"  {"FileId",-8} {"StrOff",-8} {"Type",-10} Checksum");
                    foreach (var (fileId, strOff, chkType, chkData) in fileChecksums)
                    {
                        string typeName = chkType switch { 0 => "None", 1 => "MD5", 2 => "SHA1", 3 => "SHA_256", _ => $"0x{chkType:X2}" };
                        string fileName = stringTable.TryGetValue(strOff, out var fn) ? fn : $"strOff={strOff}";
                        Console.WriteLine($"  {fileId,-8:X4} {strOff,-8} {typeName,-10} {Convert.ToHexString(chkData)}  ({fileName})");
                    }
                    Console.WriteLine();
                    break;
                default:
                    Console.WriteLine($"*** SUBSECTION type=0x{subType:X} size={subSize}");
                    Console.WriteLine();
                    break;
            }

            pos += 8 + subSize;
            pos = (pos + 3) & ~3;
        }
    }

    static void ParseStringTable(ReadOnlySpan<byte> data, Dictionary<int, string> table)
    {
        int spos = 0;
        while (spos < data.Length)
        {
            int end = data[spos..].IndexOf((byte)0);
            if (end < 0) break;
            string s = Encoding.UTF8.GetString(data.Slice(spos, end));
            table[spos] = s;
            spos += end + 1;
        }
    }

    static void ParseFileChecksums(ReadOnlySpan<byte> data, List<(int, int, byte, byte[])> checksums)
    {
        int fpos = 0;
        int fileId = 0;
        while (fpos + 6 <= data.Length)
        {
            int strOff = BinaryPrimitives.ReadInt32LittleEndian(data[fpos..]);
            byte cbChk = data[fpos + 4];
            byte chkType = data[fpos + 5];
            byte[] chkData = (fpos + 6 + cbChk <= data.Length)
                ? data.Slice(fpos + 6, cbChk).ToArray()
                : Array.Empty<byte>();
            checksums.Add((fileId, strOff, chkType, chkData));
            fileId = fpos; // file ID is the byte offset of this entry within the checksum table
            fpos += 6 + cbChk;
            fpos = (fpos + 3) & ~3;
            // the NEXT entry's fileId is fpos
            // Actually: fileId in line records is the byte offset of the file entry in the checksum table
        }
        // Fix: re-parse to set fileId correctly (it's the offset of each entry)
        checksums.Clear();
        fpos = 0;
        while (fpos + 6 <= data.Length)
        {
            int strOff = BinaryPrimitives.ReadInt32LittleEndian(data[fpos..]);
            byte cbChk = data[fpos + 4];
            byte chkType = data[fpos + 5];
            byte[] chkData = (fpos + 6 + cbChk <= data.Length)
                ? data.Slice(fpos + 6, cbChk).ToArray()
                : Array.Empty<byte>();
            checksums.Add((fpos, strOff, chkType, chkData));
            fpos += 6 + cbChk;
            fpos = (fpos + 3) & ~3;
        }
    }

    // ─── Symbol record dumping ────────────────────────────────────────────────

    static void DumpSymbolRecords(ReadOnlySpan<byte> data, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap, MetadataReader mdReader)
    {
        int spos = 0;
        while (spos + 4 <= data.Length)
        {
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data[spos..]);
            if (recLen < 2 || spos + 2 + recLen > data.Length) break;

            ushort recType = BinaryPrimitives.ReadUInt16LittleEndian(data[(spos + 2)..]);
            var payload = data.Slice(spos + 4, recLen - 2); // after reclen(2) + rectyp(2)

            switch (recType)
            {
                case 0x1101: // S_OBJNAME
                    DumpObjName(payload);
                    break;
                case 0x113D: // S_COMPILE3
                    DumpCompile3(payload);
                    break;
                case 0x112A: // S_GMANPROC
                case 0x112B: // S_LMANPROC
                    DumpManProc(recType, payload, spos, baseOffset, symRelocMap);
                    break;
                case 0x1012: // S_FRAMEPROC
                    DumpFrameProc(payload);
                    break;
                case 0x1120: // S_MANSLOT
                    DumpManSlot(payload, mdReader);
                    break;
                case 0x111C: // S_LMANDATA
                case 0x111D: // S_GMANDATA
                    DumpManData(recType, payload);
                    break;
                case 0x112D: // S_MANCONSTANT
                    DumpManConstant(payload);
                    break;
                case 0x114F: // S_PROC_ID_END
                case 0x0006: // S_END
                    Console.WriteLine($"  S_PROC_ID_END");
                    break;
                case 0x114C: // S_BUILDINFO
                    if (payload.Length >= 4)
                    {
                        uint typeIdx = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        Console.WriteLine($"  S_BUILDINFO: TypeIndex=0x{typeIdx:X4}");
                    }
                    break;
                default:
                    Console.WriteLine($"  Symbol 0x{recType:X4} (len={recLen})");
                    break;
            }

            spos += 2 + recLen;
            // No alignment between symbol records within a subsection
        }
    }

    static void DumpObjName(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return;
        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        string name = ReadNullTermString(payload[4..]);
        Console.WriteLine($"  S_OBJNAME: Signature: {sig:X8}, {name}");
    }

    static void DumpCompile3(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 30) { Console.WriteLine("  S_COMPILE3: (too short)"); return; }

        // flags(4), machine(2), verFE_Major(2), verFE_Minor(2), verFE_Build(2), verFE_QFE(2),
        // verBE_Major(2), verBE_Minor(2), verBE_Build(2), verBE_QFE(2), name(...)
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort feMajor = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        ushort feMinor = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]);
        ushort feBuild = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
        ushort beMajor = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        ushort beMinor = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
        ushort beBuild = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]);
        string verStr = ReadNullTermString(payload[22..]);

        byte lang = (byte)(flags & 0xFF);
        string langName = lang switch
        {
            0x00 => "C", 0x01 => "C++", 0x02 => "Fortran", 0x03 => "Masm",
            0x04 => "Pascal", 0x05 => "Basic", 0x06 => "Cobol", 0x07 => "Link",
            0x08 => "CVTRES", 0x09 => "CVTPGD", 0x0A => "C#", 0x0B => "VB",
            0x0C => "ILASM", 0x0D => "Java", 0x0E => "JScript", 0x0F => "MSIL",
            0x10 => "HLSL", _ => $"0x{lang:X2}"
        };

        string machineName = machine switch
        {
            0x00 => "8080", 0x01 => "8086", 0x02 => "80286", 0x03 => "80386",
            0x04 => "80486", 0x05 => "Pentium", 0x06 => "PentiumPro/II",
            0x40 => "MIPS R4000", 0x50 => "M68000", 0xA0 => "Alpha",
            0xC0 => "PPC601", 0xD0 => "SH3/SH4", 0xE0 => "ARM",
            0xF0 => "IA64", 0x100 => "AMD64", 0x104 => "ARM64",
            _ => $"0x{machine:X4}"
        };

        bool managed = (flags & 0x2000) != 0;
        Console.WriteLine($"  S_COMPILE3: Language={langName}, Machine={machineName}, Managed={managed}");
        Console.WriteLine($"    Frontend: {feMajor}.{feMinor}.{feBuild}, Backend: {beMajor}.{beMinor}.{beBuild}");
        Console.WriteLine($"    {verStr}");
    }

    static void DumpManProc(ushort recType, ReadOnlySpan<byte> payload, int recOffset, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap)
    {
        string typeName = recType == 0x112A ? "S_GMANPROC" : "S_LMANPROC";
        if (payload.Length < 34) { Console.WriteLine($"  {typeName}: (too short)"); return; }

        uint pParent = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint pEnd = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint pNext = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        uint codeLen = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        uint dbgStart = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        uint dbgEnd = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]);
        int token = BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
        int codeOff = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
        ushort seg = BinaryPrimitives.ReadUInt16LittleEndian(payload[32..]);
        byte procFlags = payload[34];

        // Try to resolve segment from relocation if it's 0
        if (seg == 0)
        {
            // The seg field is at recOffset + 4 (reclen+rectyp) + 32 relative to subsection start
            // = baseOffset + recOffset + 4 + 32 within section data
            int segFieldOffset = baseOffset + recOffset + 4 + 32;
            if (symRelocMap.TryGetValue(segFieldOffset, out var symInfo))
                seg = (ushort)symInfo.Section;
        }

        ushort retReg = (payload.Length > 36) ? BinaryPrimitives.ReadUInt16LittleEndian(payload[35..]) : (ushort)0;
        string name = (payload.Length > 37) ? ReadNullTermString(payload[37..]) : "?";

        Console.WriteLine($"  {typeName}: [{seg:X4}:{codeOff:X8}], Cb: {codeLen:X8}, Token: {token:X8}, {name}");
        Console.WriteLine($"    Parent: {pParent:X8}, End: {pEnd:X8}, Next: {pNext:X8}");
        Console.WriteLine($"    Debug start: {dbgStart:X8}, Debug end: {dbgEnd:X8}");
        Console.WriteLine($"    Flags: 0x{procFlags:X2}, Return Reg: {retReg}");
    }

    static void DumpFrameProc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 24) { Console.WriteLine("  S_FRAMEPROC: (too short)"); return; }

        uint cbFrame = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint cbPad = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint offPad = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        uint cbSaveRegs = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        uint offExHdlr = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        ushort sectExHdlr = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        uint funcFlags = (payload.Length >= 26)
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload[22..])
            : 0;

        Console.WriteLine($"  S_FRAMEPROC: frame={cbFrame}, pad={cbPad}, saveRegs={cbSaveRegs}, flags=0x{funcFlags:X8}");
    }

    static void DumpManSlot(ReadOnlySpan<byte> payload, MetadataReader mdReader)
    {
        if (payload.Length < 10) { Console.WriteLine("  S_MANSLOT: (too short)"); return; }

        uint iSlot = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint typind = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        // CV_lvar_attr: off(4) + seg(2) + flags(2) = 8 bytes
        uint attrOff = 0;
        ushort attrSeg = 0;
        ushort attrFlags = 0;
        if (payload.Length >= 18)
        {
            attrOff = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
            attrSeg = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
            attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        }

        string name = (payload.Length > 16) ? ReadNullTermString(payload[16..]) : "?";

        // typind in managed slots is often a metadata token
        string tokenStr = (typind > 0) ? $"0x{typind:X8}" : "0x00000000";

        Console.WriteLine($"  S_MANSLOT: slot={iSlot}, typind={tokenStr}, name={name}");
    }

    static void DumpManData(ushort recType, ReadOnlySpan<byte> payload)
    {
        string typeName = recType == 0x111D ? "S_GMANDATA" : "S_LMANDATA";
        if (payload.Length < 8) { Console.WriteLine($"  {typeName}: (too short)"); return; }

        uint typind = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        int off = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        ushort seg = (payload.Length >= 10) ? BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) : (ushort)0;
        string name = (payload.Length > 10) ? ReadNullTermString(payload[10..]) : "?";

        Console.WriteLine($"  {typeName}: [{seg:X4}:{off:X8}], typind=0x{typind:X8}, {name}");
    }

    static void DumpManConstant(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6) { Console.WriteLine("  S_MANCONSTANT: (too short)"); return; }
        uint typind = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        string name = (payload.Length > 6) ? ReadNullTermString(payload[6..]) : "?";
        Console.WriteLine($"  S_MANCONSTANT: typind=0x{typind:X8}, value={value}, name={name}");
    }

    // ─── Line number dumping ──────────────────────────────────────────────────

    static void DumpLineNumbers(ReadOnlySpan<byte> data, int baseOffset,
        Dictionary<int, (string Name, short Section)> symRelocMap,
        Dictionary<int, string> stringTable,
        List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)> fileChecksums)
    {
        if (data.Length < 12) return;

        int offCon = BinaryPrimitives.ReadInt32LittleEndian(data);
        ushort segCon = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int cbCon = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        // Try to resolve segment from relocation
        if (segCon == 0)
        {
            int segFieldOffset = baseOffset + 4; // segCon is 4 bytes into the subsection data
            if (symRelocMap.TryGetValue(segFieldOffset, out var symInfo))
                segCon = (ushort)symInfo.Section;
        }

        bool hasColumns = (flags & 0x0001) != 0;
        Console.WriteLine($"*** LINES [{segCon:X4}:{offCon:X8}-{offCon + cbCon:X8}], flags=0x{flags:X4}");

        int lpos = 12;
        while (lpos + 12 <= data.Length)
        {
            int offFile = BinaryPrimitives.ReadInt32LittleEndian(data[lpos..]);
            int nLines = BinaryPrimitives.ReadInt32LittleEndian(data[(lpos + 4)..]);
            int cbBlock = BinaryPrimitives.ReadInt32LittleEndian(data[(lpos + 8)..]);
            lpos += 12;

            // Resolve file name via fileChecksums → stringTable
            string fileName = ResolveFileId(offFile, fileChecksums, stringTable);
            Console.WriteLine($"  File: {fileName}");

            for (int ln = 0; ln < nLines && lpos + 8 <= data.Length; ln++)
            {
                uint lineOff = BinaryPrimitives.ReadUInt32LittleEndian(data[lpos..]);
                uint lineFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[(lpos + 4)..]);
                int lineNum = (int)(lineFlags & 0x00FFFFFF);
                bool isStatement = (lineFlags & 0x80000000) != 0;
                lpos += 8;

                Console.WriteLine($"    line {lineNum,5} at 0x{lineOff:X4}{(isStatement ? "" : " (expr)")}");
            }

            // Skip column info if present
            if (hasColumns)
                lpos += nLines * 4;
        }
    }

    static string ResolveFileId(int fileId,
        List<(int FileId, int StringOffset, byte ChecksumType, byte[] Checksum)> fileChecksums,
        Dictionary<int, string> stringTable)
    {
        foreach (var (fid, strOff, _, _) in fileChecksums)
        {
            if (fid == fileId)
            {
                if (stringTable.TryGetValue(strOff, out string name))
                    return name;
                return $"strOff={strOff}";
            }
        }
        return $"fileId=0x{fileId:X}";
    }

    // ─── .debug$T parsing ─────────────────────────────────────────────────────

    static void DumpDebugT(CoffFile coff, CoffSectionHeader section)
    {
        var data = coff.GetSectionData(section);
        if (data.Length < 4) return;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        Console.WriteLine($"  Version: {version}");
        if (version != 4) { Console.WriteLine("  (unsupported version)"); return; }

        int pos = 4;
        int typeIndex = 0x1000; // first user type index

        while (pos + 4 <= data.Length)
        {
            ushort recLen = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            if (recLen < 2 || pos + 2 + recLen > data.Length) break;

            ushort leaf = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            var payload = data.Slice(pos + 4, recLen - 2);

            string desc = DecodeTypeRecord(leaf, payload);
            Console.WriteLine($"  0x{typeIndex:X4}: {desc}");

            typeIndex++;
            pos += 2 + recLen;
        }
        Console.WriteLine();
    }

    static string DecodeTypeRecord(ushort leaf, ReadOnlySpan<byte> payload)
    {
        switch (leaf)
        {
            case 0x1008: // LF_PROCEDURE
                if (payload.Length >= 12)
                {
                    uint rvtype = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    byte calltype = payload[4];
                    byte funcattr = payload[5];
                    ushort parmcount = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
                    uint arglist = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
                    string callName = calltype switch
                    {
                        0x00 => "C Near", 0x01 => "C Far", 0x02 => "Pascal Near",
                        0x07 => "Stdcall", 0x08 => "Syscall", 0x09 => "Thiscall",
                        0x0B => "CLR Call", _ => $"0x{calltype:X2}"
                    };
                    return $"LF_PROCEDURE retType={FormatTypeIndex(rvtype)}, callType={callName}, parms={parmcount}, argList=0x{arglist:X4}";
                }
                return $"LF_PROCEDURE (len={payload.Length})";

            case 0x1201: // LF_ARGLIST
                if (payload.Length >= 4)
                {
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var args = new List<string>();
                    for (int i = 0; i < count && 4 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint argType = BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + i * 4)..]);
                        args.Add(FormatTypeIndex(argType));
                    }
                    return $"LF_ARGLIST count={count}: ({string.Join(", ", args)})";
                }
                return "LF_ARGLIST";

            case 0x1503: // LF_ARRAY
                if (payload.Length >= 8)
                {
                    uint elemType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint idxType = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    // size is a numeric leaf starting at offset 8
                    (ulong size, int sizeBytes) = ReadNumericLeaf(payload[8..]);
                    string name = (8 + sizeBytes < payload.Length) ? ReadNullTermString(payload[(8 + sizeBytes)..]) : "";
                    return $"LF_ARRAY elemType={FormatTypeIndex(elemType)}, idxType={FormatTypeIndex(idxType)}, size={size}";
                }
                return "LF_ARRAY";

            case 0x1601: // LF_FUNC_ID
                if (payload.Length >= 8)
                {
                    uint scopeId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    string name = ReadNullTermString(payload[8..]);
                    return $"LF_FUNC_ID scope=0x{scopeId:X4}, type=0x{type:X4}, name={name}";
                }
                return "LF_FUNC_ID";

            case 0x1605: // LF_STRING_ID
                if (payload.Length >= 4)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    string name = ReadNullTermString(payload[4..]);
                    string truncated = name.Length > 80 ? name[..80] + "..." : name;
                    return id != 0
                        ? $"LF_STRING_ID subStrings=0x{id:X4}, \"{truncated}\""
                        : $"LF_STRING_ID \"{truncated}\"";
                }
                return "LF_STRING_ID";

            case 0x1604: // LF_SUBSTR_LIST
                if (payload.Length >= 4)
                {
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var ids = new List<string>();
                    for (int i = 0; i < count && 4 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(4 + i * 4)..]);
                        ids.Add($"0x{id:X4}");
                    }
                    return $"LF_SUBSTR_LIST count={count}: ({string.Join(", ", ids)})";
                }
                return "LF_SUBSTR_LIST";

            case 0x1603: // LF_BUILDINFO
                if (payload.Length >= 2)
                {
                    ushort count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    var ids = new List<string>();
                    for (int i = 0; i < count && 2 + i * 4 + 4 <= payload.Length; i++)
                    {
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(2 + i * 4)..]);
                        ids.Add($"0x{id:X4}");
                    }
                    return $"LF_BUILDINFO count={count}: ({string.Join(", ", ids)})";
                }
                return "LF_BUILDINFO";

            case 0x1002: // LF_POINTER
                if (payload.Length >= 8)
                {
                    uint utype = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    uint attr = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    int ptrKind = (int)((attr >> 0) & 0x1F);
                    int ptrMode = (int)((attr >> 5) & 0x07);
                    int ptrSize = (int)((attr >> 13) & 0xFF);
                    return $"LF_POINTER to={FormatTypeIndex(utype)}, size={ptrSize}";
                }
                return "LF_POINTER";

            default:
                return $"Leaf=0x{leaf:X4} (len={payload.Length})";
        }
    }

    static string FormatTypeIndex(uint ti)
    {
        if (ti < 0x1000)
        {
            // Predefined type
            string name = ti switch
            {
                0x0000 => "T_NOTYPE", 0x0003 => "T_VOID", 0x0008 => "T_HRESULT",
                0x0010 => "T_CHAR", 0x0011 => "T_SHORT", 0x0012 => "T_LONG",
                0x0013 => "T_QUAD", 0x0020 => "T_UCHAR", 0x0021 => "T_USHORT",
                0x0022 => "T_ULONG", 0x0023 => "T_UQUAD",
                0x0040 => "T_REAL32", 0x0041 => "T_REAL64",
                0x0068 => "T_INT1", 0x0069 => "T_UINT1",
                0x0070 => "T_RCHAR", 0x0071 => "T_WCHAR",
                0x0072 => "T_INT2", 0x0073 => "T_UINT2",
                0x0074 => "T_INT4", 0x0075 => "T_UINT4",
                0x0076 => "T_INT8", 0x0077 => "T_UINT8",
                0x0103 => "T_32PVOID", 0x0403 => "T_32PVOID",
                0x0603 => "T_64PVOID",
                _ => null
            };
            if (name != null) return $"{name}(0x{ti:X4})";
            return $"T_(0x{ti:X4})";
        }
        return $"0x{ti:X4}";
    }

    static (ulong Value, int BytesRead) ReadNumericLeaf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return (0, 0);
        ushort leaf = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (leaf < 0x8000)
            return (leaf, 2);

        switch (leaf)
        {
            case 0x8000: // LF_CHAR
                return (data.Length >= 3 ? data[2] : 0u, 3);
            case 0x8001: // LF_SHORT
                return (data.Length >= 4 ? (ulong)(ushort)BinaryPrimitives.ReadInt16LittleEndian(data[2..]) : 0, 4);
            case 0x8002: // LF_USHORT
                return (data.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) : 0u, 4);
            case 0x8003: // LF_LONG
                return (data.Length >= 6 ? (ulong)(uint)BinaryPrimitives.ReadInt32LittleEndian(data[2..]) : 0, 6);
            case 0x8004: // LF_ULONG
                return (data.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(data[2..]) : 0, 6);
            default:
                return (0, 2);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        if (end < 0) end = data.Length;
        return Encoding.UTF8.GetString(data[..end]);
    }
}
