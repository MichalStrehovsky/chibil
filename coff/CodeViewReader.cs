using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;

namespace Coff
{
    /// <summary>Reads one complete C13 .debug$S section without applying relocations.</summary>
    /// <remarks>
    /// The caller must keep the underlying CoffReader undisposed, or externally supplied memory
    /// alive and unchanged. Handles are local to their reader or explicitly selected table.
    /// Address and token values are unrelocated; field offsets are relative to the section start,
    /// including the C13 signature. This reader does not read .debug$T streams.
    /// Strings require bounded NUL termination; invalid UTF-8 handling follows the supplied decoder.
    /// </remarks>
    public sealed class CodeViewReader
    {
        internal readonly MemoryBlock Block;
        private readonly MetadataStringDecoder _utf8Decoder;
        private readonly object _memoryOwner;

        public unsafe CodeViewReader(byte* debugSection, int length)
            : this(debugSection, length, MetadataStringDecoder.DefaultUTF8, null) { }

        public unsafe CodeViewReader(byte* debugSection, int length, MetadataStringDecoder utf8Decoder)
            : this(debugSection, length, utf8Decoder, null) { }

        internal unsafe CodeViewReader(byte* debugSection, int length, MetadataStringDecoder utf8Decoder, object memoryOwner)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (debugSection == null && length != 0)
                throw new ArgumentNullException(nameof(debugSection));
            ArgumentNullException.ThrowIfNull(utf8Decoder);
            if (utf8Decoder.Encoding is not UTF8Encoding)
                throw new ArgumentException("The decoder must use UTF-8.", nameof(utf8Decoder));

            Block = new MemoryBlock(debugSection, length);
            _utf8Decoder = utf8Decoder;
            _memoryOwner = memoryOwner;
            if (Block.PeekUInt32(0) != CodeView.SignatureC13)
                throw new BadImageFormatException("Unsupported CodeView signature.");
        }

        public int Length => Block.Length;
        public CodeViewSubsectionHandleCollection Subsections => new CodeViewSubsectionHandleCollection(this);

        public CodeViewSubsection GetSubsection(CodeViewSubsectionHandle handle)
        {
            if (handle.Offset < 4)
                throw new ArgumentOutOfRangeException(nameof(handle));
            var header = GetRange(handle.Offset, 8, Length);
            int size = ReadSize(header.PeekUInt32(4));
            GetRange(handle.Offset + 8, size, Length);
            return new CodeViewSubsection(this, handle.Offset, size);
        }

        public CodeViewSymbol GetSymbol(CodeViewSymbolHandle handle)
        {
            if (handle.IsNil)
                throw new ArgumentOutOfRangeException(nameof(handle));
            var subsection = GetSubsection(new CodeViewSubsectionHandle(handle.SubsectionOffset));
            if (subsection.Kind != CodeViewSubsectionKind.Symbols || handle.Offset < subsection.PayloadOffset)
                throw new ArgumentException("The handle must refer to a CodeView symbol.", nameof(handle));
            var header = GetRange(handle.Offset, 4, subsection.End);
            int size = header.PeekUInt16(0) + 2;
            if (size < 4)
                throw new BadImageFormatException("Invalid CodeView symbol length.");
            GetRange(handle.Offset, size, subsection.End);
            var symbol = new CodeViewSymbol(this, handle, size);
            if (symbol.Kind is CodeViewSymbolKind.End or CodeViewSymbolKind.ProcIdEnd && symbol.PayloadLength != 0)
                throw new BadImageFormatException("Invalid CodeView scope terminator.");
            return symbol;
        }

        internal MemoryBlock GetRange(int offset, int length, int end)
        {
            if (offset < 0 || length < 0 || end > Length || (long)offset + length > end)
                throw new BadImageFormatException("CodeView data extends beyond its record.");
            return Block.GetMemoryBlockAt(offset, length);
        }

        internal MemoryBlock GetStringBlock(int offset, int end)
        {
            var block = GetRange(offset, end - offset, end);
            int length = block.PeekBytesSpan(0, block.Length).IndexOf((byte)0);
            if (length < 0)
                throw new BadImageFormatException("Unterminated CodeView string.");
            return block.GetMemoryBlockAt(0, length);
        }

