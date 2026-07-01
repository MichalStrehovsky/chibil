// This is a System.Reflection.Metadata writer to generate managed COFF OBJ files similar to
// what C++/CLI generates with /clr:pure option.
//

// Properties Nullable=disable and AllowUnsafeBlocks=true are set in Directory.Build.props

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Coff
{
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

            // Group entries by file — CodeView requires one block per file
            int blockStart = 0;
            while (blockStart < _entries.Count)
            {
                int fileId = _entries[blockStart].File._index;
                int blockEnd = blockStart + 1;
                while (blockEnd < _entries.Count && _entries[blockEnd].File._index == fileId)
                    blockEnd++;
                int count = blockEnd - blockStart;

                builder.WriteInt32(fileId);
                builder.WriteInt32(count);
                builder.WriteInt32(12 + 8 * count);

                for (int i = blockStart; i < blockEnd; i++)
                {
                    builder.WriteInt32(_entries[i].CodeOffset);
                    builder.WriteUInt32(CodeView.LineIsStatement | (uint)_entries[i].LineNumber);
                }

                blockStart = blockEnd;
            }
        }
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
        }

        private int GetOrAddString(string s)
        {
            if (_stringTableIndex.TryGetValue(s, out int result))
                return result;

            // CodeView string tables reserve offset 0 for the empty string.
            // Add it lazily so sections with no string references omit the subsection.
            if (_stringTable.Count == 0)
            {
                _stringTable.WriteByte(0);
                _stringTableIndex.Add("", 0);
                if (s.Length == 0)
                    return 0;
            }

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
        /// Adds S_OBJNAME and S_COMPILE3 records in their own DEBUG_S_SYMBOLS subsection.
        /// Call this before adding method symbols.
        /// </summary>
        public void AddObjNameAndCompile3(string objName, CodeViewLanguage language, CodeViewMachine machine,
            ushort feMajor, ushort feMinor, ushort feBuild,
            ushort beMajor, ushort beMinor, ushort beBuild,
            string compilerVersion, CodeViewCompileFlags compileFlags = 0)
        {
            _symbolAndLineNumbersBlob.WriteUInt32((uint)CodeViewSubsectionKind.Symbols);
            var sizeFixup = _symbolAndLineNumbersBlob.ReserveBytes(4);
            int startOffset = _symbolAndLineNumbersBlob.Count;

            // S_OBJNAME: signature(4) + name(null-terminated)
            int objNameRecLen = 2 + 4 + objName.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)objNameRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.ObjName);
            _symbolAndLineNumbersBlob.WriteUInt32(0); // signature
            _symbolAndLineNumbersBlob.WriteUTF8(objName);
            _symbolAndLineNumbersBlob.WriteByte(0);

            // S_COMPILE3: flags(4) + machine(2) + versions(8*2=16) + verString(null-terminated)
            int compile3RecLen = 2 + 4 + 2 + 16 + compilerVersion.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)compile3RecLen);
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.Compile3);

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
            _symbolAndLineNumbersBlob.WriteUInt32((uint)CodeViewSubsectionKind.Symbols);
            var sizeFixup = _symbolAndLineNumbersBlob.ReserveBytes(4);
            var startOffset = _symbolAndLineNumbersBlob.Count;

            _symbolAndLineNumbersBlob.WriteUInt16((ushort)(2 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 2 + 2 + 1 + methodName.Length + 1));

            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.GManProc);

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
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.FrameProc);

            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt32(0);
            _symbolAndLineNumbersBlob.WriteInt16(0);

            // frameproc flags: matches MSVC /clr:pure (compiled /EHa, optimized for speed)
            _symbolAndLineNumbersBlob.WriteUInt32((uint)(CodeViewFrameProcFlags.AsyncEH | CodeViewFrameProcFlags.OptSpeed));

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
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.ProcIdEnd);

            new BlobWriter(sizeFixup).WriteInt32(_symbolAndLineNumbersBlob.Count - startOffset);

            _symbolAndLineNumbersBlob.Align(4);
        }

        private void EmitManSlot(CodeViewManSlot slot)
        {
            int manSlotRecLen = 2 + 4 + 4 + 4 + 2 + 2 + slot.Name.Length + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)manSlotRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.ManSlot);
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
            // S_BLOCK32: pParent(4) + pEnd(4) + len(4) + off(4) + seg(2) + name(null-term)
            int blockRecLen = 2 + 4 + 4 + 4 + 4 + 2 + 1;
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)blockRecLen);
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.Block32);
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
            _symbolAndLineNumbersBlob.WriteUInt16((ushort)CodeViewSymbolKind.End);
        }

        public void AddLineNumbers(CoffSymbolHandle methodCoffSymbol, int methodCoffSymbolDelta, int codeSize, CodeViewLineNumberBuilder lineNumbersBlob)
        {
            _symbolAndLineNumbersBlob.WriteUInt32((uint)CodeViewSubsectionKind.Lines);
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

        internal BlobBuilder Serialize()
        {
            BlobBuilder builder = new BlobBuilder();

            builder.WriteUInt32(CodeView.SignatureC13); // version

            if (_symbolAndLineNumbersBlob.Count > 0)
            {
                builder.LinkSuffix(_symbolAndLineNumbersBlob);
            }

            if (_stringTable.Count > 0)
            {
                builder.WriteUInt32((uint)CodeViewSubsectionKind.StringTable);
                builder.WriteInt32(_stringTable.Count);
                builder.LinkSuffix(_stringTable);
                builder.Align(4);
            }

            if (_fileTable.Count > 0)
            {
                builder.WriteUInt32((uint)CodeViewSubsectionKind.FileChecksums);
                builder.WriteInt32(_fileTable.Count);
                builder.LinkSuffix(_fileTable);
                builder.Align(4);
            }

            return builder;
        }

        internal BlobBuilder SerializeRelocations()
        {
            return _relocationsBlob;
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
            return new LabelHandle(_labels.Count);
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

        public void AddRelocation(int offset, ImageRelocation type, CoffSymbolHandle coffSymbol)
        {
            Builder.WriteInt32(offset);
            Builder.WriteInt32(coffSymbol._value);
            Builder.WriteUInt16((ushort)type);
        }

        public void AddSectionRelocation(int offset, CoffSymbolHandle coffSymbol)
            => AddRelocation(offset, HeaderBuilder.Machine switch
            {
                Machine.I386 => ImageRelocation.I386_SECTION,
                Machine.Amd64 => ImageRelocation.Amd64_SECTION,
                Machine.Arm64 => ImageRelocation.Arm64_SECTION,
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            }, coffSymbol);

        public void AddSectionRelativeRelocation(int offset, CoffSymbolHandle coffSymbol)
            => AddRelocation(offset, HeaderBuilder.Machine switch
            {
                Machine.I386 => ImageRelocation.I386_SECREL,
                Machine.Amd64 => ImageRelocation.Amd64_SECREL,
                Machine.Arm64 => ImageRelocation.Arm64_SECREL,
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            }, coffSymbol);

        public void AddTokenRelocation(int offset, CoffSymbolHandle coffSymbol)
            => AddRelocation(offset, HeaderBuilder.Machine switch
            {
                Machine.I386 => ImageRelocation.I386_TOKEN,
                Machine.Amd64 => ImageRelocation.Amd64_TOKEN,
                Machine.Arm64 => ImageRelocation.Arm64_TOKEN,
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            }, coffSymbol);

        public void AddAddressRelocation(int offset, CoffSymbolHandle coffSymbol)
            => AddRelocation(offset, HeaderBuilder.Machine switch
            {
                Machine.I386 => ImageRelocation.I386_DIR32,
                Machine.Amd64 => ImageRelocation.Amd64_ADDR64,
                Machine.Arm64 => ImageRelocation.Arm64_ADDR64,
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            }, coffSymbol);

        public void AddImageRelativeRelocation(int offset, CoffSymbolHandle coffSymbol)
            => AddRelocation(offset, HeaderBuilder.Machine switch
            {
                Machine.I386 => ImageRelocation.I386_DIR32NB,
                Machine.Amd64 => ImageRelocation.Amd64_ADDR32NB,
                Machine.Arm64 => ImageRelocation.Arm64_ADDR32NB,
                _ => throw new NotSupportedException($"Unsupported machine type: {HeaderBuilder.Machine}"),
            }, coffSymbol);
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
                    else if (unchecked((uint)slotIndex) <= ushort.MaxValue)
                    {
                        OpCode(ILOpCode.Ldloc);
                        CodeBuilder.WriteUInt16((ushort)slotIndex);
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
                    else if (unchecked((uint)slotIndex) <= ushort.MaxValue)
                    {
                        OpCode(ILOpCode.Stloc);
                        CodeBuilder.WriteUInt16((ushort)slotIndex);
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
            else if (unchecked((uint)slotIndex) <= ushort.MaxValue)
            {
                OpCode(ILOpCode.Ldloca);
                CodeBuilder.WriteUInt16((ushort)slotIndex);
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
                    else if (unchecked((uint)argumentIndex) <= ushort.MaxValue)
                    {
                        OpCode(ILOpCode.Ldarg);
                        CodeBuilder.WriteUInt16((ushort)argumentIndex);
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
            else if (unchecked((uint)argumentIndex) <= ushort.MaxValue)
            {
                OpCode(ILOpCode.Ldarga);
                CodeBuilder.WriteUInt16((ushort)argumentIndex);
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
            else if (unchecked((uint)argumentIndex) <= ushort.MaxValue)
            {
                OpCode(ILOpCode.Starg);
                CodeBuilder.WriteUInt16((ushort)argumentIndex);
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
        private readonly CoffSectionWithContentBuilder _coffSection;

        public BlobBuilder Builder => _coffSection.Content;

        public BlobBuilder RelocationBuilder => _coffSection.Relocations;

        public ManagedCoffSymbolTableBuilder SymbolTableBuilder { get; }

        public CoffHeaderBuilder HeaderBuilder { get; }

        public CodeViewSymbolBuilder CodeViewSymbolBuilder { get; }

        public RelocatableMethodBodyStreamEncoder(CoffSectionWithContentBuilder section, ManagedCoffSymbolTableBuilder symTabBuilder, CoffHeaderBuilder headerBuilder, CodeViewSymbolBuilder codeViewSymbolBuilder)
        {
            _coffSection = section;
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

            CoffSymbolHandle methodSymbol = SymbolTableBuilder.AddFunctionClrToken(_coffSection, coffSymbolName, metadataHandle, bodyOffset, out CoffSymbolHandle tokenCoffSymbol);
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

    public class CoffSymbolTableBuilder
    {
        protected Dictionary<string, CoffSymbolHandle> _coffSymbols = new Dictionary<string, CoffSymbolHandle>(StringComparer.Ordinal);
        protected BlobBuilder _coffStringTableBuilder = new BlobBuilder();
        protected BlobBuilder _coffSymbolTableBuilder = new BlobBuilder();
        protected readonly List<(Blob patch, CoffSectionBuilder section)> _sectionFixups = new List<(Blob, CoffSectionBuilder)>();

        private const int SymbolSize = 18;

        public int Count => _coffSymbolTableBuilder.Count / SymbolSize;

        public CoffSymbolTableBuilder() { }

        public CoffSymbolTableBuilder(ObjectFeatures objectFeatures)
        {
            GetOrAddCoffSymbol("@feat.00", (ushort)objectFeatures, CoffBuilder.AbsoluteSection, CoffSymbolType.Null, CoffSymbolStorageClass.Static, 0);
        }

        private Dictionary<string, int> _stringTableEntries = new Dictionary<string, int>(StringComparer.Ordinal);
        protected readonly List<(Blob lengthPatch, Blob relocationCountPatch, CoffSectionBuilder section)> _sectionAuxFixups = new List<(Blob, Blob, CoffSectionBuilder)>();

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

        protected void WriteSymbolName(string name)
        {
            if (name.Length <= 8)
            {
                CoffBuilder.WritePaddedName(_coffSymbolTableBuilder, name);
            }
            else
            {
                _coffSymbolTableBuilder.WriteUInt32(0);
                _coffSymbolTableBuilder.WriteInt32(GetOrAddStringTableEntry(name));
            }
        }

        protected CoffSymbolHandle WriteCoffSymbol(string name, uint value, CoffSectionBuilder section, CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols)
        {
            CoffSymbolHandle result = new CoffSymbolHandle(Count);

            WriteSymbolName(name);
            _coffSymbolTableBuilder.WriteUInt32(value);

            Blob sectionPatch = _coffSymbolTableBuilder.ReserveBytes(2);
            _sectionFixups.Add((sectionPatch, section));

            _coffSymbolTableBuilder.WriteUInt16((ushort)type);
            _coffSymbolTableBuilder.WriteByte((byte)storageClass);
            _coffSymbolTableBuilder.WriteByte(numberOfAuxSymbols);

            return result;
        }

        protected CoffSymbolHandle GetOrAddCoffSymbol(string name, uint value, CoffSectionBuilder section, CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols)
        {
            if (_coffSymbols.TryGetValue(name, out CoffSymbolHandle result))
            {
                return result;
            }

            result = WriteCoffSymbol(name, value, section, type, storageClass, numberOfAuxSymbols);
            _coffSymbols.Add(name, result);

            return result;
        }

        public CoffSymbolHandle AddComdatSectionSymbol(CoffSectionBuilder section)
        {
            if (!section.ComdatSelection.HasValue)
                throw new ArgumentException("Section is not a COMDAT section.", nameof(section));

            CoffSymbolHandle sectionSymbol = WriteCoffSymbol(
                section.Name,
                0,
                section,
                CoffSymbolType.Null,
                CoffSymbolStorageClass.Static,
                numberOfAuxSymbols: 1);

            Blob lengthPatch = _coffSymbolTableBuilder.ReserveBytes(4);
            Blob relocationCountPatch = _coffSymbolTableBuilder.ReserveBytes(2);
            _coffSymbolTableBuilder.WriteUInt16(0); // NumberOfLinenumbers
            _coffSymbolTableBuilder.WriteUInt32(0); // CheckSum

            if (section.ComdatSelection.Value == CoffComdatSelection.Associative)
            {
                Blob associatedSectionPatch = _coffSymbolTableBuilder.ReserveBytes(2);
                _sectionFixups.Add((associatedSectionPatch, section.ComdatAssociatedSection));
            }
            else
            {
                _coffSymbolTableBuilder.WriteUInt16(0);
            }

            _coffSymbolTableBuilder.WriteByte((byte)section.ComdatSelection.Value);
            _coffSymbolTableBuilder.PadTo(_coffSymbolTableBuilder.Count + 3);
            _sectionAuxFixups.Add((lengthPatch, relocationCountPatch, section));

            return sectionSymbol;
        }

        public readonly struct SectionSerializationInfo
        {
            public int Length { get; }
            public int RelocationCount { get; }

            public SectionSerializationInfo(int length, int relocationCount)
                => (Length, RelocationCount) = (length, relocationCount);
        }

        public virtual void Serialize(
            BlobBuilder builder,
            IReadOnlyDictionary<CoffSectionBuilder, int> sectionMap,
            IReadOnlyDictionary<CoffSectionBuilder, SectionSerializationInfo> sectionSerializationInfo)
        {
            foreach (var (lengthPatch, relocationCountPatch, section) in _sectionAuxFixups)
            {
                SectionSerializationInfo info = sectionSerializationInfo[section];
                new BlobWriter(lengthPatch).WriteUInt32((uint)info.Length);
                new BlobWriter(relocationCountPatch).WriteUInt16(checked((ushort)info.RelocationCount));
            }

            foreach (var (patch, section) in _sectionFixups)
            {
                var writer = new BlobWriter(patch);
                writer.WriteUInt16(checked((ushort)sectionMap[section]));
            }

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
        public ManagedCoffSymbolTableBuilder(ObjectFeatures objectFeatures)
            : base(objectFeatures)
        {
        }

        private readonly Dictionary<int, (Blob valuePatch, Blob tokenValuePatch, CoffSymbolHandle methodSymbol, CoffSymbolHandle tokenSymbol)> _preRegisteredFunctions = new();

        /// <summary>
        /// Pre-registers COFF symbols for a function MethodDef before any IL is emitted.
        /// The body offset (value field) is a placeholder patched later by AddMethodBody.
        /// This prevents conflicts when forward-referencing calls create undefined token symbols.
        /// </summary>
        public void PreRegisterFunctionClrToken(CoffSectionBuilder section, string name, EntityHandle handle)
        {
            int token = MetadataTokens.GetToken(handle);
            string tokenSymbolName = token.ToString("X8");

            if (_coffSymbols.ContainsKey(tokenSymbolName))
                return; // Already registered

            // Create decorated-name symbol with deferred value + section
            Blob methodValuePatch;
            CoffSymbolHandle methodSymbol = GetOrAddCoffSymbolCoreDeferred(name,
                section, CoffSymbolType.Function, CoffSymbolStorageClass.External, 0,
                out methodValuePatch);

            // Create CLR token symbol with deferred value + section + aux record
            Blob tokenValuePatch;
            CoffSymbolHandle tokenSymbol = GetOrAddCoffSymbolCoreDeferred(tokenSymbolName,
                section, CoffSymbolType.Function, CoffSymbolStorageClass.ClrToken, 1,
                out tokenValuePatch);

            // Write aux record linking token → method symbol
            _coffSymbolTableBuilder.WriteByte(1);
            _coffSymbolTableBuilder.WriteByte(0);
            _coffSymbolTableBuilder.WriteInt32(methodSymbol._value);
            _coffSymbolTableBuilder.PadTo(_coffSymbolTableBuilder.Count + 12);

            _preRegisteredFunctions[token] = (methodValuePatch, tokenValuePatch, methodSymbol, tokenSymbol);
        }

        private CoffSymbolHandle GetOrAddCoffSymbolCoreDeferred(string name, CoffSectionBuilder section,
            CoffSymbolType type, CoffSymbolStorageClass storageClass, byte numberOfAuxSymbols,
            out Blob valuePatch)
        {
            if (_coffSymbols.TryGetValue(name, out CoffSymbolHandle result))
            {
                valuePatch = default;
                return result;
            }

            result = new CoffSymbolHandle(Count);

            WriteSymbolName(name);

            // Reserve value field for later patching
            valuePatch = _coffSymbolTableBuilder.ReserveBytes(4);

            // Deferred section number
            Blob sectionPatch = _coffSymbolTableBuilder.ReserveBytes(2);
            _sectionFixups.Add((sectionPatch, section));

            _coffSymbolTableBuilder.WriteUInt16((ushort)type);
            _coffSymbolTableBuilder.WriteByte((byte)storageClass);
            _coffSymbolTableBuilder.WriteByte(numberOfAuxSymbols);

            _coffSymbols.Add(name, result);
            return result;
        }

        public CoffSymbolHandle AddFunctionClrToken(CoffSectionBuilder section, string name, EntityHandle handle, int sectionOffset, out CoffSymbolHandle tokenCoffSymbol)
        {
            int token = MetadataTokens.GetToken(handle);

            // Check if pre-registered — patch body offset instead of creating new symbols
            if (_preRegisteredFunctions.TryGetValue(token, out var preReg))
            {
                new BlobWriter(preReg.valuePatch).WriteUInt32((uint)sectionOffset);
                new BlobWriter(preReg.tokenValuePatch).WriteUInt32((uint)sectionOffset);
                tokenCoffSymbol = preReg.tokenSymbol;
                return preReg.methodSymbol;
            }

            CoffSymbolHandle index = GetOrAddCoffSymbol(name, (uint)sectionOffset, section, CoffSymbolType.Function, CoffSymbolStorageClass.External, 0);

            string tokenSymbolName = token.ToString("X8");
            if (!_coffSymbols.TryGetValue(tokenSymbolName, out tokenCoffSymbol))
            {
                tokenCoffSymbol = GetOrAddCoffSymbol(tokenSymbolName, (uint)sectionOffset, section, CoffSymbolType.Function, CoffSymbolStorageClass.ClrToken, 1);
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
            return GetOrAddCoffSymbol(name, 0, CoffBuilder.UndefinedSection, CoffSymbolType.Null, CoffSymbolStorageClass.ClrToken, 0);
        }

        /// <summary>
        /// Adds a CLR token symbol for a field definition.
        /// </summary>
        public CoffSymbolHandle AddDataClrToken(string name, EntityHandle handle, CoffSectionBuilder section, int sectionOffset, out CoffSymbolHandle tokenCoffSymbol, bool isExternal = false)
        {
            int token = MetadataTokens.GetToken(handle);

            var storageClass = isExternal ? CoffSymbolStorageClass.External : CoffSymbolStorageClass.Static;
            CoffSymbolHandle index = GetOrAddCoffSymbol(name, (uint)sectionOffset, section, CoffSymbolType.Null, storageClass, 0);

            string tokenSymbolName = token.ToString("X8");
            if (!_coffSymbols.TryGetValue(tokenSymbolName, out tokenCoffSymbol))
            {
                tokenCoffSymbol = GetOrAddCoffSymbol(tokenSymbolName, (uint)sectionOffset, section, CoffSymbolType.Null, CoffSymbolStorageClass.ClrToken, 1);
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

        public CoffSymbolHandle AddDataSymbol(string name, CoffSectionBuilder section, int sectionOffset)
        {
            return GetOrAddCoffSymbol(name, (uint)sectionOffset, section, CoffSymbolType.Null, CoffSymbolStorageClass.Static, 0);
        }

        /// <summary>
        /// Adds a section-bound data symbol with <see cref="CoffSymbolStorageClass.External"/>.
        /// Use for symbols that other translation units may reference by name, e.g. the
        /// bare-name aliases for /clr NEP thunks (which expose externally linked C
        /// functions to native callers) and the <c>__mep@</c> fixup slots they point at.
        /// </summary>
        public CoffSymbolHandle AddExternalDataSymbol(string name, CoffSectionBuilder section, int sectionOffset)
        {
            return GetOrAddCoffSymbol(name, (uint)sectionOffset, section, CoffSymbolType.Null, CoffSymbolStorageClass.External, 0);
        }

        /// <summary>
        /// Adds an undefined external symbol (Sect=0, Value=0) for a symbol
        /// defined in another translation unit. The linker resolves it at link time.
        /// </summary>
        public CoffSymbolHandle AddUndefinedExternalSymbol(string name, CoffSymbolType type = CoffSymbolType.Function)
        {
            return GetOrAddCoffSymbol(name, 0, CoffBuilder.UndefinedSection, type, CoffSymbolStorageClass.External, 0);
        }

        /// <summary>
        /// Adds a "common" data symbol for an uninitialized global — a Sect=0
        /// External symbol whose Value field holds the symbol's size in bytes
        /// (per the COFF spec; the linker allocates space at link time). Used
        /// for /clr uninitialized globals like <c>int g_uninitialized;</c>.
        /// The companion CLR token symbol mirrors the same Sect=0/Value=size
        /// shape with an aux record pointing at the name symbol.
        /// </summary>
        public CoffSymbolHandle AddCommonDataClrToken(string name, EntityHandle handle, int size, out CoffSymbolHandle tokenCoffSymbol)
        {
            int token = MetadataTokens.GetToken(handle);

            CoffSymbolHandle index = GetOrAddCoffSymbol(name, (uint)size, CoffBuilder.UndefinedSection, CoffSymbolType.Null, CoffSymbolStorageClass.External, 0);

            string tokenSymbolName = token.ToString("X8");
            if (!_coffSymbols.TryGetValue(tokenSymbolName, out tokenCoffSymbol))
            {
                tokenCoffSymbol = GetOrAddCoffSymbol(tokenSymbolName, (uint)size, CoffBuilder.UndefinedSection, CoffSymbolType.Null, CoffSymbolStorageClass.ClrToken, 1);
                _coffSymbolTableBuilder.WriteByte(1);
                _coffSymbolTableBuilder.WriteByte(0);
                _coffSymbolTableBuilder.WriteInt32(index._value);
                _coffSymbolTableBuilder.PadTo(_coffSymbolTableBuilder.Count + 12);
            }
            else
            {
                throw new InvalidOperationException(
                    $"CLR token symbol '{tokenSymbolName}' for common data '{name}' was already created. " +
                    $"Register common data tokens before any IL that references them.");
            }

            return index;
        }

        /// <summary>
        /// Adds an external (undefined) CLR token symbol for an imported member reference.
        /// The symbol has section=0 (IMAGE_SYM_UNDEFINED) and no aux record.
        /// </summary>
        public void AddExternalClrToken(string name, EntityHandle handle)
        {
            int token = MetadataTokens.GetToken(handle);

            GetOrAddCoffSymbol(name, 0, CoffBuilder.UndefinedSection, CoffSymbolType.Function, CoffSymbolStorageClass.External, 0);
            GetOrAddCoffSymbol(token.ToString("X8"), 0, CoffBuilder.UndefinedSection, CoffSymbolType.Function, CoffSymbolStorageClass.ClrToken, 0);
        }
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

    public abstract class CoffSectionBuilder
    {
        public string Name { get; }
        public SectionCharacteristics Characteristics { get; }
        public CoffComdatSelection? ComdatSelection { get; }
        public CoffSectionBuilder ComdatAssociatedSection { get; }

        /// <summary>Map a byte alignment to the COFF section-characteristics alignment
        /// flag (Align1Bytes = 1&lt;&lt;20 .. Align8192Bytes = 14&lt;&lt;20). The alignment
        /// must be a power of two no greater than 8192 — the largest value the 4-bit
        /// COFF alignment field can encode.</summary>
        public static SectionCharacteristics AlignmentCharacteristics(int align)
        {
            if (align < 1 || (align & (align - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(align), align,
                    "Section alignment must be a positive power of two.");
            if (align > 8192)
                throw new ArgumentOutOfRangeException(nameof(align), align,
                    "Section alignment exceeds the maximum COFF-encodable alignment (8192).");

            int log2 = 0;
            while ((1 << log2) < align) log2++;
            return (SectionCharacteristics)((uint)(log2 + 1) << 20);
        }

        public CoffSectionBuilder(
            string name,
            SectionCharacteristics characteristics,
            CoffComdatSelection? comdatSelection = null,
            CoffSectionBuilder comdatAssociatedSection = null)
        {
            if (comdatSelection == CoffComdatSelection.Associative && comdatAssociatedSection == null)
                throw new ArgumentException("Associative COMDAT sections require an associated section.", nameof(comdatAssociatedSection));

            Name = name;
            Characteristics = characteristics;
            ComdatSelection = comdatSelection;
            ComdatAssociatedSection = comdatAssociatedSection;
        }

        public abstract BlobBuilder SerializeContent(SectionLocation location);
        public virtual BlobBuilder SerializeRelocations(SectionLocation location) => null;
    }

    public class CoffSectionWithContentBuilder : CoffSectionBuilder
    {
        public BlobBuilder Content { get; set; } = new BlobBuilder();
        public BlobBuilder Relocations { get; set; } = new BlobBuilder();

        public CoffSectionWithContentBuilder(string name, SectionCharacteristics characteristics)
            : base(name, characteristics) { }

        public CoffSectionWithContentBuilder(
            string name,
            SectionCharacteristics characteristics,
            CoffComdatSelection selection,
            CoffSectionBuilder associatedSection = null)
            : base(name, characteristics | SectionCharacteristics.LinkerComdat, selection, associatedSection) { }

        public override BlobBuilder SerializeContent(SectionLocation location) => Content;
        public override BlobBuilder SerializeRelocations(SectionLocation location) => Relocations;
    }

    public sealed class UninitializedCoffSectionBuilder : CoffSectionBuilder
    {
        public int Size { get; set; }

        public UninitializedCoffSectionBuilder(string name, SectionCharacteristics characteristics)
            : base(name, characteristics) { }

        public UninitializedCoffSectionBuilder(
            string name,
            SectionCharacteristics characteristics,
            CoffComdatSelection selection,
            CoffSectionBuilder associatedSection = null)
            : base(name, characteristics | SectionCharacteristics.LinkerComdat, selection, associatedSection) { }

        public override BlobBuilder SerializeContent(SectionLocation location)
            => throw new NotSupportedException();
    }

    /// <summary>A `.debug$S` section backed by a CodeViewSymbolBuilder. Can be the
    /// module-wide non-COMDAT section, or a per-function COMDAT section that is
    /// associative to that function's `.text$mn` (matching MSVC /Gy).</summary>
    public sealed class CodeViewSectionBuilder : CoffSectionBuilder
    {
        private readonly CodeViewSymbolBuilder _codeViewSymbolBuilder;

        public CodeViewSectionBuilder(CodeViewSymbolBuilder codeviewSymbols)
            : this(codeviewSymbols, null) { }

        public CodeViewSectionBuilder(CodeViewSymbolBuilder codeviewSymbols, CoffSectionBuilder associatedSection)
            : base(".debug$S",
                SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemDiscardable |
                SectionCharacteristics.Align1Bytes | SectionCharacteristics.MemRead |
                (associatedSection != null ? SectionCharacteristics.LinkerComdat : 0),
                associatedSection != null ? CoffComdatSelection.Associative : null,
                associatedSection)
            => _codeViewSymbolBuilder = codeviewSymbols;

        public override BlobBuilder SerializeContent(SectionLocation location)
            => _codeViewSymbolBuilder.Serialize();

        public override BlobBuilder SerializeRelocations(SectionLocation location)
            => _codeViewSymbolBuilder.SerializeRelocations();
    }

    public abstract class CoffBuilder
    {
        private readonly List<CoffSectionBuilder> _sections = new();
        private readonly Dictionary<CoffSectionBuilder, int> _sectionToIndex = new();

        // Pseudo-sections for the special COFF section numbers defined by the
        // PE/COFF spec (WinNT.h). They never produce file content; they only
        // supply a SectionNumber value when a symbol is written.
        public static CoffSectionBuilder UndefinedSection { get; } = new SpecialCoffSectionBuilder();
        public static CoffSectionBuilder AbsoluteSection { get; } = new SpecialCoffSectionBuilder();

        public CoffHeaderBuilder Header { get; }

        public CoffSymbolTableBuilder SymbolTableBuilder { get; }

        public Func<IEnumerable<Blob>, BlobContentId> IdProvider { get; }
        public bool IsDeterministic { get; }

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
        }

        protected CoffBuilder(CoffHeaderBuilder header, CoffSymbolTableBuilder symbolTable, IEnumerable<CoffSectionBuilder> sections, Func<IEnumerable<Blob>, BlobContentId> deterministicIdProvider = null)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            IdProvider = deterministicIdProvider ?? BlobContentId.GetTimeBasedProvider();
            IsDeterministic = deterministicIdProvider != null;
            Header = header;
            SymbolTableBuilder = symbolTable;

            foreach (CoffSectionBuilder s in sections)
            {
                _sections.Add(s);
                _sectionToIndex.Add(s, _sections.Count);
            }

            //   IMAGE_SYM_UNDEFINED (0)    — symbol is external/undefined; the
            //                                linker resolves it from another TU.
            _sectionToIndex.Add(UndefinedSection, 0);

            //   IMAGE_SYM_ABSOLUTE (0xFFFF) — symbol holds an absolute value rather
            //                                than a section-relative address (e.g. @feat.00).
            _sectionToIndex.Add(AbsoluteSection, 0xFFFF);
        }

        public virtual BlobContentId Serialize(BlobBuilder builder)
        {
            // Define and serialize sections in two steps.
            // We need to know about all sections before serializing them.
            var serializedSections = SerializeSections();
            if (serializedSections.Length > ushort.MaxValue)
                throw new InvalidOperationException($"COFF object has {serializedSections.Length} sections; bigobj output is not supported.");

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

            if (SymbolTableBuilder != null)
            {
                var sectionSerializationInfo = new Dictionary<CoffSectionBuilder, CoffSymbolTableBuilder.SectionSerializationInfo>(serializedSections.Length);
                for (int i = 0; i < serializedSections.Length; i++)
                {
                    var section = serializedSections[i];
                    sectionSerializationInfo.Add(_sections[i], new CoffSymbolTableBuilder.SectionSerializationInfo(
                        section.SizeOfRawData,
                        section.Relocations != null ? section.Relocations.Count / 10 : 0));
                }

                SymbolTableBuilder.Serialize(builder, _sectionToIndex, sectionSerializationInfo);
            }

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
            var result = ImmutableArray.CreateBuilder<SerializedSection>(_sections.Count);
            int sizeOfPeHeaders = 20 + (40 * _sections.Count);

            var nextPointer = Align(sizeOfPeHeaders, 4);

            foreach (var section in _sections)
            {
                BlobBuilder builder;
                BlobBuilder relocs;
                int sizeOfRawData;
                int pointerToRawData;
                if (section is UninitializedCoffSectionBuilder uninitialized)
                {
                    builder = new BlobBuilder();
                    relocs = null;
                    sizeOfRawData = uninitialized.Size;
                    pointerToRawData = 0;
                }
                else
                {
                    builder = section.SerializeContent(new SectionLocation(0, nextPointer));
                    relocs = section.SerializeRelocations(new SectionLocation(0, nextPointer));
                    sizeOfRawData = Align(builder.Count, 4);
                    pointerToRawData = nextPointer;
                }

                var serialized = new SerializedSection(
                    builder,
                    relocs,
                    section.Name,
                    section.Characteristics,
                    relativeVirtualAddress: 0,
                    sizeOfRawData: sizeOfRawData,
                    pointerToRawData: pointerToRawData);

                result.Add(serialized);

                if (pointerToRawData != 0)
                {
                    nextPointer = pointerToRawData + sizeOfRawData;
                    if (relocs != null)
                        nextPointer += Align(relocs.Count, 4);
                }
            }

            return result.MoveToImmutable();
        }

        private void WriteCoffHeader(BlobBuilder builder, ImmutableArray<SerializedSection> sections, out Blob stampFixup, uint numSymbols, out Blob blobSymTableFixup)
        {
            // Machine
            builder.WriteUInt16((ushort)(Header.Machine == 0 ? Machine.I386 : Header.Machine));

            // NumberOfSections
            builder.WriteUInt16(checked((ushort)sections.Length));

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

        private class SpecialCoffSectionBuilder : CoffSectionBuilder
        {
            public SpecialCoffSectionBuilder()
                : base(null, 0) { }

            public override BlobBuilder SerializeContent(SectionLocation location) => throw new UnreachableException();
        }
    }

    public class ManagedCoffBuilder : CoffBuilder
    {
        public ManagedCoffBuilder(
            CoffHeaderBuilder header,
            MetadataRootBuilder metadataRootBuilder,
            ManagedCoffSymbolTableBuilder symbolTable,
            CodeViewSymbolBuilder codeViewSymbols,
            IEnumerable<CoffSectionBuilder> sections,
            Func<IEnumerable<Blob>, BlobContentId> deterministicIdProvider = null)
            : base(header, symbolTable, CreateSectionList(sections, metadataRootBuilder, codeViewSymbols), deterministicIdProvider)
        {
        }

        private static IEnumerable<CoffSectionBuilder> CreateSectionList(IEnumerable<CoffSectionBuilder> sections, MetadataRootBuilder metadataRootBuilder, CodeViewSymbolBuilder codeViewSymbols)
        {
            if (metadataRootBuilder == null)
            {
                throw new ArgumentNullException(nameof(metadataRootBuilder));
            }

            foreach (CoffSectionBuilder s in sections)
            {
                yield return s;
            }

            yield return new CorMetaSectionBuilder(metadataRootBuilder);

            if (codeViewSymbols != null)
            {
                yield return new CodeViewSectionBuilder(codeViewSymbols);
            }
        }

        private class CorMetaSectionBuilder : CoffSectionBuilder
        {
            private readonly MetadataRootBuilder _metadataRootBuilder;

            public CorMetaSectionBuilder(MetadataRootBuilder metadataRootBuilder)
                : base(".cormeta", SectionCharacteristics.LinkerInfo | SectionCharacteristics.Align1Bytes)
                => _metadataRootBuilder = metadataRootBuilder;

            public override BlobBuilder SerializeContent(SectionLocation location)
            {
                var metadataBuilder = new BlobBuilder();
                _metadataRootBuilder.Serialize(metadataBuilder, 0, 0);
                return metadataBuilder;
            }
        }
    }
}
