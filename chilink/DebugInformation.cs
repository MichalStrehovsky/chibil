using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed record DebugDocument(string Name, Guid HashAlgorithm, string Checksum);
internal sealed record DebugPoint(int Offset, DebugDocument Document, int StartLine, int EndLine,
    int StartColumn, int EndColumn, bool Hidden);
internal sealed record DebugVariable(int Slot, string Name);

internal sealed class DebugScope(int start, int length)
{
    public int Start { get; } = start;
    public int Length { get; } = length;
    public List<DebugVariable> Variables { get; } = [];
}

internal sealed class DebugMethod(Definition definition, int codeStart, int codeSize,
    StandaloneSignatureHandle localSignature, int localCount)
{
    public Definition Definition { get; } = definition;
    public int CodeStart { get; } = codeStart;
    public int CodeSize { get; } = codeSize;
    public StandaloneSignatureHandle LocalSignature { get; } = localSignature;
    public int LocalCount { get; } = localCount;
    public Dictionary<int, DebugPoint> Points { get; } = [];
    public List<DebugScope> Scopes { get; } = [];
}

internal sealed partial class Linker
{
    private Dictionary<SourceToken, DebugMethod> ReadDebugInformation(HashSet<string> warnings)
    {
        var result = new Dictionary<SourceToken, DebugMethod>();
        foreach (InputFile input in _inputs)
        {
            var definitions = input.Definitions.Values.Where(d =>
                d.Token.Handle.Kind == HandleKind.MethodDefinition &&
                _live.Contains(_merger.Resolve(d.Token)) &&
                _definitions.TryGetValue(_merger.Resolve(d.Token), out var selected) &&
                selected.Token == d.Token).ToHashSet();
            foreach (DebugMethod method in new DebugObjectReader(input, definitions, warnings).Read())
                result.Add(_merger.Resolve(method.Definition.Token), method);
        }
        return result;
    }

    private sealed class DebugObjectReader(InputFile input, HashSet<Definition> selected, HashSet<string> warnings)
    {
        private sealed record DebugSection(InputSection Section, CodeViewReader Reader,
            Dictionary<int, CoffRelocation> Relocations);

        private readonly List<DebugSection> _sections = [];
        private readonly List<(DebugSection Section, CodeViewSubsection Table)> _strings = [], _checksums = [];
        private readonly Dictionary<Definition, DebugMethod> _methods = [];
        private readonly HashSet<string> _warningKinds = new(StringComparer.Ordinal);
        private DebugSection _section;
        private int _offset;

        private string Context => $"{input.Path}: .debug$S section {input.Sections.IndexOf(_section.Section) + 1}, +0x{_offset:X}";
        private BadImageFormatException Invalid(string message) => new(message);

        private void Warn(string message)
        {
            if (_warningKinds.Add(message))
                warnings.Add($"{Context}: {message}");
        }

