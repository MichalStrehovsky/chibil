using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Chibil;

// A basic-block based IL builder inspired by Roslyn's ILBuilder
// (src/Compilers/Core/Portable/CodeGen/ILBuilder.cs, MIT licensed).
//
// Unlike the legacy RelocatableInstructionEncoder, this models the method as a
// graph of basic blocks. The terminating branch of a block is stored separately
// from the block's regular instruction bytes, which enables:
//   * automatic short-form branch selection (pessimistic relaxation),
//   * dead-code elimination,
//   * branch peephole optimizations,
//   * terminal-block tracking (replacing the _lastTerminalOffset hack),
//   * stack-depth / maxStack tracking owned by the builder.
//
// Each BasicBlock *is* a BlobBuilder (its regular IL bytes are its own buffer);
// the final IL stream is assembled by appending each block's terminator to its
// buffer and then copying the blocks into one output builder at materialization
// time. (Ideally the blocks would be chained zero-copy via BlobBuilder.LinkSuffix,
// but that has an upstream bug when linking into an empty destination —
// dotnet/runtime#127246 — so we copy for now.)
//
// Realize() produces a RelocatableInstructionEncoder-compatible bundle with final
// bytes / relocations / EH table / line numbers so the COFF serialization layer
// (RelocatableMethodBodyStreamEncoder.AddMethodBody) is reused unchanged.
public sealed class ILBuilder
{
    internal enum Reachability : byte
    {
        NotReachable = 0,
        Reachable = 1,
    }

    // ── Basic block ─────────────────────────────────────────────────────────

    internal class BasicBlock : BlobBuilder
    {
        public BasicBlock(ILBuilder builder, int capacity = 16) : base(capacity)
        {
            Builder = builder;
            EnclosingHandler = builder._currentHandler;
        }

        public readonly ILBuilder Builder;

        // Next block in program order (not necessarily reachable).
        public BasicBlock NextBlock;

        // Terminating branch. Nop means "fall through to NextBlock".
        public ILOpCode BranchCode = ILOpCode.Nop;

        // Target label id of the branch (0 = none).
        public int BranchLabelId;

        // Reverse opcode supplied for the branch-inversion peephole (Nop = none).
        public ILOpCode RevBranchCode = ILOpCode.Nop;

        // Final method-relative start offset (assigned during realization).
        public int Start;

        public Reachability Reachability;

        // The exception handler region this block lives inside, or null.
        public readonly EHRegion EnclosingHandler;

        // Block-relative token relocations: (offsetWithinRegularBytes, token).
        public List<(int offset, int token)> Relocations;

        // Block-relative sequence points: (offsetWithinRegularBytes, file, line).
        public List<(int offset, CodeViewFileHandle file, int line)> Lines;

        public int RegularLength => Count;

        public bool HasNoRegularInstructions => Count == 0;

        public BasicBlock BranchBlock =>
            BranchLabelId == 0 ? null : Builder._labels[BranchLabelId - 1].Block;

        public bool IsBranchToLabel => BranchLabelId != 0 && BranchCode != ILOpCode.Nop;

        public virtual int TotalSize => RegularLength + BranchCode switch
        {
            ILOpCode.Nop => 0,
            ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Endfinally => 1,
            ILOpCode.Rethrow or ILOpCode.Endfilter => 2,
            _ => 1 + BranchCode.GetBranchOperandSize(),
        };

        public void SetBranch(int newLabelId, ILOpCode branchCode, ILOpCode revBranchCode = ILOpCode.Nop)
        {
            BranchCode = branchCode;
            BranchLabelId = newLabelId;
            RevBranchCode = revBranchCode;
        }

        public void SetBranchCode(ILOpCode newBranchCode)
        {
            Debug.Assert(BranchCode.IsConditionalBranch() == newBranchCode.IsConditionalBranch());
            BranchCode = newBranchCode;
        }

        internal void AdjustForDelta(int delta)
        {
            Debug.Assert(delta <= 0);
            if (delta != 0)
                Start += delta;
        }

        // Pessimistic branch shortening (ported from Roslyn BasicBlock.ShortenBranches).
        internal void ShortenBranches(ref int delta)
        {
            if (!IsBranchToLabel)
                return;

            ILOpCode cur = BranchCode;
            if (cur.GetBranchOperandSize() == 1)
                return; // already short

            const int reduction = -3;

            int offset;
            int branchBlockStart = BranchBlock.Start;
            if (branchBlockStart > Start)
            {
                // forward branch: delta applies to both sides, cancels out
                offset = branchBlockStart - NextBlock.Start;
            }
            else
            {
                // backward branch: account for the tentative reduction of this block
                offset = branchBlockStart - (Start + TotalSize + reduction);
            }

            if (unchecked((sbyte)offset == offset))
            {
                SetBranchCode(cur.GetShortBranch());
                delta += reduction;
            }
        }

