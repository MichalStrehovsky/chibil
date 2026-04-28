// This is a System.Reflection.Metadata writer to generate managed COFF OBJ files similar to
// what C++/CLI generates with /clr:pure option.
//

// Properties Nullable=disable and AllowUnsafeBlocks=true are set in Directory.Build.props

using System;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.IO;
using System.Reflection;

namespace System.Reflection.PortableExecutable
{
    /// <summary>
    /// Builds a .CRTMA$XCC section containing function pointers (as CLR token relocations)
    /// for module initializer functions. At link time, these are merged into the CRTMA table
    /// that the CRT module constructor iterates to call initialization functions.
    /// </summary>
    public class InitializerListSectionBuilder
    {
        private readonly CoffHeaderBuilder _coffHeaderBuilder;
        private readonly ManagedCoffSymbolTableBuilder _symbolTableBuilder;
        private readonly List<EntityHandle> _initializers = new List<EntityHandle>();

        // Each slot is pointer-sized: 4 bytes on x86, 8 bytes on 64-bit targets.
        private int SlotSize => _coffHeaderBuilder.Machine == Machine.I386 ? 4 : 8;

        public InitializerListSectionBuilder(CoffHeaderBuilder coffHeaderBuilder, ManagedCoffSymbolTableBuilder symbolTableBuilder)
        {
            _coffHeaderBuilder = coffHeaderBuilder;
            _symbolTableBuilder = symbolTableBuilder;
        }

        public void AddInitializer(EntityHandle methodHandle)
        {
            _initializers.Add(methodHandle);
        }

        public bool HasInitializers => _initializers.Count > 0;

        internal void Serialize(BlobBuilder builder)
        {
            foreach (var handle in _initializers)
            {
                for (int i = 0; i < SlotSize; i++)
                    builder.WriteByte(0);
            }
        }

        internal void SerializeRelocations(BlobBuilder builder)
        {
            for (int i = 0; i < _initializers.Count; i++)
            {
                int token = MetadataTokens.GetToken(_initializers[i]);
                var tokenSymbol = _symbolTableBuilder.GetOrAddUndefinedClrTokenSymbol(
                    token.ToString("X8"));
                new CoffRelocationEncoder(_coffHeaderBuilder, builder)
                    .AddTokenRelocation(i * SlotSize, tokenSymbol);
            }
        }
    }

    public class CodeViewLineNumberBuilder
    {
        private readonly List<LineNumberEntry> _entries = new List<LineNumberEntry>();

        private struct LineNumberEntry
        {
            public readonly CodeViewFileHandle File { get; }
            public readonly int CodeOffset { get; }
            public readonly int LineNumber { get; }

            public LineNumberEntry(CodeViewFileHandle file, int codeOffset, int lineNumber)
                => (File, CodeOffset, LineNumber) = (file, codeOffset, lineNumber);
        }

        public void AddLineNumber(CodeViewFileHandle file, int codeOffset, int lineNumber)
        {
            _entries.Add(new LineNumberEntry(file, codeOffset, lineNumber));
        }

        public void Reset()
        {
            _entries.Clear();
        }

        public void Serialize(BlobBuilder builder)
        {
            if (_entries.Count == 0)
                return;

            int fileId = _entries[0].File._index;

            builder.WriteInt32(fileId);
            builder.WriteInt32(_entries.Count);
            builder.WriteInt32(12 + 8 * _entries.Count);

            foreach (LineNumberEntry entry in _entries)
            {
                if (entry.File._index != fileId)
                    throw new NotSupportedException();

                builder.WriteInt32(entry.CodeOffset);
                builder.WriteUInt32(0x80000000 | (uint)entry.LineNumber);
            }
        }
    }

    public enum CodeViewChecksumType : byte
    {
        None = 0,
        SHA256 = 3,
    }

    public enum CodeViewLanguage : byte
    {
        C = 0x00,
        Cpp = 0x01,
    }

    public enum CodeViewMachine : ushort
    {
        I386 = 0x0007,    // CV_CFL_PENTIUMIII — matches MSVC /clr:pure output
        Amd64 = 0x00D0,   // CV_CFL_X64
        Arm64 = 0x00F6,   // CV_CFL_ARM64
    }

    [Flags]
    public enum CodeViewCompileFlags : uint
    {
        None = 0,
        EditAndContinue = 0x0100,
        NoDebugInfo = 0x0200,
        LTCG = 0x0400,
        NoDataAlign = 0x0800,
        ManagedPresent = 0x1000,
        SecurityChecks = 0x2000,
        HotPatch = 0x4000,
        CVTCIL = 0x8000,
        MSILModule = 0x10000,
    }

    public struct CodeViewFileHandle
    {
        internal readonly int _index;
        internal CodeViewFileHandle(int index) => _index = index;
    }

    public struct CodeViewManSlot
    {
        public int Slot;
        public int TypeToken;
        public string Name;

        public CodeViewManSlot(int slot, int typeToken, string name)
            => (Slot, TypeToken, Name) = (slot, typeToken, name);
    }

    /// <summary>
    /// Represents a lexical block scope (S_BLOCK32) containing local variable slots
    /// and optionally nested child scopes.
    /// </summary>
    public class CodeViewLocalScope
    {
        public int CodeOffset { get; set; }
        public int CodeLength { get; set; }
        public List<CodeViewManSlot> Slots { get; } = new List<CodeViewManSlot>();
        public List<CodeViewLocalScope> Children { get; } = new List<CodeViewLocalScope>();
    }

    public class CodeViewSymbolBuilder
    {
        private readonly CoffHeaderBuilder _coffHeaderBuilder;

        private readonly Dictionary<string, int> _stringTableIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly BlobBuilder _stringTable = new BlobBuilder();

        private readonly Dictionary<string, CodeViewFileHandle> _fileIndex = new Dictionary<string, CodeViewFileHandle>(StringComparer.Ordinal);
        private readonly BlobBuilder _fileTable = new BlobBuilder();

        private readonly BlobBuilder _symbolAndLineNumbersBlob = new BlobBuilder();
        private readonly BlobBuilder _relocationsBlob = new BlobBuilder();

        public CodeViewSymbolBuilder(CoffHeaderBuilder headerBuilder)
        {
            _coffHeaderBuilder = headerBuilder;
            // Convention: string table starts with an empty string at offset 0
            _stringTable.WriteByte(0);
            _stringTableIndex.Add("", 0);
        }

        private int GetOrAddString(string s)
        {
            if (_stringTableIndex.TryGetValue(s, out int result))
                return result;

            result = _stringTable.Count;
            _stringTable.WriteUTF8(s);
            _stringTable.WriteByte(0);

            _stringTableIndex.Add(s, result);

            return result;
        }

        public CodeViewFileHandle GetOrAddFile(string name)
        {
            return GetOrAddFile(name, CodeViewChecksumType.None, Array.Empty<byte>());
        }

        public CodeViewFileHandle GetOrAddFile(string name, CodeViewChecksumType checksumType, byte[] checksumBytes)
        {
            if (_fileIndex.TryGetValue(name, out CodeViewFileHandle result))
                return result;

            result = new CodeViewFileHandle(_fileTable.Count);

            _fileTable.WriteInt32(GetOrAddString(name));
            _fileTable.WriteByte((byte)checksumBytes.Length);
            _fileTable.WriteByte((byte)checksumType);
            if (checksumBytes.Length > 0)
                _fileTable.WriteBytes(checksumBytes);
            _fileTable.Align(4);

            _fileIndex.Add(name, result);

            return result;
        }

        /// <summary>
        /// Adds S_OBJNAME and S_COMPILE3 records in their own 0xF1 subsection.
        /// Call this before adding method symbols.
        /// </summary>
        public void AddObjNameAndCompile3(string objName, CodeViewLanguage language, CodeViewMachine machine,
            ushort feMajor, ushort feMinor, ushort feBuild,
            ushort beMajor, ushort beMinor, ushort beBuild,
            string compilerVersion, CodeViewCompileFlags compileFlags = 0)
        {
            _symbolAndLineNumbersBlob.WriteUInt32(0xF1);
            var sizeFixup = _symbolAndLineNumbersBlob.ReserveBytes(4);
            int startOffset = _symbolAndLineNumbersBlob.Count;

            // S_OBJNAME (0x1101): signature(4) + name(null-terminated)
            int objNameRecLen = 2 + 4 + objName.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)objNameRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16(0x1101);
            _symbolAndLineNumbersBlob.WriteUInt32(0); // signature
            _symbolAndLineNumbersBlob.WriteUTF8(objName);
            _symbolAndLineNumbersBlob.WriteByte(0);

