//
// Code that was copied from System.Reflection.Metadata implementation verbatim. We might
// optionally want to keep this in sync with bugfixes in System.Reflection.Metadata.
//

using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// System.Reflection.Metadata enables nullable and we don't care enough to strip the annotations.
#nullable enable

namespace Coff
{
    public readonly struct LabelHandle : IEquatable<LabelHandle>
    {
        /// <summary>
        /// 1-based id identifying the label within the context of a <see cref="ControlFlowBuilder"/>.
        /// </summary>
        public int Id { get; }

        public LabelHandle(int id)
        {
            Debug.Assert(id >= 1);
            Id = id;
        }

        public bool IsNil => Id == 0;

        public bool Equals(LabelHandle other) => Id == other.Id;
        public override bool Equals([NotNullWhen(true)] object? obj) => obj is LabelHandle labelHandle && Equals(labelHandle);
        public override int GetHashCode() => Id.GetHashCode();

        public static bool operator ==(LabelHandle left, LabelHandle right) => left.Equals(right);
        public static bool operator !=(LabelHandle left, LabelHandle right) => !left.Equals(right);
    }

    /// <summary>
    /// Simple BinaryReader wrapper to:
    ///
    ///  1) throw BadImageFormat instead of EndOfStream or ArgumentOutOfRange.
    ///  2) limit reads to a subset of the base stream.
    ///
    /// Only methods that are needed to read PE headers are implemented.
    /// </summary>
    internal readonly partial struct PEBinaryReader
    {
        private readonly long _startOffset;
        private readonly long _maxOffset;
        private readonly BinaryReader _reader;

        public PEBinaryReader(Stream stream, int size)
        {
            Debug.Assert(size >= 0 && size <= (stream.Length - stream.Position));

            _startOffset = stream.Position;
            _maxOffset = _startOffset + size;
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        public int Offset
        {
            get { return (int)(_reader.BaseStream.Position - _startOffset); }
            set
            {
                CheckBounds(_startOffset, value);
                _reader.BaseStream.Seek(_startOffset + value, SeekOrigin.Begin);
            }
        }

        private byte[] ReadBytes(int count)
        {
            CheckBounds(_reader.BaseStream.Position, count);
            return _reader.ReadBytes(count);
        }

        public byte ReadByte()
        {
            CheckBounds(sizeof(byte));
            return _reader.ReadByte();
        }

        public short ReadInt16()
        {
            CheckBounds(sizeof(short));
            return _reader.ReadInt16();
        }

        public ushort ReadUInt16()
        {
            CheckBounds(sizeof(ushort));
            return _reader.ReadUInt16();
        }

        public int ReadInt32()
        {
            CheckBounds(sizeof(int));
            return _reader.ReadInt32();
        }

        public uint ReadUInt32()
        {
            CheckBounds(sizeof(uint));
            return _reader.ReadUInt32();
        }

        public ulong ReadUInt64()
        {
            CheckBounds(sizeof(ulong));
            return _reader.ReadUInt64();
        }

        /// <summary>
        /// Reads a fixed-length byte block as a null-padded UTF-8 encoded string.
        /// The padding is not included in the returned string.
        ///
        /// Note that it is legal for UTF-8 strings to contain NUL; if NUL occurs
        /// between non-NUL codepoints, it is not considered to be padding and
        /// is included in the result.
        /// </summary>
        public string ReadNullPaddedUTF8(int byteCount)
        {
            byte[] bytes = ReadBytes(byteCount);
            int nonPaddedLength = 0;
            for (int i = bytes.Length; i > 0; --i)
            {
                if (bytes[i - 1] != 0)
                {
                    nonPaddedLength = i;
                    break;
                }
            }
            return Encoding.UTF8.GetString(bytes, 0, nonPaddedLength);
        }

        private void CheckBounds(uint count)
        {
            Debug.Assert(count <= sizeof(long));  // Error message assumes we're trying to read constant small number of bytes.
            Debug.Assert(_reader.BaseStream.Position >= 0 && _maxOffset >= 0);

            // Add cannot overflow because the worst case is (ulong)long.MaxValue + uint.MaxValue < ulong.MaxValue.
            if ((ulong)_reader.BaseStream.Position + count > (ulong)_maxOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }

        private void CheckBounds(long startPosition, int count)
        {
            Debug.Assert(startPosition >= 0 && _maxOffset >= 0);

            // Add cannot overflow because the worst case is (ulong)long.MaxValue + uint.MaxValue < ulong.MaxValue.
            // Negative count is handled by overflow to greater than maximum size = int.MaxValue.
            if ((ulong)startPosition + unchecked((uint)count) > (ulong)_maxOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }
    }

    /// <summary>
    /// Represents a disposable blob of memory accessed via unsafe pointer.
    /// </summary>
    internal abstract class AbstractMemoryBlock : IDisposable
    {
        /// <summary>
        /// Pointer to the underlying data (not valid after disposal).
        /// </summary>
        public abstract unsafe byte* Pointer { get; }

        /// <summary>
        /// Size of the block.
        /// </summary>
        public abstract int Size { get; }

        public unsafe BlobReader GetReader() => new BlobReader(Pointer, Size);

        /// <summary>
        /// Creates a new stream wrapping the block's memory.
        /// </summary>
        public unsafe Stream GetStream() => new UnmanagedMemoryStream(Pointer, Size);

        /// <summary>
        /// Returns the content of the entire memory block.
        /// </summary>
        /// <remarks>
        /// Does not check bounds.
        ///
        /// Only creates a copy of the data if they are not represented by a managed byte array,
        /// or if the specified range doesn't span the entire block.
        /// </remarks>
        public virtual unsafe ImmutableArray<byte> GetContentUnchecked(int start, int length)
        {
            var result = new ReadOnlySpan<byte>(Pointer + start, length).ToImmutableArray();
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>
        /// Disposes the block.
        /// </summary>
        /// <remarks>
        /// The operation is idempotent, but must not be called concurrently with any other operations on the block.
        ///
        /// Using the block after dispose is an error in our code and therefore no effort is made to throw a tidy
        /// ObjectDisposedException and null ref or AV is possible.
        /// </remarks>
        public abstract void Dispose();
    }

    /// <summary>
    /// Class representing raw memory but not owning the memory.
    /// </summary>
    internal sealed unsafe class ExternalMemoryBlock : AbstractMemoryBlock
    {
        // keeps the owner of the memory alive as long as the block is alive:
        private readonly object _memoryOwner;

        private byte* _buffer;
        private int _size;

        public ExternalMemoryBlock(object memoryOwner, byte* buffer, int size)
        {
            _memoryOwner = memoryOwner;
            _buffer = buffer;
            _size = size;
        }

        public override void Dispose()
        {
            _buffer = null;
            _size = 0;
        }

        public override byte* Pointer => _buffer;
        public override int Size => _size;
    }

    internal abstract class MemoryBlockProvider : IDisposable
    {
        /// <summary>
        /// Creates and hydrates a memory block representing all data.
        /// </summary>
        /// <exception cref="IOException">Error while reading from the memory source.</exception>
        public AbstractMemoryBlock GetMemoryBlock()
        {
            return GetMemoryBlockImpl(0, Size);
        }

        /// <summary>
        /// Creates and hydrates a memory block representing data in the specified range.
        /// </summary>
        /// <param name="start">Starting offset relative to the beginning of the data represented by this provider.</param>
        /// <param name="size">Size of the resulting block.</param>
        /// <exception cref="IOException">Error while reading from the memory source.</exception>
        public AbstractMemoryBlock GetMemoryBlock(int start, int size)
        {
            // Add cannot overflow as it is the sum of two 32-bit values done in 64 bits.
            // Negative start or size is handle by overflow to greater than maximum size = int.MaxValue.
            if ((ulong)(unchecked((uint)start)) + unchecked((uint)size) > (ulong)this.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            return GetMemoryBlockImpl(start, size);
        }

        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        protected abstract AbstractMemoryBlock GetMemoryBlockImpl(int start, int size);

        /// <summary>
        /// Gets the <see cref="Stream"/> backing the <see cref="MemoryBlockProvider"/>, if there is one.
        /// </summary>
        /// <remarks>
        /// It is the caller's responsibility to use <paramref name="stream"/> only
        /// while locking on <paramref name="streamGuard"/>, and not read outside the
        /// bounds defined by <paramref name="imageStart"/> and <paramref name="imageSize"/>.
        /// </remarks>
        public virtual bool TryGetUnderlyingStream([NotNullWhen(true)] out Stream? stream, out long imageStart, out int imageSize, [NotNullWhen(true)] out object? streamGuard)
        {
            stream = null;
            imageStart = 0;
            imageSize = 0;
            streamGuard = null;
            return false;
        }

        /// <summary>
        /// The size of the data.
        /// </summary>
        public abstract int Size { get; }

        protected abstract void Dispose(bool disposing);

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Represents raw memory owned by an external object.
    /// </summary>
    internal sealed unsafe class ExternalMemoryBlockProvider : MemoryBlockProvider
    {
        private byte* _memory;
        private int _size;

        public ExternalMemoryBlockProvider(byte* memory, int size)
        {
            _memory = memory;
            _size = size;
        }

        public override int Size
        {
            get
            {
                return _size;
            }
        }

        protected override AbstractMemoryBlock GetMemoryBlockImpl(int start, int size)
        {
            return new ExternalMemoryBlock(this, _memory + start, size);
        }

        protected override void Dispose(bool disposing)
        {
            Debug.Assert(disposing);

            // we don't own the memory, just null out the pointer.
            _memory = null;
            _size = 0;
        }

        public byte* Pointer
        {
            get
            {
                return _memory;
            }
        }
    }

    public sealed class CoffHeader
    {
        /// <summary>
        /// The type of target machine.
        /// </summary>
        public Machine Machine { get; }

        /// <summary>
        /// The number of sections. This indicates the size of the section table, which immediately follows the headers.
        /// </summary>
        public short NumberOfSections { get; }

        /// <summary>
        /// The low 32 bits of the number of seconds since 00:00 January 1, 1970, that indicates when the file was created.
        /// </summary>
        public int TimeDateStamp { get; }

        /// <summary>
        /// The file pointer to the COFF symbol table, or zero if no COFF symbol table is present.
        /// This value should be zero for a PE image.
        /// </summary>
        public int PointerToSymbolTable { get; }

        /// <summary>
        /// The number of entries in the symbol table. This data can be used to locate the string table,
        /// which immediately follows the symbol table. This value should be zero for a PE image.
        /// </summary>
        public int NumberOfSymbols { get; }

        /// <summary>
        /// The size of the optional header, which is required for executable files but not for object files.
        /// This value should be zero for an object file.
        /// </summary>
        public short SizeOfOptionalHeader { get; }

        /// <summary>
        /// The flags that indicate the attributes of the file.
        /// </summary>
        public Characteristics Characteristics { get; }

        internal const int Size =
            sizeof(short) + // Machine
            sizeof(short) + // NumberOfSections
            sizeof(int) +   // TimeDateStamp:
            sizeof(int) +   // PointerToSymbolTable
            sizeof(int) +   // NumberOfSymbols
            sizeof(short) + // SizeOfOptionalHeader:
            sizeof(ushort); // Characteristics

        internal CoffHeader(ref PEBinaryReader reader)
        {
            Machine = (Machine)reader.ReadUInt16();
            NumberOfSections = reader.ReadInt16();
            TimeDateStamp = reader.ReadInt32();
            PointerToSymbolTable = reader.ReadInt32();
            NumberOfSymbols = reader.ReadInt32();
            SizeOfOptionalHeader = reader.ReadInt16();
            Characteristics = (Characteristics)reader.ReadUInt16();
        }
    }

    public readonly struct PEMemoryBlock
    {
        private readonly AbstractMemoryBlock _block;
        private readonly int _offset;

        internal PEMemoryBlock(AbstractMemoryBlock block, int offset = 0)
        {
            Debug.Assert(block != null);
            Debug.Assert(offset >= 0 && offset <= block.Size);

            _block = block;
            _offset = offset;
        }

        /// <summary>
        /// Pointer to the first byte of the block.
        /// </summary>
        public unsafe byte* Pointer => (_block != null) ? _block.Pointer + _offset : null;

        /// <summary>
        /// Length of the block.
        /// </summary>
        public int Length => _block?.Size - _offset ?? 0;

        /// <summary>
        /// Creates <see cref="BlobReader"/> for a blob spanning the entire block.
        /// </summary>
        public unsafe BlobReader GetReader()
        {
            return new BlobReader(Pointer, Length);
        }

        /// <summary>
        /// Creates <see cref="BlobReader"/> for a blob spanning a part of the block.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Specified range is not contained within the block.</exception>
        public unsafe BlobReader GetReader(int start, int length)
        {
            BlobUtilities.ValidateRange(Length, start, length, nameof(length));
            return new BlobReader(Pointer + start, length);
        }

        /// <summary>
        /// Reads the content of the entire block into an array.
        /// </summary>
        public ImmutableArray<byte> GetContent()
        {
            return _block?.GetContentUnchecked(_offset, Length) ?? ImmutableArray<byte>.Empty;
        }

        /// <summary>
        /// Reads the content of a part of the block into an array.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Specified range is not contained within the block.</exception>
        public ImmutableArray<byte> GetContent(int start, int length)
        {
            BlobUtilities.ValidateRange(Length, start, length, nameof(length));
            return _block?.GetContentUnchecked(_offset + start, length) ?? ImmutableArray<byte>.Empty;
        }
    }

    internal static partial class StreamExtensions
    {
        /// <summary>
        /// Resolve image size as either the given user-specified size or distance from current position to end-of-stream.
        /// Also performs the relevant argument validation and publicly visible caller has same argument names.
        /// </summary>
        /// <exception cref="ArgumentException">size is 0 and distance from current position to end-of-stream can't fit in Int32.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Size is negative or extends past the end-of-stream from current position.</exception>
        internal static int GetAndValidateSize(Stream stream, int size, string streamParameterName)
        {
            long maxSize = stream.Length - stream.Position;

            if (size < 0 || size > maxSize)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            if (size != 0)
            {
                return size;
            }

            if (maxSize > int.MaxValue)
            {
                throw new ArgumentException("Stream too large", streamParameterName);
            }

            return (int)maxSize;
        }
    }

    internal static class BlobUtilities
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ValidateRange(int bufferLength, int start, int byteCount, string byteCountParameterName)
        {
            if (start < 0 || start > bufferLength)
            {
                Throw.ArgumentOutOfRange(nameof(start));
            }

            if (byteCount < 0 || byteCount > bufferLength - start)
            {
                Throw.ArgumentOutOfRange(byteCountParameterName);
            }
        }
    }

    internal static class Throw
    {
        [DoesNotReturn]
        internal static void ArgumentOutOfRange(string parameterName)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        [DoesNotReturn]
        internal static void OutOfBounds()
        {
            throw new BadImageFormatException("Out of bounds read.");
        }
    }

    /// <summary>
    /// Represents data read from a stream.
    /// </summary>
    /// <remarks>
    /// Uses memory map to load data from streams backed by files that are bigger than <see cref="MemoryMapThreshold"/>.
    /// </remarks>
    internal sealed class StreamMemoryBlockProvider : MemoryBlockProvider
    {
        // We're trying to balance total VM usage (which is a minimum of 64KB for a memory mapped file)
        // with private working set (since heap memory will be backed by the paging file and non-sharable).
        // Internal for testing.
        internal const int MemoryMapThreshold = 16 * 1024;

        // The stream is user specified and might not be thread-safe.
        // Any read from the stream must be protected by streamGuard.
        private Stream _stream;
        private readonly object _streamGuard;

        private readonly bool _leaveOpen;
        private readonly bool _useMemoryMap;

        private readonly long _imageStart;
        private readonly int _imageSize;

        private MemoryMappedFile? _lazyMemoryMap;

        public StreamMemoryBlockProvider(Stream stream, long imageStart, int imageSize, bool leaveOpen)
        {
            Debug.Assert(stream.CanSeek && stream.CanRead);
            _stream = stream;
            _streamGuard = new object();
            _imageStart = imageStart;
            _imageSize = imageSize;
            _leaveOpen = leaveOpen;
            _useMemoryMap = stream is FileStream;
        }

        protected override void Dispose(bool disposing)
        {
            Debug.Assert(disposing);
            if (!_leaveOpen)
            {
                Interlocked.Exchange(ref _stream, null!)?.Dispose();
            }

            Interlocked.Exchange(ref _lazyMemoryMap, null)?.Dispose();
        }

        public override int Size
        {
            get
            {
                return _imageSize;
            }
        }

        /// <exception cref="IOException">Error reading from the stream.</exception>
        internal static unsafe NativeHeapMemoryBlock ReadMemoryBlockNoLock(Stream stream, long start, int size)
        {
            var block = new NativeHeapMemoryBlock(size);
            bool fault = true;
            try
            {
                stream.Seek(start, SeekOrigin.Begin);
                stream.ReadExactly(block.Pointer, size);

                fault = false;
            }
            finally
            {
                if (fault)
                {
                    block.Dispose();
                }
            }

            return block;
        }

        public override bool TryGetUnderlyingStream([NotNullWhen(true)] out Stream? stream, out long imageStart, out int imageSize, [NotNullWhen(true)] out object? streamGuard)
        {
            stream = _stream;
            imageStart = _imageStart;
            imageSize = _imageSize;
            streamGuard = _streamGuard;
            return true;
        }

        /// <exception cref="IOException">Error while reading from the stream.</exception>
        protected override AbstractMemoryBlock GetMemoryBlockImpl(int start, int size)
        {
            long absoluteStart = _imageStart + start;

            if (_useMemoryMap && size > MemoryMapThreshold)
            {
                return CreateMemoryMappedFileBlock(absoluteStart, size);
            }

            lock (_streamGuard)
            {
                return ReadMemoryBlockNoLock(_stream!, absoluteStart, size);
            }
        }

        /// <exception cref="IOException">IO error while mapping memory or not enough memory to create the mapping.</exception>
        private MemoryMappedFileBlock CreateMemoryMappedFileBlock(long start, int size)
        {
            if (_lazyMemoryMap == null)
            {
                // CreateMemoryMap might modify the stream (calls FileStream.Flush)
                lock (_streamGuard)
                {
                    try
                    {
                        // leave the underlying stream open. It will be closed by the Dispose method.
                        _lazyMemoryMap ??=
                            MemoryMappedFile.CreateFromFile(
                                fileStream: (FileStream)_stream,
                                mapName: null,
                                capacity: 0,
                                access: MemoryMappedFileAccess.Read,
                                inheritability: HandleInheritability.None,
                                leaveOpen: true);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        throw new IOException(e.Message, e);
                    }
                }
            }

            MemoryMappedViewAccessor accessor;

            lock (_streamGuard)
            {
                accessor = _lazyMemoryMap.CreateViewAccessor(start, size, MemoryMappedFileAccess.Read);
            }

            return new MemoryMappedFileBlock(accessor, accessor.SafeMemoryMappedViewHandle, accessor.PointerOffset, size);
        }
    }

    /// <summary>
    /// Represents memory block allocated on native heap.
    /// </summary>
    /// <remarks>
    /// Owns the native memory resource.
    /// </remarks>
    internal sealed class NativeHeapMemoryBlock : AbstractMemoryBlock
    {
        private sealed unsafe class DisposableData : CriticalDisposableObject
        {
            private IntPtr _pointer;

            public DisposableData(int size)
            {
#if FEATURE_CER
                // make sure the current thread isn't aborted in between allocating and storing the pointer
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                { /* intentionally left blank */ }
                finally
#endif
                {
                    _pointer = Marshal.AllocHGlobal(size);
                }
            }

            protected override void Release()
            {
#if FEATURE_CER
                // make sure the current thread isn't aborted in between zeroing the pointer and freeing the memory
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                { /* intentionally left blank */ }
                finally
#endif
                {
                    IntPtr ptr = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
                    if (ptr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }

            public byte* Pointer => (byte*)_pointer;
        }

        private readonly DisposableData _data;
        private readonly int _size;

        internal NativeHeapMemoryBlock(int size)
        {
            _data = new DisposableData(size);
            _size = size;
        }

        public override void Dispose() => _data.Dispose();
        public override unsafe byte* Pointer => _data.Pointer;
        public override int Size => _size;
    }

    internal sealed unsafe class MemoryMappedFileBlock : AbstractMemoryBlock
    {
        private sealed class DisposableData : CriticalDisposableObject
        {
            // Usually a MemoryMappedViewAccessor, but kept
            // as an IDisposable for better testability.
            private IDisposable? _accessor;
            private SafeBuffer? _safeBuffer;
            private byte* _pointer;

            public DisposableData(IDisposable accessor, SafeBuffer safeBuffer, long offset)
            {
#if FEATURE_CER
                // Make sure the current thread isn't aborted in between acquiring the pointer and assigning the fields.
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                { /* intentionally left blank */ }
                finally
#endif
                {
                    byte* basePointer = null;
                    safeBuffer.AcquirePointer(ref basePointer);

                    _accessor = accessor;
                    _safeBuffer = safeBuffer;
                    _pointer = basePointer + offset;
                }
            }

            protected override void Release()
            {
#if FEATURE_CER
                // Make sure the current thread isn't aborted in between zeroing the references and releasing/disposing.
                // Safe buffer only frees the underlying resource if its ref count drops to zero, so we have to make sure it does.
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                { /* intentionally left blank */ }
                finally
#endif
                {
                    Interlocked.Exchange(ref _safeBuffer, null)?.ReleasePointer();
                    Interlocked.Exchange(ref _accessor, null)?.Dispose();
                }

                _pointer = null;
            }

            public byte* Pointer => _pointer;
        }

        private readonly DisposableData _data;
        private readonly int _size;

        internal MemoryMappedFileBlock(IDisposable accessor, SafeBuffer safeBuffer, long offset, int size)
        {
            _data = new DisposableData(accessor, safeBuffer, offset);
            _size = size;
        }

        public override void Dispose() => _data.Dispose();
        public override byte* Pointer => _data.Pointer;
        public override int Size => _size;
    }

    internal abstract class CriticalDisposableObject : CriticalFinalizerObject, IDisposable
    {
        protected abstract void Release();

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        ~CriticalDisposableObject()
        {
            Release();
        }
    }

    internal static partial class StreamExtensions
    {
        internal static unsafe void ReadExactly(this Stream stream, byte* buffer, int size)
            => stream.ReadExactly(new Span<byte>(buffer, size));
    }

    internal readonly unsafe partial struct MemoryBlock
    {
        internal readonly byte* Pointer;
        internal readonly int Length;

        internal MemoryBlock(byte* buffer, int length)
        {
            Debug.Assert(length >= 0 && (buffer != null || length == 0));
            this.Pointer = buffer;
            this.Length = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckBounds(int offset, int byteCount)
        {
            if (unchecked((ulong)(uint)offset + (uint)byteCount) > (ulong)Length)
            {
                Throw.OutOfBounds();
            }
        }

        internal MemoryBlock GetMemoryBlockAt(int offset, int length)
        {
            CheckBounds(offset, length);
            return new MemoryBlock(Pointer + offset, length);
        }

        internal byte PeekByte(int offset)
        {
            CheckBounds(offset, sizeof(byte));
            return Pointer[offset];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint PeekUInt32(int offset)
        {
            CheckBounds(offset, sizeof(uint));

            uint result = Unsafe.ReadUnaligned<uint>(Pointer + offset);
            return BitConverter.IsLittleEndian ? result : BinaryPrimitives.ReverseEndianness(result);
        }

        internal ushort PeekUInt16(int offset)
        {
            CheckBounds(offset, sizeof(ushort));

            ushort result = Unsafe.ReadUnaligned<ushort>(Pointer + offset);
            return BitConverter.IsLittleEndian ? result : BinaryPrimitives.ReverseEndianness(result);
        }

        internal byte[] PeekBytes(int offset, int byteCount)
        {
            CheckBounds(offset, byteCount);
            return new ReadOnlySpan<byte>(Pointer + offset, byteCount).ToArray();
        }
    }
}