        internal void RewriteBranchAcrossExceptionHandler()
        {
            BasicBlock branchBlock = BranchBlock;
            if (branchBlock == null)
                return;

            if (branchBlock.EnclosingHandler != EnclosingHandler)
            {
                // Crossing an EH boundary requires leave; only unconditional br can become leave.
                if (BranchCode == ILOpCode.Br || BranchCode == ILOpCode.Br_s)
                    SetBranchCode(BranchCode.GetLeaveOpcode());
            }
        }

        // ── Branch peepholes (Release) ──────────────────────────────────────

        private BasicBlock NextNontrivial
        {
            get
            {
                var next = NextBlock;
                while (next != null && next.BranchCode == ILOpCode.Nop && next.HasNoRegularInstructions)
                    next = next.NextBlock;
                return next;
            }
        }

        internal bool OptimizeBranches(ref int delta)
        {
            if (IsBranchToLabel)
            {
                var next = NextNontrivial;
                if (next != null)
                {
                    if (TryOptimizeSameAsNext(next, ref delta)) return true;
                    if (TryOptimizeBranchToNextOrRet(next, ref delta)) return true;
                    if (TryOptimizeBranchOverUncondBranch(next, ref delta)) return true;
                    if (TryOptimizeBranchToEquivalent(next, ref delta)) return true;
                }
            }
            return false;
        }

        private bool TryOptimizeSameAsNext(BasicBlock next, ref int delta)
        {
            if (next.HasNoRegularInstructions &&
                next.BranchCode == BranchCode &&
                next.BranchBlock != null && BranchBlock != null &&
                next.BranchBlock.Start == BranchBlock.Start &&
                next.EnclosingHandler == EnclosingHandler)
            {
                int diff = BranchCode.Size() + BranchCode.GetBranchOperandSize();
                delta -= diff;
                SetBranch(0, ILOpCode.Nop);

                // If this block became trivial, redirect labels pointing here to next.
                if (HasNoRegularInstructions)
                {
                    var labels = Builder._labels;
                    for (int i = 0; i < labels.Count; i++)
                    {
                        if (labels[i].Block == this)
                        {
                            var li = labels[i];
                            li.Block = next;
                            labels[i] = li;
                        }
                    }
                }
                return true;
            }
            return false;
        }

        private bool TryOptimizeBranchToNextOrRet(BasicBlock next, ref int delta)
        {
            ILOpCode cur = BranchCode;
            if (cur == ILOpCode.Br || cur == ILOpCode.Br_s)
            {
                // branch to next block
                if (BranchBlock.Start - next.Start == 0 && next.EnclosingHandler == EnclosingHandler)
                {
                    SetBranch(0, ILOpCode.Nop);
                    delta -= cur.Size() + cur.GetBranchOperandSize();
                    return true;
                }

                // branch to a lone ret block
                if (BranchBlock.HasNoRegularInstructions && BranchBlock.BranchCode == ILOpCode.Ret)
                {
                    SetBranch(0, ILOpCode.Ret);
                    delta -= cur.Size() + cur.GetBranchOperandSize() - 1;
                    return true;
                }
            }
            return false;
        }

        private bool TryOptimizeBranchOverUncondBranch(BasicBlock next, ref int delta)
        {
            if (next.HasNoRegularInstructions &&
                next.NextBlock != null &&
                BranchBlock != null &&
                next.NextBlock.Start == BranchBlock.Start &&
                (next.BranchCode == ILOpCode.Br || next.BranchCode == ILOpCode.Br_s) &&
                next.BranchBlock != next &&
                next.EnclosingHandler == EnclosingHandler)
            {
                ILOpCode revBrOp = GetReversedBranchOp();
                if (revBrOp != ILOpCode.Nop)
                {
                    // The blocks between this and BranchBlock (including next) are removed.
                    // This is only safe if no label still points at them (otherwise a branch
                    // elsewhere would dangle). OptimizeLabels normally forwards such labels,
                    // but guard here defensively.
                    for (var probe = NextBlock; probe != BranchBlock; probe = probe.NextBlock)
                    {
                        if (Builder.AnyLabelPointsTo(probe))
                            return false;
                    }

                    var toRemove = NextBlock;
                    var branchBlock = BranchBlock;
                    while (toRemove != branchBlock)
                    {
                        toRemove.Reachability = Reachability.NotReachable;
                        toRemove = toRemove.NextBlock;
                    }

                    next.Reachability = Reachability.NotReachable;
                    delta -= next.TotalSize;

                    if (next.BranchCode == ILOpCode.Br_s)
                        revBrOp = revBrOp.GetShortBranch();

                    NextBlock = branchBlock;

                    ILOpCode origBrOp = BranchCode;
                    SetBranch(next.BranchLabelId, revBrOp, origBrOp);
                    return true;
                }
            }
            return false;
        }