            // S_COMPILE3 (0x113C): flags(4) + machine(2) + versions(8*2=16) + verString(null-terminated)
            int compile3RecLen = 2 + 4 + 2 + 16 + compilerVersion.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)compile3RecLen);
            _symbolAndLineNumbersBlob.WriteUInt16(0x113C);

            // flags: language in low byte, other flags in higher bytes
            uint flags = (uint)language | (uint)compileFlags;
            _symbolAndLineNumbersBlob.WriteUInt32(flags);

            _symbolAndLineNumbersBlob.WriteUInt16((ushort)machine);// target machine
            _symbolAndLineNumbersBlob.WriteUInt16(feMajor);
            _symbolAndLineNumbersBlob.WriteUInt16(feMinor);
            _symbolAndLineNumbersBlob.WriteUInt16(feBuild);
            _symbolAndLineNumbersBlob.WriteUInt16(0); // FE QFE
            _symbolAndLineNumbersBlob.WriteUInt16(beMajor);
            _symbolAndLineNumbersBlob.WriteUInt16(beMinor);
            _symbolAndLineNumbersBlob.WriteUInt16(beBuild);
            _symbolAndLineNumbersBlob.WriteUInt16(0); // BE QFE
            _symbolAndLineNumbersBlob.WriteUTF8(compilerVersion);
            _symbolAndLineNumbersBlob.WriteByte(0);

            new BlobWriter(sizeFixup).WriteInt32(_symbolAndLineNumbersBlob.Count - startOffset);
            _symbolAndLineNumbersBlob.Align(4);
        }

        private void EmitSymbolsAndLineNumbersSectionReloc(CoffSymbolHandle coffSymbol)
        {
            new CoffRelocationEncoder(_coffHeaderBuilder, _relocationsBlob)
                .AddSectionRelocation(_symbolAndLineNumbersBlob.Count + 4 /* Header */, coffSymbol);
        }

        private void EmitSymbolsAndLineNumbersSectionRelativeReloc(CoffSymbolHandle coffSymbol)
        {
            new CoffRelocationEncoder(_coffHeaderBuilder, _relocationsBlob)
                .AddSectionRelativeRelocation(_symbolAndLineNumbersBlob.Count + 4 /* Header */, coffSymbol);
        }

        private void EmitSymbolsAndLineNumbersTokenReloc(CoffSymbolHandle coffSymbol)
        {
            new CoffRelocationEncoder(_coffHeaderBuilder, _relocationsBlob)
                .AddTokenRelocation(_symbolAndLineNumbersBlob.Count + 4 /* Header */, coffSymbol);
        }

        public void AddMethodSymbol(string methodName, CoffSymbolHandle methodCoffSymbol, int methodCoffSymbolDelta, CoffSymbolHandle methodTokenSymbol, int codeSize,
            IReadOnlyList<CodeViewManSlot> localSlots = null,
            IReadOnlyList<CodeViewLocalScope> localScopes = null)
        {
            _symbolAndLineNumbersBlob.WriteUInt32(0xF1);
            var sizeFixup = _symbolAndLineNumbersBlob.ReserveBytes(4);
            var startOffset = _symbolAndLineNumbersBlob.Count;

            _symbolAndLineNumbersBlob.WriteUInt16((ushort)(2 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 2 + 2 + 1 + methodName.Length + 1));

            _symbolAndLineNumbersBlob.WriteUInt16(0x112a);

            _symbolAndLineNumbersBlob.WriteUInt32(0);
            _symbolAndLineNumbersBlob.WriteUInt32(0);
            _symbolAndLineNumbersBlob.WriteUInt32(0);

            _symbolAndLineNumbersBlob.WriteInt32(codeSize);

            _symbolAndLineNumbersBlob.WriteUInt32(0);
            _symbolAndLineNumbersBlob.WriteUInt32(0);

            EmitSymbolsAndLineNumbersTokenReloc(methodTokenSymbol);
            _symbolAndLineNumbersBlob.WriteUInt32(0);

            EmitSymbolsAndLineNumbersSectionRelativeReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt32(methodCoffSymbolDelta);

            EmitSymbolsAndLineNumbersSectionReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt16(0);

            _symbolAndLineNumbersBlob.WriteByte(1);

            _symbolAndLineNumbersBlob.WriteUInt16(0);

            _symbolAndLineNumbersBlob.WriteUTF8(methodName);
            _symbolAndLineNumbersBlob.WriteByte(0);

            // frameproc
            _symbolAndLineNumbersBlob.WriteUInt16(2 + 4 + 4 + 4 + 4 + 4 + 2 + 1 + 1 + 1 + 1);
            _symbolAndLineNumbersBlob.WriteUInt16(0x1012);

            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt16(0);

            // MAGIC
            _symbolAndLineNumbersBlob.WriteInt32(0x00100200);

            // S_MANSLOT records for local variables (function-level)
            if (localSlots != null)
            {
                foreach (var slot in localSlots)
                    EmitManSlot(slot);
            }

            // S_BLOCK32 + S_MANSLOT + S_END for nested lexical scopes
            if (localScopes != null)
            {
                foreach (var scope in localScopes)
                    EmitLocalScope(scope, methodCoffSymbol, methodCoffSymbolDelta);
            }

            // end method
            _symbolAndLineNumbersBlob.WriteUInt16(2);
            _symbolAndLineNumbersBlob.WriteUInt16(0x114F);

            new BlobWriter(sizeFixup).WriteInt32(_symbolAndLineNumbersBlob.Count - startOffset);

            _symbolAndLineNumbersBlob.Align(4);
        }

        private void EmitManSlot(CodeViewManSlot slot)
        {
            int manSlotRecLen = 2 + 4 + 4 + 4 + 2 + 2 + slot.Name.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)manSlotRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16(0x1120); // S_MANSLOT
            _symbolAndLineNumbersBlob.WriteInt32(slot.Slot);
            _symbolAndLineNumbersBlob.WriteInt32(slot.TypeToken);
            _symbolAndLineNumbersBlob.WriteInt32(0); // attr.off
            _symbolAndLineNumbersBlob.WriteInt16(0); // attr.seg
            _symbolAndLineNumbersBlob.WriteUInt16(0); // attr.flags
            _symbolAndLineNumbersBlob.WriteUTF8(slot.Name);
            _symbolAndLineNumbersBlob.WriteByte(0);
        }

        private void EmitLocalScope(CodeViewLocalScope scope, CoffSymbolHandle methodCoffSymbol, int methodCoffSymbolDelta)
        {
            // S_BLOCK32 (0x1103): pParent(4) + pEnd(4) + len(4) + off(4) + seg(2) + name(null-term)
            int blockRecLen = 2 + 4 + 4 + 4 + 4 + 2 + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)blockRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16(0x1103); // S_BLOCK32
            _symbolAndLineNumbersBlob.WriteInt32(0); // pParent (fixup by linker)
            _symbolAndLineNumbersBlob.WriteInt32(0); // pEnd (fixup by linker)
            _symbolAndLineNumbersBlob.WriteInt32(scope.CodeLength); // len

            EmitSymbolsAndLineNumbersSectionRelativeReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt32(methodCoffSymbolDelta + scope.CodeOffset); // off

            EmitSymbolsAndLineNumbersSectionReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt16(0); // seg

            _symbolAndLineNumbersBlob.WriteByte(0); // name (empty)

            // Emit slots within this scope
            foreach (var slot in scope.Slots)
                EmitManSlot(slot);

            // Emit nested child scopes
            foreach (var child in scope.Children)
                EmitLocalScope(child, methodCoffSymbol, methodCoffSymbolDelta);

            // S_END
            _symbolAndLineNumbersBlob.WriteUInt16(2);
            _symbolAndLineNumbersBlob.WriteUInt16(0x0006); // S_END
        }

        public void AddLineNumbers(CoffSymbolHandle methodCoffSymbol, int methodCoffSymbolDelta, int codeSize, CodeViewLineNumberBuilder lineNumbersBlob)
        {
            _symbolAndLineNumbersBlob.WriteUInt32(0xF2);
            var sizeFixup = _symbolAndLineNumbersBlob.ReserveBytes(4);
            int startOffset = _symbolAndLineNumbersBlob.Count;

            EmitSymbolsAndLineNumbersSectionRelativeReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt32(methodCoffSymbolDelta);

            EmitSymbolsAndLineNumbersSectionReloc(methodCoffSymbol);
            _symbolAndLineNumbersBlob.WriteInt16(0);

            _symbolAndLineNumbersBlob.WriteInt16(0);

            _symbolAndLineNumbersBlob.WriteInt32(codeSize);

            lineNumbersBlob.Serialize(_symbolAndLineNumbersBlob);

            new BlobWriter(sizeFixup).WriteInt32(_symbolAndLineNumbersBlob.Count - startOffset);

            _symbolAndLineNumbersBlob.Align(4);
        }

        internal void Serialize(BlobBuilder builder)
        {
            builder.WriteUInt32(4); // version

            if (_symbolAndLineNumbersBlob.Count > 0)
            {
                builder.LinkSuffix(_symbolAndLineNumbersBlob);
            }

            if (_stringTable.Count > 0)
            {
                builder.WriteUInt32(0xF3); // String table
                builder.WriteInt32(_stringTable.Count);
                builder.LinkSuffix(_stringTable);
                builder.Align(4);
            }

            if (_fileTable.Count > 0)
            {
                builder.WriteUInt32(0xF4); // File checksums
                builder.WriteInt32(_fileTable.Count);
                builder.LinkSuffix(_fileTable);
                builder.Align(4);
            }
        }

        internal void SerializeRelocations(BlobBuilder builder)
        {
            // Workaround for dotnet/runtime#127246: BlobBuilder.LinkSuffix drops
            // frozen chunks when the destination builder is empty.  The bug is in
            // the `if (!isEmpty)` guard inside LinkSuffix – when the destination
            // has Count == 0 it skips chain relinking, so the destination's
            // _nextOrPrevious stays as a self-pointer and any frozen chunks the
            // suffix accumulated via Expand are silently lost.
            //
            // This method is always called with a fresh (empty) BlobBuilder
            // created in ManagedCoffBuilder.SerializeRelocations.  When the cast
            // scenario has ≥6 methods, _relocationsBlob exceeds the default 256-
            // byte chunk size and Expand splits it into two chunks.  The empty-
            // destination LinkSuffix then keeps only the head chunk, losing the
            // first ~256 bytes of relocation data.  Because BlobBuilder.Count is
            // still correct (PreviousLength is updated before the guard), the
            // section-layout arithmetic and COFF header remain internally
            // consistent, but ToArray / GetBlobs yields fewer bytes than Count.
            // The shortfall shifts every subsequent byte in the file leftward,
            // so the symbol-table offset recorded in the COFF header now points
            // past the real data into a block of trailing zeroes – which the
            // ObjDumper reports as "body not found in symbol table".
            //
            // Flattening via ToArray avoids the bug entirely: ToArray iterates
            // the chunk chain (which is still intact on the source builder) and
            // produces a correct contiguous copy; WriteBytes then appends it to
            // the destination in a single, chunk-boundary-safe write.
            builder.WriteBytes(_relocationsBlob.ToArray());
        }
    }

    public readonly struct RelocatableExceptionRegionEncoder
    {
        private const int TableHeaderSize = 4;

        private const int SmallRegionSize =
            sizeof(short) +  // Flags
            sizeof(short) +  // TryOffset
            sizeof(byte) +   // TryLength
            sizeof(short) +  // HandlerOffset
            sizeof(byte) +   // HandleLength
            sizeof(int);     // ClassToken | FilterOffset

        private const int FatRegionSize =
            sizeof(int) +    // Flags
            sizeof(int) +    // TryOffset
            sizeof(int) +    // TryLength
            sizeof(int) +    // HandlerOffset
            sizeof(int) +    // HandleLength
            sizeof(int);     // ClassToken | FilterOffset

        private const int ThreeBytesMaxValue = 0xffffff;
        internal const int MaxSmallExceptionRegions = (byte.MaxValue - TableHeaderSize) / SmallRegionSize;
        internal const int MaxExceptionRegions = (ThreeBytesMaxValue - TableHeaderSize) / FatRegionSize;

        public BlobBuilder Builder { get; }
        public BlobBuilder RelocationBuilder { get; }
        public CoffHeaderBuilder HeaderBuilder { get; }
        public ManagedCoffSymbolTableBuilder SymbolTableBuilder { get; }
        public bool HasSmallFormat { get; }

        internal RelocatableExceptionRegionEncoder(BlobBuilder builder, BlobBuilder relocationBuilder, CoffHeaderBuilder headerBuilder, ManagedCoffSymbolTableBuilder symTableBuilder, bool hasSmallFormat)
        {
            Builder = builder;
            RelocationBuilder = relocationBuilder;
            HeaderBuilder = headerBuilder;
            SymbolTableBuilder = symTableBuilder;
            HasSmallFormat = hasSmallFormat;
        }

        public static bool IsSmallRegionCount(int exceptionRegionCount) =>
            unchecked((uint)exceptionRegionCount) <= MaxSmallExceptionRegions;

        public static bool IsSmallExceptionRegion(int startOffset, int length) =>
            unchecked((uint)startOffset) <= ushort.MaxValue && unchecked((uint)length) <= byte.MaxValue;

        internal static bool IsSmallExceptionRegionFromBounds(int startOffset, int endOffset) =>
            IsSmallExceptionRegion(startOffset, endOffset - startOffset);

        internal static int GetExceptionTableSize(int exceptionRegionCount, bool isSmallFormat) =>
            TableHeaderSize + exceptionRegionCount * (isSmallFormat ? SmallRegionSize : FatRegionSize);

        internal static bool IsExceptionRegionCountInBounds(int exceptionRegionCount) =>
            unchecked((uint)exceptionRegionCount) <= MaxExceptionRegions;

        internal static bool IsValidCatchTypeHandle(EntityHandle catchType)
        {
            return !catchType.IsNil &&
                   (catchType.Kind == HandleKind.TypeDefinition ||
                    catchType.Kind == HandleKind.TypeSpecification ||
                    catchType.Kind == HandleKind.TypeReference);
        }

        internal static RelocatableExceptionRegionEncoder SerializeTableHeader(BlobBuilder builder, BlobBuilder relocationBuilder, CoffHeaderBuilder headerBuilder, ManagedCoffSymbolTableBuilder symTableBuilder, int exceptionRegionCount, bool hasSmallRegions)
        {
            Debug.Assert(exceptionRegionCount > 0);

            const byte EHTableFlag = 0x01;
            const byte FatFormatFlag = 0x40;

            bool hasSmallFormat = hasSmallRegions && IsSmallRegionCount(exceptionRegionCount);
            int dataSize = GetExceptionTableSize(exceptionRegionCount, hasSmallFormat);

            builder.Align(4);
            if (hasSmallFormat)
            {
                builder.WriteByte(EHTableFlag);
                builder.WriteByte(unchecked((byte)dataSize));
                builder.WriteInt16(0);
            }
            else
            {
                Debug.Assert(dataSize <= 0x00ffffff);
                builder.WriteByte(EHTableFlag | FatFormatFlag);
                builder.WriteByte(unchecked((byte)dataSize));
                builder.WriteUInt16(unchecked((ushort)(dataSize >> 8)));
            }

            return new RelocatableExceptionRegionEncoder(builder, relocationBuilder, headerBuilder, symTableBuilder, hasSmallFormat);
        }

        public RelocatableExceptionRegionEncoder AddFinally(int tryOffset, int tryLength, int handlerOffset, int handlerLength)
        {
            return Add(ExceptionRegionKind.Finally, tryOffset, tryLength, handlerOffset, handlerLength, default(EntityHandle), 0);
        }

        public RelocatableExceptionRegionEncoder AddFault(int tryOffset, int tryLength, int handlerOffset, int handlerLength)
        {
            return Add(ExceptionRegionKind.Fault, tryOffset, tryLength, handlerOffset, handlerLength, default(EntityHandle), 0);
        }

        public RelocatableExceptionRegionEncoder AddCatch(int tryOffset, int tryLength, int handlerOffset, int handlerLength, EntityHandle catchType)
        {
            return Add(ExceptionRegionKind.Catch, tryOffset, tryLength, handlerOffset, handlerLength, catchType, 0);
        }

        public RelocatableExceptionRegionEncoder AddFilter(int tryOffset, int tryLength, int handlerOffset, int handlerLength, int filterOffset)
        {
            return Add(ExceptionRegionKind.Filter, tryOffset, tryLength, handlerOffset, handlerLength, default(EntityHandle), filterOffset);
        }

        public RelocatableExceptionRegionEncoder Add(
            ExceptionRegionKind kind,
            int tryOffset,
            int tryLength,
            int handlerOffset,
            int handlerLength,
            EntityHandle catchType = default(EntityHandle),
            int filterOffset = 0)
        {
            if (Builder == null)
            {
                throw new InvalidOperationException();
            }

            if (HasSmallFormat)
            {
                if (unchecked((ushort)tryOffset) != tryOffset) throw new ArgumentOutOfRangeException(nameof(tryOffset));
                if (unchecked((byte)tryLength) != tryLength) throw new ArgumentOutOfRangeException(nameof(tryLength));
                if (unchecked((ushort)handlerOffset) != handlerOffset) throw new ArgumentOutOfRangeException(nameof(handlerOffset));
                if (unchecked((byte)handlerLength) != handlerLength) throw new ArgumentOutOfRangeException(nameof(handlerLength));
            }
            else
            {
                if (tryOffset < 0) throw new ArgumentOutOfRangeException(nameof(tryOffset));
                if (tryLength < 0) throw new ArgumentOutOfRangeException(nameof(tryLength));
                if (handlerOffset < 0) throw new ArgumentOutOfRangeException(nameof(handlerOffset));
                if (handlerLength < 0) throw new ArgumentOutOfRangeException(nameof(handlerLength));
            }

            int catchTokenOrOffset;
            bool isToken;
            switch (kind)
            {
                case ExceptionRegionKind.Catch:
                    if (!IsValidCatchTypeHandle(catchType))
                    {
                        throw new ArgumentException(nameof(catchType));
                    }

                    catchTokenOrOffset = MetadataTokens.GetToken(catchType);
                    isToken = true;
                    break;

                case ExceptionRegionKind.Filter:
                    if (filterOffset < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(filterOffset));
                    }

                    catchTokenOrOffset = filterOffset;
                    isToken = false;
                    break;

                case ExceptionRegionKind.Finally:
                case ExceptionRegionKind.Fault:
                    catchTokenOrOffset = 0;
                    isToken = false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            AddUnchecked(kind, tryOffset, tryLength, handlerOffset, handlerLength, catchTokenOrOffset, isToken);
            return this;
        }

        internal void AddUnchecked(
            ExceptionRegionKind kind,
            int tryOffset,
            int tryLength,
            int handlerOffset,
            int handlerLength,
            int catchTokenOrOffset,
            bool isToken)
        {
            if (HasSmallFormat)
            {
                Builder.WriteUInt16((ushort)kind);
                Builder.WriteUInt16((ushort)tryOffset);
                Builder.WriteByte((byte)tryLength);
                Builder.WriteUInt16((ushort)handlerOffset);
                Builder.WriteByte((byte)handlerLength);
            }
            else
            {
                Builder.WriteInt32((int)kind);
                Builder.WriteInt32(tryOffset);
                Builder.WriteInt32(tryLength);
                Builder.WriteInt32(handlerOffset);
                Builder.WriteInt32(handlerLength);
            }

            if (isToken)
            {
                new ManagedCoffRelocationEncoder(HeaderBuilder, Builder, SymbolTableBuilder)
                    .AddClrRelocation(Builder.Count, catchTokenOrOffset);
                Builder.WriteInt32(0);
            }
            else
            {
                Builder.WriteInt32(catchTokenOrOffset);
            }
        }
    }

    public sealed class RelocatableControlFlowBuilder
    {
        private readonly struct BranchInfo
        {
            internal readonly int ILOffset;
            internal readonly LabelHandle Label;
            private readonly byte _opCode;

            internal ILOpCode OpCode => (ILOpCode)_opCode;

            internal BranchInfo(int ilOffset, LabelHandle label, ILOpCode opCode)
            {
                ILOffset = ilOffset;
                Label = label;
                _opCode = (byte)opCode;
            }

            internal int GetBranchDistance(ImmutableArray<int>.Builder labels, ILOpCode branchOpCode, int branchILOffset, bool isShortBranch)
            {
                int labelTargetOffset = labels[Label.Id - 1];
                if (labelTargetOffset < 0)
                {
                    throw new InvalidOperationException(Label.Id.ToString());
                }

                int branchInstructionSize = 1 + (isShortBranch ? sizeof(sbyte) : sizeof(int));
                int distance = labelTargetOffset - (ILOffset + branchInstructionSize);

                if (isShortBranch && unchecked((sbyte)distance) != distance)
                {
                    // We could potentially implement algorithm that automatically fixes up branch instructions to accomodate for bigger distances (short vs long),
                    // however an optimal algorithm would be rather complex (something like: calculate topological ordering of crossing branch instructions
                    // and then use fixed point to eliminate cycles). If the caller doesn't care about optimal IL size they can use long branches whenever the
                    // distance is unknown upfront. If they do they probably implement more sophisticated algorithm for IL layout optimization already.
                    throw new InvalidOperationException();
                }

                return distance;
            }
        }

        internal readonly struct ExceptionHandlerInfo
        {
            public readonly ExceptionRegionKind Kind;
            public readonly LabelHandle TryStart, TryEnd, HandlerStart, HandlerEnd, FilterStart;
            public readonly EntityHandle CatchType;

            public ExceptionHandlerInfo(
                ExceptionRegionKind kind,
                LabelHandle tryStart,
                LabelHandle tryEnd,
                LabelHandle handlerStart,
                LabelHandle handlerEnd,
                LabelHandle filterStart,
                EntityHandle catchType)
            {
                Kind = kind;
                TryStart = tryStart;
                TryEnd = tryEnd;
                HandlerStart = handlerStart;
                HandlerEnd = handlerEnd;
                FilterStart = filterStart;
                CatchType = catchType;
            }
        }

        private readonly struct SwitchInfo
        {
            internal readonly int ILOffset;
            internal readonly ImmutableArray<LabelHandle> Labels;

            internal SwitchInfo(int ilOffset, ImmutableArray<LabelHandle> labels)
            {
                ILOffset = ilOffset;
                Labels = labels;
            }
        }

        private readonly ImmutableArray<BranchInfo>.Builder _branches;
        private readonly ImmutableArray<SwitchInfo>.Builder _switches;
        private readonly ImmutableArray<int>.Builder _labels;
        private ImmutableArray<ExceptionHandlerInfo>.Builder _lazyExceptionHandlers;

        public RelocatableControlFlowBuilder()
        {
            _branches = ImmutableArray.CreateBuilder<BranchInfo>();
            _switches = ImmutableArray.CreateBuilder<SwitchInfo>();
            _labels = ImmutableArray.CreateBuilder<int>();
        }

        internal void Clear()
        {
            _branches.Clear();
            _switches.Clear();
            _labels.Clear();
            _lazyExceptionHandlers?.Clear();
        }

        internal LabelHandle AddLabel()
        {
            _labels.Add(-1);
            unsafe
            {
                int labelHandle = _labels.Count;
                return *(LabelHandle*)&labelHandle;
            }
        }

        internal void AddBranch(int ilOffset, LabelHandle label, ILOpCode opCode)
        {
            Debug.Assert(ilOffset >= 0);
            ValidateLabel(label, nameof(label));
            _branches.Add(new BranchInfo(ilOffset, label, opCode));
        }

        internal void AddSwitch(int ilOffset, ImmutableArray<LabelHandle> labels)
        {
            Debug.Assert(ilOffset >= 0);
            Debug.Assert(labels.Length > 0);
            foreach (var label in labels)
                ValidateLabel(label, nameof(labels));
            _switches.Add(new SwitchInfo(ilOffset, labels));
        }

        internal bool HasFixups => _branches.Count > 0 || _switches.Count > 0;

        internal void MarkLabel(int ilOffset, LabelHandle label)
        {
            Debug.Assert(ilOffset >= 0);
            ValidateLabel(label, nameof(label));
            _labels[label.Id - 1] = ilOffset;
        }

        private int GetLabelOffsetChecked(LabelHandle label)
        {
            int offset = _labels[label.Id - 1];
            if (offset < 0)
            {
                throw new InvalidOperationException(label.Id.ToString());
            }

            return offset;
        }

        private void ValidateLabel(LabelHandle label, string parameterName)
        {
            if (label.IsNil)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (label.Id > _labels.Count)
            {
                throw new InvalidOperationException(parameterName);
            }
        }

        public void AddFinallyRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd) =>
            AddExceptionRegion(ExceptionRegionKind.Finally, tryStart, tryEnd, handlerStart, handlerEnd);

        public void AddFaultRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd) =>
            AddExceptionRegion(ExceptionRegionKind.Fault, tryStart, tryEnd, handlerStart, handlerEnd);

        public void AddCatchRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd, EntityHandle catchType)
        {
            if (!RelocatableExceptionRegionEncoder.IsValidCatchTypeHandle(catchType))
            {
                throw new ArgumentException(nameof(catchType));
            }

            AddExceptionRegion(ExceptionRegionKind.Catch, tryStart, tryEnd, handlerStart, handlerEnd, catchType: catchType);
        }

        public void AddFilterRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd, LabelHandle filterStart)
        {
            ValidateLabel(filterStart, nameof(filterStart));
            AddExceptionRegion(ExceptionRegionKind.Filter, tryStart, tryEnd, handlerStart, handlerEnd, filterStart: filterStart);
        }

        private void AddExceptionRegion(
            ExceptionRegionKind kind,
            LabelHandle tryStart,
            LabelHandle tryEnd,
            LabelHandle handlerStart,
            LabelHandle handlerEnd,
            LabelHandle filterStart = default(LabelHandle),
            EntityHandle catchType = default(EntityHandle))
        {
            ValidateLabel(tryStart, nameof(tryStart));
            ValidateLabel(tryEnd, nameof(tryEnd));
            ValidateLabel(handlerStart, nameof(handlerStart));
            ValidateLabel(handlerEnd, nameof(handlerEnd));

            if (_lazyExceptionHandlers == null)
            {
                _lazyExceptionHandlers = ImmutableArray.CreateBuilder<ExceptionHandlerInfo>();
            }

            _lazyExceptionHandlers.Add(new ExceptionHandlerInfo(kind, tryStart, tryEnd, handlerStart, handlerEnd, filterStart, catchType));
        }

        private IEnumerable<BranchInfo> Branches => _branches;

        private IEnumerable<int> Labels => _labels;

        internal int BranchCount => _branches.Count;

        internal int ExceptionHandlerCount => _lazyExceptionHandlers?.Count ?? 0;

        internal void CopyCodeAndFixupBranches(BlobBuilder srcBuilder, BlobBuilder dstBuilder)
        {
            var branch = _branches[0];
            int branchIndex = 0;

            // offset within the source builder
            int srcOffset = 0;

            // current offset within the current source blob
            int srcBlobOffset = 0;

            foreach (Blob srcBlob in srcBuilder.GetBlobs())
            {
                byte[] srcBlobBuffer = srcBlob.GetBytes().Array;

                Debug.Assert(
                    srcBlobOffset == 0 ||
                    srcBlobOffset == 1 && srcBlobBuffer[0] == 0xff ||
                    srcBlobOffset == 4 && srcBlobBuffer[0] == 0xff && srcBlobBuffer[1] == 0xff && srcBlobBuffer[2] == 0xff && srcBlobBuffer[3] == 0xff);

                while (true)
                {
                    // copy bytes preceding the next branch, or till the end of the blob:
                    int chunkSize = Math.Min(branch.ILOffset - srcOffset, srcBlob.Length - srcBlobOffset);
                    dstBuilder.WriteBytes(srcBlobBuffer, srcBlobOffset, chunkSize);
                    srcOffset += chunkSize;
                    srcBlobOffset += chunkSize;

                    // there is no branch left in the blob:
                    if (srcBlobOffset == srcBlob.Length)
                    {
                        srcBlobOffset = 0;
                        break;
                    }

                    Debug.Assert(srcBlobBuffer[srcBlobOffset] == (byte)branch.OpCode);

                    int operandSize = branch.OpCode.GetBranchOperandSize();
                    bool isShortInstruction = operandSize == 1;

                    // Note: the 4B operand is contiguous since we wrote it via BlobBuilder.WriteInt32()
                    Debug.Assert(
                        srcBlobOffset + 1 == srcBlob.Length ||
                        (isShortInstruction ?
                           srcBlobBuffer[srcBlobOffset + 1] == 0xff :
                           BitConverter.ToUInt32(srcBlobBuffer, srcBlobOffset + 1) == 0xffffffff));

                    // write branch opcode:
                    dstBuilder.WriteByte(srcBlobBuffer[srcBlobOffset]);

                    int branchDistance = branch.GetBranchDistance(_labels, branch.OpCode, srcOffset, isShortInstruction);

                    // write branch operand:
                    if (isShortInstruction)
                    {
                        dstBuilder.WriteSByte((sbyte)branchDistance);
                    }
                    else
                    {
                        dstBuilder.WriteInt32(branchDistance);
                    }

                    srcOffset += sizeof(byte) + operandSize;

                    // next branch:
                    branchIndex++;
                    if (branchIndex == _branches.Count)
                    {
                        branch = new BranchInfo(int.MaxValue, label: default, opCode: default);
                    }
                    else
                    {
                        branch = _branches[branchIndex];
                    }

                    // the branch starts at the very end and its operand is in the next blob:
                    if (srcBlobOffset == srcBlob.Length - 1)
                    {
                        srcBlobOffset = operandSize;
                        break;
                    }

                    // skip fake branch instruction:
                    srcBlobOffset += sizeof(byte) + operandSize;
                }
            }
        }

        /// <summary>
        /// Copies code and fixes up both branch and switch instruction operands.
        /// Flattens the source to a byte array to avoid blob-boundary complexity
        /// when switch instructions span chunks.
        /// </summary>
        internal void CopyCodeAndFixupBranchesAndSwitches(BlobBuilder srcBuilder, BlobBuilder dstBuilder)
        {
            if (_switches.Count == 0)
            {
                CopyCodeAndFixupBranches(srcBuilder, dstBuilder);
                return;
            }

            byte[] code = srcBuilder.ToArray();

            // Fix up branches
            foreach (var branch in _branches)
            {
                int operandSize = branch.OpCode.GetBranchOperandSize();
                bool isShort = operandSize == 1;
                int distance = branch.GetBranchDistance(_labels, branch.OpCode, branch.ILOffset, isShort);
                int operandOffset = branch.ILOffset + 1; // skip opcode byte
                if (isShort)
                    code[operandOffset] = (byte)(sbyte)distance;
                else
                    BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(operandOffset), distance);
            }

            // Fix up switches
            foreach (var sw in _switches)
            {
                int countOffset = sw.ILOffset + 1; // skip switch opcode byte
                int n = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(countOffset));
                Debug.Assert(n == sw.Labels.Length);

                int switchEndOffset = sw.ILOffset + 1 + 4 + n * 4;
                for (int i = 0; i < n; i++)
                {
                    int targetLabel = _labels[sw.Labels[i].Id - 1];
                    if (targetLabel < 0)
                        throw new InvalidOperationException(sw.Labels[i].Id.ToString());

                    int delta = targetLabel - switchEndOffset;
                    int deltaOffset = countOffset + 4 + i * 4;
                    BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(deltaOffset), delta);
                }
            }

            dstBuilder.WriteBytes(code);
        }

        internal void SerializeExceptionTable(BlobBuilder builder, BlobBuilder relocationBuilder, CoffHeaderBuilder headerBuilder, ManagedCoffSymbolTableBuilder symTableBuilder)
        {
            if (_lazyExceptionHandlers == null || _lazyExceptionHandlers.Count == 0)
            {
                return;
            }

            var regionEncoder = RelocatableExceptionRegionEncoder.SerializeTableHeader(builder, relocationBuilder, headerBuilder, symTableBuilder, _lazyExceptionHandlers.Count, HasSmallExceptionRegions());

            foreach (var handler in _lazyExceptionHandlers)
            {
                // Note that labels have been validated when added to the handler list,
                // they might not have been marked though.

                int tryStart = GetLabelOffsetChecked(handler.TryStart);
                int tryEnd = GetLabelOffsetChecked(handler.TryEnd);
                int handlerStart = GetLabelOffsetChecked(handler.HandlerStart);
                int handlerEnd = GetLabelOffsetChecked(handler.HandlerEnd);

                if (tryStart > tryEnd)
                {
                    throw new InvalidOperationException();
                }

                if (handlerStart > handlerEnd)
                {
                    throw new InvalidOperationException();
                }

                int catchTokenOrOffset = handler.Kind switch
                {
                    ExceptionRegionKind.Catch => MetadataTokens.GetToken(handler.CatchType),
                    ExceptionRegionKind.Filter => GetLabelOffsetChecked(handler.FilterStart),
                    _ => 0,
                };

                regionEncoder.AddUnchecked(
                    handler.Kind,
                    tryStart,
                    tryEnd - tryStart,
                    handlerStart,
                    handlerEnd - handlerStart,
                    catchTokenOrOffset,
                    handler.Kind == ExceptionRegionKind.Catch);
            }
        }

        private bool HasSmallExceptionRegions()
        {
            Debug.Assert(_lazyExceptionHandlers != null);

            if (!RelocatableExceptionRegionEncoder.IsSmallRegionCount(_lazyExceptionHandlers.Count))
            {
                return false;
            }

            foreach (var handler in _lazyExceptionHandlers)
            {
                if (!RelocatableExceptionRegionEncoder.IsSmallExceptionRegionFromBounds(GetLabelOffsetChecked(handler.TryStart), GetLabelOffsetChecked(handler.TryEnd)) ||
                    !RelocatableExceptionRegionEncoder.IsSmallExceptionRegionFromBounds(GetLabelOffsetChecked(handler.HandlerStart), GetLabelOffsetChecked(handler.HandlerEnd)))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public class MethodRelocationBuilder
    {
        private struct Relocation
        {
            public readonly int Offset;
            public readonly int Token;
            public Relocation(int offset, int token) => (Offset, Token) = (offset, token);
        }

        private List<Relocation> _relocations = new List<Relocation>();

        public void Reset() => _relocations.Clear();

        public void AddTokenRelocation(int offset, int token) => _relocations.Add(new Relocation(offset, token));

        internal void Append(BlobBuilder relocationStream, int delta, CoffHeaderBuilder headerBuilder, ManagedCoffSymbolTableBuilder symbolTableBuilder)
        {
            var encoder = new ManagedCoffRelocationEncoder(headerBuilder, relocationStream, symbolTableBuilder);

            foreach (Relocation r in _relocations)
            {
                encoder.AddClrRelocation(r.Offset + delta, r.Token);
            }
        }
    }

    public readonly struct CoffRelocationEncoder
    {
        public CoffHeaderBuilder HeaderBuilder { get; }
        public BlobBuilder Builder { get; }

        public CoffRelocationEncoder(CoffHeaderBuilder headerBuilder, BlobBuilder builder)
            => (HeaderBuilder, Builder) = (headerBuilder, builder);

        public void AddSectionRelocation(int offset, CoffSymbolHandle coffSymbol)
        {
            Builder.WriteInt32(offset);
            Builder.WriteInt32(coffSymbol._value);
            Builder.WriteUInt16(HeaderBuilder.Machine switch
            {
                Machine.I386 => 0x000A,      // IMAGE_REL_I386_SECTION
                Machine.Amd64 => 0x000A,     // IMAGE_REL_AMD64_SECTION
                Machine.Arm64 => 0x000D,     // IMAGE_REL_ARM64_SECTION
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            });
        }

        public void AddSectionRelativeRelocation(int offset, CoffSymbolHandle coffSymbol)
        {
            Builder.WriteInt32(offset);
            Builder.WriteInt32(coffSymbol._value);
            Builder.WriteUInt16(HeaderBuilder.Machine switch
            {
                Machine.I386 => 0x000B,      // IMAGE_REL_I386_SECREL
                Machine.Amd64 => 0x000B,     // IMAGE_REL_AMD64_SECREL
                Machine.Arm64 => 0x0008,     // IMAGE_REL_ARM64_SECREL
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            });
        }

        public void AddTokenRelocation(int offset, CoffSymbolHandle coffSymbol)
        {
            Builder.WriteInt32(offset);
            Builder.WriteInt32(coffSymbol._value);
            Builder.WriteUInt16(HeaderBuilder.Machine switch
            {
                Machine.I386 => 0x000C,      // IMAGE_REL_I386_TOKEN
                Machine.Amd64 => 0x000D,     // IMAGE_REL_AMD64_TOKEN
                Machine.Arm64 => 0x000C,     // IMAGE_REL_ARM64_TOKEN
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            });
        }
    }

    public readonly struct ManagedCoffRelocationEncoder
    {
        public CoffHeaderBuilder HeaderBuilder { get; }
        public BlobBuilder Builder { get; }
        public ManagedCoffSymbolTableBuilder SymbolTableBuilder { get; }

        public ManagedCoffRelocationEncoder(CoffHeaderBuilder headerBuilder, BlobBuilder builder, ManagedCoffSymbolTableBuilder symbolTableBuilder)
            => (HeaderBuilder, Builder, SymbolTableBuilder) = (headerBuilder, builder, symbolTableBuilder);

        public void AddClrRelocation(int offset, int token)
        {
            string symbolName = token.ToString("X8");

            // CLR token symbols for inline IL references use section 0 (undefined).
            // The linker resolves them by the token value encoded in the symbol name.
            CoffSymbolHandle tokenSymbol = SymbolTableBuilder.GetOrAddUndefinedClrTokenSymbol(symbolName);

            new CoffRelocationEncoder(HeaderBuilder, Builder)
                .AddTokenRelocation(offset, tokenSymbol);
        }
    }

    public readonly struct RelocatableInstructionEncoder
    {
        public BlobBuilder CodeBuilder { get; }
        public MethodRelocationBuilder RelocationBuilder { get; }
        public RelocatableControlFlowBuilder ControlFlowBuilder { get; }
        public CodeViewLineNumberBuilder LineNumberBuilder { get; }

        public RelocatableInstructionEncoder(BlobBuilder codeBuilder, MethodRelocationBuilder relocationBuilder = null, RelocatableControlFlowBuilder controlFlowBuilder = null, CodeViewLineNumberBuilder lineNumberBuilder = null)
        {
            if (codeBuilder == null)
            {
                throw new ArgumentNullException();
            }

            CodeBuilder = codeBuilder;
            RelocationBuilder = relocationBuilder;
            ControlFlowBuilder = controlFlowBuilder;
            LineNumberBuilder = lineNumberBuilder;
        }

        public int Offset => CodeBuilder.Count;

        public void OpCode(ILOpCode code)
        {
            if (unchecked((byte)code) == (ushort)code)
            {
                CodeBuilder.WriteByte((byte)code);
            }
            else
            {
                CodeBuilder.WriteUInt16BE((ushort)code);
            }
        }

        public void Token(EntityHandle handle)
        {
            Token(MetadataTokens.GetToken(handle));
        }

        public void Token(int token)
        {
            GetRelocationBuilder().AddTokenRelocation(CodeBuilder.Count, token);
            CodeBuilder.WriteInt32(0);
        }

        public void LoadString(UserStringHandle handle)
        {
            OpCode(ILOpCode.Ldstr);
            Token(MetadataTokens.GetToken(handle));
        }

        public void Call(EntityHandle methodHandle)
        {
            if (methodHandle.Kind != HandleKind.MethodDefinition &&
                methodHandle.Kind != HandleKind.MethodSpecification &&
                methodHandle.Kind != HandleKind.MemberReference)
            {
                throw new ArgumentException(nameof(methodHandle));
            }

            OpCode(ILOpCode.Call);
            Token(methodHandle);
        }

        public void Call(MethodDefinitionHandle methodHandle)
        {
            OpCode(ILOpCode.Call);
            Token(methodHandle);
        }

        public void Call(MethodSpecificationHandle methodHandle)
        {
            OpCode(ILOpCode.Call);
            Token(methodHandle);
        }

        public void Call(MemberReferenceHandle methodHandle)
        {
            OpCode(ILOpCode.Call);
            Token(methodHandle);
        }

        public void CallIndirect(StandaloneSignatureHandle signature)
        {
            OpCode(ILOpCode.Calli);
            Token(signature);
        }

        public void LoadConstantI4(int value)
        {
            ILOpCode code;
            switch (value)
            {
                case -1: code = ILOpCode.Ldc_i4_m1; break;
                case 0: code = ILOpCode.Ldc_i4_0; break;
                case 1: code = ILOpCode.Ldc_i4_1; break;
                case 2: code = ILOpCode.Ldc_i4_2; break;
                case 3: code = ILOpCode.Ldc_i4_3; break;
                case 4: code = ILOpCode.Ldc_i4_4; break;
                case 5: code = ILOpCode.Ldc_i4_5; break;
                case 6: code = ILOpCode.Ldc_i4_6; break;
                case 7: code = ILOpCode.Ldc_i4_7; break;
                case 8: code = ILOpCode.Ldc_i4_8; break;

                default:
                    if (unchecked((sbyte)value == value))
                    {
                        OpCode(ILOpCode.Ldc_i4_s);
                        CodeBuilder.WriteSByte((sbyte)value);
                    }
                    else
                    {
                        OpCode(ILOpCode.Ldc_i4);
                        CodeBuilder.WriteInt32(value);
                    }

                    return;
            }

            OpCode(code);
        }

        public void LoadConstantI8(long value)
        {
            OpCode(ILOpCode.Ldc_i8);
            CodeBuilder.WriteInt64(value);
        }

        public void LoadConstantR4(float value)
        {
            OpCode(ILOpCode.Ldc_r4);
            CodeBuilder.WriteSingle(value);
        }

        public void LoadConstantR8(double value)
        {
            OpCode(ILOpCode.Ldc_r8);
            CodeBuilder.WriteDouble(value);
        }

        public void LoadLocal(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: OpCode(ILOpCode.Ldloc_0); break;
                case 1: OpCode(ILOpCode.Ldloc_1); break;
                case 2: OpCode(ILOpCode.Ldloc_2); break;
                case 3: OpCode(ILOpCode.Ldloc_3); break;

                default:
                    if (unchecked((uint)slotIndex) <= byte.MaxValue)
                    {
                        OpCode(ILOpCode.Ldloc_s);
                        CodeBuilder.WriteByte((byte)slotIndex);
                    }
                    else if (slotIndex > 0)
                    {
                        OpCode(ILOpCode.Ldloc);
                        CodeBuilder.WriteInt32(slotIndex);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(slotIndex));
                    }

                    break;
            }
        }

        public void StoreLocal(int slotIndex)
        {
            switch (slotIndex)
            {
                case 0: OpCode(ILOpCode.Stloc_0); break;
                case 1: OpCode(ILOpCode.Stloc_1); break;
                case 2: OpCode(ILOpCode.Stloc_2); break;
                case 3: OpCode(ILOpCode.Stloc_3); break;

                default:
                    if (unchecked((uint)slotIndex) <= byte.MaxValue)
                    {
                        OpCode(ILOpCode.Stloc_s);
                        CodeBuilder.WriteByte((byte)slotIndex);
                    }
                    else if (slotIndex > 0)
                    {
                        OpCode(ILOpCode.Stloc);
                        CodeBuilder.WriteInt32(slotIndex);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(slotIndex));
                    }

                    break;
            }
        }

        public void LoadLocalAddress(int slotIndex)
        {
            if (unchecked((uint)slotIndex) <= byte.MaxValue)
            {
                OpCode(ILOpCode.Ldloca_s);
                CodeBuilder.WriteByte((byte)slotIndex);
            }
            else if (slotIndex > 0)
            {
                OpCode(ILOpCode.Ldloca);
                CodeBuilder.WriteInt32(slotIndex);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            }
        }

        public void LoadArgument(int argumentIndex)
        {
            switch (argumentIndex)
            {
                case 0: OpCode(ILOpCode.Ldarg_0); break;
                case 1: OpCode(ILOpCode.Ldarg_1); break;
                case 2: OpCode(ILOpCode.Ldarg_2); break;
                case 3: OpCode(ILOpCode.Ldarg_3); break;

                default:
                    if (unchecked((uint)argumentIndex) <= byte.MaxValue)
                    {
                        OpCode(ILOpCode.Ldarg_s);
                        CodeBuilder.WriteByte((byte)argumentIndex);
                    }
                    else if (argumentIndex > 0)
                    {
                        OpCode(ILOpCode.Ldarg);
                        CodeBuilder.WriteInt32(argumentIndex);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(argumentIndex));
                    }

                    break;
            }
        }

        public void LoadArgumentAddress(int argumentIndex)
        {
            if (unchecked((uint)argumentIndex) <= byte.MaxValue)
            {
                OpCode(ILOpCode.Ldarga_s);
                CodeBuilder.WriteByte((byte)argumentIndex);
            }
            else if (argumentIndex > 0)
            {
                OpCode(ILOpCode.Ldarga);
                CodeBuilder.WriteInt32(argumentIndex);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(argumentIndex));
            }
        }

        public void StoreArgument(int argumentIndex)
        {
            if (unchecked((uint)argumentIndex) <= byte.MaxValue)
            {
                OpCode(ILOpCode.Starg_s);
                CodeBuilder.WriteByte((byte)argumentIndex);
            }
            else if (argumentIndex > 0)
            {
                OpCode(ILOpCode.Starg);
                CodeBuilder.WriteInt32(argumentIndex);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(argumentIndex));
            }
        }

        public LabelHandle DefineLabel()
        {
            return GetBranchBuilder().AddLabel();
        }

        public void Branch(ILOpCode code, LabelHandle label)
        {
            // throws if code is not a branch:
            int size = code.GetBranchOperandSize();

            GetBranchBuilder().AddBranch(Offset, label, code);
            OpCode(code);

            // -1 points in the middle of the branch instruction and is thus invalid.
            // We want to produce invalid IL so that if the caller doesn't patch the branches
            // the branch instructions will be invalid in an obvious way.
            if (size == 1)
            {
                CodeBuilder.WriteSByte(-1);
            }
            else
            {
                Debug.Assert(size == 4);
                CodeBuilder.WriteInt32(-1);
            }
        }

        public void Switch(params LabelHandle[] labels)
        {
            if (labels == null || labels.Length == 0)
                throw new ArgumentException("Switch requires at least one label.", nameof(labels));

            GetBranchBuilder().AddSwitch(Offset, labels.ToImmutableArray());
            OpCode(ILOpCode.Switch);
            CodeBuilder.WriteInt32(labels.Length);
            foreach (var _ in labels)
                CodeBuilder.WriteInt32(-1); // placeholder deltas, patched during fixup
        }

        public void MarkLabel(LabelHandle label)
        {
            GetBranchBuilder().MarkLabel(Offset, label);
        }

        public void MarkLineNumber(CodeViewFileHandle fileId, int lineNumber)
        {
            GetLineNumberBuilder().AddLineNumber(fileId, CodeBuilder.Count, lineNumber);
        }
        private RelocatableControlFlowBuilder GetBranchBuilder()
        {
            if (ControlFlowBuilder == null)
            {
                throw new InvalidOperationException();
            }

            return ControlFlowBuilder;
        }

        private MethodRelocationBuilder GetRelocationBuilder() => RelocationBuilder ?? throw new InvalidOperationException();
        private CodeViewLineNumberBuilder GetLineNumberBuilder() => LineNumberBuilder ?? throw new InvalidOperationException();
    }

    public readonly struct RelocatableMethodBodyStreamEncoder
    {
        public BlobBuilder Builder { get; }

        public BlobBuilder RelocationBuilder { get; }

        public ManagedCoffSymbolTableBuilder SymbolTableBuilder { get; }

        public CoffHeaderBuilder HeaderBuilder { get; }

        public CodeViewSymbolBuilder CodeViewSymbolBuilder { get; }

        public RelocatableMethodBodyStreamEncoder(BlobBuilder bodyStreamBuilder, BlobBuilder relocationStreamBuilder, ManagedCoffSymbolTableBuilder symTabBuilder, CoffHeaderBuilder headerBuilder, CodeViewSymbolBuilder codeViewSymbolBuilder)
        {
            Builder = bodyStreamBuilder;
            RelocationBuilder = relocationStreamBuilder;
            SymbolTableBuilder = symTabBuilder;
            HeaderBuilder = headerBuilder;
            CodeViewSymbolBuilder = codeViewSymbolBuilder;
        }

        public int AddMethodBody(
            MethodDefinitionHandle metadataHandle,
            string coffSymbolName,
            RelocatableInstructionEncoder instructionEncoder,
            int maxStack = 8,
            StandaloneSignatureHandle localVariablesSignature = default,
            MethodBodyAttributes attributes = MethodBodyAttributes.InitLocals,
            bool hasDynamicStackAllocation = false,
            IReadOnlyList<CodeViewManSlot> localSlots = null,
            IReadOnlyList<CodeViewLocalScope> localScopes = null,
            string debugName = null)
        {
            if (unchecked((uint)maxStack) > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxStack));
            }

            // The branch fixup code expects the operands of branch instructions in the code builder to be contiguous.
            // That's true when we emit thru InstructionEncoder. Taking it as a parameter instead of separate
            // code and flow builder parameters ensures they match each other.
            var codeBuilder = instructionEncoder.CodeBuilder;
            var flowBuilder = instructionEncoder.ControlFlowBuilder;
            var relocBuilder = instructionEncoder.RelocationBuilder;
            var lineNumberBuilder = instructionEncoder.LineNumberBuilder;

            if (codeBuilder == null)
            {
                throw new ArgumentNullException(nameof(instructionEncoder));
            }

            int exceptionRegionCount = flowBuilder?.ExceptionHandlerCount ?? 0;
            if (!RelocatableExceptionRegionEncoder.IsExceptionRegionCountInBounds(exceptionRegionCount))
            {
                throw new ArgumentOutOfRangeException(nameof(instructionEncoder));
            }

            int bodyOffset = SerializeHeader(codeBuilder.Count, (ushort)maxStack, exceptionRegionCount, attributes, localVariablesSignature, hasDynamicStackAllocation);

            CoffSymbolHandle methodSymbol = SymbolTableBuilder.AddFunctionClrToken(coffSymbolName, metadataHandle, bodyOffset, out CoffSymbolHandle tokenCoffSymbol);
            int methodCoffSymbolDelta = Builder.Count - bodyOffset;
            CodeViewSymbolBuilder?.AddMethodSymbol(debugName ?? coffSymbolName, methodSymbol, methodCoffSymbolDelta, tokenCoffSymbol, codeBuilder.Count, localSlots, localScopes);
            if (lineNumberBuilder != null)
                CodeViewSymbolBuilder?.AddLineNumbers(methodSymbol, methodCoffSymbolDelta, codeBuilder.Count, lineNumberBuilder);

            relocBuilder?.Append(RelocationBuilder, Builder.Count, HeaderBuilder, SymbolTableBuilder);

            if (flowBuilder?.HasFixups == true)
            {
                flowBuilder.CopyCodeAndFixupBranchesAndSwitches(codeBuilder, Builder);
            }
            else
            {
                codeBuilder.WriteContentTo(Builder);
            }

            flowBuilder?.SerializeExceptionTable(Builder, RelocationBuilder, HeaderBuilder, SymbolTableBuilder);

            return bodyOffset;
        }

        private int SerializeHeader(
            int codeSize,
            ushort maxStack,
            int exceptionRegionCount,
            MethodBodyAttributes attributes,
            StandaloneSignatureHandle localVariablesSignature,
            bool hasDynamicStackAllocation)
        {
            const int TinyFormat = 2;
            const int FatFormat = 3;
            const int MoreSections = 8;
            const byte InitLocals = 0x10;

            bool initLocals = (attributes & MethodBodyAttributes.InitLocals) != 0;

            bool isTiny = codeSize < 64 &&
                          maxStack <= 8 &&
                          localVariablesSignature.IsNil && (!hasDynamicStackAllocation || !initLocals) &&
                          exceptionRegionCount == 0;

            int offset;
            if (isTiny)
            {
                offset = Builder.Count;
                Builder.WriteByte((byte)((codeSize << 2) | TinyFormat));
            }
            else
            {
                Builder.Align(4);

                offset = Builder.Count;

                ushort flags = (3 << 12) | FatFormat;
                if (exceptionRegionCount > 0)
                {
                    flags |= MoreSections;
                }

                if (initLocals)
                {
                    flags |= InitLocals;
                }

                Builder.WriteUInt16((ushort)((int)attributes | flags));
                Builder.WriteUInt16(maxStack);
                Builder.WriteInt32(codeSize);

                if (!localVariablesSignature.IsNil)
                {
                    new ManagedCoffRelocationEncoder(HeaderBuilder, RelocationBuilder, SymbolTableBuilder)
                        .AddClrRelocation(Builder.Count, MetadataTokens.GetToken(localVariablesSignature));
                }
                Builder.WriteInt32(0);
                
            }

            return offset;
        }
    }

    [Flags]
    public enum ObjectFeatures : ushort
    {
        None,
        PureMsil = 2,
        SafeMsil = 4,
    }

    public class CoffSymbolTableBuilder
    {
        protected Dictionary<string, CoffSymbolHandle> _coffSymbols = new Dictionary<string, CoffSymbolHandle>(StringComparer.Ordinal);
        protected BlobBuilder _coffStringTableBuilder = new BlobBuilder();
        protected BlobBuilder _coffSymbolTableBuilder = new BlobBuilder();

        private const int SymbolSize = 18;

        public int Count => _coffSymbolTableBuilder.Count / SymbolSize;

        public CoffSymbolTableBuilder() { }

        public CoffSymbolTableBuilder(ObjectFeatures objectFeatures)
        {
            GetOrAddCoffSymbol("@feat.00", (ushort)objectFeatures, 0xFFFF, CoffSymbolType.Null, CoffSymbolStorageClass.Static, 0);
        }

        private Dictionary<string, int> _stringTableEntries = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Gets or adds a string to the COFF string table and returns its offset
        /// (including the 4-byte size prefix). Used for long section names.
        /// </summary>
        public int GetOrAddStringTableEntry(string name)
        {
            if (_stringTableEntries.TryGetValue(name, out int offset))
                return offset;

            offset = _coffStringTableBuilder.Count + 4; // +4 for the size prefix
            _coffStringTableBuilder.WriteUTF8(name);
            _coffStringTableBuilder.WriteByte(0);
            _stringTableEntries.Add(name, offset);
            return offset;
        }

        protected CoffSymbolHandle GetOrAddCoffSymbol(string name, uint value, ushort sectionNumber, CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols)
        {
            return GetOrAddCoffSymbolCore(name, value, b => b.WriteUInt16(sectionNumber), type, storageClass, numberOfAuxSymbols);
        }

        protected CoffSymbolHandle GetOrAddCoffSymbolCore(string name, uint value, Action<BlobBuilder> writeSectionNumber, CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols)
        {
            if (_coffSymbols.TryGetValue(name, out CoffSymbolHandle result))
            {
                return result;
            }

            result = new CoffSymbolHandle(Count);

            if (name.Length <= 8)
            {
                CoffBuilder.WritePaddedName(_coffSymbolTableBuilder, name);
            }
            else
            {
                _coffSymbolTableBuilder.WriteUInt32(0);
                _coffSymbolTableBuilder.WriteInt32(GetOrAddStringTableEntry(name));
            }

            _coffSymbolTableBuilder.WriteUInt32(value);
            writeSectionNumber(_coffSymbolTableBuilder);
            _coffSymbolTableBuilder.WriteUInt16((ushort)type);
            _coffSymbolTableBuilder.WriteByte((byte)storageClass);
            _coffSymbolTableBuilder.WriteByte(numberOfAuxSymbols);

            _coffSymbols.Add(name, result);

            return result;
        }

        public virtual void Serialize(BlobBuilder builder)
        {
            builder.LinkSuffix(_coffSymbolTableBuilder);
            builder.WriteInt32(_coffStringTableBuilder.Count + 4);
            builder.LinkSuffix(_coffStringTableBuilder);
        }
    }

    public struct CoffSymbolHandle
    {
        internal readonly int _value;
        internal CoffSymbolHandle(int value) => _value = value;
    }

    public class ManagedCoffSymbolTableBuilder : CoffSymbolTableBuilder
    {
        private readonly List<(Blob patch, LogicalSection section)> _deferredSectionFixups = new List<(Blob, LogicalSection)>();
        private bool _deferredSectionsResolved;

        public ManagedCoffSymbolTableBuilder(ObjectFeatures objectFeatures)
            : base(objectFeatures)
        {
        }

        public CoffSymbolHandle AddFunctionClrToken(string name, EntityHandle handle, int sectionOffset, out CoffSymbolHandle tokenCoffSymbol)
        {
            int token = MetadataTokens.GetToken(handle);

            CoffSymbolHandle index = GetOrAddCoffSymbolDeferred(name, (uint)sectionOffset, LogicalSection.Text, CoffSymbolType.Function, CoffSymbolStorageClass.External, 0);

            string tokenSymbolName = token.ToString("X8");
            if (!_coffSymbols.TryGetValue(tokenSymbolName, out tokenCoffSymbol))
            {
                tokenCoffSymbol = GetOrAddCoffSymbolDeferred(tokenSymbolName, (uint)sectionOffset, LogicalSection.Text, CoffSymbolType.Function, CoffSymbolStorageClass.ClrToken, 1);
                _coffSymbolTableBuilder.WriteByte(1);
                _coffSymbolTableBuilder.WriteByte(0);
                _coffSymbolTableBuilder.WriteInt32(index._value);
                _coffSymbolTableBuilder.PadTo(_coffSymbolTableBuilder.Count + 12);
            }
            else
            {
                // A prior undefined CLR token symbol exists (created by an IL relocation
                // before this function token was registered). This means the caller violated
                // the ordering requirement: AddFunctionClrToken must be called before any IL
                // that references this token is emitted.
                throw new InvalidOperationException(
                    $"CLR token symbol '{tokenSymbolName}' for function '{name}' was already created as undefined. " +
                    $"Register function tokens before emitting IL that references them.");
            }

            return index;
        }

        /// <summary>
        /// Creates an undefined CLR token symbol (section 0) for inline IL references.
        /// Used internally by relocation encoders.
        /// </summary>
        public CoffSymbolHandle GetOrAddUndefinedClrTokenSymbol(string name)
        {
            return GetOrAddCoffSymbol(name, 0, 0, CoffSymbolType.Null, CoffSymbolStorageClass.ClrToken, 0);
        }

        /// <summary>
        /// Adds a CLR token symbol for a field definition using a <see cref="LogicalSection"/>
        /// whose actual section number is resolved later via <see cref="ResolveDeferredSections"/>.
        /// </summary>
        public CoffSymbolHandle AddDataClrToken(string name, EntityHandle handle, LogicalSection section, int sectionOffset, out CoffSymbolHandle tokenCoffSymbol)
        {
            int token = MetadataTokens.GetToken(handle);

            CoffSymbolHandle index = GetOrAddCoffSymbolDeferred(name, (uint)sectionOffset, section, CoffSymbolType.Null, CoffSymbolStorageClass.Static, 0);

            string tokenSymbolName = token.ToString("X8");
            if (!_coffSymbols.TryGetValue(tokenSymbolName, out tokenCoffSymbol))
            {
                tokenCoffSymbol = GetOrAddCoffSymbolDeferred(tokenSymbolName, (uint)sectionOffset, section, CoffSymbolType.Null, CoffSymbolStorageClass.ClrToken, 1);
                _coffSymbolTableBuilder.WriteByte(1);
                _coffSymbolTableBuilder.WriteByte(0);
                _coffSymbolTableBuilder.WriteInt32(index._value);
                _coffSymbolTableBuilder.PadTo(_coffSymbolTableBuilder.Count + 12);
            }
            else
            {
                throw new InvalidOperationException(
                    $"CLR token symbol '{tokenSymbolName}' for data '{name}' was already created as undefined. " +
                    $"Register data tokens before emitting IL that references them.");
            }

            return index;
        }

        private CoffSymbolHandle GetOrAddCoffSymbolDeferred(string name, uint value, LogicalSection section,
            CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols)
        {
            if (_deferredSectionsResolved)
                throw new InvalidOperationException("Cannot add deferred symbols after sections have been resolved.");
            return GetOrAddCoffSymbolCore(name, value, b =>
            {
                Blob patch = b.ReserveBytes(2);
                _deferredSectionFixups.Add((patch, section));
            }, type, storageClass, numberOfAuxSymbols);
        }

        /// <summary>
        /// Patches all deferred section number fields using the provided resolver.
        /// Must be called exactly once, before the symbol table is serialized.
        /// </summary>
        public void ResolveDeferredSections(Func<LogicalSection, int> resolver)
        {
            if (_deferredSectionsResolved)
                throw new InvalidOperationException("Deferred sections have already been resolved.");
            _deferredSectionsResolved = true;

            foreach (var (patch, section) in _deferredSectionFixups)
            {
                var writer = new BlobWriter(patch);
                writer.WriteUInt16((ushort)resolver(section));
            }
        }

        public override void Serialize(BlobBuilder builder)
        {
            if (!_deferredSectionsResolved && _deferredSectionFixups.Count > 0)
                throw new InvalidOperationException("Deferred section fixups have not been resolved. Call ResolveDeferredSections before serializing.");
            base.Serialize(builder);
        }

        /// <summary>
        /// Adds an external (undefined) CLR token symbol for an imported member reference.
        /// The symbol has section=0 (IMAGE_SYM_UNDEFINED) and no aux record.
        /// </summary>
        public void AddExternalClrToken(string name, EntityHandle handle)
        {
            int token = MetadataTokens.GetToken(handle);

            GetOrAddCoffSymbol(name, 0, 0, CoffSymbolType.Function, CoffSymbolStorageClass.External, 0);
            GetOrAddCoffSymbol(token.ToString("X8"), 0, 0, CoffSymbolType.Function, CoffSymbolStorageClass.ClrToken, 0);
        }
    }

    /// <summary>
    /// Identifies a logical section whose actual 1-based COFF section number
    /// is not known until ManagedCoffBuilder lays out its sections.
    /// </summary>
    public enum LogicalSection { Text, Data, RData, Crtma }

    public enum CoffSymbolType : short
    {
        Null = 0x00,
        Function = 0x20,
    }

    public enum CoffSymbolStorageClass : byte
    {
        External = 2,
        Static = 3,
        ClrToken = 107,
    }

    public sealed class CoffHeaderBuilder
    {
        public Machine Machine { get; }
        public Characteristics ImageCharacteristics { get; }

        public CoffHeaderBuilder(Machine machine, Characteristics characteristics)
        {
            Machine = machine;
            ImageCharacteristics = characteristics;
        }
    }

    public abstract class CoffBuilder
    {
        public CoffHeaderBuilder Header { get; }

        public CoffSymbolTableBuilder SymbolTableBuilder { get; }

        public Func<IEnumerable<Blob>, BlobContentId> IdProvider { get; }
        public bool IsDeterministic { get; }

        private readonly Lazy<ImmutableArray<Section>> _lazySections;

        protected readonly struct Section
        {
            public readonly string Name;
            public readonly SectionCharacteristics Characteristics;

            public Section(string name, SectionCharacteristics characteristics)
            {
                if (name == null)
                {
                    throw new ArgumentNullException(nameof(name));
                }

                Name = name;
                Characteristics = characteristics;
            }
        }

        private readonly struct SerializedSection
        {
            public readonly BlobBuilder Builder;
            public readonly BlobBuilder Relocations;

            public readonly string Name;
            public readonly SectionCharacteristics Characteristics;
            public readonly int RelativeVirtualAddress;
            public readonly int SizeOfRawData;
            public readonly int PointerToRawData;

            public SerializedSection(BlobBuilder builder, BlobBuilder relocations, string name, SectionCharacteristics characteristics, int relativeVirtualAddress, int sizeOfRawData, int pointerToRawData)
            {
                Name = name;
                Characteristics = characteristics;
                Builder = builder;
                Relocations = relocations;
                RelativeVirtualAddress = relativeVirtualAddress;
                SizeOfRawData = sizeOfRawData;
                PointerToRawData = pointerToRawData;
            }

            public int VirtualSize => Builder.Count;
        }

        protected CoffBuilder(CoffHeaderBuilder header, CoffSymbolTableBuilder symbolTable, Func<IEnumerable<Blob>, BlobContentId> deterministicIdProvider)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            IdProvider = deterministicIdProvider ?? BlobContentId.GetTimeBasedProvider();
            IsDeterministic = deterministicIdProvider != null;
            Header = header;
            SymbolTableBuilder = symbolTable;
            _lazySections = new Lazy<ImmutableArray<Section>>(CreateSections);

        }

        protected ImmutableArray<Section> GetSections()
        {
            var sections = _lazySections.Value;
            if (sections.IsDefault)
            {
                throw new InvalidOperationException();
            }

            return sections;
        }

        protected abstract ImmutableArray<Section> CreateSections();

        protected abstract BlobBuilder SerializeSection(string name, SectionLocation location);

        protected abstract BlobBuilder SerializeRelocations(string name, SectionLocation location);

        public virtual BlobContentId Serialize(BlobBuilder builder)
        {
            // Define and serialize sections in two steps.
            // We need to know about all sections before serializing them.
            var serializedSections = SerializeSections();

            Blob stampFixup;
            Blob symTableFixup;
            WriteCoffHeader(builder, serializedSections, out stampFixup, (uint)(SymbolTableBuilder?.Count ?? 0), out symTableFixup);
            WriteSectionHeaders(builder, serializedSections);
            builder.Align(4);

            foreach (var section in serializedSections)
            {
                builder.LinkSuffix(section.Builder);
                builder.Align(4);
                if (section.Relocations != null)
                {
                    builder.LinkSuffix(section.Relocations);
                    builder.Align(4);
                }
            }

            var symTableFixupWriter = new BlobWriter(symTableFixup);
            symTableFixupWriter.WriteInt32(builder.Count);

            SymbolTableBuilder?.Serialize(builder);

            var contentId = IdProvider(builder.GetBlobs());

            // patch timestamp in COFF header:
            var stampWriter = new BlobWriter(stampFixup);
            stampWriter.WriteUInt32(contentId.Stamp);
            Debug.Assert(stampWriter.RemainingBytes == 0);

            return contentId;
        }

        internal static int Align(int position, int alignment)
        {
            Debug.Assert(position >= 0 && alignment > 0);

            int result = position & ~(alignment - 1);
            if (result == position)
            {
                return result;
            }

            return result + alignment;
        }

        private ImmutableArray<SerializedSection> SerializeSections()
        {
            var sections = GetSections();
            var result = ImmutableArray.CreateBuilder<SerializedSection>(sections.Length);
            int sizeOfPeHeaders = 20 + (40 * sections.Length);

            var nextPointer = Align(sizeOfPeHeaders, 4);

            foreach (var section in sections)
            {
                var builder = SerializeSection(section.Name, new SectionLocation(0, nextPointer));
                var relocs = SerializeRelocations(section.Name, new SectionLocation(0, nextPointer));

                var serialized = new SerializedSection(
                    builder,
                    relocs,
                    section.Name,
                    section.Characteristics,
                    relativeVirtualAddress: 0,
                    sizeOfRawData: Align(builder.Count, 4),
                    pointerToRawData: nextPointer);

                result.Add(serialized);

                nextPointer = serialized.PointerToRawData + serialized.SizeOfRawData;
                if (relocs != null)
                    nextPointer += Align(relocs.Count, 4);
            }

            return result.MoveToImmutable();
        }

        private void WriteCoffHeader(BlobBuilder builder, ImmutableArray<SerializedSection> sections, out Blob stampFixup, uint numSymbols, out Blob blobSymTableFixup)
        {
            // Machine
            builder.WriteUInt16((ushort)(Header.Machine == 0 ? Machine.I386 : Header.Machine));

            // NumberOfSections
            builder.WriteUInt16((ushort)sections.Length);

            // TimeDateStamp:
            stampFixup = builder.ReserveBytes(sizeof(uint));

            // PointerToSymbolTable:
            // The file pointer to the COFF symbol table, or zero if no COFF symbol table is present.
            // This value should be zero for a PE image.
            blobSymTableFixup = builder.ReserveBytes(sizeof(uint));

            // NumberOfSymbols:
            // The number of entries in the symbol table. This data can be used to locate the string table,
            // which immediately follows the symbol table. This value should be zero for a PE image.
            builder.WriteUInt32(numSymbols);

            // SizeOfOptionalHeader:
            // The size of the optional header, which is required for executable files but not for object files.
            // This value should be zero for an object file.
            builder.WriteUInt16(0);

            // Characteristics
            builder.WriteUInt16((ushort)Header.ImageCharacteristics);
        }

        private void WriteSectionHeaders(BlobBuilder builder, ImmutableArray<SerializedSection> serializedSections)
        {
            foreach (var serializedSection in serializedSections)
            {
                WriteSectionHeader(builder, serializedSection);
            }
        }

        internal static void WritePaddedName(BlobBuilder builder, string name)
        {
            for (int j = 0, m = name.Length; j < 8; j++)
            {
                if (j < m)
                {
                    builder.WriteByte((byte)name[j]);
                }
                else
                {
                    builder.WriteByte(0);
                }
            }
        }

        private void WriteSectionHeader(BlobBuilder builder, SerializedSection serializedSection)
        {
            if (serializedSection.Name.Length <= 8)
            {
                WritePaddedName(builder, serializedSection.Name);
            }
            else
            {
                // Long section names: write "/<offset>" where offset is into the COFF string table
                int stringOffset = SymbolTableBuilder.GetOrAddStringTableEntry(serializedSection.Name);
                string offsetStr = "/" + stringOffset.ToString();
                WritePaddedName(builder, offsetStr);
            }

            builder.WriteUInt32(0); // VirtualSize: should be 0 for object files
            builder.WriteUInt32((uint)serializedSection.RelativeVirtualAddress);
            builder.WriteUInt32((uint)serializedSection.SizeOfRawData);
            builder.WriteUInt32((uint)serializedSection.PointerToRawData);
            builder.WriteInt32(serializedSection.Relocations != null ? serializedSection.SizeOfRawData + serializedSection.PointerToRawData : 0); // PointerToRelocations
            builder.WriteUInt32(0); // PointerToLinenumbers
            builder.WriteUInt16(serializedSection.Relocations != null ? checked((ushort)(serializedSection.Relocations.Count / 10)) : (ushort)0); // NumberOfRelocations
            builder.WriteUInt16(0); // NumberOfLinenumbers
            builder.WriteUInt32((uint)serializedSection.Characteristics);
        }
    }

    public class ManagedCoffBuilder : CoffBuilder
    {
        private const string CorMetaSectionName = ".cormeta";
        private const string TextSectionName = ".text$mn";
        private const string DataSectionName = ".data";
        private const string RDataSectionName = ".rdata";
        private const string CrtmaSectionName = ".CRTMA$XCC";
        private const string CodeViewSymbolsSectionName = ".debug$S";

        private readonly CodeViewSymbolBuilder _codeViewSymbols;
        private readonly MetadataRootBuilder _metadataRootBuilder;
        private readonly BlobBuilder _ilStream;
        private readonly BlobBuilder _ilRelocs;
        private readonly BlobBuilder _dataStream;
        private readonly BlobBuilder _dataRelocs;
        private readonly BlobBuilder _rdataStream;
        private readonly InitializerListSectionBuilder _initializerList;

        public ManagedCoffBuilder(
            CoffHeaderBuilder header,
            MetadataRootBuilder metadataRootBuilder,
            ManagedCoffSymbolTableBuilder symbolTable,
            CodeViewSymbolBuilder codeViewSymbols,
            BlobBuilder ilStream,
            BlobBuilder ilRelocs,
            BlobBuilder dataStream = null,
            BlobBuilder dataRelocs = null,
            BlobBuilder rdataStream = null,
            InitializerListSectionBuilder initializerList = null,
            Func<IEnumerable<Blob>, BlobContentId> deterministicIdProvider = null)
            : base(header, symbolTable, deterministicIdProvider)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            if (metadataRootBuilder == null)
            {
                throw new ArgumentNullException(nameof(metadataRootBuilder));
            }

            if (ilStream == null)
            {
                throw new ArgumentNullException(nameof(ilStream));
            }

            _codeViewSymbols = codeViewSymbols;
            _metadataRootBuilder = metadataRootBuilder;
            _ilStream = ilStream;
            _ilRelocs = ilRelocs;
            _dataStream = dataStream;
            _dataRelocs = dataRelocs;
            _rdataStream = rdataStream;
            _initializerList = initializerList;
        }

        public override BlobContentId Serialize(BlobBuilder builder)
        {
            if (SymbolTableBuilder is ManagedCoffSymbolTableBuilder managedSymtab)
            {
                managedSymtab.ResolveDeferredSections(section => section switch
                {
                    LogicalSection.Text => TextSectionNumber,
                    LogicalSection.Data => DataSectionNumber,
                    LogicalSection.RData => RDataSectionNumber,
                    LogicalSection.Crtma => CrtmaSectionNumber,
                    _ => throw new InvalidOperationException($"Unknown logical section: {section}")
                });
            }
            return base.Serialize(builder);
        }

        /// <summary>
        /// Returns the 1-based section number for a given section, computed from the section list.
        /// Returns -1 if the section is not present.
        /// </summary>
        private int GetSectionNumber(string sectionName)
        {
            var sections = GetSections();
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i].Name == sectionName)
                    return i + 1;
            }
            return -1;
        }

        /// <summary>
        /// Returns the 1-based section number for the .text$mn section.
        /// </summary>
        private int TextSectionNumber => GetSectionNumber(TextSectionName);

        /// <summary>
        /// Returns the 1-based section number for the .data section, or -1 if no .data section.
        /// </summary>
        private int DataSectionNumber => GetSectionNumber(DataSectionName);

        /// <summary>
        /// Returns the 1-based section number for the .rdata section, or -1 if not present.
        /// </summary>
        private int RDataSectionNumber => GetSectionNumber(RDataSectionName);

        /// <summary>
        /// Returns the 1-based section number for the .CRTMA$XCC section, or -1 if not present.
        /// </summary>
        private int CrtmaSectionNumber => GetSectionNumber(CrtmaSectionName);

        protected override ImmutableArray<Section> CreateSections()
        {
            var builder = ImmutableArray.CreateBuilder<Section>();
            builder.Add(new Section(TextSectionName, SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode | SectionCharacteristics.Align4Bytes));
            if (_rdataStream != null && _rdataStream.Count > 0)
            {
                builder.Add(new Section(RDataSectionName, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes));
            }
            if (_dataStream != null && _dataStream.Count > 0)
            {
                builder.Add(new Section(DataSectionName, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | SectionCharacteristics.Align4Bytes));
            }
            builder.Add(new Section(CorMetaSectionName, SectionCharacteristics.LinkerInfo | SectionCharacteristics.Align1Bytes));
            if (_codeViewSymbols != null)
            {
                builder.Add(new Section(CodeViewSymbolsSectionName, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemDiscardable | SectionCharacteristics.Align1Bytes | SectionCharacteristics.MemRead));
            }
            if (_initializerList != null && _initializerList.HasInitializers)
            {
                var crtmaAlign = Header.Machine == Machine.I386 ? SectionCharacteristics.Align4Bytes : SectionCharacteristics.Align8Bytes;
                builder.Add(new Section(CrtmaSectionName, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | crtmaAlign));
            }

            return builder.ToImmutable();
        }

        protected override BlobBuilder SerializeSection(string name, SectionLocation location) =>
            name switch
            {
                TextSectionName => SerializeTextSection(location),
                RDataSectionName => _rdataStream,
                DataSectionName => _dataStream,
                CorMetaSectionName => SerializeCorMetaSection(location),
                CodeViewSymbolsSectionName => SerializeCodeViewSymbols(location),
                CrtmaSectionName => SerializeCrtmaSection(),
                _ => throw new ArgumentException(),
            };

        protected override BlobBuilder SerializeRelocations(string name, SectionLocation location)
        {
            if (name == TextSectionName)
            {
                return _ilRelocs;
            }
            else if (name == DataSectionName)
            {
                return _dataRelocs;
            }
            else if (name == CrtmaSectionName)
            {
                var b = new BlobBuilder();
                _initializerList.SerializeRelocations(b);
                return b;
            }
            else if (name == CodeViewSymbolsSectionName)
            {
                BlobBuilder builder = new BlobBuilder();
                _codeViewSymbols.SerializeRelocations(builder);
                return builder;
            }

            return null;
        }

        private BlobBuilder SerializeTextSection(SectionLocation location)
        {
            return _ilStream;
        }

        private BlobBuilder SerializeCorMetaSection(SectionLocation location)
        {
            var metadataBuilder = new BlobBuilder();
            _metadataRootBuilder.Serialize(metadataBuilder, 0, 0);
            return metadataBuilder;
        }

        private BlobBuilder SerializeCodeViewSymbols(SectionLocation location)
        {
            var builder = new BlobBuilder();
            _codeViewSymbols.Serialize(builder);
            return builder;
        }

        private BlobBuilder SerializeCrtmaSection()
        {
            var builder = new BlobBuilder();
            _initializerList.Serialize(builder);
            return builder;
        }
    }
}
