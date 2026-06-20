using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class CodeGen
{
    private readonly TypeSystem _types;
    private readonly MsilObjectEmitter _emit;
    private readonly CodeViewFileHandle _cvFile;
    private RelocatableInstructionEncoder _enc;
    private readonly Obj _currentFn;
    private readonly Dictionary<Obj, int> _localSlots;
    private readonly Dictionary<Obj, int> _paramSlots;
    private readonly List<(CType ty, int slot)> _scratchLocals;
    private readonly int _scratchLocalBase;
    private int _maxStack, _stackDepth;
    private readonly LabelHandle[] _labels;

    private void Push() { _stackDepth++; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Push(int n) { _stackDepth += n; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Pop() { Debug.Assert(_stackDepth > 0, "stack underflow"); _stackDepth--; }
    private void Pop(int n) { Debug.Assert(_stackDepth >= n, "stack underflow"); _stackDepth -= n; }

    public CodeGen(TypeSystem types, MsilObjectEmitter emit, Obj fn, CodeViewFileHandle cvFile)
    {
        _types = types;
        _emit = emit;
        _currentFn = fn;
        _cvFile = cvFile;
        _enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
        _localSlots = new Dictionary<Obj, int>();
        _paramSlots = new Dictionary<Obj, int>();
        _scratchLocals = new List<(CType, int)>();
        _labels = new LabelHandle[fn.LabelCount];
        for (int i = 0; i < _labels.Length; i++)
            _labels[i] = _enc.DefineLabel();

        // Assign parameter slots
        int argIdx = 0;
        for (Obj param = fn.Params; param != null; param = param.Next)
            _paramSlots[param] = argIdx++;

        // Assign local slots for user locals
        int localIdx = 0;
        for (Obj local = fn.Locals; local != null; local = local.Next)
        {
            if (local.IsLocal && !_paramSlots.ContainsKey(local))
            {
                _localSlots[local] = localIdx++;
            }
        }
        _scratchLocalBase = localIdx;
    }

    public static CompiledMethod EmitFunction(TypeSystem types, MsilObjectEmitter emit, Obj fn, CodeViewFileHandle cvFile)
    {
        return new CodeGen(types, emit, fn, cvFile).Emit();
    }

    private CompiledMethod Emit()
    {
        // Emit function body
        GenStmt(_currentFn.Body);

        CType returnTy = _currentFn.Ty.ReturnTy;
        if (returnTy.Kind != TypeKind.Void)
        {
            if (IsStructOrUnion(returnTy))
            {
                // For struct return, push a zeroed struct.
                int scratch = GetOrAddScratchLocal(returnTy);
                _enc.LoadLocalAddress(scratch); Push();
                _enc.OpCode(ILOpCode.Initobj); _enc.Token(_emit.GetStructTypeHandle(returnTy)); Pop();
                _enc.LoadLocal(scratch); Push();
            }
            else
            {
                EmitTypedZero(returnTy);
            }
        }
        _enc.OpCode(ILOpCode.Ret);

        // Build locals signature
        int totalLocals = _scratchLocalBase + _scratchLocals.Count;
        StandaloneSignatureHandle localsSig = default;
        if (totalLocals > 0)
        {
            var localsSigBlob = new BlobBuilder();
            localsSigBlob.WriteByte(0x07); // LOCAL_SIG
            localsSigBlob.WriteCompressedInteger(totalLocals);

            // User locals
            for (Obj local = _currentFn.Locals; local != null; local = local.Next)
            {
                if (_localSlots.ContainsKey(local))
                    _emit.EncodeType(localsSigBlob, _types.FlexibleAggregateStorageType(local.Ty));
            }

            // Scratch locals
            foreach (var (ty, _) in _scratchLocals)
                _emit.EncodeType(localsSigBlob, ty);

            localsSig = _emit.AddStandaloneSignature(localsSigBlob);
        }

        // Build CodeView local slot info
        var localSlotList = new List<CodeViewManSlot>();
        foreach (var (local, slot) in _localSlots)
        {
            if (local.Name != null && localsSig != default)
            {
                localSlotList.Add(new CodeViewManSlot(slot,
                    MetadataTokens.GetToken(localsSig), local.Name));
            }
        }

        return new CompiledMethod(_enc, _maxStack, localsSig, localSlotList.Count > 0 ? localSlotList.ToArray() : null);
    }

    private LabelHandle GetLabel(int label) => _labels[label - 1];

    private void GenExprDiscard(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Assign:
                GenAssign(node, wantValue: false);
                return;
            case NodeKind.Comma:
                GenExprDiscard(node.Lhs);
                GenExprDiscard(node.Rhs);
                return;
            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                    GenStmt(n);
                return;
            case NodeKind.Cast when node.Ty.Kind == TypeKind.Void:
                GenExprDiscard(node.Lhs);
                return;
        }

        int depthBefore = _stackDepth;
        GenExpr(node);
        while (_stackDepth > depthBefore)
        {
            _enc.OpCode(ILOpCode.Pop);
            Pop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scratch locals
    // ═══════════════════════════════════════════════════════════════

    private int GetOrAddScratchLocal(CType ty)
    {
        // Reuse existing scratch local of same type.
        // For struct/union/array, require exact type identity (TypeDef handle match)
        // since IL verifier requires assignment-compatible value types.
        foreach (var (existingTy, slot) in _scratchLocals)
        {
            if (existingTy.Kind == ty.Kind && existingTy.Size == ty.Size &&
                existingTy.IsUnsigned == ty.IsUnsigned)
            {
                // Struct/union/array: only reuse if same canonical type
                if (IsAggregateType(ty))
                {
                    if (_types.GetTypeId(existingTy) == _types.GetTypeId(ty))
                        return slot;
                    continue; // different struct type, keep looking
                }
                return slot;
            }
        }
        return AddFreshScratchLocal(ty);
    }

    private int AddFreshScratchLocal(CType ty)
    {
        int newSlot = _scratchLocalBase + _scratchLocals.Count;
        _scratchLocals.Add((ty, newSlot));
        return newSlot;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Address generation (GenAddr)
    // ═══════════════════════════════════════════════════════════════

    private void GenAddr(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.Ty.Kind == TypeKind.Vla)
                {
                    // VLA pointer — load the stored pointer
                    LoadLocalOrParam(node.Var);
                    return;
                }
                if (node.Var.IsFunction || node.Var.Ty.Kind == TypeKind.Func)
                {
                    // &func — emit function address (same as GenExpr Var for functions)
                    EmitFunctionAddress(node.Var);
                    return;
                }
                if (node.Var.IsLocal)
                {
                    if (_paramSlots.TryGetValue(node.Var, out int argIdx))
                    {
                        _enc.LoadArgumentAddress(argIdx); Push();
                    }
                    else if (_localSlots.TryGetValue(node.Var, out int localIdx))
                    {
                        _enc.LoadLocalAddress(localIdx); Push();
                    }
                    return;
                }
                // Global variable
                EntityHandle fieldDef = _emit.GetFieldToken(node.Var);
                _enc.OpCode(ILOpCode.Ldsflda); _enc.Token(fieldDef); Push();
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                return;

            case NodeKind.Comma:
                GenExprDiscard(node.Lhs);
                GenAddr(node.Rhs);
                return;

            case NodeKind.Member:
                switch (_emit.GetMemberAccessKind(node.Lhs.Ty, node.Member))
                {
                    case ManagedAggregateMemberAccessKind.OffsetAddress:
                        GenAddr(node.Lhs);
                        if (node.Member.Offset != 0)
                        {
                            EmitConstI4(node.Member.Offset);
                            _enc.OpCode(ILOpCode.Add);
                            Pop();
                        }
                        return;
                    case ManagedAggregateMemberAccessKind.MetadataField:
                        GenMetadataFieldOwnerAddress(node.Lhs);
                        _enc.OpCode(ILOpCode.Ldflda);
                        _enc.Token(_emit.GetAggregateFieldToken(node.Lhs.Ty, node.Member));
                        return;
                    default:
                        throw new UnreachableException();
                }

            case NodeKind.FunCall when IsStructOrUnion(node.Ty):
            case NodeKind.Assign when IsStructOrUnion(node.Ty):
            case NodeKind.Cond when IsStructOrUnion(node.Ty):
                GenExpr(node);
                if (!HasManagedTypeDefinition(node.Ty))
                {
                    // Nested/flattened struct — GenExpr returned an address.
                    int scratch = GetOrAddScratchLocal(_types.PointerTo(_types.TyVoid));
                    _enc.StoreLocal(scratch); Pop();
                    _enc.LoadLocal(scratch); Push();
                    return;
                }

                int valueScratch = GetOrAddScratchLocal(node.Ty);
                _enc.StoreLocal(valueScratch); Pop();
                _enc.LoadLocalAddress(valueScratch); Push();
                return;

            case NodeKind.FunCall:
            case NodeKind.Assign:
            case NodeKind.Cond:
                break;

            case NodeKind.VlaPtr:
                if (_localSlots.TryGetValue(node.Var, out int vlaSlot))
                {
                    _enc.LoadLocalAddress(vlaSlot); Push();
                }
                return;
        }
        Util.ErrorTok(node.Tok, "not an lvalue");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Load and Store
    // ═══════════════════════════════════════════════════════════════

    private void Load(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Array or TypeKind.Func or TypeKind.Vla:
                // Address IS the value
                return;
            case TypeKind.Struct or TypeKind.Union:
            {
                if (!HasManagedTypeDefinition(ty))
                {
                    // No TypeDef (nested/flattened struct) — address stays on stack.
                    // Caller accesses individual members via offset arithmetic.
                    return;
                }
                var handle = _emit.GetStructTypeHandle(ty);
                _enc.OpCode(ILOpCode.Ldobj); _enc.Token(handle);
                return;
            }
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Ldind_r4); return;
            case TypeKind.Double or TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Ldind_r8); return;
        }

        // Integer types
        if (ty.Size == 1) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u1 : ILOpCode.Ldind_i1);
        else if (ty.Size == 2) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u2 : ILOpCode.Ldind_i2);
        else if (ty.Size == 4) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u4 : ILOpCode.Ldind_i4);
        else _enc.OpCode(ILOpCode.Ldind_i8);
    }

    private void Store(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Struct or TypeKind.Union:
            {
                if (!HasManagedTypeDefinition(ty))
                {
                    // No TypeDef (nested/flattened struct) — use cpblk with the safest
                    // unaligned prefix because member addresses may be only byte-aligned.
                    // Stack: dest_addr, src_addr → unaligned. cpblk(dest, src, size)
                    EmitConstI4(ty.Size);
                    _enc.OpCode(ILOpCode.Unaligned); _enc.CodeBuilder.WriteByte(1);
                    _enc.OpCode(ILOpCode.Cpblk); Pop(3);
                    return;
                }
                var handle = _emit.GetStructTypeHandle(ty);
                _enc.OpCode(ILOpCode.Stobj); _enc.Token(handle);
                Pop(2);
                return;
            }
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Stind_r4); Pop(2); return;
            case TypeKind.Double or TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Stind_r8); Pop(2); return;
        }

        if (ty.Size == 1) _enc.OpCode(ILOpCode.Stind_i1);
        else if (ty.Size == 2) _enc.OpCode(ILOpCode.Stind_i2);
        else if (ty.Size == 4) _enc.OpCode(ILOpCode.Stind_i4);
        else _enc.OpCode(ILOpCode.Stind_i8);
        Pop(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helper: Load local or parameter
    // ═══════════════════════════════════════════════════════════════

    private void LoadLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.LoadArgument(argIdx); Push();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.LoadLocal(localIdx); Push();
        }
    }

    private void StoreLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.StoreArgument(argIdx); Pop();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.StoreLocal(localIdx); Pop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant loading helpers
    // ═══════════════════════════════════════════════════════════════

    private void EmitConstI4(int value)
    {
        _enc.LoadConstantI4(value); Push();
    }

    private void EmitConstI4(long value)
    {
        _enc.LoadConstantI4((int)value); Push();
    }

    private void EmitConstI8(long value)
    {
        _enc.LoadConstantI8(value); Push();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch normalization helpers
    // ═══════════════════════════════════════════════════════════════

    private void EmitBranch(ILOpCode opcode, LabelHandle label, CType conditionType)
    {
        if (TypeSystem.IsFlonum(conditionType))
            EmitNonZero(conditionType);
        _enc.Branch(opcode, label);
        Pop();
    }

    private void EmitNonZero(CType ty)
    {
        if (TypeSystem.IsFlonum(ty))
        {
            EmitTypedZero(ty);
            _enc.OpCode(ILOpCode.Ceq); Pop();
            EmitConstI4(0);
            _enc.OpCode(ILOpCode.Ceq); Pop();
            return;
        }

        EmitConstI4(0);
        if (ty.Kind is TypeKind.Ptr or TypeKind.Func or TypeKind.Array or TypeKind.Vla)
            _enc.OpCode(ILOpCode.Conv_i);
        else if (ty.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i8);
        _enc.OpCode(ILOpCode.Cgt_un); Pop();
    }

    private void EmitTypedZero(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                return;
            case TypeKind.Double or TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                return;
            case TypeKind.LLong:
                EmitConstI8(0);
                return;
            case TypeKind.Long when _types.DataModel.LongSize == 8:
                // LP64: long is 8 bytes = int64
                EmitConstI8(0);
                return;
            case TypeKind.Ptr or TypeKind.Func or TypeKind.Array or TypeKind.Vla:
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Conv_i);
                return;
            default:
                EmitConstI4(0);
                return;
        }
    }

    private static bool IsStructOrUnion(CType ty) =>
        ty.Kind is TypeKind.Struct or TypeKind.Union;

    private static bool IsAggregateType(CType ty) =>
        ty.Kind is TypeKind.Struct or TypeKind.Union or TypeKind.Array;

    private bool HasManagedTypeDefinition(CType ty) =>
        !TypeSystem.UsesFlexibleAggregateStorage(ty) &&
        _emit.GetAggregateRepresentationKind(ty) == ManagedAggregateRepresentationKind.TypeDefinition;

    private void GenMetadataFieldOwnerAddress(Node owner)
    {
        GenAddr(owner);
        // Flexible clones are stored as byte buffers; reinterpret them for field ops.
        if (TypeSystem.UsesFlexibleAggregateStorage(owner.Ty))
            _enc.OpCode(ILOpCode.Conv_u);
    }

    private void GenByValueOperand(Node value, CType targetTy)
    {
        if (targetTy != null &&
            IsStructOrUnion(value.Ty) &&
            IsStructOrUnion(targetTy) &&
            !HasManagedTypeDefinition(value.Ty) &&
            HasManagedTypeDefinition(targetTy))
        {
            GenAddr(value);
            _enc.OpCode(ILOpCode.Ldobj);
            _enc.Token(_emit.GetStructTypeHandle(targetTy));
            return;
        }

        GenExpr(value);
    }

    /// <summary>Push a callable function address onto the evaluation stack.</summary>
    private void EmitFunctionAddress(Obj fn)
    {
        CType funcTy = fn.Ty;
        if (funcTy.CallConv == CallConv.Clrcall)
        {
            EntityHandle md = _emit.GetFunctionToken(fn);
            _enc.OpCode(ILOpCode.Ldftn); _enc.Token(md); Push();
        }
        else
        {
            // unmanaged: load the native function pointer from __unep@ field
            FieldDefinitionHandle unepField = _emit.GetOrReserveUnepFieldToken(fn);
            _enc.OpCode(ILOpCode.Ldsfld); _enc.Token(unepField); Push();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression code generation (GenExpr)
    // ═══════════════════════════════════════════════════════════════

    private void GenExpr(Node node)
    {
        // Mark line number for debug info
        if (node.Tok?.File != null)
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);

        switch (node.Kind)
        {
            case NodeKind.NullExpr: return;

            case NodeKind.Num:
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float:
                        _enc.LoadConstantR4((float)node.FVal); Push(); return;
                    case TypeKind.Double:
                    case TypeKind.LDouble:
                        _enc.LoadConstantR8(node.FVal); Push(); return;
                    case TypeKind.LLong:
                        EmitConstI8(node.Val); return;
                    default:
                        if (node.Ty.Kind == TypeKind.Long && _types.DataModel.LongSize == 8)
                            EmitConstI8(node.Val);
                        else
                            EmitConstI4(node.Val);
                        return;
                }

            case NodeKind.Neg:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Neg);
                return;

            case NodeKind.Var:
                if (node.Ty.Kind == TypeKind.Func || node.Var.IsFunction)
                {
                    EmitFunctionAddress(node.Var);
                    return;
                }
                if (node.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    // Simple scalar local/param — use direct load
                    LoadLocalOrParam(node.Var);
                    return;
                }
                if (!node.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    _enc.OpCode(ILOpCode.Ldsfld);
                    _enc.Token(_emit.GetFieldToken(node.Var));
                    Push();
                    return;
                }
                GenAddr(node);
                Load(node.Ty);
                return;

            case NodeKind.Member:
                if (node.Ty.Kind is not (TypeKind.Array or TypeKind.Func or TypeKind.Vla) &&
                    _emit.GetMemberAccessKind(node.Lhs.Ty, node.Member) == ManagedAggregateMemberAccessKind.MetadataField)
                {
                    GenMetadataFieldOwnerAddress(node.Lhs);
                    _enc.OpCode(ILOpCode.Ldfld);
                    _enc.Token(_emit.GetAggregateFieldToken(node.Lhs.Ty, node.Member));
                    if (node.Member.IsBitfield)
                        ExtractBitfieldValue(node.Member);
                    return;
                }
                GenAddr(node);
                Load(node.Ty);
                if (node.Member.IsBitfield)
                    ExtractBitfieldValue(node.Member);
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                Load(node.Ty);
                return;

            case NodeKind.Addr:
                GenAddr(node.Lhs);
                return;

            case NodeKind.Assign:
                GenAssign(node, wantValue: true);
                return;

            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                {
                    if (n.Next == null && n.Kind == NodeKind.ExprStmt)
                    {
                        // Last expression in statement expression — its value IS the result.
                        GenExpr(n.Lhs);
                    }
                    else
                    {
                        GenStmt(n);
                    }
                }
                return;

            case NodeKind.Comma:
            {
                int depthBeforeComma = _stackDepth;
                GenExprDiscard(node.Lhs);
                Debug.Assert(_stackDepth == depthBeforeComma);
                GenExpr(node.Rhs);
                return;
            }

            case NodeKind.Cast:
                GenExpr(node.Lhs);
                EmitCast(node.Lhs.Ty, node.Ty);
                return;

            case NodeKind.MemZero:
                if (_localSlots.TryGetValue(node.Var, out int mzSlot))
                {
                    _enc.LoadLocalAddress(mzSlot); Push();
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                else
                {
                    GenAddr(new Node { Kind = NodeKind.Var, Var = node.Var, Tok = node.Tok, Ty = node.Var.Ty });
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                return;

            case NodeKind.Cond:
            {
                int savedDepth = _stackDepth;
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                EmitBranch(ILOpCode.Brfalse, elseLabel, node.Cond.Ty);
                _stackDepth = savedDepth;
                GenExpr(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(elseLabel);
                GenExpr(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.Not:
                GenExpr(node.Lhs);
                EmitTypedZero(node.Lhs.Ty);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;

            case NodeKind.BitNot:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Not);
                return;

            case NodeKind.LogAnd:
            {
                int savedDepth = _stackDepth;
                var falseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                EmitBranch(ILOpCode.Brfalse, falseLabel, node.Lhs.Ty);
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                EmitBranch(ILOpCode.Brfalse, falseLabel, node.Rhs.Ty);
                _stackDepth = savedDepth;
                EmitConstI4(1);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(falseLabel);
                EmitConstI4(0);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.LogOr:
            {
                int savedDepth = _stackDepth;
                var trueLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                EmitBranch(ILOpCode.Brtrue, trueLabel, node.Lhs.Ty);
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                EmitBranch(ILOpCode.Brtrue, trueLabel, node.Rhs.Ty);
                _stackDepth = savedDepth;
                EmitConstI4(0);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(trueLabel);
                EmitConstI4(1);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.FunCall:
                GenFunCall(node);
                return;

            case NodeKind.Cas:
                GenCas(node);
                return;

            case NodeKind.Exch:
                GenExch(node);
                return;
        }

        // Binary operations
        GenExpr(node.Lhs);
        GenExpr(node.Rhs);

        switch (node.Kind)
        {
            case NodeKind.Add: _enc.OpCode(ILOpCode.Add); Pop(); return;
            case NodeKind.Sub: _enc.OpCode(ILOpCode.Sub); Pop(); return;
            case NodeKind.Mul: _enc.OpCode(ILOpCode.Mul); Pop(); return;
            case NodeKind.Div:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Div_un : ILOpCode.Div); Pop(); return;
            case NodeKind.Mod:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Rem_un : ILOpCode.Rem); Pop(); return;
            case NodeKind.BitAnd: _enc.OpCode(ILOpCode.And); Pop(); return;
            case NodeKind.BitOr: _enc.OpCode(ILOpCode.Or); Pop(); return;
            case NodeKind.BitXor: _enc.OpCode(ILOpCode.Xor); Pop(); return;
            case NodeKind.Shl: _enc.OpCode(ILOpCode.Shl); Pop(); return;
            case NodeKind.Shr:
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop(); return;
            case NodeKind.Eq: _enc.OpCode(ILOpCode.Ceq); Pop(); return;
            case NodeKind.Ne:
                _enc.OpCode(ILOpCode.Ceq); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case NodeKind.Lt:
                // clt already returns 0 for NaN (unordered), which is correct for C's
                // "NaN < x is false". Only Le needs the _un variant (via inverted cgt.un).
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Clt_un : ILOpCode.Clt); Pop(); return;
            case NodeKind.Le:
                // a <= b  ≡  !(a > b)  ≡  (cgt_un == 0) for unsigned/float
                // For floats, must use Cgt_un so NaN comparisons return unordered=1→false
                _enc.OpCode((node.Lhs.Ty.IsUnsigned || TypeSystem.IsFlonum(node.Lhs.Ty))
                    ? ILOpCode.Cgt_un : ILOpCode.Cgt); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
        }
        Util.ErrorTok(node.Tok, "invalid expression");
    }

    private void GenAssign(Node node, bool wantValue)
    {
        if (node.Lhs.Kind == NodeKind.Member && node.Lhs.Member.IsBitfield)
        {
            GenBitfieldAssign(node, wantValue);
            return;
        }

        if (node.Lhs.Kind == NodeKind.Member &&
            _emit.GetMemberAccessKind(node.Lhs.Lhs.Ty, node.Lhs.Member) == ManagedAggregateMemberAccessKind.MetadataField)
        {
            GenMetadataFieldOwnerAddress(node.Lhs.Lhs);
            GenExpr(node.Rhs);
            if (wantValue)
            {
                int assignScratch = GetOrAddScratchLocal(node.Ty);
                _enc.OpCode(ILOpCode.Dup);
                Push();
                _enc.StoreLocal(assignScratch);
                Pop();
                _enc.OpCode(ILOpCode.Stfld);
                _enc.Token(_emit.GetAggregateFieldToken(node.Lhs.Lhs.Ty, node.Lhs.Member));
                Pop(2);
                _enc.LoadLocal(assignScratch);
                Push();
            }
            else
            {
                _enc.OpCode(ILOpCode.Stfld);
                _enc.Token(_emit.GetAggregateFieldToken(node.Lhs.Lhs.Ty, node.Lhs.Member));
                Pop(2);
            }
            return;
        }

        if (node.Lhs.Kind == NodeKind.Var && !IsAggregateType(node.Ty))
        {
            GenExpr(node.Rhs);
            if (wantValue)
            {
                _enc.OpCode(ILOpCode.Dup);
                Push();
            }

            if (node.Lhs.Var.IsLocal)
            {
                StoreLocalOrParam(node.Lhs.Var);
            }
            else
            {
                _enc.OpCode(ILOpCode.Stsfld);
                _enc.Token(_emit.GetFieldToken(node.Lhs.Var));
                Pop();
            }
            return;
        }

        GenAddr(node.Lhs);
        if (IsStructOrUnion(node.Ty) && !HasManagedTypeDefinition(node.Ty))
        {
            if (wantValue)
            {
                // Nested/flattened struct: GenExpr(rhs) returns an address.
                // Save dest address before generating rhs so the assignment
                // expression result refers to the destination, not the source.
                // Use a fresh scratch to avoid clobber by inner chain assignments.
                var destScratch = AddFreshScratchLocal(_types.PointerTo(_types.TyVoid));
                _enc.OpCode(ILOpCode.Dup);
                Push();
                _enc.StoreLocal(destScratch);
                Pop();
                GenExpr(node.Rhs);
                Store(node.Ty);
                _enc.LoadLocal(destScratch);
                Push();
            }
            else
            {
                GenExpr(node.Rhs);
                Store(node.Ty);
            }
            return;
        }

        GenExpr(node.Rhs);
        if (wantValue)
        {
            int assignScratch = GetOrAddScratchLocal(node.Ty);
            _enc.OpCode(ILOpCode.Dup);
            Push();
            _enc.StoreLocal(assignScratch);
            Pop();
            Store(node.Ty);
            _enc.LoadLocal(assignScratch);
            Push();
        }
        else
        {
            Store(node.Ty);
        }
    }

    // ─── Function call ───────────────────────────────────────────

    private void GenFunCall(Node node)
    {
        CType funcTy = node.FuncTy;
        bool isIndirect = node.Lhs.Kind != NodeKind.Var || !node.Lhs.Var.IsFunction;

        // Check for alloca
        if (!isIndirect && node.Lhs.Var.Name == "alloca")
        {
            GenExpr(node.Args);
            _enc.OpCode(ILOpCode.Localloc);
            // Stack: size → ptr (net 0)
            return;
        }

        // Push arguments
        int argCount = 0;
        CType paramTy = funcTy.Params;
        for (Node arg = node.Args; arg != null; arg = arg.Next)
        {
            GenByValueOperand(arg, paramTy);
            paramTy = paramTy?.Next;
            argCount++;
        }

        if (isIndirect)
        {
            // Indirect call — push function pointer, then calli
            GenExpr(node.Lhs);

            // Build standalone signature for calli
            var calliSig = new BlobBuilder();
            _emit.EncodeFnPtrSignature(calliSig, funcTy);

            var calliSigHandle = _emit.AddStandaloneSignature(calliSig);
            _enc.CallIndirect(calliSigHandle);
            Pop(argCount + 1); // pop args + function pointer
        }
        else
        {
            // Direct call
            _enc.Call(_emit.GetFunctionToken(node.Lhs.Var));
            Pop(argCount);
        }

        // Push return value if non-void
        if (funcTy.ReturnTy.Kind != TypeKind.Void)
            Push();
    }

    // ─── Atomic operations ───────────────────────────────────────

    private void GenCas(Node node)
    {
        // __atomic_compare_exchange_n → Interlocked.CompareExchange(ref, value, comparand)
        // Returns bool: true if exchange happened
        GenExpr(node.CasAddr);  // address
        GenExpr(node.CasNew);   // desired value
        GenExpr(node.CasOld);   // Load old value from *old_ptr
        Load(node.CasOld.Ty.Base);

        // Call Interlocked.CompareExchange(ref int, int, int)
        var cxchgRef = _emit.Binder.GetCompareExchangeInt32Ref();
        _enc.Call(cxchgRef);
        Pop(2); // 3 args → 1 result

        // Compare result with comparand to get bool
        GenExpr(node.CasOld);
        Load(node.CasOld.Ty.Base);
        _enc.OpCode(ILOpCode.Ceq); Pop();
    }

    private void GenExch(Node node)
    {
        // __atomic_exchange_n → Interlocked.Exchange(ref int, int)
        GenExpr(node.Lhs); // address
        GenExpr(node.Rhs); // new value

        var xchgRef = _emit.Binder.GetExchangeInt32Ref();
        _enc.Call(xchgRef);
        Pop(); // 2 args → 1 result
    }

    // ─── Bitfield assignment ─────────────────────────────────────

    private void GenBitfieldAssign(Node node, bool wantValue)
    {
        Member mem = node.Lhs.Member;
        GenAddr(node.Lhs);

        // Save address for later store
        _enc.OpCode(ILOpCode.Dup); Push();

        GenExpr(node.Rhs);

        ulong mask = BitMask(mem.BitWidth);

        // Mask and shift new value into position
        EmitBitfieldStorageConst(mem, mask);
        _enc.OpCode(ILOpCode.And); Pop();
        if (mem.BitOffset > 0)
        {
            EmitConstI4(mem.BitOffset);
            _enc.OpCode(ILOpCode.Shl); Pop();
        }

        // Load old value, mask out old bits, OR in new bits
        // Stack: addr, shifted_new
        // We need: addr, (old & ~field_mask) | shifted_new
        // Duplicate addr, load old value
        // This requires reordering; use scratch
        int newValScratch = GetOrAddScratchLocal(mem.Ty.Size <= 4 ? _types.TyInt : mem.Ty);
        _enc.StoreLocal(newValScratch); Pop();
        _enc.OpCode(ILOpCode.Dup); Push(); // dup addr
        Load(mem.Ty); // load old value

        ulong clearMask = ~(mask << mem.BitOffset);
        EmitBitfieldStorageConst(mem, clearMask);
        _enc.OpCode(ILOpCode.And); Pop();
        _enc.LoadLocal(newValScratch); Push();
        _enc.OpCode(ILOpCode.Or); Pop();

        Store(node.Ty);

        if (wantValue)
        {
            _enc.OpCode(ILOpCode.Dup); Push();
            Load(mem.Ty);
            ExtractBitfieldValue(mem);
            int assignScratch = GetOrAddScratchLocal(node.Ty);
            _enc.StoreLocal(assignScratch); Pop();
            _enc.OpCode(ILOpCode.Pop); Pop(); // discard the saved destination address
            _enc.LoadLocal(assignScratch);
            Push();
        }
        else
        {
            _enc.OpCode(ILOpCode.Pop); Pop(); // discard the saved destination address
        }
    }

    private static ulong BitMask(int width) =>
        width >= 64 ? ulong.MaxValue : (1UL << width) - 1;

    private void EmitBitfieldStorageConst(Member mem, ulong value)
    {
        if (mem.Ty.Size <= 4)
            EmitConstI4(unchecked((int)value));
        else
            EmitConstI8(unchecked((long)value));
    }

    private void ExtractBitfieldValue(Member mem)
    {
        int storageBits = mem.Ty.Size <= 4 ? 32 : 64;
        int shift = storageBits - mem.BitWidth - mem.BitOffset;
        if (shift > 0)
        {
            EmitConstI4(shift);
            _enc.OpCode(ILOpCode.Shl); Pop();
        }

        int rightShift = storageBits - mem.BitWidth;
        if (rightShift > 0)
        {
            EmitConstI4(rightShift);
            _enc.OpCode(mem.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop();
        }
    }

    // ─── Type cast ───────────────────────────────────────────────

    private void EmitCast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) { if (from.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); } return; }
        if (to.Kind == TypeKind.Bool)
        {
            EmitNonZero(from);
            return;
        }

        // From float/double
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsInteger(to))
        {
            if (to.Size <= 4)
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u4 : ILOpCode.Conv_i4);
            else
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (TypeSystem.IsInteger(from) && TypeSystem.IsFlonum(to))
        {
            if (from.IsUnsigned)
            {
                // conv.r.un interprets the stack value as unsigned for all integer sizes
                _enc.OpCode(ILOpCode.Conv_r_un);
                if (to.Kind == TypeKind.Float)
                    _enc.OpCode(ILOpCode.Conv_r4); // conv.r.un produces float64, narrow to float32
            }
            else if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsFlonum(to))
        {
            if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }

        // Integer → integer
        if (to.Kind == TypeKind.Ptr)
        {
            // Pointer: use conv.i/conv.u (native int) — correct for both 32-bit and 64-bit
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u : ILOpCode.Conv_i);
            return;
        }
        if ((to.Kind == TypeKind.Long && _types.DataModel.LongSize == 8) || to.Kind == TypeKind.LLong)
        {
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (to.Size == 1)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u1 : ILOpCode.Conv_i1);
        else if (to.Size == 2)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u2 : ILOpCode.Conv_i2);
        else if (to.Size == 4 && from.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i4);
        // If same size, no conv needed
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statement code generation (GenStmt)
    // ═══════════════════════════════════════════════════════════════

    private void GenStmt(Node node)
    {
        if (node.Tok?.File != null)
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo);

        switch (node.Kind)
        {
            case NodeKind.If:
            {
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                EmitBranch(ILOpCode.Brfalse, elseLabel, node.Cond.Ty);
                GenStmt(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _enc.MarkLabel(elseLabel);
                if (node.Els != null) GenStmt(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.For:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = GetLabel(node.ContLabelId);
                var brkLabel = GetLabel(node.BrkLabelId);

                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
                if (node.Cond != null)
                {
                    GenExpr(node.Cond);
                    EmitBranch(ILOpCode.Brfalse, brkLabel, node.Cond.Ty);
                }
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                if (node.Inc != null)
                {
                    int incDepth = _stackDepth;
                    GenExprDiscard(node.Inc);
                    Debug.Assert(_stackDepth == incDepth);
                }
                _enc.Branch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Do:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = GetLabel(node.ContLabelId);
                var brkLabel = GetLabel(node.BrkLabelId);

                _enc.MarkLabel(beginLabel);
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                GenExpr(node.Cond);
                EmitBranch(ILOpCode.Brtrue, beginLabel, node.Cond.Ty);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Switch:
            {
                var brkLabel = GetLabel(node.BrkLabelId);

                // x64: always if/else chain (no IL switch)
                GenExpr(node.Cond);
                int condScratch = GetOrAddScratchLocal(node.Cond.Ty);
                _enc.StoreLocal(condScratch); Pop();
                bool is64 = node.Cond.Ty.Size == 8;

                for (Node c = node.CaseNext; c != null; c = c.CaseNext)
                {
                    var caseLabel = GetLabel(c.LabelId);
                    if (c.Begin == c.End)
                    {
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.Branch(ILOpCode.Beq, caseLabel); Pop(2);
                    }
                    else
                    {
                        // Range case: val - begin <= (end - begin)
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.OpCode(ILOpCode.Sub); Pop();
                        if (is64) EmitConstI8(c.End - c.Begin); else EmitConstI4(c.End - c.Begin);
                        _enc.Branch(ILOpCode.Ble_un, caseLabel); Pop(2);
                    }
                }

                if (node.DefaultCase != null)
                {
                    var defaultLabel = GetLabel(node.DefaultCase.LabelId);
                    _enc.Branch(ILOpCode.Br, defaultLabel);
                }
                else
                {
                    _enc.Branch(ILOpCode.Br, brkLabel);
                }

                GenStmt(node.Then);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Case:
                _enc.MarkLabel(GetLabel(node.LabelId));
                GenStmt(node.Lhs);
                return;

            case NodeKind.Block:
                for (Node n = node.Body; n != null; n = n.Next)
                    GenStmt(n);
                return;

            case NodeKind.Goto:
                _enc.Branch(ILOpCode.Br, GetLabel(node.LabelId));
                return;

            case NodeKind.Label:
                _enc.MarkLabel(GetLabel(node.LabelId));
                GenStmt(node.Lhs);
                return;

            case NodeKind.Return:
                if (node.Lhs != null)
                {
                    GenByValueOperand(node.Lhs, _currentFn.Ty.ReturnTy);
                    Pop(); // ret consumes
                }
                _enc.OpCode(ILOpCode.Ret);
                return;

            case NodeKind.ExprStmt:
            {
                int depthBefore = _stackDepth;
                GenExprDiscard(node.Lhs);
                Debug.Assert(_stackDepth == depthBefore);
                return;
            }

            case NodeKind.Asm:
                Util.ErrorTok(node.Tok, "inline assembly not supported in MSIL");
                return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }
}

public record struct CompiledMethod(
    RelocatableInstructionEncoder Instructions,
    int MaxStack,
    StandaloneSignatureHandle LocalVariables,
    CodeViewManSlot[] LocalDebugInfo);