        public IEnumerable<DebugMethod> Read()
        {
            try
            {
                foreach (InputSection section in input.Sections.Where(s => s.Name == ".debug$S" && IsSelected(s)))
                {
                    _section = new DebugSection(section, null, []);
                    _offset = 0;
                    var reader = input.Reader.GetCodeViewReader(section.Header);
                    var relocations = new Dictionary<int, CoffRelocation>();
                    var ends = new SortedDictionary<int, int>();
                    for (int i = 0; i < section.Relocations.NumberOfRelocations; i++)
                    {
                        var relocation = section.Relocations.GetRelocation(i);
                        int offset = checked((int)relocation.Offset);
                        int width = relocation.Type switch
                        {
                            ImageRelocation.Amd64_SECTION => 2,
                            ImageRelocation.Amd64_SECREL or ImageRelocation.Amd64_TOKEN => 4,
                            _ => throw Invalid($"unsupported CodeView relocation {relocation.Type}")
                        };
                        if (offset > reader.Length - width || !relocations.TryAdd(offset, relocation))
                            throw Invalid("invalid or duplicate CodeView relocation");
                        ends.Add(offset, checked(offset + width));
                    }
                    int previousEnd = 0;
                    foreach (var range in ends)
                    {
                        if (range.Key < previousEnd)
                            throw Invalid("overlapping CodeView relocations");
                        previousEnd = range.Value;
                    }
                    _section = new DebugSection(section, reader, relocations);
                    _sections.Add(_section);
                    foreach (var handle in reader.Subsections)
                    {
                        var subsection = reader.GetSubsection(handle);
                        _offset = subsection.Offset;
                        if (subsection.IsIgnored)
                            continue;
                        switch (subsection.Kind)
                        {
                            case CodeViewSubsectionKind.StringTable: _strings.Add((_section, subsection)); break;
                            case CodeViewSubsectionKind.FileChecksums: _checksums.Add((_section, subsection)); break;
                            case CodeViewSubsectionKind.Symbols:
                            case CodeViewSubsectionKind.Lines: break;
                            default: Warn($"unsupported CodeView subsection 0x{subsection.RawKind:X}; source information may be omitted"); break;
                        }
                    }
                }
                foreach (DebugSection section in _sections)
                {
                    _section = section;
                    foreach (var handle in section.Reader.Subsections)
                    {
                        var subsection = section.Reader.GetSubsection(handle);
                        _offset = subsection.Offset;
                        if (!subsection.IsIgnored && subsection.Kind == CodeViewSubsectionKind.Symbols)
                            ReadSymbols(subsection);
                    }
                }
                foreach (DebugSection section in _sections)
                {
                    _section = section;
                    foreach (var handle in section.Reader.Subsections)
                    {
                        var subsection = section.Reader.GetSubsection(handle);
                        _offset = subsection.Offset;
                        if (!subsection.IsIgnored && subsection.Kind == CodeViewSubsectionKind.Lines)
                            ReadLines(subsection.GetLines());
                    }
                }
                return _methods.Values;
            }
            catch (BadImageFormatException ex) { throw new LinkException($"{Context}: invalid CodeView: {ex.Message}"); }
            catch (OverflowException ex) { throw new LinkException($"{Context}: invalid CodeView range: {ex.Message}"); }
            catch (ArgumentException ex) { throw new LinkException($"{Context}: invalid CodeView reference: {ex.Message}"); }
        }

        private static bool IsSelected(InputSection section)
        {
            for (InputSection current = section; current != null; current = current.Associated)
                if (current.Winner != current || (current != section && !current.Live))
                    return false;
            return true;
        }

        private CoffRelocation? Relocation(int field, ImageRelocation type)
        {
            if (!_section.Relocations.TryGetValue(field, out var relocation))
                return null;
            if (relocation.Type != type)
                throw Invalid($"expected {type} relocation at +0x{field:X}, found {relocation.Type}");
            return relocation;
        }

        private Handle Token(int field, uint raw)
        {
            if (Relocation(field, ImageRelocation.Amd64_TOKEN) is CoffRelocation relocation)
            {
                var symbol = input.Symbols[relocation.Symbol];
                if (raw != 0 || symbol.Coff.StorageClass != CoffSymbolStorageClass.ClrToken || symbol.Token.IsNil)
                    throw Invalid("TOKEN relocation must reference a CLR token with a zero addend");
                return symbol.Token;
            }
            return MetadataTokens.Handle(checked((int)raw));
        }