        internal unsafe string DecodeString(MemoryBlock block)
        {
            string result = _utf8Decoder.GetString(block.Pointer, block.Length);
            GC.KeepAlive(this);
            return result;
        }

        internal static unsafe BlobReader CreateBlobReader(MemoryBlock block)
            => new BlobReader(block.Pointer, block.Length);

        internal static int ReadSize(uint size)
        {
            if (size > int.MaxValue)
                throw new BadImageFormatException("CodeView size exceeds the supported range.");
            return (int)size;
        }

        internal static int Align4(int offset, int end)
        {
            long aligned = ((long)offset + 3) & ~3L;
            if (aligned > end)
                throw new BadImageFormatException("Truncated CodeView alignment padding.");
            return (int)aligned;
        }
    }

    /// <summary>A section-relative subsection location; the default handle is nil.</summary>
    public readonly struct CodeViewSubsectionHandle : IEquatable<CodeViewSubsectionHandle>
    {
        internal CodeViewSubsectionHandle(int offset) => Offset = offset;
        public bool IsNil => Offset == 0;
        public int Offset { get; }
        public bool Equals(CodeViewSubsectionHandle other) => Offset == other.Offset;
        public override bool Equals(object obj) => obj is CodeViewSubsectionHandle other && Equals(other);
        public override int GetHashCode() => Offset;
        public static bool operator ==(CodeViewSubsectionHandle left, CodeViewSubsectionHandle right) => left.Equals(right);
        public static bool operator !=(CodeViewSubsectionHandle left, CodeViewSubsectionHandle right) => !left.Equals(right);
    }

    /// <summary>A section-relative symbol location; the default handle is nil.</summary>
    public readonly struct CodeViewSymbolHandle : IEquatable<CodeViewSymbolHandle>
    {
        internal readonly int SubsectionOffset;
        internal CodeViewSymbolHandle(int offset, int subsectionOffset)
            => (Offset, SubsectionOffset) = (offset, subsectionOffset);
        public bool IsNil => Offset == 0;
        public int Offset { get; }
        public bool Equals(CodeViewSymbolHandle other) => Offset == other.Offset;
        public override bool Equals(object obj) => obj is CodeViewSymbolHandle other && Equals(other);
        public override int GetHashCode() => Offset;
        public static bool operator ==(CodeViewSymbolHandle left, CodeViewSymbolHandle right) => left.Equals(right);
        public static bool operator !=(CodeViewSymbolHandle left, CodeViewSymbolHandle right) => !left.Equals(right);
    }

    /// <summary>A byte offset into a caller-selected string table. Offset zero is a valid empty string.</summary>
    public readonly struct CodeViewStringHandle : IEquatable<CodeViewStringHandle>
    {
        internal CodeViewStringHandle(int offset) => Offset = offset;
        public int Offset { get; }
        public bool Equals(CodeViewStringHandle other) => Offset == other.Offset;
        public override bool Equals(object obj) => obj is CodeViewStringHandle other && Equals(other);
        public override int GetHashCode() => Offset;
        public static bool operator ==(CodeViewStringHandle left, CodeViewStringHandle right) => left.Equals(right);
        public static bool operator !=(CodeViewStringHandle left, CodeViewStringHandle right) => !left.Equals(right);
    }

    /// <summary>A subsection header and bounded payload, including unknown and ignored kinds.</summary>
    /// <remarks>Offsets are section-relative. Size includes the header but excludes alignment padding.
    /// Kind excludes only the ignore bit, which is preserved in RawKind and exposed by IsIgnored.</remarks>
    public readonly struct CodeViewSubsection
    {
        internal readonly CodeViewReader Reader;
        private readonly int _payloadLength;
        internal CodeViewSubsection(CodeViewReader reader, int offset, int payloadLength)
            => (Reader, Offset, _payloadLength) = (reader, offset, payloadLength);
        public CodeViewSubsectionHandle Handle => new CodeViewSubsectionHandle(Offset);
        public uint RawKind => Reader.Block.PeekUInt32(Offset);
        public CodeViewSubsectionKind Kind => (CodeViewSubsectionKind)(RawKind & 0x7fffffff);
        public bool IsIgnored => (RawKind & 0x80000000) != 0;
        public int Offset { get; }
        public int Size => 8 + PayloadLength;
        public int PayloadOffset => Offset + 8;
        public int PayloadLength => _payloadLength;
        internal int End => PayloadOffset + PayloadLength;
        internal MemoryBlock Data => Reader.GetRange(PayloadOffset, PayloadLength, End);

        /// <summary>Returns a borrowed payload reader. Keep the memory owner alive and undisposed.</summary>
        public BlobReader GetPayloadReader() => CodeViewReader.CreateBlobReader(Data);

        public CodeViewSymbolHandleCollection GetSymbols()
        {
            RequireKind(CodeViewSubsectionKind.Symbols);
            return new CodeViewSymbolHandleCollection(this);
        }

        public CodeViewLineTable GetLines()
        {
            RequireKind(CodeViewSubsectionKind.Lines);
            if (PayloadLength < 12)
                throw new BadImageFormatException("Truncated CodeView line contribution.");
            return new CodeViewLineTable(this);
        }

        public CodeViewStringTable GetStringTable()
        {
            RequireKind(CodeViewSubsectionKind.StringTable);
            return new CodeViewStringTable(this);
        }

        public CodeViewFileChecksumTable GetFileChecksums()
        {
            RequireKind(CodeViewSubsectionKind.FileChecksums);
            return new CodeViewFileChecksumTable(this);
        }

        private void RequireKind(CodeViewSubsectionKind kind)
        {
            if (Kind != kind)
                throw new InvalidOperationException("The CodeView subsection has a different kind.");
        }
    }

    /// <summary>A symbol header and bounded payload. All offsets are section-relative.</summary>
    /// <remarks>
    /// Size includes the length and kind fields; PayloadLength excludes both.
    /// Typed getters throw InvalidOperationException for an incompatible kind and
    /// BadImageFormatException for a truncated payload.
    /// </remarks>
    public readonly struct CodeViewSymbol
    {
        internal readonly CodeViewReader Reader;
        internal CodeViewSymbol(CodeViewReader reader, CodeViewSymbolHandle handle, int size)
            => (Reader, Handle, Size) = (reader, handle, size);
        public CodeViewSymbolHandle Handle { get; }
        public CodeViewSubsectionHandle Subsection => new CodeViewSubsectionHandle(Handle.SubsectionOffset);
        public CodeViewSymbolKind Kind => (CodeViewSymbolKind)Reader.Block.PeekUInt16(Offset + 2);
        public int Offset => Handle.Offset;
        public int Size { get; }
        public int PayloadOffset => Offset + 4;
        public int PayloadLength => Size - 4;
        internal MemoryBlock Data => Reader.GetRange(PayloadOffset, PayloadLength, Offset + Size);
        internal string ReadString(int offset)
            => Reader.DecodeString(Reader.GetStringBlock(PayloadOffset + offset, Offset + Size));
        /// <summary>Returns a borrowed payload reader. Keep the memory owner alive and undisposed.</summary>
        public BlobReader GetPayloadReader() => CodeViewReader.CreateBlobReader(Data);

        public CodeViewObjectNameSymbol GetObjectName()
            => new CodeViewObjectNameSymbol(GetTypedSymbol(CodeViewSymbolKind.ObjName, 5));
        public CodeViewCompile3Symbol GetCompile3()
            => new CodeViewCompile3Symbol(GetTypedSymbol(CodeViewSymbolKind.Compile3, 23));
        public CodeViewManagedProcedureSymbol GetManagedProcedure()
            => new CodeViewManagedProcedureSymbol(GetTypedSymbol(CodeViewSymbolKind.GManProc, 38));
        public CodeViewFrameProcedureSymbol GetFrameProcedure()
            => new CodeViewFrameProcedureSymbol(GetTypedSymbol(CodeViewSymbolKind.FrameProc, 26));
        public CodeViewManagedSlotSymbol GetManagedSlot()
            => new CodeViewManagedSlotSymbol(GetTypedSymbol(CodeViewSymbolKind.ManSlot, 17));
        public CodeViewBlockSymbol GetBlock()
            => new CodeViewBlockSymbol(GetTypedSymbol(CodeViewSymbolKind.Block32, 19));

        private CodeViewSymbol GetTypedSymbol(CodeViewSymbolKind kind, int minimumSize)
        {
            if (Kind != kind)
                throw new InvalidOperationException("The CodeView symbol has a different kind.");
            if (PayloadLength < minimumSize)
                throw new BadImageFormatException("Truncated CodeView symbol payload.");
            return this;
        }
    }

    /// <summary>An S_OBJNAME record.</summary>
    public readonly struct CodeViewObjectNameSymbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewObjectNameSymbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint Signature => _symbol.Data.PeekUInt32(0);
        public string Name => _symbol.ReadString(4);
    }

    /// <summary>An S_COMPILE3 record, retaining unknown machine, language, and flag values.</summary>
    public readonly struct CodeViewCompile3Symbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewCompile3Symbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint RawFlags => _symbol.Data.PeekUInt32(0);
        public CodeViewLanguage Language => (CodeViewLanguage)(RawFlags & 0xff);
        public CodeViewCompileFlags Flags => (CodeViewCompileFlags)(RawFlags & 0xffffff00);
        public CodeViewMachine Machine => (CodeViewMachine)_symbol.Data.PeekUInt16(4);
        public ushort FrontendMajorVersion => _symbol.Data.PeekUInt16(6);
        public ushort FrontendMinorVersion => _symbol.Data.PeekUInt16(8);
        public ushort FrontendBuildVersion => _symbol.Data.PeekUInt16(10);
        public ushort FrontendQfeVersion => _symbol.Data.PeekUInt16(12);
        public ushort BackendMajorVersion => _symbol.Data.PeekUInt16(14);
        public ushort BackendMinorVersion => _symbol.Data.PeekUInt16(16);
        public ushort BackendBuildVersion => _symbol.Data.PeekUInt16(18);
        public ushort BackendQfeVersion => _symbol.Data.PeekUInt16(20);
        public string CompilerVersion => _symbol.ReadString(22);
    }

    /// <summary>An S_GMANPROC record. Token and address values are unrelocated; field offsets are section-relative.</summary>
    public readonly struct CodeViewManagedProcedureSymbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewManagedProcedureSymbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint ParentSymbolOffset => _symbol.Data.PeekUInt32(0);
        public uint EndSymbolOffset => _symbol.Data.PeekUInt32(4);
        public uint NextSymbolOffset => _symbol.Data.PeekUInt32(8);
        public uint CodeSize => _symbol.Data.PeekUInt32(12);
        public uint DebugStart => _symbol.Data.PeekUInt32(16);
        public uint DebugEnd => _symbol.Data.PeekUInt32(20);
        public uint Token => _symbol.Data.PeekUInt32(24);
        public uint CodeOffset => _symbol.Data.PeekUInt32(28);
        public ushort Segment => _symbol.Data.PeekUInt16(32);
        public byte Flags => _symbol.Data.PeekByte(34);
        public ushort ReturnRegister => _symbol.Data.PeekUInt16(35);
        public string Name => _symbol.ReadString(37);
        public int TokenFieldOffset => _symbol.PayloadOffset + 24;
        public int CodeOffsetFieldOffset => _symbol.PayloadOffset + 28;
        public int SegmentFieldOffset => _symbol.PayloadOffset + 32;
    }

    /// <summary>An S_FRAMEPROC record. Exception-handler field offsets are section-relative.</summary>
    public readonly struct CodeViewFrameProcedureSymbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewFrameProcedureSymbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint FrameSize => _symbol.Data.PeekUInt32(0);
        public uint PaddingSize => _symbol.Data.PeekUInt32(4);
        public uint PaddingOffset => _symbol.Data.PeekUInt32(8);
        public uint SavedRegistersSize => _symbol.Data.PeekUInt32(12);
        public uint ExceptionHandlerOffset => _symbol.Data.PeekUInt32(16);
        public ushort ExceptionHandlerSegment => _symbol.Data.PeekUInt16(20);
        public CodeViewFrameProcFlags Flags => (CodeViewFrameProcFlags)_symbol.Data.PeekUInt32(22);
        public int ExceptionHandlerOffsetFieldOffset => _symbol.PayloadOffset + 16;
        public int ExceptionHandlerSegmentFieldOffset => _symbol.PayloadOffset + 20;
    }

    /// <summary>An S_MANSLOT record. TypeIndex is a raw producer-specific value, not a resolved metadata handle.</summary>
    public readonly struct CodeViewManagedSlotSymbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewManagedSlotSymbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint SlotIndex => _symbol.Data.PeekUInt32(0);
        public uint TypeIndex => _symbol.Data.PeekUInt32(4);
        public uint CodeOffset => _symbol.Data.PeekUInt32(8);
        public ushort Segment => _symbol.Data.PeekUInt16(12);
        public ushort Flags => _symbol.Data.PeekUInt16(14);
        public string Name => _symbol.ReadString(16);
        public int TypeIndexFieldOffset => _symbol.PayloadOffset + 4;
        public int CodeOffsetFieldOffset => _symbol.PayloadOffset + 8;
        public int SegmentFieldOffset => _symbol.PayloadOffset + 12;
    }

    /// <summary>An S_BLOCK32 record. Code and segment field offsets are section-relative; symbol links remain raw.</summary>
    public readonly struct CodeViewBlockSymbol
    {
        private readonly CodeViewSymbol _symbol;
        internal CodeViewBlockSymbol(CodeViewSymbol symbol) => _symbol = symbol;
        public CodeViewSymbolHandle Handle => _symbol.Handle;
        public uint ParentSymbolOffset => _symbol.Data.PeekUInt32(0);
        public uint EndSymbolOffset => _symbol.Data.PeekUInt32(4);
        public uint CodeSize => _symbol.Data.PeekUInt32(8);
        public uint CodeOffset => _symbol.Data.PeekUInt32(12);
        public ushort Segment => _symbol.Data.PeekUInt16(16);
        public string Name => _symbol.ReadString(18);
        public int CodeOffsetFieldOffset => _symbol.PayloadOffset + 12;
        public int SegmentFieldOffset => _symbol.PayloadOffset + 16;
    }

    /// <summary>Resolves byte offsets into one string-table subsection, including bounded suffix references.</summary>
    public readonly struct CodeViewStringTable
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewStringTable(CodeViewSubsection subsection) => _subsection = subsection;
        public CodeViewSubsectionHandle Subsection => _subsection.Handle;
        public CodeViewStringHandleCollection Strings => new CodeViewStringHandleCollection(_subsection);

        private MemoryBlock GetStringBlock(CodeViewStringHandle handle)
        {
            if ((uint)handle.Offset >= (uint)_subsection.PayloadLength)
                throw new BadImageFormatException("Invalid CodeView string-table offset.");
            return _subsection.Reader.GetStringBlock(_subsection.PayloadOffset + handle.Offset, _subsection.End);
        }

        public string GetString(CodeViewStringHandle handle)
            => _subsection.Reader.DecodeString(GetStringBlock(handle));

        /// <summary>Returns borrowed UTF-8 bytes, excluding the NUL terminator.</summary>
        public BlobReader GetBlobReader(CodeViewStringHandle handle)
            => CodeViewReader.CreateBlobReader(GetStringBlock(handle));
    }

    /// <summary>Reads one checksum table; callers explicitly choose the string table for filename lookup.</summary>
    public readonly struct CodeViewFileChecksumTable
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewFileChecksumTable(CodeViewSubsection subsection) => _subsection = subsection;
        public CodeViewSubsectionHandle Subsection => _subsection.Handle;
        public CodeViewFileHandleCollection Files => new CodeViewFileHandleCollection(_subsection);

        public CodeViewFileChecksum GetFileChecksum(CodeViewFileHandle handle)
        {
            if ((uint)handle.Offset >= (uint)_subsection.PayloadLength)
                throw new BadImageFormatException("Invalid CodeView checksum-table offset.");
            var checksum = new CodeViewFileChecksum(_subsection, _subsection.PayloadOffset + handle.Offset);
            _ = checksum.End;
            return checksum;
        }
    }

    /// <summary>A checksum entry. Offset is section-relative; Handle and Name are table-relative.</summary>
    public readonly struct CodeViewFileChecksum
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewFileChecksum(CodeViewSubsection subsection, int offset)
            => (_subsection, Offset) = (subsection, offset);
        public CodeViewFileHandle Handle => new CodeViewFileHandle(Offset - _subsection.PayloadOffset);
        public CodeViewSubsectionHandle Subsection => _subsection.Handle;
        public int Offset { get; }
        private MemoryBlock Data => _subsection.Reader.GetRange(Offset, _subsection.End - Offset, _subsection.End);
        public CodeViewStringHandle Name => new CodeViewStringHandle(unchecked((int)Data.PeekUInt32(0)));
        public CodeViewChecksumType ChecksumType => (CodeViewChecksumType)Data.PeekByte(5);
        public int ChecksumLength => Data.PeekByte(4);
        internal int End
        {
            get
            {
                _subsection.Reader.GetRange(Offset, 6, _subsection.End);
                _subsection.Reader.GetRange(Offset + 6, ChecksumLength, _subsection.End);
                return CodeViewReader.Align4(Offset + 6 + ChecksumLength, _subsection.End);
            }
        }
        /// <summary>Returns borrowed checksum bytes, excluding alignment padding.</summary>
        public BlobReader GetChecksumReader()
            => CodeViewReader.CreateBlobReader(_subsection.Reader.GetRange(Offset + 6, ChecksumLength, _subsection.End));
    }

    /// <summary>A line contribution with unrelocated addresses and section-relative relocation field offsets.</summary>
    public readonly struct CodeViewLineTable
    {
        internal readonly CodeViewSubsection Data;
        internal CodeViewLineTable(CodeViewSubsection subsection) => Data = subsection;
        public CodeViewSubsectionHandle Subsection => Data.Handle;
        public uint CodeOffset => Data.Data.PeekUInt32(0);
        public ushort Segment => Data.Data.PeekUInt16(4);
        public ushort Flags => Data.Data.PeekUInt16(6);
        public bool HasColumns => (Flags & 1) != 0;
        public uint CodeSize => Data.Data.PeekUInt32(8);
        public int CodeOffsetFieldOffset => Data.PayloadOffset;
        public int SegmentFieldOffset => Data.PayloadOffset + 4;
        public CodeViewLineFileBlockCollection FileBlocks => new CodeViewLineFileBlockCollection(this);
    }

    /// <summary>A line-file block. Offset is section-relative; File refers to a caller-selected checksum table.</summary>
    /// <remarks>Size includes the header, line entries, and any column array.</remarks>
    public readonly struct CodeViewLineFileBlock
    {
        private readonly CodeViewLineTable _table;
        internal CodeViewLineFileBlock(CodeViewLineTable table, int offset)
        {
            _table = table;
            Offset = offset;
            var header = table.Data.Reader.GetRange(offset, 12, table.Data.End);
            LineCount = CodeViewReader.ReadSize(header.PeekUInt32(4));
            Size = CodeViewReader.ReadSize(header.PeekUInt32(8));
            long requiredSize = 12 + (table.HasColumns ? 12L : 8L) * LineCount;
            if (Size != requiredSize)
                throw new BadImageFormatException("Inconsistent CodeView line-file block size.");
            table.Data.Reader.GetRange(offset, Size, table.Data.End);
        }
        public int Offset { get; }
        public CodeViewFileHandle File => new CodeViewFileHandle(unchecked((int)_table.Data.Reader.Block.PeekUInt32(Offset)));
        public int LineCount { get; }
        public int Size { get; }
        public CodeViewLineCollection Lines => new CodeViewLineCollection(_table.Data.Reader, Offset + 12, LineCount, _table.HasColumns);
    }

    /// <summary>A source line. Offset is section-relative; CodeOffset is relative to its code contribution.</summary>
    /// <remarks>Columns are null when absent. Special line values remain raw, without Portable PDB projection.</remarks>
    public readonly struct CodeViewLine
    {
        private readonly CodeViewReader _reader;
        private readonly int _columnOffset;
        internal CodeViewLine(CodeViewReader reader, int offset, int columnOffset)
            => (_reader, Offset, _columnOffset) = (reader, offset, columnOffset);
        public int Offset { get; }
        public uint CodeOffset => _reader.Block.PeekUInt32(Offset);
        public uint RawFlags => _reader.Block.PeekUInt32(Offset + 4);
        public int StartLine => (int)(RawFlags & 0xffffff);
        public byte EndLineDelta => (byte)((RawFlags >> 24) & 0x7f);
        public bool IsStatement => (RawFlags & CodeView.LineIsStatement) != 0;
        public ushort? StartColumn => _columnOffset < 0 ? null : _reader.Block.PeekUInt16(_columnOffset);
        public ushort? EndColumn => _columnOffset < 0 ? null : _reader.Block.PeekUInt16(_columnOffset + 2);
    }

    internal struct CodeViewCursor
    {
        private readonly int _start;
        internal readonly int End;
        private int _next;
        internal int Offset { get; private set; }
        internal CodeViewCursor(int start, int end)
            => (_start, End, _next, Offset) = (start, end, start, -1);
        internal bool MoveNext()
        {
            Offset = _next < End ? _next : -1;
            return Offset >= 0;
        }
        internal void Advance(int next)
        {
            if (next <= Offset || next > End)
                throw new BadImageFormatException("Invalid CodeView record extent.");
            _next = next;
        }
        internal void Reset() => (_next, Offset) = (_start, -1);
    }

    /// <summary>Enumerates subsection handles, validating framing and four-byte alignment.</summary>
    public readonly struct CodeViewSubsectionHandleCollection : IEnumerable<CodeViewSubsectionHandle>
    {
        private readonly CodeViewReader _reader;
        internal CodeViewSubsectionHandleCollection(CodeViewReader reader) => _reader = reader;
        public Enumerator GetEnumerator() => new Enumerator(_reader);
        IEnumerator<CodeViewSubsectionHandle> IEnumerable<CodeViewSubsectionHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewSubsectionHandle>
        {
            private readonly CodeViewReader _reader;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewReader reader)
                => (_reader, _cursor) = (reader, new CodeViewCursor(4, reader.Length));
            public CodeViewSubsectionHandle Current => new CodeViewSubsectionHandle(_cursor.Offset);
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                var subsection = _reader.GetSubsection(Current);
                _cursor.Advance(CodeViewReader.Align4(subsection.End, _cursor.End));
                return true;
            }
            public void Reset() => _cursor.Reset();
            public void Dispose() { }
        }
    }

    /// <summary>Enumerates length-prefixed symbol records without imposing inter-record alignment.</summary>
    public readonly struct CodeViewSymbolHandleCollection : IEnumerable<CodeViewSymbolHandle>
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewSymbolHandleCollection(CodeViewSubsection subsection) => _subsection = subsection;
        public Enumerator GetEnumerator() => new Enumerator(_subsection);
        IEnumerator<CodeViewSymbolHandle> IEnumerable<CodeViewSymbolHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewSymbolHandle>
        {
            private readonly CodeViewSubsection _subsection;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewSubsection subsection)
                => (_subsection, _cursor) = (subsection, new CodeViewCursor(subsection.PayloadOffset, subsection.End));
            public CodeViewSymbolHandle Current => new CodeViewSymbolHandle(_cursor.Offset, _subsection.Offset);
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                var symbol = _subsection.Reader.GetSymbol(Current);
                _cursor.Advance(symbol.Offset + symbol.Size);
                return true;
            }
            public void Reset() => _cursor.Reset();
            public void Dispose() { }
        }
    }

    /// <summary>Enumerates consecutive NUL-terminated entries in a string table.</summary>
    public readonly struct CodeViewStringHandleCollection : IEnumerable<CodeViewStringHandle>
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewStringHandleCollection(CodeViewSubsection subsection) => _subsection = subsection;
        public Enumerator GetEnumerator() => new Enumerator(_subsection);
        IEnumerator<CodeViewStringHandle> IEnumerable<CodeViewStringHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewStringHandle>
        {
            private readonly CodeViewSubsection _subsection;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewSubsection subsection)
                => (_subsection, _cursor) = (subsection, new CodeViewCursor(subsection.PayloadOffset, subsection.End));
            public CodeViewStringHandle Current => new CodeViewStringHandle(_cursor.Offset - _subsection.PayloadOffset);
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                var text = _subsection.Reader.GetStringBlock(_cursor.Offset, _cursor.End);
                _cursor.Advance(_cursor.Offset + text.Length + 1);
                return true;
            }
            public void Reset() => _cursor.Reset();
            public void Dispose() { }
        }
    }

    /// <summary>Enumerates aligned checksum-entry byte offsets, including valid offset zero.</summary>
    public readonly struct CodeViewFileHandleCollection : IEnumerable<CodeViewFileHandle>
    {
        private readonly CodeViewSubsection _subsection;
        internal CodeViewFileHandleCollection(CodeViewSubsection subsection) => _subsection = subsection;
        public Enumerator GetEnumerator() => new Enumerator(_subsection);
        IEnumerator<CodeViewFileHandle> IEnumerable<CodeViewFileHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewFileHandle>
        {
            private readonly CodeViewSubsection _subsection;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewSubsection subsection)
                => (_subsection, _cursor) = (subsection, new CodeViewCursor(subsection.PayloadOffset, subsection.End));
            public CodeViewFileHandle Current => new CodeViewFileHandle(_cursor.Offset - _subsection.PayloadOffset);
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                var checksum = new CodeViewFileChecksum(_subsection, _cursor.Offset);
                _cursor.Advance(checksum.End);
                return true;
            }
            public void Reset() => _cursor.Reset();
            public void Dispose() { }
        }
    }

    /// <summary>Enumerates bounded file blocks within a line contribution.</summary>
    public readonly struct CodeViewLineFileBlockCollection : IEnumerable<CodeViewLineFileBlock>
    {
        private readonly CodeViewLineTable _table;
        internal CodeViewLineFileBlockCollection(CodeViewLineTable table) => _table = table;
        public Enumerator GetEnumerator() => new Enumerator(_table);
        IEnumerator<CodeViewLineFileBlock> IEnumerable<CodeViewLineFileBlock>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewLineFileBlock>
        {
            private readonly CodeViewLineTable _table;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewLineTable table)
                => (_table, _cursor) = (table, new CodeViewCursor(table.Data.PayloadOffset + 12, table.Data.End));
            public CodeViewLineFileBlock Current { get; private set; }
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                Current = new CodeViewLineFileBlock(_table, _cursor.Offset);
                _cursor.Advance(Current.Offset + Current.Size);
                return true;
            }
            public void Reset()
            {
                _cursor.Reset();
                Current = default;
            }
            public void Dispose() { }
        }
    }

    /// <summary>Enumerates line entries with their corresponding optional columns.</summary>
    public readonly struct CodeViewLineCollection : IReadOnlyCollection<CodeViewLine>
    {
        private readonly CodeViewReader _reader;
        private readonly int _start, _count;
        private readonly bool _hasColumns;
        internal CodeViewLineCollection(CodeViewReader reader, int start, int count, bool hasColumns)
            => (_reader, _start, _count, _hasColumns) = (reader, start, count, hasColumns);
        public int Count => _count;
        public Enumerator GetEnumerator() => new Enumerator(_reader, _start, _count, _hasColumns);
        IEnumerator<CodeViewLine> IEnumerable<CodeViewLine>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CodeViewLine>
        {
            private readonly CodeViewReader _reader;
            private readonly int _linesStart, _columnsStart;
            private CodeViewCursor _cursor;
            internal Enumerator(CodeViewReader reader, int start, int count, bool hasColumns)
            {
                _reader = reader;
                _linesStart = start;
                int end = start + count * 8;
                _cursor = new CodeViewCursor(start, end);
                _columnsStart = hasColumns ? end : -1;
            }
            public CodeViewLine Current { get; private set; }
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (!_cursor.MoveNext())
                    return false;
                int columnOffset = _columnsStart < 0 ? -1 : _columnsStart + (_cursor.Offset - _linesStart) / 2;
                Current = new CodeViewLine(_reader, _cursor.Offset, columnOffset);
                _cursor.Advance(_cursor.Offset + 8);
                return true;
            }
            public void Reset()
            {
                _cursor.Reset();
                Current = default;
            }
            public void Dispose() { }
        }
    }
}