        private bool TryOptimizeBranchToEquivalent(BasicBlock next, ref int delta)
        {
            ILOpCode cur = BranchCode;
            if (cur.IsConditionalBranch() && next.EnclosingHandler == EnclosingHandler)
            {
                if ((BranchBlock != null && BranchBlock.Start - next.Start == 0) ||
                    AreIdentical(BranchBlock, next))
                {
                    SetBranch(0, ILOpCode.Nop);
                    WriteByte((byte)ILOpCode.Pop);
                    delta -= cur.Size() + cur.GetBranchOperandSize() - 1;

                    if (cur.IsRelationalBranch())
                    {
                        WriteByte((byte)ILOpCode.Pop);
                        delta += 1;
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool AreIdentical(BasicBlock one, BasicBlock another)
        {
            if (one == null || another == null)
                return false;
            if (one.BranchCode == another.BranchCode &&
                !one.BranchCode.CanFallThrough() &&
                one.BranchLabelId == another.BranchLabelId)
            {
                return one.ContentEquals(another);
            }
            return false;
        }

        private ILOpCode GetReversedBranchOp()
        {
            if (RevBranchCode != ILOpCode.Nop)
                return RevBranchCode;

            // For some instructions the reverse can be inferred unambiguously; for
            // relational ones it depends on whether the operands were float or
            // integer (we cannot tell here), so leave those to a caller-supplied
            // RevBranchCode and return Nop ("no safe reverse").
            return BranchCode switch
            {
                ILOpCode.Brfalse or ILOpCode.Brfalse_s => ILOpCode.Brtrue,
                ILOpCode.Brtrue or ILOpCode.Brtrue_s => ILOpCode.Brfalse,
                ILOpCode.Beq or ILOpCode.Beq_s => ILOpCode.Bne_un,
                ILOpCode.Bne_un or ILOpCode.Bne_un_s => ILOpCode.Beq,
                _ => ILOpCode.Nop,
            };
        }
    }

    // ── Exception handler region (dormant: API + serialization, no CodeGen wiring) ──

    internal sealed class EHRegion
    {
        public ExceptionRegionKind Kind;
        public int TryStart, TryEnd, HandlerStart, HandlerEnd, FilterStart; // label ids
        public EntityHandle CatchType;
    }

    // ── Local variable scope (dormant) ──────────────────────────────────────

    private sealed class ScopeInfo
    {
        public readonly ScopeInfo Parent;
        public List<BasicBlock> Blocks;
        public List<CodeViewManSlot> Locals;
        public List<ScopeInfo> Children;

        public ScopeInfo(ScopeInfo parent) => Parent = parent;

        public void AddBlock(BasicBlock b) => (Blocks ??= new()).Add(b);
        public void AddLocal(CodeViewManSlot s) => (Locals ??= new()).Add(s);
        public void AddChild(ScopeInfo s) => (Children ??= new()).Add(s);
    }

    // ── Label state ─────────────────────────────────────────────────────────

    private struct LabelInfo
    {
        public BasicBlock Block;
        public int Stack;
        public bool HasStack;
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private readonly List<LabelInfo> _labels = new();
    private BasicBlock _leaderBlock;
    private BasicBlock _currentBlock;
    private BasicBlock _lastCompleteBlock;

    private int _curStack;
    private int _maxStack;

    private readonly List<EHRegion> _ehRegions = new();
    private EHRegion _currentHandler;

    private ScopeInfo _rootScope;
    private ScopeInfo _currentScope;

    private bool _realized;

    private readonly bool _optimize;

    public ILBuilder(bool optimize = false)
    {
        _optimize = optimize;
        _rootScope = new ScopeInfo(null);
        _currentScope = _rootScope;
    }

    public int MaxStack => _maxStack;
    public int StackDepth => _curStack;

    // ── Stack tracking ──────────────────────────────────────────────────────

    public void AdjustStack(int delta)
    {
        _curStack += delta;
        Debug.Assert(_curStack >= 0, "stack underflow");
        if (_curStack > _maxStack)
            _maxStack = _curStack;
    }

    // ── Block lifecycle ─────────────────────────────────────────────────────

    private BasicBlock GetCurrentBlock()
    {
        if (_currentBlock == null)
            CreateBlock();
        return _currentBlock;
    }

    private void CreateBlock()
    {
        var block = new BasicBlock(this);
        if (_leaderBlock == null)
            _leaderBlock = block;
        else
            _lastCompleteBlock.NextBlock = block;

        _currentBlock = block;
        _currentScope.AddBlock(block);
    }

    private void EndBlock()
    {
        if (_currentBlock != null)
        {
            _lastCompleteBlock = _currentBlock;
            _currentBlock = null;
        }
    }

    /// <summary>
    /// True if the current point can fall through (i.e. the current block is open
    /// and unterminated). Replaces CodeGen's _lastTerminalOffset hack.
    /// </summary>
    public bool IsReachable => _currentBlock != null;

    // ── Labels ──────────────────────────────────────────────────────────────

    public LabelHandle DefineLabel()
    {
        _labels.Add(default);
        return MakeLabel(_labels.Count);
    }

    public void MarkLabel(LabelHandle label)
    {
        // Terminate any open block; the label starts a new block at offset 0.
        BasicBlock ended = _currentBlock;
        EndBlock();
        var block = GetCurrentBlock();

        int idx = label.Id - 1;
        LabelInfo info = _labels[idx];

        bool reachedViaFallThrough = ended != null && ended.BranchCode.CanFallThrough();

        if (info.HasStack)
        {
            // A branch to this label already fixed its expected depth.
            Debug.Assert(!reachedViaFallThrough || info.Stack == _curStack,
                "forward branches and fall-through must agree on stack depth");
            _curStack = info.Stack;
        }
        else
        {
            info.HasStack = true;
            info.Stack = _curStack;
        }
        info.Block = block;
        _labels[idx] = info;
    }

    private void RecordLabelStack(LabelHandle label)
    {
        int idx = label.Id - 1;
        LabelInfo info = _labels[idx];
        if (info.HasStack)
        {
            Debug.Assert(info.Stack == _curStack, "branches to same label with different stacks");
        }
        else
        {
            info.HasStack = true;
            info.Stack = _curStack;
            _labels[idx] = info;
        }
    }

    private static unsafe LabelHandle MakeLabel(int id)
    {
        return *(LabelHandle*)&id;
    }

    // ── Emit: non-terminator opcodes ────────────────────────────────────────

    private static void WriteOpCode(BlobBuilder b, ILOpCode code)
    {
        if (code.Size() == 1)
        {
            b.WriteByte((byte)code);
        }
        else
        {
            b.WriteByte((byte)((ushort)code >> 8));
            b.WriteByte((byte)((ushort)code & 0xff));
        }
    }

    public void OpCode(ILOpCode code)
    {
        Debug.Assert(!code.IsControlTransfer(),
            "control-transfer opcodes must be emitted via Branch/EmitRet/Switch");
        AdjustStack(code.NetStackBehavior());
        WriteOpCode(GetCurrentBlock(), code);
    }

    /// <summary>Writes a raw operand byte (e.g. the alignment after an unaligned. prefix).</summary>
    public void WriteByte(byte value)
    {
        GetCurrentBlock().WriteByte(value);
    }

    public void Token(EntityHandle handle) => Token(MetadataTokens.GetToken(handle));

    public void Token(int token)
    {
        var block = GetCurrentBlock();
        (block.Relocations ??= new()).Add((block.Count, token));
        block.WriteInt32(0);
    }

    public void LoadString(UserStringHandle handle)
    {
        OpCode(ILOpCode.Ldstr);
        Token(MetadataTokens.GetToken(handle));
    }

    // Variable-stack-behavior opcodes take an explicit net stack adjustment.
    public void Call(EntityHandle methodHandle, int stackAdjustment)
    {
        AdjustStack(stackAdjustment);
        WriteOpCode(GetCurrentBlock(), ILOpCode.Call);
        Token(methodHandle);
    }

    public void CallIndirect(StandaloneSignatureHandle signature, int stackAdjustment)
    {
        AdjustStack(stackAdjustment);
        WriteOpCode(GetCurrentBlock(), ILOpCode.Calli);
        Token(MetadataTokens.GetToken(signature));
    }

    public void LoadConstantI4(int value)
    {
        if (value is >= -1 and <= 8)
        {
            // Ldc_i4_m1 (0x15) through Ldc_i4_8 (0x1E) are contiguous, so the value
            // -1..8 maps directly onto the compact opcode.
            OpCode((ILOpCode)((int)ILOpCode.Ldc_i4_0 + value));
        }
        else if (unchecked((sbyte)value) == value)
        {
            OpCode(ILOpCode.Ldc_i4_s);
            GetCurrentBlock().WriteSByte((sbyte)value);
        }
        else
        {
            OpCode(ILOpCode.Ldc_i4);
            GetCurrentBlock().WriteInt32(value);
        }
    }

    public void LoadConstantI8(long value)
    {
        // A long that fits in 32 bits is cheaper as ldc.i4(.s) + conv: 2-6 bytes
        // vs. a 9-byte ldc.i8. (Matches Roslyn's EmitLongConstant.)
        if (value is >= int.MinValue and <= int.MaxValue)
        {
            LoadConstantI4((int)value);
            OpCode(ILOpCode.Conv_i8);
        }
        else if (value is >= uint.MinValue and <= uint.MaxValue)
        {
            LoadConstantI4(unchecked((int)value));
            OpCode(ILOpCode.Conv_u8);
        }
        else
        {
            OpCode(ILOpCode.Ldc_i8);
            GetCurrentBlock().WriteInt64(value);
        }
    }

    public void LoadConstantR4(float value)
    {
        OpCode(ILOpCode.Ldc_r4);
        GetCurrentBlock().WriteSingle(value);
    }

    public void LoadConstantR8(double value)
    {
        OpCode(ILOpCode.Ldc_r8);
        GetCurrentBlock().WriteDouble(value);
    }

    public void LoadLocal(int slotIndex) => EmitVarSlot(slotIndex, ILOpCode.Ldloc_0, ILOpCode.Ldloc_s, ILOpCode.Ldloc);

    public void StoreLocal(int slotIndex) => EmitVarSlot(slotIndex, ILOpCode.Stloc_0, ILOpCode.Stloc_s, ILOpCode.Stloc);

    public void LoadArgument(int argIndex) => EmitVarSlot(argIndex, ILOpCode.Ldarg_0, ILOpCode.Ldarg_s, ILOpCode.Ldarg);

    // ldloca/starg/ldarga have no compact _0.._3 forms, only short + wide.
    public void LoadLocalAddress(int slotIndex) => EmitVarSlotWide(slotIndex, ILOpCode.Ldloca_s, ILOpCode.Ldloca);

    public void StoreArgument(int argIndex) => EmitVarSlotWide(argIndex, ILOpCode.Starg_s, ILOpCode.Starg);

    public void LoadArgumentAddress(int argIndex) => EmitVarSlotWide(argIndex, ILOpCode.Ldarga_s, ILOpCode.Ldarga);

    // Emits a local/argument access. Slots 0..3 use the compact single-byte forms,
    // which are contiguous in the opcode table (op0 + slot), so no per-slot switch
    // is needed; larger slots fall back to the short/wide operand forms.
    private void EmitVarSlot(int slot, ILOpCode op0, ILOpCode opShort, ILOpCode opWide)
    {
        if (slot <= 3)
            OpCode((ILOpCode)((int)op0 + slot));
        else
            EmitVarSlotWide(slot, opShort, opWide);
    }

    private void EmitVarSlotWide(int slot, ILOpCode opShort, ILOpCode opWide)
    {
        if ((uint)slot <= byte.MaxValue)
        {
            OpCode(opShort);
            GetCurrentBlock().WriteByte((byte)slot);
        }
        else
        {
            OpCode(opWide);
            GetCurrentBlock().WriteUInt16((ushort)slot);
        }
    }

    // ── Emit: terminators ───────────────────────────────────────────────────

    public void Branch(ILOpCode code, LabelHandle label, ILOpCode reverseCode = ILOpCode.Nop)
    {
        Debug.Assert(code.IsBranch());
        AdjustStack(code.NetStackBehavior());
        RecordLabelStack(label);
        var block = GetCurrentBlock();
        block.SetBranch(label.Id, code, reverseCode);
        EndBlock();
    }

    public void Switch(LabelHandle[] labels)
    {
        if (labels == null || labels.Length == 0)
            throw new ArgumentException("Switch requires at least one label.", nameof(labels));

        AdjustStack(-1);
        foreach (var l in labels)
            RecordLabelStack(l);

        // The value-computing instructions stay in the (now ended) current block,
        // which falls through into a dedicated switch block.
        EndBlock();
        var block = new SwitchBlock(this, labels);
        if (_leaderBlock == null)
            _leaderBlock = block;
        else
            _lastCompleteBlock.NextBlock = block;

        _currentScope.AddBlock(block);
        _lastCompleteBlock = block;
        _currentBlock = null;
    }

    /// <summary>Emits ret. Pops the (0 or 1) value currently on the stack.</summary>
    public void EmitRet()
    {
        Debug.Assert(_curStack <= 1, "ret with extra values on the stack");
        _curStack = 0;
        var block = GetCurrentBlock();
        block.BranchCode = ILOpCode.Ret;
        EndBlock();
    }

    // ── Line numbers (sequence points) ──────────────────────────────────────

    public void MarkLineNumber(CodeViewFileHandle file, int line)
    {
        var block = GetCurrentBlock();
        (block.Lines ??= new()).Add((block.Count, file, line));
    }

    // ── Exception handling (dormant) ────────────────────────────────────────

    public void AddCatchRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd, EntityHandle catchType)
        => _ehRegions.Add(new EHRegion
        {
            Kind = ExceptionRegionKind.Catch,
            TryStart = tryStart.Id,
            TryEnd = tryEnd.Id,
            HandlerStart = handlerStart.Id,
            HandlerEnd = handlerEnd.Id,
            CatchType = catchType,
        });

    public void AddFinallyRegion(LabelHandle tryStart, LabelHandle tryEnd, LabelHandle handlerStart, LabelHandle handlerEnd)
        => _ehRegions.Add(new EHRegion
        {
            Kind = ExceptionRegionKind.Finally,
            TryStart = tryStart.Id,
            TryEnd = tryEnd.Id,
            HandlerStart = handlerStart.Id,
            HandlerEnd = handlerEnd.Id,
        });

    // ── Local scopes (dormant) ──────────────────────────────────────────────

    public void OpenLocalScope()
    {
        var scope = new ScopeInfo(_currentScope);
        _currentScope.AddChild(scope);
        _currentScope = scope;
    }

    public void CloseLocalScope()
    {
        Debug.Assert(_currentScope.Parent != null, "unbalanced CloseLocalScope");
        _currentScope = _currentScope.Parent;
    }

    public void AddLocalToScope(CodeViewManSlot slot) => _currentScope.AddLocal(slot);

    // ── Realization ─────────────────────────────────────────────────────────

    public RealizedMethod Realize()
    {
        Debug.Assert(!_realized, "Realize called twice");
        _realized = true;

        // A realizable method body must contain at least a terminator. CodeGen always
        // emits one (a typed-zero + ret for non-void returns), so an empty builder is
        // a usage error rather than something to paper over with a possibly-invalid ret.
        if (_leaderBlock == null)
            throw new InvalidOperationException(
                "Cannot realize an ILBuilder with no emitted instructions; the body must end with a terminator.");

        // 1) Initial DCE.
        MarkReachableBlocks();
        DropUnreachableBlocks();

        // 1b) Forward labels through trivial blocks, then re-run DCE. This keeps the
        //     branch peepholes' invariant that nothing branches to a removable
        //     trivial block (ported from Roslyn ILBuilder.OptimizeLabels). Like
        //     Roslyn, this is an optimization-only transform.
        if (_optimize && OptimizeLabels())
        {
            MarkAllBlocksUnreachable();
            MarkReachableBlocks();
            DropUnreachableBlocks();
        }

        // 2) br -> leave across EH boundaries (before offsets/shortening).
        if (_ehRegions.Count > 0)
        {
            for (var b = _leaderBlock; b != null; b = b.NextBlock)
                b.RewriteBranchAcrossExceptionHandler();
        }

        // 3) Offsets + shortening + peepholes, re-running DCE while blocks drop.
        while (ComputeOffsetsAndAdjustBranches())
        {
            MarkAllBlocksUnreachable();
            MarkReachableBlocks();
            if (!DropUnreachableBlocks())
                break;
        }

        // 4) Materialize: append each block's terminator into its own buffer, then
        // copy it into the output stream.
        var code = new BlobBuilder();
        var relocBuilder = new MethodRelocationBuilder();
        var lineBuilder = new CodeViewLineNumberBuilder();
        int lastLineOffset = -1;

        for (var block = _leaderBlock; block != null; block = block.NextBlock)
        {
            Debug.Assert(code.Count == block.Start, "block layout offset mismatch");

            int blockStart = block.Start;

            if (block.Relocations != null)
                foreach (var (offset, token) in block.Relocations)
                    relocBuilder.AddTokenRelocation(blockStart + offset, token);

            if (block.Lines != null)
                foreach (var (offset, file, line) in block.Lines)
                {
                    int abs = blockStart + offset;
                    if (abs == lastLineOffset)
                        continue; // de-dup consecutive points at the same offset
                    lineBuilder.AddLineNumber(file, abs, line);
                    lastLineOffset = abs;
                }

            // Append the terminator to the block's own buffer, then copy it into
            // the output. We would prefer the zero-copy `code.LinkSuffix(block)`,
            // but BlobBuilder.LinkSuffix drops content when linking a multi-chunk
            // suffix into an *empty* destination (the leader block hits this on the
            // first iteration). Fixed upstream in dotnet/runtime#127246 but not yet
            // in the SRM we build against, so copy instead.
            AppendTerminator(block);
            block.WriteContentTo(code);
        }

        // EH table: only build the control-flow builder when EH regions exist.
        RelocatableControlFlowBuilder flow = null;
        if (_ehRegions.Count > 0)
            flow = BuildExceptionFlow();

        var enc = new RelocatableInstructionEncoder(code, relocBuilder, flow, lineBuilder);
        var scopes = BuildLocalScopes();
        return new RealizedMethod(enc, _maxStack, scopes);
    }

    private void AppendTerminator(BasicBlock block)
    {
        switch (block.BranchCode)
        {
            case ILOpCode.Nop:
                return; // fall-through block; nothing to append (already Nop)

            case ILOpCode.Ret:
            case ILOpCode.Throw:
            case ILOpCode.Endfinally:
            case ILOpCode.Rethrow:
            case ILOpCode.Endfilter:
                WriteOpCode(block, block.BranchCode);
                break;

            default:
                if (block is SwitchBlock sw)
                {
                    WriteOpCode(block, ILOpCode.Switch);
                    block.WriteInt32(sw.BranchLabelIds.Length);
                    int switchEnd = block.Start + block.TotalSize;
                    foreach (int labelId in sw.BranchLabelIds)
                        block.WriteInt32(_labels[labelId - 1].Block.Start - switchEnd);
                }
                else
                {
                    // Regular branch: write opcode + relative operand.
                    ILOpCode code = block.BranchCode;
                    int operandSize = code.GetBranchOperandSize();
                    int instrSize = 1 + operandSize;
                    int distance = block.BranchBlock.Start - (block.Start + block.RegularLength + instrSize);
                    WriteOpCode(block, code);
                    if (operandSize == 1)
                    {
                        Debug.Assert(unchecked((sbyte)distance) == distance);
                        block.WriteSByte((sbyte)distance);
                    }
                    else
                    {
                        block.WriteInt32(distance);
                    }
                }
                break;
        }

        // The terminator is now part of the block's byte buffer. Clear the separate
        // BranchCode so TotalSize == Count (the materialized size) instead of adding
        // the terminator length a second time.
        block.BranchCode = ILOpCode.Nop;
    }

    internal bool AnyLabelPointsTo(BasicBlock block)
    {
        foreach (var info in _labels)
            if (info.Block == block)
                return true;
        return false;
    }

    // Forward each label that targets a trivial block (no regular instructions that
    // either falls through or unconditionally branches) to its ultimate destination.
    private bool OptimizeLabels()
    {
        bool changed = false;
        for (int i = 0; i < _labels.Count; i++)
        {
            LabelInfo info = _labels[i];
            BasicBlock block = info.Block;
            if (block == null)
                continue;

            BasicBlock orig = block;
            var seen = new HashSet<BasicBlock>();
            while (block != null && block.HasNoRegularInstructions && seen.Add(block))
            {
                if (block.BranchCode == ILOpCode.Nop)
                {
                    BasicBlock next = block.NextBlock;
                    if (next == null || next.EnclosingHandler != block.EnclosingHandler)
                        break;
                    block = next;
                }
                else if ((block.BranchCode == ILOpCode.Br || block.BranchCode == ILOpCode.Br_s) &&
                         block.BranchBlock != null && block.BranchBlock != block &&
                         block.BranchBlock.EnclosingHandler == block.EnclosingHandler)
                {
                    block = block.BranchBlock;
                }
                else
                {
                    break;
                }
            }

            if (block != null && block != orig)
            {
                info.Block = block;
                _labels[i] = info;
                changed = true;
            }
        }
        return changed;
    }

    // ── Reachability / DCE ──────────────────────────────────────────────────

    private void MarkAllBlocksUnreachable()
    {
        for (var b = _leaderBlock; b != null; b = b.NextBlock)
            b.Reachability = Reachability.NotReachable;
    }

    private void MarkReachableBlocks()
    {
        var stack = new Stack<BasicBlock>();
        MarkReachableFrom(stack, _leaderBlock);

        // EH handlers are reachable via exception dispatch, not normal CFG edges.
        foreach (var r in _ehRegions)
        {
            MarkReachableFrom(stack, _labels[r.HandlerStart - 1].Block);
            if (r.Kind == ExceptionRegionKind.Filter && r.FilterStart != 0)
                MarkReachableFrom(stack, _labels[r.FilterStart - 1].Block);
        }

        while (stack.Count != 0)
            MarkReachableFrom(stack, stack.Pop());
    }

    private void MarkReachableFrom(Stack<BasicBlock> stack, BasicBlock block)
    {
    tryAgain:
        if (block == null || block.Reachability != Reachability.NotReachable)
            return;

        block.Reachability = Reachability.Reachable;

        ILOpCode branchCode = block.BranchCode;
        if (branchCode == ILOpCode.Nop)
        {
            block = block.NextBlock;
            goto tryAgain;
        }

        if (branchCode.CanFallThrough())
            Push(stack, block.NextBlock);

        if (block is SwitchBlock sw)
        {
            foreach (int labelId in sw.BranchLabelIds)
                Push(stack, _labels[labelId - 1].Block);
        }
        else
        {
            Push(stack, block.BranchBlock);
        }
    }

    private static void Push(Stack<BasicBlock> stack, BasicBlock block)
    {
        if (block != null && block.Reachability == Reachability.NotReachable)
            stack.Push(block);
    }

    private bool DropUnreachableBlocks()
    {
        bool dropped = false;
        var current = _leaderBlock;
        while (current != null && current.NextBlock != null)
        {
            if (current.NextBlock.Reachability == Reachability.NotReachable)
            {
                current.NextBlock = current.NextBlock.NextBlock;
                dropped = true;
            }
            else
            {
                current = current.NextBlock;
            }
        }
        return dropped;
    }

    // ── Offsets + shortening ────────────────────────────────────────────────

    private bool ComputeOffsetsAndAdjustBranches()
    {
        // Forward pass: assign Start = prev.Start + prev.TotalSize.
        int pos = 0;
        for (var b = _leaderBlock; b != null; b = b.NextBlock)
        {
            b.Start = pos;
            pos += b.TotalSize;
        }

        bool branchesOptimized = false;
        int delta;
        int guard = 0;
        do
        {
            Debug.Assert(guard++ < 1_000_000, "branch shortening failed to converge");
            delta = 0;
            for (var b = _leaderBlock; b != null; b = b.NextBlock)
            {
                b.AdjustForDelta(delta);
                if (_optimize)
                    branchesOptimized |= b.OptimizeBranches(ref delta);
                b.ShortenBranches(ref delta);
            }
        } while (delta < 0);

        return branchesOptimized;
    }

    // ── EH table ────────────────────────────────────────────────────────────

    private RelocatableControlFlowBuilder BuildExceptionFlow()
    {
        var flow = new RelocatableControlFlowBuilder();

        // Re-create labels in the flow builder at final offsets.
        var map = new Dictionary<int, LabelHandle>();
        LabelHandle FlowLabel(int id)
        {
            if (!map.TryGetValue(id, out var lh))
            {
                lh = flow.AddLabel();
                flow.MarkLabel(_labels[id - 1].Block.Start, lh);
                map[id] = lh;
            }
            return lh;
        }

        foreach (var r in _ehRegions)
        {
            switch (r.Kind)
            {
                case ExceptionRegionKind.Catch:
                    flow.AddCatchRegion(FlowLabel(r.TryStart), FlowLabel(r.TryEnd),
                        FlowLabel(r.HandlerStart), FlowLabel(r.HandlerEnd), r.CatchType);
                    break;
                case ExceptionRegionKind.Finally:
                    flow.AddFinallyRegion(FlowLabel(r.TryStart), FlowLabel(r.TryEnd),
                        FlowLabel(r.HandlerStart), FlowLabel(r.HandlerEnd));
                    break;
            }
        }

        return flow;
    }

    // ── Local scope tree ────────────────────────────────────────────────────

    private List<CodeViewLocalScope> BuildLocalScopes()
    {
        var result = new List<CodeViewLocalScope>();
        foreach (var child in _rootScope.Children ?? new List<ScopeInfo>())
            EmitScope(child, result);
        return result.Count > 0 ? result : null;
    }

    private static (int begin, int end) EmitScope(ScopeInfo scope, List<CodeViewLocalScope> output)
    {
        int begin = int.MaxValue;
        int end = 0;

        if (scope.Blocks != null)
            foreach (var b in scope.Blocks)
            {
                if (b.Reachability != Reachability.NotReachable)
                {
                    begin = Math.Min(begin, b.Start);
                    end = Math.Max(end, b.Start + b.TotalSize);
                }
            }

        var childScopes = new List<CodeViewLocalScope>();
        if (scope.Children != null)
            foreach (var child in scope.Children)
            {
                var (cb, ce) = EmitScope(child, childScopes);
                if (ce > cb)
                {
                    begin = Math.Min(begin, cb);
                    end = Math.Max(end, ce);
                }
            }

        if (scope.Locals != null && end > begin)
        {
            var cv = new CodeViewLocalScope { CodeOffset = begin, CodeLength = end - begin };
            cv.Slots.AddRange(scope.Locals);
            cv.Children.AddRange(childScopes);
            output.Add(cv);
        }
        else
        {
            // No locals here: surface child scopes to the parent.
            output.AddRange(childScopes);
        }

        return (begin, end);
    }

    // ── Switch block ────────────────────────────────────────────────────────

    internal sealed class SwitchBlock : BasicBlock
    {
        public readonly int[] BranchLabelIds;

        public SwitchBlock(ILBuilder builder, LabelHandle[] labels) : base(builder)
        {
            BranchCode = ILOpCode.Switch;
            BranchLabelIds = new int[labels.Length];
            for (int i = 0; i < labels.Length; i++)
                BranchLabelIds[i] = labels[i].Id;
        }

        // While the switch terminator is still pending, its size is opcode + count +
        // one int32 displacement per case. After AppendTerminator writes those bytes
        // and clears BranchCode to Nop, fall back to base (TotalSize == Count).
        public override int TotalSize => BranchCode == ILOpCode.Switch
            ? RegularLength + 5 + 4 * BranchLabelIds.Length
            : base.TotalSize;
    }
}

/// <summary>
/// Output of <see cref="ILBuilder.Realize"/>: a legacy-encoder-compatible bundle
/// (final IL bytes + relocations + EH table + line numbers) plus maxStack and the
/// optional local-scope tree.
/// </summary>
public readonly struct RealizedMethod
{
    public readonly RelocatableInstructionEncoder Instructions;
    public readonly int MaxStack;
    public readonly List<CodeViewLocalScope> LocalScopes;

    public RealizedMethod(RelocatableInstructionEncoder instructions, int maxStack, List<CodeViewLocalScope> localScopes)
    {
        Instructions = instructions;
        MaxStack = maxStack;
        LocalScopes = localScopes;
    }
}