        private (InputSection Section, int Offset) Address(int offsetField, uint rawOffset, int segmentField, ushort rawSegment)
        {
            long offset = rawOffset;
            int segment = rawSegment;
            InputSection offsetSection = null;
            if (Relocation(offsetField, ImageRelocation.Amd64_SECREL) is CoffRelocation offsetReloc)
            {
                var symbol = input.Symbols[offsetReloc.Symbol];
                offsetSection = symbol.Section ?? throw Invalid("SECREL relocation has no target section");
                offset += symbol.Coff.Value;
            }
            if (Relocation(segmentField, ImageRelocation.Amd64_SECTION) is CoffRelocation segmentReloc)
            {
                var symbol = input.Symbols[segmentReloc.Symbol];
                if (symbol.Section == null)
                    throw Invalid("SECTION relocation has no target section");
                segment = checked(segment + symbol.Coff.SectionNumber.Number);
            }
            if (segment <= 0 || segment > input.Sections.Count)
                throw Invalid("invalid CodeView segment");
            InputSection section = input.Sections[segment - 1];
            if ((offsetSection != null && offsetSection != section) || !section.IsCode ||
                offset > section.Header.SizeOfRawData)
                throw Invalid("inconsistent or out-of-range CodeView address");
            return (section, checked((int)offset));
        }

        private unsafe DebugMethod Method(Definition definition)
        {
            byte[] bytes = definition.Section.GetData();
            int offset = definition.Offset;
            if (offset < 0 || offset >= bytes.Length)
                throw Invalid("method body is outside its section");
            int codeSize;
            StandaloneSignatureHandle signature;
            fixed (byte* p = bytes)
            {
                var body = MethodBodyBlock.Create(new BlobReader(p + offset, bytes.Length - offset));
                codeSize = body.GetILReader().Length;
                signature = body.LocalSignature;
            }
            int headerSize = (bytes[offset] & 3) == 2 ? 1 :
                (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) >> 12) * 4;
            if (headerSize >= 12)
            {
                int field = offset + 8;
                for (int i = 0; i < definition.Section.Relocations.NumberOfRelocations; i++)
                {
                    var relocation = definition.Section.Relocations.GetRelocation(i);
                    if (relocation.Offset != field)
                        continue;
                    var symbol = input.Symbols[relocation.Symbol];
                    if (relocation.Type != ImageRelocation.Amd64_TOKEN || !signature.IsNil ||
                        symbol.Token.Kind != HandleKind.StandaloneSignature || symbol.Token.IsNil)
                        throw Invalid("invalid method local-signature relocation");
                    signature = (StandaloneSignatureHandle)symbol.Token;
                }
            }
            int locals = 0;
            if (!signature.IsNil)
            {
                var blob = input.Metadata.GetBlobReader(input.Metadata.GetStandaloneSignature(signature).Signature);
                if (blob.ReadSignatureHeader().Kind != SignatureKind.LocalVariables)
                    throw Invalid("method has a non-local standalone signature");
                locals = blob.ReadCompressedInteger();
            }
            return new DebugMethod(definition, checked(offset + headerSize), codeSize, signature, locals);
        }

