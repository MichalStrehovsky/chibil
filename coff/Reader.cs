using System;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Coff
{
    public class CoffReader : IDisposable
    {
        private MemoryBlockProvider _coffObject;

        // If we read the data from the COFF object lazily (_coffObject != null) we defer reading the headers.
        private CoffHeaders _lazyCoffHeaders;

        private AbstractMemoryBlock _lazySectionHeadersBlock;
        private AbstractMemoryBlock _lazySymbolTableBlock;
        private AbstractMemoryBlock _lazyStringTableBlock;
        private AbstractMemoryBlock _lazyMetadataBlock;
        private AbstractMemoryBlock _lazyImageBlock;
        private AbstractMemoryBlock[] _lazySectionBlocks;
        private AbstractMemoryBlock[] _lazySectionRelocationBlocks;

        public unsafe CoffReader(byte* coffObject, int size)
        {
            ArgumentNullException.ThrowIfNull(coffObject, nameof(coffObject));

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            _coffObject = new ExternalMemoryBlockProvider(coffObject, size);
        }

        /// <summary>
        /// Creates a COFF object reader over a COFF object stored in a stream.
        /// </summary>
        /// <param name="coffStream">COFF object stream.</param>
        /// <exception cref="ArgumentNullException"><paramref name="coffStream"/> is null.</exception>
        /// <remarks>
        /// Ownership of the stream is transferred to the <see cref="CoffReader"/> upon successful validation of constructor arguments. It will be
        /// disposed by the <see cref="CoffReader"/> and the caller must not manipulate it.
        /// </remarks>
        public CoffReader(Stream coffStream)
            : this(coffStream, PEStreamOptions.Default)
        {
        }

        /// <summary>
        /// Creates a COFF object reader over a COFF object stored in a stream beginning at its current position and ending at the end of the stream.
        /// </summary>
        /// <param name="coffStream">COFF object stream.</param>
        /// <param name="options">
        /// Options specifying how sections of the COFF object are read from the stream.
        ///
        /// Unless <see cref="PEStreamOptions.LeaveOpen"/> is specified, ownership of the stream is transferred to the <see cref="CoffReader"/>
        /// upon successful argument validation. It will be disposed by the <see cref="CoffReader"/> and the caller must not manipulate it.
        ///
        /// Unless <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/> is specified no data
        /// is read from the stream during the construction of the <see cref="CoffReader"/>. Furthermore, the stream must not be manipulated
        /// by caller while the <see cref="CoffReader"/> is alive and undisposed.
        ///
        /// If <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/>, the <see cref="CoffReader"/>
        /// will have read all of the data requested during construction. As such, if <see cref="PEStreamOptions.LeaveOpen"/> is also
        /// specified, the caller retains full ownership of the stream and is assured that it will not be manipulated by the <see cref="CoffReader"/>
        /// after construction.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="coffStream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> has an invalid value.</exception>
        /// <exception cref="IOException">Error reading from the stream (only when prefetching data).</exception>
        /// <exception cref="BadImageFormatException"><see cref="PEStreamOptions.PrefetchMetadata"/> is specified and the COFF headers of the object are invalid.</exception>
        public CoffReader(Stream coffStream, PEStreamOptions options)
            : this(coffStream, options, 0)
        {
        }

        /// <summary>
        /// Creates a COFF object reader over a COFF object of the given size beginning at the stream's current position.
        /// </summary>
        /// <param name="coffStream">COFF object stream.</param>
        /// <param name="size">COFF object size.</param>
        /// <param name="options">
        /// Options specifying how sections of the COFF object are read from the stream.
        ///
        /// Unless <see cref="PEStreamOptions.LeaveOpen"/> is specified, ownership of the stream is transferred to the <see cref="CoffReader"/>
        /// upon successful argument validation. It will be disposed by the <see cref="CoffReader"/> and the caller must not manipulate it.
        ///
        /// Unless <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/> is specified no data
        /// is read from the stream during the construction of the <see cref="CoffReader"/>. Furthermore, the stream must not be manipulated
        /// by caller while the <see cref="CoffReader"/> is alive and undisposed.
        ///
        /// If <see cref="PEStreamOptions.PrefetchMetadata"/> or <see cref="PEStreamOptions.PrefetchEntireImage"/>, the <see cref="CoffReader"/>
        /// will have read all of the data requested during construction. As such, if <see cref="PEStreamOptions.LeaveOpen"/> is also
        /// specified, the caller retains full ownership of the stream and is assured that it will not be manipulated by the <see cref="CoffReader"/>
        /// after construction.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Size is negative or extends past the end of the stream.</exception>
        /// <exception cref="IOException">Error reading from the stream (only when prefetching data).</exception>
        /// <exception cref="BadImageFormatException"><see cref="PEStreamOptions.PrefetchMetadata"/> is specified and the COFF headers of the object are invalid.</exception>
        public unsafe CoffReader(Stream coffStream, PEStreamOptions options, int size)
        {
            if (coffStream is null)
            {
                throw new ArgumentNullException(nameof(coffStream));
            }

            if (!coffStream.CanRead || !coffStream.CanSeek)
            {
                throw new ArgumentException("Must support Read and Seek", nameof(coffStream));
            }

            if ((options & PEStreamOptions.IsLoadedImage) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            long start = coffStream.Position;
            int actualSize = StreamExtensions.GetAndValidateSize(coffStream, size, nameof(coffStream));

            bool closeStream = true;
            try
            {
                if ((options & (PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage)) == 0)
                {
                    _coffObject = new StreamMemoryBlockProvider(coffStream, start, actualSize, (options & PEStreamOptions.LeaveOpen) != 0);
                    closeStream = false;
                }
                else
                {
                    // Read in the entire COFF object or metadata blob:
                    if ((options & PEStreamOptions.PrefetchEntireImage) != 0)
                    {
                        var imageBlock = StreamMemoryBlockProvider.ReadMemoryBlockNoLock(coffStream, start, actualSize);
                        _lazyImageBlock = imageBlock;
                        _coffObject = new ExternalMemoryBlockProvider(imageBlock.Pointer, imageBlock.Size);

                        // if the caller asked for metadata initialize the COFF headers (calculates metadata offset):
                        if ((options & PEStreamOptions.PrefetchMetadata) != 0)
                        {
                            _lazyCoffHeaders = new CoffHeaders(imageBlock.GetStream(), imageBlock.Size);
                        }
                    }
                    else
                    {
                        // _coffObject is left null, but _lazyMetadataBlock is initialized up front.
                        _lazyCoffHeaders = new CoffHeaders(coffStream, actualSize);

                        if (_lazyCoffHeaders.MetadataStartOffset != -1)
                        {
                            _lazyMetadataBlock = StreamMemoryBlockProvider.ReadMemoryBlockNoLock(coffStream, start + _lazyCoffHeaders.MetadataStartOffset, _lazyCoffHeaders.MetadataSize);
                        }
                    }
                    // We read all we need, the stream is going to be closed.
                }
            }
            finally
            {
                if (closeStream && (options & PEStreamOptions.LeaveOpen) == 0)
                {
                    coffStream.Dispose();
                }
            }
        }

        public void Dispose()
        {
            _lazyCoffHeaders = null;

            _coffObject?.Dispose();
            _coffObject = null;

            _lazyImageBlock?.Dispose();
            _lazyImageBlock = null;

            _lazySectionHeadersBlock?.Dispose();
            _lazySectionHeadersBlock = null;

            _lazySymbolTableBlock?.Dispose();
            _lazySymbolTableBlock = null;

            _lazyStringTableBlock?.Dispose();
            _lazyStringTableBlock = null;

            _lazyMetadataBlock?.Dispose();
            _lazyMetadataBlock = null;

            var sectionBlocks = _lazySectionBlocks;
            if (sectionBlocks != null)
            {
                foreach (var block in sectionBlocks)
                {
                    block?.Dispose();
                }

                _lazySectionBlocks = null;
            }

            var sectionRelocationBlocks = _lazySectionRelocationBlocks;
            if (sectionRelocationBlocks != null)
            {
                foreach (var block in sectionRelocationBlocks)
                {
                    block?.Dispose();
                }

                _lazySectionRelocationBlocks = null;
            }
        }

        private MemoryBlockProvider GetCoffObject()
        {
            var coffObject = _coffObject;
            if (coffObject == null)
            {
                if (_lazyCoffHeaders == null)
                {
                    throw new ObjectDisposedException(nameof(CoffReader));
                }

                throw new InvalidOperationException();
            }

            return coffObject;
        }

        /// <summary>
        /// Gets the COFF headers.
        /// </summary>
        /// <exception cref="BadImageFormatException">The headers contain invalid data.</exception>
        /// <exception cref="IOException">Error reading from the stream.</exception>
        public CoffHeaders CoffHeaders
        {
            get
            {
                if (_lazyCoffHeaders == null)
                {
                    InitializeCoffHeaders();
                }

                return _lazyCoffHeaders;
            }
        }

        /// <summary>
        /// Gets a reader over the COFF section table.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the
        /// lifetime of the returned reader.
        /// </remarks>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public unsafe CoffSectionTableReader GetSectionTableReader()
        {
            AbstractMemoryBlock block = GetSectionHeadersBlock();
            return new CoffSectionTableReader(block.Pointer, block.Size, memoryOwner: this);
        }

        private void InitializeCoffHeaders()
        {
            MemoryBlockProvider coffObject = GetCoffObject();

            CoffHeaders headers;
            // If the COFF object is backed by a stream, use that to read the headers.
            if (coffObject.TryGetUnderlyingStream(out Stream stream, out long imageStart, out int imageSize, out object streamGuard))
            {
                lock (streamGuard)
                {
                    Debug.Assert(imageStart >= 0 && imageStart <= stream.Length);
                    stream.Seek(imageStart, SeekOrigin.Begin);
                    headers = new CoffHeaders(stream, imageSize);
                }
            }
            // Otherwise, get the memory block and wrap it in a stream.
            else
            {
                // No need to acquire any lock here; GetStream() creates a new stream.
                AbstractMemoryBlock memoryBlock = coffObject.GetMemoryBlock();
                headers = new CoffHeaders(memoryBlock.GetStream(), memoryBlock.Size);
            }

            Interlocked.CompareExchange(ref _lazyCoffHeaders, headers, null);
        }

        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        /// <exception cref="InvalidOperationException">COFF object doesn't have metadata.</exception>
        private AbstractMemoryBlock GetMetadataBlock()
        {
            if (!HasMetadata)
            {
                throw new InvalidOperationException();
            }

            if (_lazyMetadataBlock == null)
            {
                var newBlock = GetCoffObject().GetMemoryBlock(CoffHeaders.MetadataStartOffset, CoffHeaders.MetadataSize);
                if (Interlocked.CompareExchange(ref _lazyMetadataBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return _lazyMetadataBlock;
        }

        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        /// <exception cref="InvalidOperationException">COFF object not available.</exception>
        private AbstractMemoryBlock GetCoffSectionBlock(CoffSection section)
        {
            int index = section._index;

            Debug.Assert(index >= 0 && index < CoffHeaders.CoffHeader.NumberOfSections);

            var coffObject = GetCoffObject();

            if (_lazySectionBlocks == null)
            {
                Interlocked.CompareExchange(ref _lazySectionBlocks, new AbstractMemoryBlock[CoffHeaders.CoffHeader.NumberOfSections], null);
            }

            AbstractMemoryBlock existingBlock = Volatile.Read(ref _lazySectionBlocks[index]);
            if (existingBlock != null)
            {
                return existingBlock;
            }

            int size = section.PointerToRawData == 0 ? 0 : section.SizeOfRawData;

            AbstractMemoryBlock newBlock = coffObject.GetMemoryBlock(section.PointerToRawData, size);


            if (Interlocked.CompareExchange(ref _lazySectionBlocks[index], newBlock, null) != null)
            {
                // another thread created the block already, we need to dispose ours:
                newBlock.Dispose();
            }

            return _lazySectionBlocks[index]!;
        }

        /// <summary>
        /// Loads the specified COFF section into memory and returns a memory block that spans the section.
        /// </summary>
        /// <exception cref="InvalidOperationException">COFF object not available.</exception>
        public PEMemoryBlock GetSectionData(CoffSection section)
        {
            return new PEMemoryBlock(GetCoffSectionBlock(section));
        }

        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        /// <exception cref="InvalidOperationException">COFF object not available.</exception>
        private AbstractMemoryBlock GetCoffSectionRelocationBlock(CoffSection section)
        {
            int index = section._index;

            Debug.Assert(index >= 0 && index < CoffHeaders.CoffHeader.NumberOfSections);

            var coffObject = GetCoffObject();

            if (_lazySectionRelocationBlocks == null)
            {
                Interlocked.CompareExchange(ref _lazySectionRelocationBlocks, new AbstractMemoryBlock[CoffHeaders.CoffHeader.NumberOfSections], null);
            }

            AbstractMemoryBlock existingBlock = Volatile.Read(ref _lazySectionRelocationBlocks[index]);
            if (existingBlock != null)
            {
                return existingBlock;
            }

            int size = section.PointerToRelocations == 0 ? 0 : section.NumberOfRelocations * CoffSection.RelocationSize;

            AbstractMemoryBlock newBlock = coffObject.GetMemoryBlock(section.PointerToRelocations, size);


            if (Interlocked.CompareExchange(ref _lazySectionRelocationBlocks[index], newBlock, null) != null)
            {
                // another thread created the block already, we need to dispose ours:
                newBlock.Dispose();
            }

            return _lazySectionRelocationBlocks[index]!;
        }

        /// <summary>
        /// Loads the COFF relocation entries for the specified section into memory and returns a memory block that spans them.
        /// </summary>
        /// <exception cref="InvalidOperationException">COFF object not available.</exception>
        public PEMemoryBlock GetSectionRelocations(CoffSection section)
        {
            return new PEMemoryBlock(GetCoffSectionRelocationBlock(section));
        }

        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        /// <exception cref="InvalidOperationException">COFF object doesn't have metadata.</exception>
        private AbstractMemoryBlock GetSectionHeadersBlock()
        {
            if (_lazySectionHeadersBlock == null)
            {
                var newBlock = GetCoffObject().GetMemoryBlock(CoffHeaders.SectionHeadersStartOffset, CoffHeaders.CoffHeader.NumberOfSections * CoffSection.Size);
                if (Interlocked.CompareExchange(ref _lazySectionHeadersBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return _lazySectionHeadersBlock;
        }

        private AbstractMemoryBlock GetSymbolTableBlock()
        {
            if (_lazySymbolTableBlock == null)
            {
                var newBlock = GetCoffObject().GetMemoryBlock(CoffHeaders.CoffHeader.PointerToSymbolTable, CoffHeaders.CoffHeader.NumberOfSymbols * CoffSymbol.Size);
                if (Interlocked.CompareExchange(ref _lazySymbolTableBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return _lazySymbolTableBlock;
        }

        private AbstractMemoryBlock GetStringTableBlock()
        {
            if (_lazyStringTableBlock == null)
            {
                var newBlock = GetCoffObject().GetMemoryBlock(CoffHeaders.StringTableStartOffset, CoffHeaders.StringTableSize);
                if (Interlocked.CompareExchange(ref _lazyStringTableBlock, newBlock, null) != null)
                {
                    // another thread created the block already, we need to dispose ours:
                    newBlock.Dispose();
                }
            }

            return _lazyStringTableBlock;
        }

        /// <summary>
        /// Returns true if the object file contains CLI metadata.
        /// </summary>
        /// <exception cref="BadImageFormatException">The object headers contain invalid data.</exception>
        /// <exception cref="IOException">Error reading from the underlying stream.</exception>
        public bool HasMetadata
        {
            get { return CoffHeaders.MetadataSize > 0; }
        }

        /// <summary>
        /// Loads COFF section that contains CLI metadata.
        /// </summary>
        /// <exception cref="InvalidOperationException">The COFF object doesn't contain metadata (<see cref="HasMetadata"/> returns false).</exception>
        /// <exception cref="BadImageFormatException">The COFF headers contain invalid data.</exception>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public PEMemoryBlock GetMetadata()
        {
            return new PEMemoryBlock(GetMetadataBlock());
        }

        /// <summary>
        /// Gets a reader over the COFF symbol table.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the
        /// lifetime of the returned reader.
        /// </remarks>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public unsafe CoffSymbolTableReader GetSymbolTableReader()
        {
            AbstractMemoryBlock block = GetSymbolTableBlock();
            return new CoffSymbolTableReader(block.Pointer, block.Size, memoryOwner: this);
        }

        /// <summary>
        /// Gets a reader over the COFF string table.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the
        /// lifetime of the returned reader.
        /// </remarks>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public CoffStringTableReader GetStringTableReader()
            => GetStringTableReader(MetadataStringDecoder.DefaultUTF8);

        /// <summary>
        /// Gets a reader over the COFF string table using the given UTF-8 decoder.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the
        /// lifetime of the returned reader.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="utf8Decoder"/> is null.</exception>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public unsafe CoffStringTableReader GetStringTableReader(MetadataStringDecoder utf8Decoder)
        {
            AbstractMemoryBlock sectionTable = GetSectionHeadersBlock();
            AbstractMemoryBlock symbolTable = GetSymbolTableBlock();
            AbstractMemoryBlock stringTable = GetStringTableBlock();
            return new CoffStringTableReader(sectionTable.Pointer, sectionTable.Size, symbolTable.Pointer, symbolTable.Size, stringTable.Pointer, stringTable.Size, utf8Decoder, memoryOwner: this);
        }
    }

    public sealed class CoffHeaders
    {
        private readonly CoffHeader _coffHeader;
        private readonly int _sectionHeadersStartOffset;

        private readonly int _metadataStartOffset = -1;
        private readonly int _metadataSize;

        private readonly int _stringTableStartOffset = -1;
        private readonly int _stringTableSize;

        /// <summary>
        /// Reads COFF headers from the current location in the stream.
        /// </summary>
        /// <param name="coffStream">Stream containing COFF object of the given size starting at its current position.</param>
        /// <param name="size">Size of the COFF object.</param>
        /// <exception cref="BadImageFormatException">The data read from stream have invalid format.</exception>
        /// <exception cref="IOException">Error reading from the stream.</exception>
        /// <exception cref="ArgumentException">The stream doesn't support seek operations.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="coffStream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Size is negative or extends past the end of the stream.</exception>
        public CoffHeaders(Stream coffStream, int size)
        {
            ArgumentNullException.ThrowIfNull(coffStream, nameof(coffStream));

            if (!coffStream.CanRead || !coffStream.CanSeek)
            {
                throw new ArgumentException("Stream must support Read and Seek", nameof(coffStream));
            }

            int actualSize = StreamExtensions.GetAndValidateSize(coffStream, size, nameof(coffStream));
            var reader = new PEBinaryReader(coffStream, actualSize);

            _coffHeader = new CoffHeader(ref reader);

            // In COFF files the size of the optional header must be zero.
            if (_coffHeader.SizeOfOptionalHeader != 0)
            {
                throw new BadImageFormatException();
            }

            _sectionHeadersStartOffset = reader.Offset;

            for (int i = 0; i < _coffHeader.NumberOfSections; i++)
            {
                int oldOffset = reader.Offset;
                if (reader.ReadSectionNameEquals(".cormeta"u8))
                {
                    reader.Offset += 4 + 4;
                    _metadataSize = reader.ReadInt32();
                    _metadataStartOffset = reader.ReadInt32();
                    break;
                }

                reader.Offset = oldOffset + CoffSection.Size;
            }

            if (_coffHeader.PointerToSymbolTable != 0)
            {
                // The string table begins with a 4-byte size field that counts itself, immediately
                // following the symbol table. StringTableStartOffset points at that size field and
                // StringTableSize includes it, so symbol name offsets (which are relative to the
                // start of the string table) index directly into the block.
                _stringTableStartOffset = _coffHeader.PointerToSymbolTable + _coffHeader.NumberOfSymbols * CoffSymbol.Size;
                reader.Offset = _stringTableStartOffset;
                _stringTableSize = reader.ReadInt32();
            }
        }

        /// <summary>
        /// Gets the offset (in bytes) from the start of the COFF object to the start of section headers.
        /// </summary>
        public int SectionHeadersStartOffset
        {
            get { return _sectionHeadersStartOffset; }
        }

        /// <summary>
        /// Gets the offset (in bytes) from the start of the COFF object to the start of the CLI metadata,
        /// or -1 if the object does not contain metadata.
        /// </summary>
        public int MetadataStartOffset
        {
            get { return _metadataStartOffset; }
        }

        /// <summary>
        /// Gets the size of the CLI metadata, or 0 if the COFF object does not contain metadata.
        /// </summary>
        public int MetadataSize
        {
            get { return _metadataSize; }
        }

        public int StringTableStartOffset
        {
            get { return _stringTableStartOffset; }
        }

        public int StringTableSize
        {
            get { return _stringTableSize; }
        }

        /// <summary>
        /// Gets the COFF header of the COFF object.
        /// </summary>
        public CoffHeader CoffHeader
        {
            get { return _coffHeader; }
        }
    }

    /// <summary>
    /// Distinguishes the special COFF section numbers from a reference to a physical section.
    /// </summary>
    public enum CoffSectionHandleKind
    {
        Debug = -2,
        Absolute = -1,
        Undefined = 0,
        Physical = 1,
    }

    /// <summary>
    /// An opaque handle to a COFF section record. It stores the one-based section number as found in the
    /// section table (and in a symbol's section number field). Values that are not <see cref="Kind"/>
    /// <see cref="CoffSectionHandleKind.Physical"/> do not reference a section in the table. Use
    /// <see cref="CoffSectionTableReader.GetCoffSection(CoffSectionHandle)"/> to get a queryable view of a
    /// physical section.
    /// </summary>
    public readonly struct CoffSectionHandle
    {
        internal readonly int _value;

        internal CoffSectionHandle(int value) => _value = value;

        /// <summary>
        /// The kind of section this handle refers to. <see cref="CoffSectionHandleKind.Physical"/> means it
        /// references a section in the table (a one-based section number); the other values are the special
        /// COFF section numbers.
        /// </summary>
        public CoffSectionHandleKind Kind
            => _value >= 1 ? CoffSectionHandleKind.Physical : (CoffSectionHandleKind)_value;
    }

    /// <summary>
    /// A handle to a COFF section name. It is an offset into the section table pointing at the 8-byte name
    /// field of a section header. The name is either stored inline in that field (a short name, null-padded)
    /// or, when the field starts with a forward slash ('/'), the remaining bytes are an ASCII decimal offset
    /// into the COFF string table. Use <see cref="CoffStringTableReader.GetString(CoffSectionNameHandle)"/>
    /// to resolve it to a string.
    /// </summary>
    public readonly struct CoffSectionNameHandle
    {
        // Size of the fixed inline name field in a COFF section header.
        internal const int Size = 8;

        private readonly int _sectionTableOffset;

        internal CoffSectionNameHandle(int sectionTableOffset)
            => _sectionTableOffset = sectionTableOffset;

        /// <summary>
        /// The offset of the section's 8-byte name field within the section table.
        /// </summary>
        internal int SectionTableOffset => _sectionTableOffset;
    }

    /// <summary>
    /// A queryable view of a single COFF section header. Obtained from
    /// <see cref="CoffSectionTableReader.GetCoffSection(CoffSectionHandle)"/>.
    /// </summary>
    public readonly struct CoffSection
    {
        internal const int Size = 40;

        /// <summary>
        /// The size in bytes of a single COFF relocation entry (IMAGE_RELOCATION).
        /// </summary>
        internal const int RelocationSize = 10;

        private readonly CoffSectionTableReader _reader;
        internal readonly int _index;

        internal CoffSection(CoffSectionTableReader reader, int index)
        {
            _reader = reader;
            _index = index;
        }

        public CoffSectionHandle Handle => new CoffSectionHandle(_index + 1);

        /// <summary>
        /// The name of the section, either inline or a reference into the string table.
        /// </summary>
        public CoffSectionNameHandle Name => _reader.GetName(_index);

        /// <summary>
        /// The total size of the section when loaded into memory.
        /// If this value is greater than <see cref="SizeOfRawData"/>, the section is zero-padded.
        /// This field is valid only for PE images and should be set to zero for object files.
        /// </summary>
        public int VirtualSize => _reader.GetVirtualSize(_index);

        /// <summary>
        /// For PE images, the address of the first byte of the section relative to the image base when the
        /// section is loaded into memory. For object files, this field is the address of the first byte before
        /// relocation is applied; for simplicity, compilers should set this to zero. Otherwise,
        /// it is an arbitrary value that is subtracted from offsets during relocation.
        /// </summary>
        public int VirtualAddress => _reader.GetVirtualAddress(_index);

        /// <summary>
        /// The size of the section (for object files) or the size of the initialized data on disk (for image files).
        /// For PE images, this must be a multiple of <see cref="PEHeader.FileAlignment"/>.
        /// If this is less than <see cref="VirtualSize"/>, the remainder of the section is zero-filled.
        /// Because the <see cref="SizeOfRawData"/> field is rounded but the <see cref="VirtualSize"/> field is not,
        /// it is possible for <see cref="SizeOfRawData"/> to be greater than <see cref="VirtualSize"/> as well.
        ///  When a section contains only uninitialized data, this field should be zero.
        /// </summary>
        public int SizeOfRawData => _reader.GetSizeOfRawData(_index);

        /// <summary>
        /// The file pointer to the first page of the section within the COFF file.
        /// For PE images, this must be a multiple of <see cref="PEHeader.FileAlignment"/>.
        /// For object files, the value should be aligned on a 4 byte boundary for best performance.
        /// When a section contains only uninitialized data, this field should be zero.
        /// </summary>
        public int PointerToRawData => _reader.GetPointerToRawData(_index);

        /// <summary>
        /// The file pointer to the beginning of relocation entries for the section.
        /// This is set to zero for PE images or if there are no relocations.
        /// </summary>
        public int PointerToRelocations => _reader.GetPointerToRelocations(_index);

        /// <summary>
        /// The file pointer to the beginning of line-number entries for the section.
        /// This is set to zero if there are no COFF line numbers.
        /// This value should be zero for an image because COFF debugging information is deprecated.
        /// </summary>
        public int PointerToLineNumbers => _reader.GetPointerToLineNumbers(_index);

        /// <summary>
        /// The number of relocation entries for the section. This is set to zero for PE images.
        /// </summary>
        public ushort NumberOfRelocations => _reader.GetNumberOfRelocations(_index);

        /// <summary>
        /// The number of line-number entries for the section.
        ///  This value should be zero for an image because COFF debugging information is deprecated.
        /// </summary>
        public ushort NumberOfLineNumbers => _reader.GetNumberOfLineNumbers(_index);

        /// <summary>
        /// The flags that describe the characteristics of the section.
        /// </summary>
        public SectionCharacteristics SectionCharacteristics => _reader.GetSectionCharacteristics(_index);
    }

    /// <summary>
    /// Reads the COFF section table. Provides random access to section headers via
    /// <see cref="GetCoffSection(CoffSectionHandle)"/> and enumeration of the section records via
    /// <see cref="Sections"/>.
    /// </summary>
    public class CoffSectionTableReader
    {
        private MemoryBlock _block;
        private readonly int _numberOfSections;
        private readonly object _memoryOwner;

        public unsafe CoffSectionTableReader(byte* sectionTable, int length)
            : this(sectionTable, length, memoryOwner: null)
        {
        }

        internal unsafe CoffSectionTableReader(byte* sectionTable, int length, object memoryOwner)
        {
            _block = new MemoryBlock(sectionTable, length);
            _numberOfSections = length / CoffSection.Size;
            _memoryOwner = memoryOwner;
        }

        /// <summary>
        /// The number of section headers in the table.
        /// </summary>
        public int NumberOfSections => _numberOfSections;

        /// <summary>
        /// Gets the <see cref="CoffSection"/> referred to by <paramref name="handle"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle"/> does not reference a physical section.</exception>
        public CoffSection GetCoffSection(CoffSectionHandle handle)
        {
            if (handle._value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(handle));
            }

            return new CoffSection(this, handle._value - 1);
        }

        /// <summary>
        /// Enumerates handles to the section records.
        /// </summary>
        public CoffSectionHandleCollection Sections => new CoffSectionHandleCollection(this);

        internal CoffSectionNameHandle GetName(int index)
            => new CoffSectionNameHandle(index * CoffSection.Size);

        internal int GetVirtualSize(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 8);

        internal int GetVirtualAddress(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 12);

        internal int GetSizeOfRawData(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 16);

        internal int GetPointerToRawData(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 20);

        internal int GetPointerToRelocations(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 24);

        internal int GetPointerToLineNumbers(int index)
            => (int)_block.PeekUInt32(index * CoffSection.Size + 28);

        internal ushort GetNumberOfRelocations(int index)
            => _block.PeekUInt16(index * CoffSection.Size + 32);

        internal ushort GetNumberOfLineNumbers(int index)
            => _block.PeekUInt16(index * CoffSection.Size + 34);

        internal SectionCharacteristics GetSectionCharacteristics(int index)
            => (SectionCharacteristics)_block.PeekUInt32(index * CoffSection.Size + 36);
    }

    /// <summary>
    /// A collection of <see cref="CoffSectionHandle"/> that enumerates the section records of a
    /// <see cref="CoffSectionTableReader"/>.
    /// </summary>
    public readonly struct CoffSectionHandleCollection : IEnumerable<CoffSectionHandle>
    {
        private readonly CoffSectionTableReader _reader;

        internal CoffSectionHandleCollection(CoffSectionTableReader reader)
            => _reader = reader;

        public Enumerator GetEnumerator() => new Enumerator(_reader);

        IEnumerator<CoffSectionHandle> IEnumerable<CoffSectionHandle>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<CoffSectionHandle>
        {
            private readonly CoffSectionTableReader _reader;
            private int _current;

            internal Enumerator(CoffSectionTableReader reader)
            {
                _reader = reader;
                _current = -1;
            }

            public CoffSectionHandle Current => new CoffSectionHandle(_current + 1);

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_current + 1 >= _reader.NumberOfSections)
                {
                    return false;
                }

                _current++;
                return true;
            }

            public void Reset()
            {
                _current = -1;
            }

            public void Dispose() { }
        }
    }

    internal readonly partial struct PEBinaryReader
    {
        public bool ReadSectionNameEquals(ReadOnlySpan<byte> name)
        {
            CheckBounds(_reader.BaseStream.Position, CoffSectionNameHandle.Size);
            Span<byte> buffer = stackalloc byte[CoffSectionNameHandle.Size];
            _reader.ReadExactly(buffer);
            return buffer.StartsWith(name)
                && (name.Length == CoffSectionNameHandle.Size || buffer[name.Length] == 0);
        }
    }

    // EditorBrowsable(Never) so that we don't clutter the completion list with these extensions; a user
    // is likely looking to work with the <see cref="CoffReader"/> type directly rather than invoke these
    // extension methods as regular statics.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class CoffReaderExtensions
    {
        /// <summary>
        /// Gets a <see cref="MetadataReader"/> from a <see cref="CoffReader"/>.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the lifetime of the metadata reader.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="coffReader"/> is null</exception>
        /// <exception cref="PlatformNotSupportedException">The current platform is big-endian.</exception>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public static MetadataReader GetMetadataReader(this CoffReader coffReader)
        {
            return GetMetadataReader(coffReader, MetadataReaderOptions.ApplyWindowsRuntimeProjections, null);
        }

        /// <summary>
        /// Gets a <see cref="MetadataReader"/> from a <see cref="CoffReader"/>.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> alive and undisposed throughout the lifetime of the metadata reader.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="coffReader"/> is null</exception>
        /// <exception cref="PlatformNotSupportedException">The current platform is big-endian.</exception>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public static MetadataReader GetMetadataReader(this CoffReader coffReader, MetadataReaderOptions options)
        {
            return GetMetadataReader(coffReader, options, null);
        }

        /// <summary>
        /// Gets a <see cref="MetadataReader"/> from a <see cref="CoffReader"/>.
        /// </summary>
        /// <remarks>
        /// The caller must keep the <see cref="CoffReader"/> undisposed throughout the lifetime of the metadata reader.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="coffReader"/> is null</exception>
        /// <exception cref="ArgumentException">The encoding of <paramref name="utf8Decoder"/> is not <see cref="UTF8Encoding"/>.</exception>
        /// <exception cref="PlatformNotSupportedException">The current platform is big-endian.</exception>
        /// <exception cref="IOException">IO error while reading from the underlying stream.</exception>
        public static unsafe MetadataReader GetMetadataReader(this CoffReader coffReader, MetadataReaderOptions options, MetadataStringDecoder utf8Decoder)
        {
            if (coffReader is null)
            {
                throw new ArgumentNullException(nameof(coffReader));
            }

            var metadata = coffReader.GetMetadata();
            return CreateMetadataReader(metadata.Pointer, metadata.Length, options, utf8Decoder, memoryOwner: coffReader);

            [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
            extern static MetadataReader CreateMetadataReader(byte* metadata, int length, MetadataReaderOptions options, MetadataStringDecoder utf8Decoder, object memoryOwner);
        }
    }

    // Adapted (subset) of System.Reflection.Metadata's internal Throw helper. It lives here rather
    // than in SrmCopies.cs because it is not a verbatim copy (only the members chibil needs, and
    // OutOfBounds uses a literal message instead of the SRM resource string).
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

    // COFF-specific helpers on the memory block. Per convention, non-SRM additions to
    // MemoryBlock live here rather than in SrmCopies.cs (which holds verbatim copies).
    internal readonly unsafe partial struct MemoryBlock
    {
        // Reads a null-terminated UTF-8 string, stopping at the terminator or the end of the block.
        internal string PeekUtf8NullTerminated(int offset, MetadataStringDecoder utf8Decoder)
        {
            CheckBounds(offset, 0);
            return PeekUtf8NullTerminated(offset, Length - offset, utf8Decoder);
        }

        // Reads a null-terminated UTF-8 string, reading at most maxLength bytes (used for the fixed
        // 8-byte inline name field of a COFF symbol, which is not null-terminated when it is full).
        internal string PeekUtf8NullTerminated(int offset, int maxLength, MetadataStringDecoder utf8Decoder)
        {
            CheckBounds(offset, maxLength);
            var span = new ReadOnlySpan<byte>(Pointer + offset, maxLength);
            int length = span.IndexOf((byte)0);
            if (length < 0)
            {
                length = maxLength;
            }

            return utf8Decoder.GetString(Pointer + offset, length);
        }

        // Returns a read-only span over a range of the block, used for in-place parsing (e.g. the
        // ASCII decimal string-table offset embedded in a long COFF section name) without allocating.
        internal ReadOnlySpan<byte> PeekBytesSpan(int offset, int length)
        {
            CheckBounds(offset, length);
            return new ReadOnlySpan<byte>(Pointer + offset, length);
        }
    }

    /// <summary>
    /// A handle to a COFF symbol name. It is an offset into the symbol table pointing at the 8-byte
    /// name field of a symbol record. The name is either stored inline in that field (a short name,
    /// null-padded) or, when the first four bytes of the field are zero, the remaining four bytes are
    /// an offset into the COFF string table. Use
    /// <see cref="CoffStringTableReader.GetString(CoffStringHandle)"/> to resolve it to a string.
    /// </summary>
    public readonly struct CoffStringHandle
    {
        // Size of the fixed inline name field in a COFF symbol record.
        internal const int Size = 8;

        private readonly int _symbolTableOffset;

        internal CoffStringHandle(int symbolTableOffset)
            => _symbolTableOffset = symbolTableOffset;

        /// <summary>
        /// The offset of the symbol's 8-byte name field within the symbol table.
        /// </summary>
        internal int SymbolTableOffset => _symbolTableOffset;
    }

    /// <summary>
    /// A queryable view of a single COFF symbol record. Obtained from
    /// <see cref="CoffSymbolTableReader.GetCoffSymbol(CoffSymbolHandle)"/>.
    /// </summary>
    public readonly struct CoffSymbol
    {
        internal const int Size = 18;

        private readonly CoffSymbolTableReader _reader;
        private readonly int _index;

        internal CoffSymbol(CoffSymbolTableReader reader, int index)
        {
            _reader = reader;
            _index = index;
        }

        public CoffSymbolHandle Handle => new CoffSymbolHandle(_index);

        /// <summary>
        /// The symbol name, either inline or a reference into the string table.
        /// </summary>
        public CoffStringHandle Name => _reader.GetName(_index);

        /// <summary>
        /// The value associated with the symbol. Its interpretation depends on
        /// <see cref="SectionNumber"/> and <see cref="StorageClass"/>.
        /// </summary>
        public uint Value => _reader.GetValue(_index);

        /// <summary>
        /// A handle to the section the symbol is defined in. The handle's
        /// <see cref="CoffSectionHandle.Kind"/> is <see cref="CoffSectionHandleKind.Physical"/> for a
        /// symbol defined in a section, or a special value (undefined, absolute, or debug) otherwise.
        /// </summary>
        public CoffSectionHandle SectionNumber => new CoffSectionHandle(_reader.GetSectionNumber(_index));

        /// <summary>
        /// The symbol type.
        /// </summary>
        public CoffSymbolType Type => _reader.GetSymbolType(_index);

        /// <summary>
        /// The storage class of the symbol.
        /// </summary>
        public CoffSymbolStorageClass StorageClass => _reader.GetStorageClass(_index);

        /// <summary>
        /// The number of auxiliary records that follow this symbol record.
        /// </summary>
        public byte NumberOfAuxSymbols => _reader.GetNumberOfAuxSymbols(_index);
    }

    /// <summary>
    /// Reads the COFF symbol table. Provides random access to symbols via
    /// <see cref="GetCoffSymbol(CoffSymbolHandle)"/> and enumeration of the primary symbol
    /// records (skipping auxiliary records) via <see cref="Symbols"/>.
    /// </summary>
    public class CoffSymbolTableReader
    {
        private MemoryBlock _block;
        private readonly int _numberOfSymbols;
        private readonly object _memoryOwner;

        public unsafe CoffSymbolTableReader(byte* symbolTable, int length)
            : this(symbolTable, length, memoryOwner: null)
        {
        }

        internal unsafe CoffSymbolTableReader(byte* symbolTable, int length, object memoryOwner)
        {
            _block = new MemoryBlock(symbolTable, length);
            _numberOfSymbols = length / CoffSymbol.Size;
            _memoryOwner = memoryOwner;
        }

        /// <summary>
        /// The total number of symbol table records, including auxiliary records.
        /// </summary>
        public int NumberOfSymbols => _numberOfSymbols;

        /// <summary>
        /// Gets the <see cref="CoffSymbol"/> referred to by <paramref name="handle"/>.
        /// </summary>
        public CoffSymbol GetCoffSymbol(CoffSymbolHandle handle)
            => new CoffSymbol(this, handle._value);

        /// <summary>
        /// Enumerates handles to the primary symbol records, skipping auxiliary records.
        /// </summary>
        public CoffSymbolHandleCollection Symbols => new CoffSymbolHandleCollection(this);

        internal CoffStringHandle GetName(int index)
            => new CoffStringHandle(index * CoffSymbol.Size);

        internal uint GetValue(int index)
            => _block.PeekUInt32(index * CoffSymbol.Size + 8);

        internal short GetSectionNumber(int index)
            => (short)_block.PeekUInt16(index * CoffSymbol.Size + 12);

        internal CoffSymbolType GetSymbolType(int index)
            => (CoffSymbolType)_block.PeekUInt16(index * CoffSymbol.Size + 14);

        internal CoffSymbolStorageClass GetStorageClass(int index)
            => (CoffSymbolStorageClass)_block.PeekByte(index * CoffSymbol.Size + 16);

        internal byte GetNumberOfAuxSymbols(int index)
            => _block.PeekByte(index * CoffSymbol.Size + 17);
    }

    /// <summary>
    /// A collection of <see cref="CoffSymbolHandle"/> that enumerates the primary symbol records
    /// of a <see cref="CoffSymbolTableReader"/>, skipping the auxiliary records that follow them.
    /// </summary>
    public readonly struct CoffSymbolHandleCollection : IEnumerable<CoffSymbolHandle>
    {
        private readonly CoffSymbolTableReader _reader;

        internal CoffSymbolHandleCollection(CoffSymbolTableReader reader)
            => _reader = reader;

        public Enumerator GetEnumerator() => new Enumerator(_reader);

        IEnumerator<CoffSymbolHandle> IEnumerable<CoffSymbolHandle>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<CoffSymbolHandle>
        {
            private readonly CoffSymbolTableReader _reader;
            private int _current;
            private int _next;

            internal Enumerator(CoffSymbolTableReader reader)
            {
                _reader = reader;
                _current = -1;
                _next = 0;
            }

            public CoffSymbolHandle Current => new CoffSymbolHandle(_current);

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_next >= _reader.NumberOfSymbols)
                {
                    return false;
                }

                _current = _next;
                _next += 1 + _reader.GetNumberOfAuxSymbols(_current);
                return true;
            }

            public void Reset()
            {
                _current = -1;
                _next = 0;
            }

            public void Dispose() { }
        }
    }

    /// <summary>
    /// Resolves <see cref="CoffStringHandle"/> (symbol names) and <see cref="CoffSectionNameHandle"/>
    /// (section names) to strings. Short names are read inline from the symbol table or section table;
    /// long names are read from the COFF string table. In both cases the decoded bytes come from stable
    /// memory owned by the <see cref="CoffReader"/>, so a caching <see cref="MetadataStringDecoder"/> may
    /// safely retain pointers to them.
    /// </summary>
    public class CoffStringTableReader
    {
        // The inline (short) name field lives in the symbol table or the section table; a long name is
        // stored in the string table and referenced by an offset held in that field.
        private MemoryBlock _sectionTable;
        private MemoryBlock _symbolTable;
        private MemoryBlock _stringTable;
        private readonly MetadataStringDecoder _utf8Decoder;
        private readonly object _memoryOwner;

        public unsafe CoffStringTableReader(byte* sectionTable, int sectionTableLength, byte* symbolTable, int symbolTableLength, byte* stringTable, int stringTableLength)
            : this(sectionTable, sectionTableLength, symbolTable, symbolTableLength, stringTable, stringTableLength, MetadataStringDecoder.DefaultUTF8, memoryOwner: null)
        {
        }

        /// <exception cref="ArgumentNullException"><paramref name="utf8Decoder"/> is null.</exception>
        public unsafe CoffStringTableReader(byte* sectionTable, int sectionTableLength, byte* symbolTable, int symbolTableLength, byte* stringTable, int stringTableLength, MetadataStringDecoder utf8Decoder)
            : this(sectionTable, sectionTableLength, symbolTable, symbolTableLength, stringTable, stringTableLength, utf8Decoder, memoryOwner: null)
        {
        }

        /// <exception cref="ArgumentNullException"><paramref name="utf8Decoder"/> is null.</exception>
        internal unsafe CoffStringTableReader(byte* sectionTable, int sectionTableLength, byte* symbolTable, int symbolTableLength, byte* stringTable, int stringTableLength, MetadataStringDecoder utf8Decoder, object memoryOwner)
        {
            ArgumentNullException.ThrowIfNull(utf8Decoder);

            _sectionTable = new MemoryBlock(sectionTable, sectionTableLength);
            _symbolTable = new MemoryBlock(symbolTable, symbolTableLength);
            _stringTable = new MemoryBlock(stringTable, stringTableLength);
            _utf8Decoder = utf8Decoder;
            _memoryOwner = memoryOwner;
        }

        /// <summary>
        /// Resolves a symbol name handle to a string, handling both inline names and names stored
        /// in the string table.
        /// </summary>
        public string GetString(CoffStringHandle handle)
        {
            int offset = handle.SymbolTableOffset;

            // When the first four bytes of the name field are zero, the remaining four bytes are an
            // offset into the string table; otherwise the name is stored inline in the 8-byte field.
            if (_symbolTable.PeekUInt32(offset) == 0)
            {
                int stringTableOffset = (int)_symbolTable.PeekUInt32(offset + 4);
                return _stringTable.PeekUtf8NullTerminated(stringTableOffset, _utf8Decoder);
            }

            return _symbolTable.PeekUtf8NullTerminated(offset, CoffStringHandle.Size, _utf8Decoder);
        }

        /// <summary>
        /// Resolves a section name handle to a string, handling both inline names and names stored
        /// in the string table.
        /// </summary>
        /// <exception cref="BadImageFormatException">The long-name string-table offset is malformed.</exception>
        public string GetString(CoffSectionNameHandle handle)
        {
            int offset = handle.SectionTableOffset;

            // When the name field starts with a forward slash ('/'), the remaining bytes are an ASCII
            // decimal offset into the string table; otherwise the name is stored inline in the 8-byte field.
            if (_sectionTable.PeekByte(offset) == (byte)'/')
            {
                ReadOnlySpan<byte> digits = _sectionTable.PeekBytesSpan(offset + 1, CoffSectionNameHandle.Size - 1);

                // The "/<decimalOffset>" name is NUL-padded to the fixed 8-byte name field. TryParse
                // stops at the first non-digit and reports how many bytes it consumed; require that the
                // digits are followed by either the end of the field or NUL padding (and reject empty).
                if (!Utf8Parser.TryParse(digits, out int stringTableOffset, out int consumed) ||
                    (consumed != digits.Length && digits[consumed] != 0))
                {
                    throw new BadImageFormatException();
                }

                return _stringTable.PeekUtf8NullTerminated(stringTableOffset, _utf8Decoder);
            }

            return _sectionTable.PeekUtf8NullTerminated(offset, CoffSectionNameHandle.Size, _utf8Decoder);
        }
    }

    public static class RelocationDecodingExtensions
    {
        public static CoffSymbolHandle ReadSymbolHandle(this ref BlobReader reader)
        {
            return new CoffSymbolHandle(reader.ReadInt32());
        }
    }
}
