using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Coff
{
    /// <summary>
    /// Reads a Microsoft COFF archive. Returned readers, records, blocks, and spans
    /// borrow its memory and must not be used after this reader is disposed.
    /// </summary>
    public sealed class CoffArchiveReader : IDisposable
    {
        internal const int HeaderSize = 60;
        private readonly object _guard = new object();
        private MemoryBlockProvider _provider;
        private AbstractMemoryBlock _block;
        private ExternalMemoryBlock[] _memberBlocks;
        private CoffArchiveMemberTableReader _members;
        private CoffArchiveSymbolTableReader _symbols;
        private CoffArchiveStringTableReader _strings;
        private int _firstLinker = -1;
        private int _secondLinker = -1;
        private int _longNames = -1;

        public unsafe CoffArchiveReader(byte* archive, int size)
        {
            ArgumentNullException.ThrowIfNull(archive, nameof(archive));
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            _provider = new ExternalMemoryBlockProvider(archive, size);
        }

        /// <summary>Reads from the current stream position, taking ownership of the stream.</summary>
        public CoffArchiveReader(Stream archiveStream)
            : this(archiveStream, CoffArchiveStreamOptions.Default, 0)
        {
        }

        public CoffArchiveReader(Stream archiveStream, CoffArchiveStreamOptions options)
            : this(archiveStream, options, 0)
        {
        }

        /// <summary>
        /// Reads a bounded archive from the current stream position. Size zero means the
        /// remaining stream. Prefetch copies the archive; otherwise data is loaded lazily.
        /// </summary>
        public unsafe CoffArchiveReader(Stream archiveStream, CoffArchiveStreamOptions options, int size)
        {
            ArgumentNullException.ThrowIfNull(archiveStream);
            if (!archiveStream.CanRead || !archiveStream.CanSeek)
                throw new ArgumentException("Must support Read and Seek.", nameof(archiveStream));
            if ((options & ~(CoffArchiveStreamOptions.LeaveOpen | CoffArchiveStreamOptions.PrefetchEntireArchive)) != 0)
                throw new ArgumentOutOfRangeException(nameof(options));

            int actualSize = StreamExtensions.GetAndValidateSize(archiveStream, size, nameof(archiveStream));
            bool leaveOpen = (options & CoffArchiveStreamOptions.LeaveOpen) != 0;
            if ((options & CoffArchiveStreamOptions.PrefetchEntireArchive) == 0)
            {
                _provider = new StreamMemoryBlockProvider(archiveStream, archiveStream.Position, actualSize, leaveOpen);
            }
            else
            {
                try
                {
                    _block = StreamMemoryBlockProvider.ReadMemoryBlockNoLock(archiveStream, archiveStream.Position, actualSize);
                    _provider = new ExternalMemoryBlockProvider(_block.Pointer, _block.Size, _block);
                }
                finally
                {
                    if (!leaveOpen)
                        archiveStream.Dispose();
                }
            }
        }

        public void Dispose()
        {
            lock (_guard)
            {
                if (_memberBlocks != null)
                {
                    foreach (var block in _memberBlocks)
                        block?.Dispose();
                    _memberBlocks = null;
                }
                _provider?.Dispose();
                _provider = null;
                _block?.Dispose();
                _block = null;
            }
        }

        internal void CheckAlive()
        {
            if (_provider == null)
                throw new ObjectDisposedException(nameof(CoffArchiveReader));
        }

        internal AbstractMemoryBlock GetBlock()
        {
            lock (_guard)
            {
                CheckAlive();
                return _block ??= _provider.GetMemoryBlock();
            }
        }

        internal unsafe ReadOnlySpan<byte> Bytes(int offset, int length)
        {
            AbstractMemoryBlock block = GetBlock();
            if (offset < 0 || length < 0 || offset > block.Size - length)
                throw Invalid(offset, "Range extends beyond the archive.");
            return new ReadOnlySpan<byte>(block.Pointer + offset, length);
        }

        internal static BadImageFormatException Invalid(int offset, string message)
            => new BadImageFormatException($"Archive offset 0x{offset:X}: {message}");

        public bool HasSymbolTable
        {
            get
            {
                GetMemberTableReader();
                return _secondLinker >= 0;
            }
        }

        public CoffArchiveMemberTableReader GetMemberTableReader()
        {
            lock (_guard)
            {
                CheckAlive();
                if (_members == null)
                    _members = ReadMembers();
                return _members;
            }
        }

        public CoffArchiveSymbolTableReader GetSymbolTableReader()
        {
            lock (_guard)
            {
                GetMemberTableReader();
                if (_secondLinker < 0)
                    throw new InvalidOperationException("The archive has no second linker member.");
                return _symbols ??= new CoffArchiveSymbolTableReader(this, _secondLinker);
            }
        }

        public CoffArchiveStringTableReader GetStringTableReader()
        {
            lock (_guard)
            {
                GetMemberTableReader();
                return _strings ??= new CoffArchiveStringTableReader(this, MetadataStringDecoder.DefaultUTF8);
            }
        }

        public CoffArchiveStringTableReader GetStringTableReader(MetadataStringDecoder utf8Decoder)
        {
            ArgumentNullException.ThrowIfNull(utf8Decoder);
            GetMemberTableReader();
            return new CoffArchiveStringTableReader(this, utf8Decoder);
        }

        private CoffArchiveMemberTableReader ReadMembers()
        {
            int length = GetBlock().Size;
            if (length < 8 || !Bytes(0, 8).SequenceEqual("!<arch>\n"u8))
                throw Invalid(0, "Invalid archive signature.");

            int count = 0;
            int firstLinker = -1, secondLinker = -1, longNames = -1;
            for (int offset = 8; offset < length;)
            {
                ReadOnlySpan<byte> header = Bytes(offset, HeaderSize);
                if (!header.Slice(58, 2).SequenceEqual("`\n"u8))
                    throw Invalid(offset, "Invalid member header trailer.");
                ReadNumber(offset, 16, 12, 10, long.MaxValue, "timestamp", false, signed: true);
                ReadNumber(offset, 28, 6, 10, int.MaxValue, "user ID", false);
                ReadNumber(offset, 34, 6, 10, int.MaxValue, "group ID", false);
                ReadNumber(offset, 40, 8, 8, int.MaxValue, "mode", false);
                int size = MemberSize(offset);
                ReadOnlySpan<byte> name = HeaderName(offset);
                if (name.IsEmpty)
                    throw Invalid(offset, "Empty member name.");
                if (name.SequenceEqual("/"u8))
                {
                    if (count == 0)
                        firstLinker = offset;
                    else if (count == 1 && firstLinker >= 0)
                        secondLinker = offset;
                }
                else if (name.SequenceEqual("//"u8))
                {
                    if (longNames >= 0)
                        throw Invalid(offset, "Duplicate longnames member.");
                    longNames = offset;
                }
                else if (name[0] != (byte)'/' && name[^1] != (byte)'/')
                {
                    throw Invalid(offset, "Unsupported or unterminated member name.");
                }
                offset = NextMember(offset, size, length);
                count++;
            }

            int[] offsets = new int[count];
            int next = 8;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = next;
                next = NextMember(next, MemberSize(next), length);
            }
            _firstLinker = firstLinker;
            _secondLinker = secondLinker;
            _longNames = longNames;
            var reader = new CoffArchiveMemberTableReader(this, offsets);
            foreach (int offset in offsets)
                MemberName(offset);
            return reader;
        }

        private int NextMember(int offset, int size, int length)
        {
            long end = (long)offset + HeaderSize + size;
            if (end > length)
                throw Invalid(offset, "Member payload extends beyond the archive.");
            if ((end & 1) != 0)
            {
                if (end == length || Bytes((int)end, 1)[0] != (byte)'\n')
                    throw Invalid(offset, "Missing newline alignment padding.");
                end++;
            }
            return (int)end;
        }

        internal long? ReadNumber(int header, int start, int length, int radix, long maximum, string field, bool required, bool signed = false)
        {
            ReadOnlySpan<byte> text = Bytes(header + start, length).TrimEnd((byte)' ');
            if (text.IsEmpty)
            {
                if (required)
                    throw Invalid(header, $"Missing {field}.");
                return null;
            }
            bool negative = signed && text[0] == (byte)'-';
            if (negative)
            {
                text = text[1..];
                if (text.IsEmpty)
                    throw Invalid(header, $"Invalid {field}.");
            }
            long value = 0;
            foreach (byte character in text)
            {
                int digit = character - (byte)'0';
                if (digit < 0 || digit >= radix || value > (maximum - digit) / radix)
                    throw Invalid(header, $"Invalid or overflowing {field}.");
                value = value * radix + digit;
            }
            return negative ? -value : value;
        }

        internal int MemberSize(int offset)
            => (int)ReadNumber(offset, 48, 10, 10, int.MaxValue, "member size", true).Value;

        private ReadOnlySpan<byte> HeaderName(int offset)
            => Bytes(offset, 16).TrimEnd((byte)' ');

        internal CoffArchiveMemberKind MemberKind(int offset)
        {
            CheckAlive();
            if (offset == _firstLinker) return CoffArchiveMemberKind.FirstLinkerMember;
            if (offset == _secondLinker) return CoffArchiveMemberKind.SecondLinkerMember;
            if (offset == _longNames) return CoffArchiveMemberKind.LongNames;
            ReadOnlySpan<byte> name = HeaderName(offset);
            return name[0] == (byte)'/' && (name.Length == 1 || name[1] < (byte)'0' || name[1] > (byte)'9')
                ? CoffArchiveMemberKind.OtherSpecial : CoffArchiveMemberKind.Regular;
        }

        internal CoffArchiveStringHandle MemberName(int offset)
        {
            ReadOnlySpan<byte> name = HeaderName(offset);
            if (name.Length > 1 && name[0] == (byte)'/' && name[1] >= (byte)'0' && name[1] <= (byte)'9')
            {
                int relative = 0;
                foreach (byte character in name[1..])
                {
                    int digit = character - (byte)'0';
                    if (digit < 0 || digit > 9 || relative > (int.MaxValue - digit) / 10)
                        throw Invalid(offset, "Invalid longname offset.");
                    relative = relative * 10 + digit;
                }
                if (_longNames < 0 || relative >= MemberSize(_longNames))
                    throw Invalid(offset, "Longname offset is outside the longnames member.");
                int start = _longNames + HeaderSize;
                int cursor = start + relative;
                return ReadString(ref cursor, start + MemberSize(_longNames), offset, "longname");
            }
            int nameLength = name.Length;
            if (name[0] != (byte)'/')
                nameLength--;
            return new CoffArchiveStringHandle(offset, nameLength);
        }

        internal CoffArchiveStringHandle ReadString(ref int cursor, int end, int member, string field)
        {
            int length = Bytes(cursor, end - cursor).IndexOf((byte)0);
            if (length < 0)
                throw Invalid(member, $"Unterminated {field}.");
            var handle = new CoffArchiveStringHandle(cursor, length);
            cursor += length + 1;
            return handle;
        }

        private void ValidateMember(CoffArchiveMember member)
        {
            CheckAlive();
            if (member.Owner != this)
                throw new ArgumentException("The member must belong to this archive.", nameof(member));
        }

        public PEMemoryBlock GetMemberData(CoffArchiveMember member)
        {
            return new PEMemoryBlock(GetMemberBlock(member));
        }

        private unsafe AbstractMemoryBlock GetMemberBlock(CoffArchiveMember member)
        {
            lock (_guard)
            {
                ValidateMember(member);
                _memberBlocks ??= new ExternalMemoryBlock[_members.NumberOfMembers];
                int index = _members.IndexOf(member.HeaderOffset);
                if (_memberBlocks[index] == null)
                {
                    AbstractMemoryBlock archive = GetBlock();
                    int start = member.DataOffset;
                    int size = member.SizeOfData;
                    Bytes(start, size);
                    _memberBlocks[index] = new ExternalMemoryBlock(this, archive.Pointer + start, size);
                }
                return _memberBlocks[index];
            }
        }

        public CoffImportObject GetImportObject(CoffArchiveMember member)
        {
            ValidateMember(member);
            if (member.Kind != CoffArchiveMemberKind.Regular)
                return default;
            int offset = member.DataOffset;
            ReadOnlySpan<byte> data = Bytes(offset, member.SizeOfData);
            if (data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xffff0000)
                return default;
            if (data.Length < 6)
                throw Invalid(offset, "Truncated anonymous/import signature.");
            // Bigobj and anonymous object headers share the signature, but not version zero.
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0)
                return default;
            if (data.Length < 20)
                throw Invalid(offset, "Truncated short import header.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            if (size > data.Length - 20)
                throw Invalid(offset, "Import data extends beyond its member.");
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
            CoffImportType type = (CoffImportType)(flags & 3);
            CoffImportNameType nameType = (CoffImportNameType)((flags >> 2) & 7);
            if (type > CoffImportType.Const || nameType > CoffImportNameType.ExportAs)
                throw Invalid(offset, "Unsupported import type or name type.");

            int cursor = offset + 20;
            int end = cursor + (int)size;
            CoffArchiveStringHandle symbol = ReadString(ref cursor, end, offset, "import symbol");
            CoffArchiveStringHandle dll = ReadString(ref cursor, end, offset, "DLL name");
            CoffArchiveStringHandle exportAs = nameType == CoffImportNameType.ExportAs
                ? ReadString(ref cursor, end, offset, "export-as name") : default;
            CoffArchiveStringHandle import = symbol;
            if (nameType == CoffImportNameType.Ordinal)
                import = default;
            else if (nameType == CoffImportNameType.ExportAs)
                import = exportAs;
            else if (nameType == CoffImportNameType.NoPrefix || nameType == CoffImportNameType.Undecorate)
            {
                ReadOnlySpan<byte> name = StringBytes(symbol);
                int prefix = !name.IsEmpty && (name[0] == (byte)'?' || name[0] == (byte)'@' || name[0] == (byte)'_') ? 1 : 0;
                name = name[prefix..];
                int at = nameType == CoffImportNameType.Undecorate ? name.IndexOf((byte)'@') : -1;
                import = new CoffArchiveStringHandle(symbol.Offset + prefix, at >= 0 ? at : name.Length);
            }
            return new CoffImportObject(this, member.Handle, offset, symbol, dll, exportAs, import);
        }

        /// <summary>
        /// Opens an ordinary object without copying. Dispose the child before the archive;
        /// disposing the child never disposes the archive.
        /// </summary>
        public unsafe CoffReader GetCoffReader(CoffArchiveMember member)
        {
            ValidateMember(member);
            if (member.Kind != CoffArchiveMemberKind.Regular || !GetImportObject(member).IsDefault)
                throw new InvalidOperationException("The member is not an ordinary COFF object.");
            PEMemoryBlock data = GetMemberData(member);
            return new CoffReader(data.Pointer, data.Length, memoryOwner: this);
        }

        internal ReadOnlySpan<byte> StringBytes(CoffArchiveStringHandle handle)
        {
            CheckAlive();
            if (handle.IsNil)
                throw new ArgumentOutOfRangeException(nameof(handle));
            return Bytes(handle.Offset, handle.Length);
        }

        internal unsafe string DecodeString(CoffArchiveStringHandle handle, MetadataStringDecoder decoder)
        {
            StringBytes(handle);
            string result = decoder.GetString(GetBlock().Pointer + handle.Offset, handle.Length);
            GC.KeepAlive(this);
            return result;
        }
    }

    public readonly struct CoffArchiveMemberHandle : IEquatable<CoffArchiveMemberHandle>
    {
        internal readonly int Offset;
        internal CoffArchiveMemberHandle(int offset) => Offset = offset;
        public bool IsNil => Offset == 0;
        public bool Equals(CoffArchiveMemberHandle other) => Offset == other.Offset;
        public override bool Equals(object obj) => obj is CoffArchiveMemberHandle other && Equals(other);
        public override int GetHashCode() => Offset.GetHashCode();
        public static bool operator ==(CoffArchiveMemberHandle left, CoffArchiveMemberHandle right) => left.Equals(right);
        public static bool operator !=(CoffArchiveMemberHandle left, CoffArchiveMemberHandle right) => !left.Equals(right);
    }

    public readonly struct CoffArchiveSymbolHandle : IEquatable<CoffArchiveSymbolHandle>
    {
        internal readonly int Row;
        internal CoffArchiveSymbolHandle(int row) => Row = row;
        public bool IsNil => Row == 0;
        public bool Equals(CoffArchiveSymbolHandle other) => Row == other.Row;
        public override bool Equals(object obj) => obj is CoffArchiveSymbolHandle other && Equals(other);
        public override int GetHashCode() => Row.GetHashCode();
        public static bool operator ==(CoffArchiveSymbolHandle left, CoffArchiveSymbolHandle right) => left.Equals(right);
        public static bool operator !=(CoffArchiveSymbolHandle left, CoffArchiveSymbolHandle right) => !left.Equals(right);
    }

    public readonly struct CoffArchiveStringHandle : IEquatable<CoffArchiveStringHandle>
    {
        internal readonly int Offset;
        internal readonly int Length;
        internal CoffArchiveStringHandle(int offset, int length) => (Offset, Length) = (offset, length);
        public bool IsNil => Offset == 0;
        public bool Equals(CoffArchiveStringHandle other) => Offset == other.Offset && Length == other.Length;
        public override bool Equals(object obj) => obj is CoffArchiveStringHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Offset, Length);
        public static bool operator ==(CoffArchiveStringHandle left, CoffArchiveStringHandle right) => left.Equals(right);
        public static bool operator !=(CoffArchiveStringHandle left, CoffArchiveStringHandle right) => !left.Equals(right);
    }

    /// <summary>A borrowed view of a physical archive member header.</summary>
    public readonly struct CoffArchiveMember
    {
        private readonly CoffArchiveMemberTableReader _reader;
        private readonly int _offset;
        internal CoffArchiveMember(CoffArchiveMemberTableReader reader, int offset) => (_reader, _offset) = (reader, offset);
        internal CoffArchiveReader Owner => _reader?.Archive;
        public CoffArchiveMemberHandle Handle => new CoffArchiveMemberHandle(_offset);
        public CoffArchiveMemberKind Kind => _reader.Archive.MemberKind(_offset);
        public CoffArchiveStringHandle Name => _reader.Archive.MemberName(_offset);
        public int HeaderOffset => _offset;
        public int DataOffset => _offset + CoffArchiveReader.HeaderSize;
        public int SizeOfData => _reader.Archive.MemberSize(_offset);
        public long? TimeDateStamp => _reader.Archive.ReadNumber(_offset, 16, 12, 10, long.MaxValue, "timestamp", false, signed: true);
        public int? UserId => (int?)_reader.Archive.ReadNumber(_offset, 28, 6, 10, int.MaxValue, "user ID", false);
        public int? GroupId => (int?)_reader.Archive.ReadNumber(_offset, 34, 6, 10, int.MaxValue, "group ID", false);
        public int? Mode => (int?)_reader.Archive.ReadNumber(_offset, 40, 8, 8, int.MaxValue, "mode", false);
    }

    public sealed class CoffArchiveMemberTableReader
    {
        internal readonly CoffArchiveReader Archive;
        private readonly int[] _offsets;
        internal CoffArchiveMemberTableReader(CoffArchiveReader archive, int[] offsets) => (Archive, _offsets) = (archive, offsets);
        public int NumberOfMembers { get { Archive.CheckAlive(); return _offsets.Length; } }
        public CoffArchiveMemberHandleCollection Members => new CoffArchiveMemberHandleCollection(this);
        public CoffArchiveMember GetCoffArchiveMember(CoffArchiveMemberHandle handle)
        {
            Archive.CheckAlive();
            if (handle.IsNil || IndexOf(handle.Offset) < 0)
                throw new ArgumentOutOfRangeException(nameof(handle));
            return new CoffArchiveMember(this, handle.Offset);
        }
        internal CoffArchiveMemberHandle HandleAt(int index) => new CoffArchiveMemberHandle(_offsets[index]);
        internal int IndexOf(int offset) => Array.BinarySearch(_offsets, offset);
        internal bool Contains(int offset) => IndexOf(offset) >= 0;
    }

    /// <summary>A borrowed second-linker directory. Names are ordered by their encoded bytes.</summary>
    public sealed class CoffArchiveSymbolTableReader
    {
        internal readonly CoffArchiveReader Archive;
        private readonly int _memberOffsets;
        private readonly int _memberCount;
        private readonly int _symbolIndexes;
        private readonly int[] _names;

        internal CoffArchiveSymbolTableReader(CoffArchiveReader archive, int header)
        {
            Archive = archive;
            int start = header + CoffArchiveReader.HeaderSize;
            int end = start + archive.MemberSize(header);
            int cursor = start;
            _memberCount = ReadCount(ref cursor, end);
            _memberOffsets = cursor;
            Advance(ref cursor, _memberCount, 4, end);
            int symbolCount = ReadCount(ref cursor, end);
            _symbolIndexes = cursor;
            Advance(ref cursor, symbolCount, 2, end);
            if (symbolCount > end - cursor)
                throw CoffArchiveReader.Invalid(header, "Symbol strings are truncated.");

            CoffArchiveMemberTableReader members = archive.GetMemberTableReader();
            for (int i = 0; i < _memberCount; i++)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(archive.Bytes(_memberOffsets + i * 4, 4));
                if (value > int.MaxValue || !members.Contains((int)value) ||
                    archive.MemberKind((int)value) != CoffArchiveMemberKind.Regular)
                    throw CoffArchiveReader.Invalid(header, "Invalid indexed member offset.");
            }

            _names = new int[symbolCount + 1];
            CoffArchiveStringHandle previousName = default;
            for (int i = 0; i < symbolCount; i++)
            {
                _names[i] = cursor;
                CoffArchiveStringHandle name = archive.ReadString(ref cursor, end, header, "symbol name");
                if (i != 0 && archive.StringBytes(previousName).SequenceCompareTo(archive.StringBytes(name)) > 0)
                    throw CoffArchiveReader.Invalid(header, "Symbol names are not in lexical order.");
                previousName = name;
                int index = BinaryPrimitives.ReadUInt16LittleEndian(archive.Bytes(_symbolIndexes + i * 2, 2));
                if (index == 0 || index > _memberCount)
                    throw CoffArchiveReader.Invalid(header, "Invalid one-based symbol member index.");
            }
            _names[symbolCount] = cursor;
        }

        private int ReadCount(ref int cursor, int end)
        {
            if (end - cursor < 4)
                throw CoffArchiveReader.Invalid(cursor, "Truncated linker count.");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(Archive.Bytes(cursor, 4));
            cursor += 4;
            if (count > int.MaxValue)
                throw CoffArchiveReader.Invalid(cursor - 4, "Linker count is too large.");
            return (int)count;
        }

        private static void Advance(ref int cursor, int count, int size, int end)
        {
            long next = cursor + (long)count * size;
            if (next > end)
                throw CoffArchiveReader.Invalid(cursor, "Truncated linker array.");
            cursor = (int)next;
        }

        public int NumberOfIndexedMembers { get { Archive.CheckAlive(); return _memberCount; } }
        public int NumberOfSymbols { get { Archive.CheckAlive(); return _names.Length - 1; } }
        public CoffArchiveSymbolHandleCollection Symbols => new CoffArchiveSymbolHandleCollection(this, 0, NumberOfSymbols);
        public CoffArchiveMemberHandle GetIndexedMember(int index)
        {
            Archive.CheckAlive();
            if ((uint)index >= (uint)_memberCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new CoffArchiveMemberHandle((int)BinaryPrimitives.ReadUInt32LittleEndian(Archive.Bytes(_memberOffsets + index * 4, 4)));
        }
        public CoffArchiveSymbol GetCoffArchiveSymbol(CoffArchiveSymbolHandle handle)
        {
            if (handle.Row <= 0 || handle.Row > NumberOfSymbols)
                throw new ArgumentOutOfRangeException(nameof(handle));
            return new CoffArchiveSymbol(this, handle.Row - 1);
        }
        internal CoffArchiveStringHandle NameAt(int index)
            => new CoffArchiveStringHandle(_names[index], _names[index + 1] - _names[index] - 1);
        internal CoffArchiveMemberHandle MemberAt(int index)
            => GetIndexedMember(BinaryPrimitives.ReadUInt16LittleEndian(Archive.Bytes(_symbolIndexes + index * 2, 2)) - 1);

        /// <summary>Returns all exact case-sensitive matches, or an empty collection.</summary>
        public CoffArchiveSymbolHandleCollection FindSymbols(ReadOnlySpan<byte> utf8Name)
        {
            Archive.CheckAlive();
            if (utf8Name.IndexOf((byte)0) >= 0)
                throw new ArgumentException("Symbol names cannot contain NUL.", nameof(utf8Name));
            int first = Bound(utf8Name, false);
            int last = Bound(utf8Name, true);
            return new CoffArchiveSymbolHandleCollection(this, first, last - first);
        }

        private int Bound(ReadOnlySpan<byte> name, bool upper)
        {
            int low = 0, high = NumberOfSymbols;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                int comparison = Archive.StringBytes(NameAt(middle)).SequenceCompareTo(name);
                if (comparison < 0 || (upper && comparison == 0))
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }

    public readonly struct CoffArchiveSymbol
    {
        private readonly CoffArchiveSymbolTableReader _reader;
        private readonly int _index;
        internal CoffArchiveSymbol(CoffArchiveSymbolTableReader reader, int index) => (_reader, _index) = (reader, index);
        public CoffArchiveSymbolHandle Handle => new CoffArchiveSymbolHandle(_index + 1);
        public CoffArchiveStringHandle Name => _reader.NameAt(_index);
        public CoffArchiveMemberHandle Member => _reader.MemberAt(_index);
    }

    public sealed class CoffArchiveStringTableReader
    {
        private readonly CoffArchiveReader _archive;
        private readonly MetadataStringDecoder _decoder;
        internal CoffArchiveStringTableReader(CoffArchiveReader archive, MetadataStringDecoder decoder) => (_archive, _decoder) = (archive, decoder);
        public string GetString(CoffArchiveStringHandle handle) => _archive.DecodeString(handle, _decoder);
        public ReadOnlySpan<byte> GetUtf8Bytes(CoffArchiveStringHandle handle) => _archive.StringBytes(handle);
    }

    public readonly struct CoffImportObject
    {
        private readonly CoffArchiveReader _archive;
        private readonly CoffArchiveMemberHandle _member;
        private readonly int _offset;
        private readonly CoffArchiveStringHandle _symbol, _dll, _exportAs, _import;
        internal CoffImportObject(CoffArchiveReader archive, CoffArchiveMemberHandle member, int offset,
            CoffArchiveStringHandle symbol, CoffArchiveStringHandle dll, CoffArchiveStringHandle exportAs, CoffArchiveStringHandle import)
        {
            _archive = archive;
            _member = member;
            _offset = offset;
            _symbol = symbol;
            _dll = dll;
            _exportAs = exportAs;
            _import = import;
        }
        private CoffArchiveReader Archive
        {
            get
            {
                if (IsDefault)
                    throw new InvalidOperationException("There is no short import object.");
                _archive.CheckAlive();
                return _archive;
            }
        }
        public bool IsDefault => _archive == null;
        public CoffArchiveMemberHandle Member { get { Archive.CheckAlive(); return _member; } }
        public ushort Version => BinaryPrimitives.ReadUInt16LittleEndian(Archive.Bytes(_offset + 4, 2));
        public Machine Machine => (Machine)BinaryPrimitives.ReadUInt16LittleEndian(Archive.Bytes(_offset + 6, 2));
        public uint TimeDateStamp => BinaryPrimitives.ReadUInt32LittleEndian(Archive.Bytes(_offset + 8, 4));
        public int SizeOfData => (int)BinaryPrimitives.ReadUInt32LittleEndian(Archive.Bytes(_offset + 12, 4));
        public ushort OrdinalOrHint => BinaryPrimitives.ReadUInt16LittleEndian(Archive.Bytes(_offset + 16, 2));
        public ushort TypeInfo => BinaryPrimitives.ReadUInt16LittleEndian(Archive.Bytes(_offset + 18, 2));
        public CoffImportType Type => (CoffImportType)(TypeInfo & 3);
        public CoffImportNameType NameType => (CoffImportNameType)((TypeInfo >> 2) & 7);
        public CoffArchiveStringHandle SymbolName { get { Archive.CheckAlive(); return _symbol; } }
        public CoffArchiveStringHandle DllName { get { Archive.CheckAlive(); return _dll; } }
        public CoffArchiveStringHandle ExportAsName { get { Archive.CheckAlive(); return _exportAs; } }
        public CoffArchiveStringHandle ImportName { get { Archive.CheckAlive(); return _import; } }
    }

    public readonly struct CoffArchiveMemberHandleCollection : IReadOnlyCollection<CoffArchiveMemberHandle>
    {
        private readonly CoffArchiveMemberTableReader _reader;
        internal CoffArchiveMemberHandleCollection(CoffArchiveMemberTableReader reader) => _reader = reader;
        public int Count => _reader == null ? 0 : _reader.NumberOfMembers;
        public Enumerator GetEnumerator() => new Enumerator(_reader);
        IEnumerator<CoffArchiveMemberHandle> IEnumerable<CoffArchiveMemberHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CoffArchiveMemberHandle>
        {
            private readonly CoffArchiveMemberTableReader _reader;
            private int _index;
            internal Enumerator(CoffArchiveMemberTableReader reader) => (_reader, _index) = (reader, -1);
            public CoffArchiveMemberHandle Current
            {
                get
                {
                    if (_reader == null || _index < 0 || _index >= _reader.NumberOfMembers)
                        throw new InvalidOperationException();
                    return _reader.HandleAt(_index);
                }
            }
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                int count = _reader?.NumberOfMembers ?? 0;
                if (_index >= count - 1)
                {
                    _index = count;
                    return false;
                }
                _index++;
                return true;
            }
            public void Reset() => _index = -1;
            public void Dispose() { }
        }
    }

    public readonly struct CoffArchiveSymbolHandleCollection : IReadOnlyCollection<CoffArchiveSymbolHandle>
    {
        private readonly CoffArchiveSymbolTableReader _reader;
        private readonly int _start, _count;
        internal CoffArchiveSymbolHandleCollection(CoffArchiveSymbolTableReader reader, int start, int count)
            => (_reader, _start, _count) = (reader, start, count);
        public int Count { get { _reader?.Archive.CheckAlive(); return _count; } }
        public Enumerator GetEnumerator() => new Enumerator(_reader, _start, _count);
        IEnumerator<CoffArchiveSymbolHandle> IEnumerable<CoffArchiveSymbolHandle>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public struct Enumerator : IEnumerator<CoffArchiveSymbolHandle>
        {
            private readonly CoffArchiveSymbolTableReader _reader;
            private readonly int _start, _end;
            private int _index;
            internal Enumerator(CoffArchiveSymbolTableReader reader, int start, int count)
                => (_reader, _start, _end, _index) = (reader, start, start + count, start - 1);
            public CoffArchiveSymbolHandle Current
            {
                get
                {
                    _reader?.Archive.CheckAlive();
                    if (_index < _start || _index >= _end)
                        throw new InvalidOperationException();
                    return new CoffArchiveSymbolHandle(_index + 1);
                }
            }
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                _reader?.Archive.CheckAlive();
                if (_index >= _end - 1)
                {
                    _index = _end;
                    return false;
                }
                _index++;
                return true;
            }
            public void Reset() => _index = _start - 1;
            public void Dispose() { }
        }
    }
}