        private void ReadSymbols(CodeViewSubsection subsection)
        {
            DebugMethod method = null;
            bool inProcedure = false;
            var scopes = new Stack<DebugScope>();
            foreach (var handle in subsection.GetSymbols())
            {
                var symbol = _section.Reader.GetSymbol(handle);
                _offset = symbol.Offset;
                switch (symbol.Kind)
                {
                    case CodeViewSymbolKind.ObjName: _ = symbol.GetObjectName().Name; break;
                    case CodeViewSymbolKind.Compile3: _ = symbol.GetCompile3().CompilerVersion; break;
                    case CodeViewSymbolKind.GManProc:
                        if (inProcedure)
                            throw Invalid("nested managed procedure");
                        inProcedure = true;
                        var procedure = symbol.GetManagedProcedure();
                        Handle token = Token(procedure.TokenFieldOffset, procedure.Token);
                        if (token.IsNil || token.Kind != HandleKind.MethodDefinition ||
                            !input.Definitions.TryGetValue((EntityHandle)token, out var definition))
                            throw Invalid("managed procedure has no MethodDef/body association");
                        if (!selected.Contains(definition))
                            break;
                        method = Method(definition);
                        var address = Address(procedure.CodeOffsetFieldOffset, procedure.CodeOffset,
                            procedure.SegmentFieldOffset, procedure.Segment);
                        if (address.Section != definition.Section || address.Offset != method.CodeStart ||
                            procedure.CodeSize != method.CodeSize)
                            throw Invalid($"procedure '{procedure.Name}' disagrees with its selected method body");
                        if (!_methods.TryAdd(definition, method))
                            throw Invalid("multiple debug procedures describe the same selected method");
                        var root = new DebugScope(0, method.CodeSize);
                        method.Scopes.Add(root);
                        scopes.Push(root);
                        break;
                    case CodeViewSymbolKind.FrameProc: _ = symbol.GetFrameProcedure().Flags; break;
                    case CodeViewSymbolKind.ManSlot:
                        if (!inProcedure)
                            throw Invalid("local slot outside a managed procedure");
                        if (method == null)
                            break;
                        var slot = symbol.GetManagedSlot();
                        if (slot.SlotIndex >= method.LocalCount)
                            throw Invalid($"local '{slot.Name}' has an out-of-range slot {slot.SlotIndex}");
                        Handle type = Token(slot.TypeIndexFieldOffset, slot.TypeIndex);
                        if (!type.IsNil && (type.Kind != HandleKind.StandaloneSignature ||
                            (StandaloneSignatureHandle)type != method.LocalSignature))
                            throw Invalid("local slot does not reference its method's local signature");
                        if (slot.CodeOffset != 0 || slot.Segment != 0 || slot.Flags != 0)
                            Warn("unsupported S_MANSLOT attributes; local names/slots and containing scopes are preserved");
                        var variable = new DebugVariable(checked((int)slot.SlotIndex), slot.Name);
                        if (scopes.Peek().Variables.Any(v => v.Slot == variable.Slot))
                            throw Invalid("duplicate local slot in the same lexical scope");
                        scopes.Peek().Variables.Add(variable);
                        break;
                    case CodeViewSymbolKind.Block32:
                        if (!inProcedure)
                            throw Invalid("lexical block outside a managed procedure");
                        if (method == null)
                            break;
                        var block = symbol.GetBlock();
                        var location = Address(block.CodeOffsetFieldOffset, block.CodeOffset, block.SegmentFieldOffset, block.Segment);
                        int start = checked(location.Offset - method.CodeStart);
                        int length = checked((int)block.CodeSize);
                        DebugScope parent = scopes.Peek();
                        if (location.Section != method.Definition.Section || start < parent.Start ||
                            (long)start + length > (long)parent.Start + parent.Length)
                            throw Invalid("lexical block is outside its containing scope");
                        var scope = new DebugScope(start, length);
                        method.Scopes.Add(scope);
                        scopes.Push(scope);
                        break;
                    case CodeViewSymbolKind.End:
                        if (method != null)
                        {
                            if (scopes.Count <= 1)
                                throw Invalid("unexpected lexical scope terminator");
                            scopes.Pop();
                        }
                        else if (!inProcedure)
                            throw Invalid("unexpected scope terminator");
                        break;
                    case CodeViewSymbolKind.ProcIdEnd:
                        if (!inProcedure || (method != null && scopes.Count != 1))
                            throw Invalid("unbalanced managed procedure scopes");
                        inProcedure = false;
                        method = null;
                        scopes.Clear();
                        break;
                    default:
                        if (!inProcedure || method != null)
                            Warn($"unsupported CodeView symbol 0x{(ushort)symbol.Kind:X4}; source information may be omitted");
                        break;
                }
            }
            if (inProcedure)
                throw Invalid("unterminated managed procedure");
        }

        private CodeViewSubsection Table(List<(DebugSection Section, CodeViewSubsection Table)> tables, string name)
        {
            var local = tables.Where(t => t.Section == _section).ToArray();
            if (local.Length == 1)
                return local[0].Table;
            if (local.Length == 0 && tables.Count == 1)
                return tables[0].Table;
            throw Invalid($"missing or ambiguous {name} table");
        }

        private DebugDocument Document(CodeViewFileHandle handle)
        {
            var file = Table(_checksums, "checksum").GetFileChecksums().GetFileChecksum(handle);
            string name = Table(_strings, "string").GetStringTable().GetString(file.Name);
            var checksum = file.GetChecksumReader();
            Guid algorithm = default;
            string hash = "";
            if (file.ChecksumType == CodeViewChecksumType.SHA256)
            {
                if (checksum.RemainingBytes != 32)
                    throw Invalid("invalid SHA-256 document checksum length");
                algorithm = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");
                hash = Convert.ToHexString(checksum.ReadBytes(32));
            }
            else if (file.ChecksumType == CodeViewChecksumType.None)
            {
                if (checksum.RemainingBytes != 0)
                    throw Invalid("checksum bytes without a checksum algorithm");
            }
            else
                Warn($"unsupported document checksum algorithm {(byte)file.ChecksumType}; checksum omitted");
            return new DebugDocument(name, algorithm, hash);
        }

        private void ReadLines(CodeViewLineTable lines)
        {
            var address = Address(lines.CodeOffsetFieldOffset, lines.CodeOffset, lines.SegmentFieldOffset, lines.Segment);
            if (!address.Section.Live || address.Section.Winner != address.Section)
                return;
            long end = (long)address.Offset + lines.CodeSize;
            if (end > address.Section.Header.SizeOfRawData)
                throw Invalid("line contribution extends beyond its section");
            var candidates = _methods.Values.Where(m => m.Definition.Section == address.Section &&
                address.Offset >= m.CodeStart && end <= (long)m.CodeStart + m.CodeSize).ToArray();
            if (candidates.Length == 0)
            {
                Warn("line contribution has no supported managed procedure; source lines omitted");
                return;
            }
            if (candidates.Length != 1)
                throw Invalid("ambiguous managed method for line contribution");
            DebugMethod method = candidates[0];
            if ((lines.Flags & ~1) != 0)
                Warn("unsupported CodeView line flags; source lines are preserved without these flags");
            foreach (var block in lines.FileBlocks)
            {
                if (block.LineCount == 0)
                    continue;
                DebugDocument document = Document(block.File);
                foreach (var line in block.Lines)
                {
                    _offset = line.Offset;
                    if (line.CodeOffset >= lines.CodeSize)
                        throw Invalid("source line offset is outside its contribution");
                    int offset = checked(address.Offset - method.CodeStart + (int)line.CodeOffset);
                    bool hidden = line.StartLine is 0xfeefee or 0xf00f00;
                    int endLine = checked(line.StartLine + line.EndLineDelta);
                    if (!hidden && endLine >= SequencePoint.HiddenLine)
                    {
                        Warn("source line exceeds the Portable PDB line range; sequence point omitted");
                        continue;
                    }
                    if (line.StartLine == 0xf00f00)
                        Warn("CodeView step-through line semantics are represented as hidden sequence points");
                    if (!line.IsStatement)
                        Warn("Portable PDB sequence points cannot preserve the CodeView expression/statement distinction");
                    int startColumn = line.StartColumn ?? 0;
                    int endColumn = line.EndColumn ?? (line.EndLineDelta == 0 ? 1 : 0);
                    if (!hidden && (startColumn == ushort.MaxValue || endColumn == ushort.MaxValue))
                    {
                        Warn("source column exceeds the Portable PDB column range; columns omitted");
                        startColumn = 0;
                        endColumn = line.EndLineDelta == 0 ? 1 : 0;
                    }
                    if (!hidden && line.EndLineDelta == 0 && endColumn <= startColumn)
                        throw Invalid("invalid source column range");
                    var point = new DebugPoint(offset, document, line.StartLine, endLine, startColumn, endColumn, hidden);
                    if (method.Points.TryGetValue(offset, out var previous) && previous != point)
                        throw Invalid("conflicting source mappings at the same IL offset");
                    if (previous == null)
                        method.Points.Add(offset, point);
                }
            }
        }
    }
}
