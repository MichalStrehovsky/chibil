namespace Chibil;

/// <summary>Classification of a failed constant-expression evaluation.</summary>
public enum EvalFailure
{
    /// <summary>The evaluation succeeded.</summary>
    None,
    /// <summary>
    /// The expression is genuinely not a compile-time constant (e.g. it reads a
    /// variable or calls a function). Callers that permit runtime evaluation
    /// (block-scope array bounds → VLA, mutable initializers) may fall back.
    /// </summary>
    NotConstant,
    /// <summary>
    /// The expression <em>is</em> a constant but is ill-formed for use as one
    /// (division/modulo by zero, signed MIN/-1 overflow, an out-of-range shift,
    /// a non-integer value in an integer-constant context, or a comma operator
    /// in a constant expression). This is always a hard error, never a fallback.
    /// </summary>
    IllFormed,
}

/// <summary>
/// Result of a constant-expression evaluation attempt. <see cref="Success"/> is a
/// shared singleton; a failure carries a <see cref="Failure"/> classification and a
/// diagnostic message. The evaluated value is returned via an out parameter and is
/// only meaningful when <see cref="IsSuccess"/>.
/// </summary>
public sealed class EvalResult
{
    public static readonly EvalResult Success = new(EvalFailure.None, null);

    public EvalFailure Failure { get; }

    /// <summary>Failure reason; null iff the evaluation succeeded.</summary>
    public string Message { get; }

    public bool IsSuccess => Failure == EvalFailure.None;
    public bool IsIllFormed => Failure == EvalFailure.IllFormed;

    private EvalResult(EvalFailure failure, string message)
    {
        Failure = failure;
        Message = message;
    }

    /// <summary>Not a compile-time constant — callers may fall back to runtime evaluation.</summary>
    public static EvalResult NotConstant(string message) => new(EvalFailure.NotConstant, message);

    /// <summary>A constant expression that is invalid in this context — always diagnose.</summary>
    public static EvalResult IllFormed(string message) => new(EvalFailure.IllFormed, message);
}

/// <summary>
/// An already-evaluated compile-time constant scalar: either an integer (held as a
/// <see cref="long"/> at its promoted stack width) or a floating-point value, tagged
/// with its C type. Produced by the recursive evaluator and by code-gen folding, and
/// consumed by the single-level <c>Fold*</c> combinators.
/// </summary>
public readonly struct ConstValue
{
    public readonly long IntValue;
    public readonly double FloatValue;
    public readonly CType Ty;

    private ConstValue(long i, double f, CType ty)
    {
        IntValue = i;
        FloatValue = f;
        Ty = ty;
    }

    public static ConstValue Int(long value, CType ty) => new(value, 0, ty);
    public static ConstValue Float(double value, CType ty) => new(0, value, ty);

    /// <summary>Whether this value is floating-point (derived from its type).</summary>
    public bool IsFloat => Ty != null && TypeSystem.IsFlonum(Ty);

    /// <summary>The value as a double, applying unsigned interpretation for integer values.</summary>
    public double AsDouble => IsFloat ? FloatValue : (Ty.IsUnsigned ? (ulong)IntValue : IntValue);

    /// <summary>C truthiness of the value (nonzero).</summary>
    public bool IsTruthy => IsFloat ? FloatValue != 0 : IntValue != 0;
}

/// <summary>
/// Compile-time constant expression evaluation, shared by the parser (contexts
/// where C requires a constant expression: enum/case/array-size/bitfield/#if and
/// static initializers) and by the code generator (folding of constant numeric
/// expressions).
///
/// The evaluator walks the tree once and returns an <see cref="EvalResult"/>.
/// Every integer arithmetic/shift/bitwise result is normalized to its
/// integer-promoted type width (see <see cref="NarrowPromoted"/>) so that the
/// folded value is identical to what the runtime IL would compute on the
/// evaluation stack — folding must never change observable behavior.
///
/// A <c>strict</c> mode is used for integer-constant-expression (ICE) contexts
/// (enum/case/array-bounds/#if/_Alignas): it additionally rejects the comma
/// operator, matching C's requirement that a constant expression contain a comma
/// only inside an unevaluated subexpression (which the short-circuiting of
/// <c>?:</c>, <c>&amp;&amp;</c> and <c>||</c> guarantees is never visited here).
/// </summary>
public sealed class ConstEval
{
    private readonly TypeSystem _types;

    public ConstEval(TypeSystem types)
    {
        _types = types;
    }

    // ─── Public API ────────────────────────────────────────────────

    /// <summary>Evaluate a pure integer constant (no relocation/label), non-strict.</summary>
    public EvalResult TryEvalInt(Node node, out long value)
        => EvalInt(node, allowLabel: false, strict: false, out value, out _);

    /// <summary>
    /// Evaluate a strict integer constant expression (enum/case/array-bound/#if).
    /// Requires an integer-typed result and rejects the comma operator.
    /// </summary>
    public EvalResult TryEvalConstInt(Node node, out long value)
    {
        value = 0;
        _types.AddType(node);
        if (!TypeSystem.IsInteger(node.Ty))
            return EvalResult.IllFormed("expression is not an integer constant expression");
        return EvalInt(node, allowLabel: false, strict: true, out value, out _);
    }

    /// <summary>
    /// Evaluate an integer-or-address constant for a static initializer. On
    /// success, a non-null <paramref name="label"/> denotes a symbol whose
    /// address is added to <paramref name="value"/> (the relocation addend).
    /// Uses strict mode: an evaluated comma operator is not a constant expression.
    /// </summary>
    public EvalResult TryEvalAddr(Node node, out long value, out string label)
        => EvalInt(node, allowLabel: true, strict: true, out value, out label);

    /// <summary>Evaluate a strict integer constant for a static bit-field initializer.</summary>
    public EvalResult TryEvalInitInt(Node node, out long value)
        => EvalInt(node, allowLabel: false, strict: true, out value, out _);

    /// <summary>Evaluate a floating-point constant, non-strict (code-gen folding).</summary>
    public EvalResult TryEvalDouble(Node node, out double value)
        => EvalDouble(node, strict: false, out value);

    /// <summary>Evaluate a floating-point constant for a static initializer (strict: no comma).</summary>
    public EvalResult TryEvalInitDouble(Node node, out double value)
        => EvalDouble(node, strict: true, out value);

    // ─── Floating-point evaluator ──────────────────────────────────

    private EvalResult EvalDouble(Node node, bool strict, out double value)
    {
        value = 0;
        _types.AddType(node);

        if (TypeSystem.IsInteger(node.Ty))
        {
            EvalResult ri = EvalInt(node, allowLabel: false, strict, out long iv, out _);
            if (!ri.IsSuccess) return ri;
            value = node.Ty.IsUnsigned ? (ulong)iv : iv;
            return EvalResult.Success;
        }

        double result;
        switch (node.Kind)
        {
            case NodeKind.Add:
            case NodeKind.Sub:
            case NodeKind.Mul:
            case NodeKind.Div:
            {
                EvalResult rl = EvalDouble(node.Lhs, strict, out double l);
                if (!rl.IsSuccess) return rl;
                EvalResult rr = EvalDouble(node.Rhs, strict, out double r);
                if (!rr.IsSuccess) return rr;
                EvalResult rf = FoldBinary(node.Kind, node.Ty, node.Ty,
                    ConstValue.Float(l, node.Ty), ConstValue.Float(r, node.Ty), out ConstValue cv);
                if (!rf.IsSuccess) return rf;
                value = cv.FloatValue;
                return EvalResult.Success;
            }
            case NodeKind.Neg:
            {
                EvalResult r = EvalDouble(node.Lhs, strict, out double l);
                if (!r.IsSuccess) return r;
                FoldUnary(NodeKind.Neg, node.Ty, ConstValue.Float(l, node.Ty), out ConstValue cv);
                value = cv.FloatValue;
                return EvalResult.Success;
            }
            case NodeKind.Cond:
            {
                EvalResult rc = TryIsTruthy(node.Cond, strict, out bool t);
                if (!rc.IsSuccess) return rc;
                EvalResult rb = EvalDouble(t ? node.Then : node.Els, strict, out result);
                if (!rb.IsSuccess) return rb;
                break;
            }
            case NodeKind.Comma:
            {
                if (strict)
                    return EvalResult.IllFormed("comma operator in constant expression");
                EvalResult rl = EvalConstDiscard(node.Lhs);
                if (!rl.IsSuccess) return rl;
                EvalResult rr = EvalDouble(node.Rhs, strict, out result);
                if (!rr.IsSuccess) return rr;
                break;
            }
            case NodeKind.Cast:
            {
                EvalResult r = EvalDouble(node.Lhs, strict, out double l);
                if (!r.IsSuccess) return r;
                result = l;
                break;
            }
            case NodeKind.Num:
                result = node.FVal;
                break;
            default:
                return EvalResult.NotConstant($"not a compile-time constant (node={node.Kind})");
        }

        // Round intermediate float-typed results to float precision, matching
        // the runtime's r4 arithmetic (r8 for double/long double).
        value = node.Ty.Kind == TypeKind.Float ? (float)result : result;
        return EvalResult.Success;
    }

    // ─── Core integer/address evaluator ────────────────────────────

    private EvalResult EvalInt(Node node, bool allowLabel, bool strict, out long value, out string label)
    {
        value = 0;
        label = null;
        _types.AddType(node);

        // A floating node used in an integer position (only reachable without an
        // explicit cast in malformed trees); reduce via double then truncate.
        if (TypeSystem.IsFlonum(node.Ty))
        {
            EvalResult rf = EvalDouble(node, strict, out double d);
            if (!rf.IsSuccess) return rf;
            value = DoubleToInt(d, node.Ty);
            return EvalResult.Success;
        }

        unchecked
        {
            switch (node.Kind)
            {
                case NodeKind.Num:
                    value = node.Val;
                    return EvalResult.Success;

                case NodeKind.Add:
                case NodeKind.Sub:
                {
                    // The address-bearing operand (if any) may only be the left one.
                    EvalResult rl = EvalInt(node.Lhs, allowLabel, strict, out long l, out label);
                    if (!rl.IsSuccess) return rl;
                    EvalResult rr = EvalInt(node.Rhs, false, strict, out long r, out _);
                    if (!rr.IsSuccess) return rr;
                    // Address (relocation) arithmetic is not narrowed and bypasses the
                    // numeric fold; the label carries the symbol, value the addend.
                    if (label != null)
                    {
                        value = node.Kind == NodeKind.Add ? l + r : l - r;
                        return EvalResult.Success;
                    }
                    return FoldIntBinary(node, l, r, out value);
                }

                case NodeKind.Mul:
                case NodeKind.Div:
                case NodeKind.Mod:
                case NodeKind.BitAnd:
                case NodeKind.BitOr:
                case NodeKind.BitXor:
                case NodeKind.Shl:
                case NodeKind.Shr:
                {
                    EvalResult r = EvalBothInt(node, strict, out long a, out long b);
                    if (!r.IsSuccess) return r;
                    return FoldIntBinary(node, a, b, out value);
                }

                case NodeKind.Neg:
                case NodeKind.BitNot:
                {
                    EvalResult r = EvalInt(node.Lhs, false, strict, out long l, out _);
                    if (!r.IsSuccess) return r;
                    FoldUnary(node.Kind, node.Ty, ConstValue.Int(l, node.Ty), out ConstValue cv);
                    value = cv.IntValue;
                    return EvalResult.Success;
                }

                case NodeKind.Eq:
                case NodeKind.Ne:
                case NodeKind.Lt:
                case NodeKind.Le:
                {
                    EvalResult r = EvalBothOperands(node, strict, out ConstValue a, out ConstValue b);
                    if (!r.IsSuccess) return r;
                    FoldBinary(node.Kind, node.Ty, node.Lhs.Ty, a, b, out ConstValue cv);
                    value = cv.IntValue;
                    return EvalResult.Success;
                }

                case NodeKind.Not:
                {
                    EvalResult r = TryIsTruthy(node.Lhs, strict, out bool t);
                    if (!r.IsSuccess) return r;
                    value = t ? 0 : 1;
                    return EvalResult.Success;
                }

                case NodeKind.LogAnd:
                {
                    EvalResult rl = TryIsTruthy(node.Lhs, strict, out bool a);
                    if (!rl.IsSuccess) return rl;
                    if (!a) { value = 0; return EvalResult.Success; } // short-circuit: rhs unevaluated
                    EvalResult rr = TryIsTruthy(node.Rhs, strict, out bool b);
                    if (!rr.IsSuccess) return rr;
                    value = b ? 1 : 0;
                    return EvalResult.Success;
                }

                case NodeKind.LogOr:
                {
                    EvalResult rl = TryIsTruthy(node.Lhs, strict, out bool a);
                    if (!rl.IsSuccess) return rl;
                    if (a) { value = 1; return EvalResult.Success; } // short-circuit: rhs unevaluated
                    EvalResult rr = TryIsTruthy(node.Rhs, strict, out bool b);
                    if (!rr.IsSuccess) return rr;
                    value = b ? 1 : 0;
                    return EvalResult.Success;
                }

                case NodeKind.Cond:
                {
                    EvalResult rc = TryIsTruthy(node.Cond, strict, out bool t);
                    if (!rc.IsSuccess) return rc;
                    // Only the selected arm is evaluated (matches C ?: semantics).
                    return EvalInt(t ? node.Then : node.Els, allowLabel, strict, out value, out label);
                }

                case NodeKind.Comma:
                {
                    if (strict)
                        return EvalResult.IllFormed("comma operator in constant expression");
                    EvalResult rl = EvalConstDiscard(node.Lhs);
                    if (!rl.IsSuccess) return rl;
                    return EvalInt(node.Rhs, allowLabel, strict, out value, out label);
                }

                case NodeKind.Cast:
                {
                    if (node.Ty.Kind == TypeKind.Bool)
                    {
                        EvalResult r = TryIsTruthy(node.Lhs, strict, out bool t);
                        if (!r.IsSuccess) return r;
                        value = t ? 1 : 0;
                        return EvalResult.Success;
                    }
                    if (TypeSystem.IsFlonum(node.Lhs.Ty) && TypeSystem.IsInteger(node.Ty))
                    {
                        EvalResult r = EvalDouble(node.Lhs, strict, out double d);
                        if (!r.IsSuccess) return r;
                        FoldCast(node.Lhs.Ty, node.Ty, ConstValue.Float(d, node.Lhs.Ty), out ConstValue cvf);
                        value = cvf.IntValue;
                        return EvalResult.Success;
                    }
                    EvalResult rc = EvalInt(node.Lhs, allowLabel, strict, out long v, out label);
                    if (!rc.IsSuccess) return rc;
                    FoldCast(node.Lhs.Ty, node.Ty, ConstValue.Int(v, node.Lhs.Ty), out ConstValue cv);
                    value = cv.IntValue;
                    return EvalResult.Success;
                }

                case NodeKind.Addr:
                    if (!allowLabel) return EvalResult.NotConstant("not a compile-time constant");
                    return EvalRval(node.Lhs, out value, out label);

                case NodeKind.Member:
                    if (!allowLabel) return EvalResult.NotConstant("not a compile-time constant");
                    if (node.Ty.Kind != TypeKind.Array) return EvalResult.NotConstant("invalid initializer");
                    return EvalRval(node, out value, out label);

                case NodeKind.Var:
                    if (!allowLabel) return EvalResult.NotConstant("not a compile-time constant");
                    if (node.Var.Ty.Kind != TypeKind.Array && node.Var.Ty.Kind != TypeKind.Func)
                        return EvalResult.NotConstant("invalid initializer");
                    label = node.Var.Name;
                    return EvalResult.Success;

                case NodeKind.Deref:
                    // Array/function subscript on a global: *(arr + i) whose result
                    // is an array/function type (decays to a pointer).
                    if (!allowLabel) return EvalResult.NotConstant("not a compile-time constant");
                    if (node.Ty.Kind == TypeKind.Array || node.Ty.Kind == TypeKind.Func)
                        return EvalInt(node.Lhs, allowLabel: true, strict, out value, out label);
                    return EvalResult.NotConstant("not a compile-time constant");
            }
        }

        return EvalResult.NotConstant($"not a compile-time constant (node={node.Kind})");
    }

    private EvalResult EvalRval(Node node, out long value, out string label)
    {
        value = 0;
        label = null;
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.IsLocal) return EvalResult.NotConstant("not a compile-time constant");
                label = node.Var.Name;
                return EvalResult.Success;
            case NodeKind.Deref:
                return EvalInt(node.Lhs, allowLabel: true, strict: false, out value, out label);
            case NodeKind.Member:
            {
                EvalResult r = EvalRval(node.Lhs, out value, out label);
                if (!r.IsSuccess) return r;
                value += node.Member.Offset;
                return EvalResult.Success;
            }
        }
        return EvalResult.NotConstant("invalid initializer");
    }

    /// <summary>
    /// Fold an integer binary operator over two evaluated operand values, using the
    /// node's result type and its left-operand type (for signedness / shift width).
    /// </summary>
    private EvalResult FoldIntBinary(Node node, long a, long b, out long value)
    {
        EvalResult r = FoldBinary(node.Kind, node.Ty, node.Lhs.Ty,
            ConstValue.Int(a, node.Lhs.Ty), ConstValue.Int(b, node.Rhs.Ty), out ConstValue cv);
        value = cv.IntValue;
        return r;
    }

    /// <summary>
    /// Evaluate both operands of a comparison as <see cref="ConstValue"/>s, choosing
    /// the floating-point or integer path from the (converted) left-operand type.
    /// </summary>
    private EvalResult EvalBothOperands(Node node, bool strict, out ConstValue a, out ConstValue b)
    {
        a = default;
        b = default;
        if (TypeSystem.IsFlonum(node.Lhs.Ty))
        {
            EvalResult rl = EvalDouble(node.Lhs, strict, out double dl);
            if (!rl.IsSuccess) return rl;
            EvalResult rr = EvalDouble(node.Rhs, strict, out double dr);
            if (!rr.IsSuccess) return rr;
            a = ConstValue.Float(dl, node.Lhs.Ty);
            b = ConstValue.Float(dr, node.Rhs.Ty);
            return EvalResult.Success;
        }
        EvalResult ri = EvalBothInt(node, strict, out long ia, out long ib);
        if (!ri.IsSuccess) return ri;
        a = ConstValue.Int(ia, node.Lhs.Ty);
        b = ConstValue.Int(ib, node.Rhs.Ty);
        return EvalResult.Success;
    }

    // ─── Single-level fold combinators ─────────────────────────────
    //
    // These combine ALREADY-EVALUATED operand constants for one operator and are
    // the single source of truth for constant arithmetic semantics. Both the
    // recursive evaluator above and code-gen's bottom-up folding call them, so the
    // integer-promotion narrowing, signed/unsigned selection, float rounding, and
    // ill-formed diagnostics live in exactly one place.

    /// <summary>
    /// Combine two operand constants with a binary operator, normalizing the result
    /// to <paramref name="resultTy"/>. <paramref name="operandTy"/> is the common
    /// (converted) operand type that selects signedness / float-ness and — for
    /// shifts — the promoted width of the shifted value.
    /// </summary>
    public EvalResult FoldBinary(NodeKind kind, CType resultTy, CType operandTy,
        ConstValue lhs, ConstValue rhs, out ConstValue result)
    {
        result = default;
        bool flonum = TypeSystem.IsFlonum(operandTy);

        // Floating-point arithmetic (only these four operators are float-valued).
        if (flonum && kind is NodeKind.Add or NodeKind.Sub or NodeKind.Mul or NodeKind.Div)
        {
            double l = lhs.AsDouble, r = rhs.AsDouble;
            double d = kind switch
            {
                NodeKind.Add => l + r,
                NodeKind.Sub => l - r,
                NodeKind.Mul => l * r,
                _ => l / r,
            };
            result = MakeFloat(d, resultTy);
            return EvalResult.Success;
        }

        switch (kind)
        {
            case NodeKind.Add:
            case NodeKind.Sub:
            case NodeKind.Mul:
            case NodeKind.Div:
            case NodeKind.Mod:
            case NodeKind.BitAnd:
            case NodeKind.BitOr:
            case NodeKind.BitXor:
            case NodeKind.Shl:
            case NodeKind.Shr:
            {
                unchecked
                {
                    long a = lhs.IntValue, b = rhs.IntValue;
                    long res;
                    switch (kind)
                    {
                        case NodeKind.Add: res = a + b; break;
                        case NodeKind.Sub: res = a - b; break;
                        case NodeKind.Mul: res = a * b; break;
                        case NodeKind.Div:
                        case NodeKind.Mod:
                            if (b == 0)
                                return EvalResult.IllFormed("division by zero in constant expression");
                            if (!resultTy.IsUnsigned && b == -1 && a == IntTypeMin(resultTy))
                                return EvalResult.IllFormed("overflow in constant expression");
                            if (kind == NodeKind.Div)
                                res = resultTy.IsUnsigned ? (long)((ulong)a / (ulong)b) : a / b;
                            else
                                res = resultTy.IsUnsigned ? (long)((ulong)a % (ulong)b) : a % b;
                            break;
                        case NodeKind.BitAnd: res = a & b; break;
                        case NodeKind.BitOr: res = a | b; break;
                        case NodeKind.BitXor: res = a ^ b; break;
                        default: // Shl / Shr
                        {
                            // The shifted operand is evaluated at its promoted stack
                            // width (32 or 64 bits). C leaves counts < 0 or >= width
                            // undefined.
                            int width = operandTy.Size <= 4 ? 32 : 64;
                            if (b < 0 || b >= width)
                                return EvalResult.IllFormed("shift count out of range in constant expression");
                            int c = (int)b;
                            if (kind == NodeKind.Shl)
                                res = a << c; // high bits dropped by NarrowPromoted below
                            else if (operandTy.IsUnsigned)
                                res = width == 32 ? (long)((uint)a >> c) : (long)((ulong)a >> c);
                            else
                                res = width == 32 ? (int)a >> c : a >> c;
                            break;
                        }
                    }
                    result = ConstValue.Int(NarrowPromoted(res, resultTy), resultTy);
                    return EvalResult.Success;
                }
            }

            case NodeKind.Eq:
            case NodeKind.Ne:
            {
                bool eq = flonum ? lhs.AsDouble == rhs.AsDouble : lhs.IntValue == rhs.IntValue;
                long v = (kind == NodeKind.Eq ? eq : !eq) ? 1 : 0;
                result = ConstValue.Int(v, resultTy);
                return EvalResult.Success;
            }

            case NodeKind.Lt:
            case NodeKind.Le:
            {
                bool res;
                if (flonum)
                {
                    double l = lhs.AsDouble, r = rhs.AsDouble;
                    res = kind switch
                    {
                        NodeKind.Lt => l < r,
                        NodeKind.Le => l <= r,
                        _ => l >= r,
                    };
                }
                else if (operandTy.IsUnsigned)
                {
                    ulong l = (ulong)lhs.IntValue, r = (ulong)rhs.IntValue;
                    res = kind switch
                    {
                        NodeKind.Lt => l < r,
                        NodeKind.Le => l <= r,
                        _ => l >= r,
                    };
                }
                else
                {
                    long l = lhs.IntValue, r = rhs.IntValue;
                    res = kind switch
                    {
                        NodeKind.Lt => l < r,
                        NodeKind.Le => l <= r,
                        _ => l >= r,
                    };
                }
                result = ConstValue.Int(res ? 1 : 0, resultTy);
                return EvalResult.Success;
            }
        }

        return EvalResult.NotConstant($"not a foldable binary operator ({kind})");
    }

    /// <summary>Combine one operand constant with a unary operator.</summary>
    public EvalResult FoldUnary(NodeKind kind, CType resultTy, ConstValue operand, out ConstValue result)
    {
        result = default;
        switch (kind)
        {
            case NodeKind.Neg:
                if (TypeSystem.IsFlonum(resultTy))
                    result = MakeFloat(-operand.AsDouble, resultTy);
                else
                    result = ConstValue.Int(NarrowPromoted(unchecked(-operand.IntValue), resultTy), resultTy);
                return EvalResult.Success;

            case NodeKind.BitNot:
                result = ConstValue.Int(NarrowPromoted(~operand.IntValue, resultTy), resultTy);
                return EvalResult.Success;

            case NodeKind.Not:
                result = ConstValue.Int(operand.IsTruthy ? 0 : 1, resultTy);
                return EvalResult.Success;
        }
        return EvalResult.NotConstant($"not a foldable unary operator ({kind})");
    }

    /// <summary>Apply a scalar cast to an operand constant.</summary>
    public EvalResult FoldCast(CType fromTy, CType toTy, ConstValue operand, out ConstValue result)
    {
        if (toTy.Kind == TypeKind.Bool)
        {
            result = ConstValue.Int(operand.IsTruthy ? 1 : 0, toTy);
            return EvalResult.Success;
        }
        if (TypeSystem.IsFlonum(toTy))
        {
            double d = operand.IsFloat
                ? operand.FloatValue
                : (fromTy.IsUnsigned ? (ulong)operand.IntValue : operand.IntValue);
            result = MakeFloat(d, toTy);
            return EvalResult.Success;
        }
        // Target is integer.
        if (operand.IsFloat)
            result = ConstValue.Int(DoubleToInt(operand.FloatValue, toTy), toTy);
        else
            result = ConstValue.Int(TruncateCast(operand.IntValue, toTy), toTy);
        return EvalResult.Success;
    }

    /// <summary>Round a double to <c>float</c> precision when the target type is <c>float</c>.</summary>
    private static ConstValue MakeFloat(double value, CType ty)
        => ConstValue.Float(ty.Kind == TypeKind.Float ? (float)value : value, ty);

    // ─── Helpers ───────────────────────────────────────────────────

    /// <summary>Evaluate both operands of a binary node as pure integer constants.</summary>
    private EvalResult EvalBothInt(Node node, bool strict, out long lhs, out long rhs)
    {
        rhs = 0;
        EvalResult rl = EvalInt(node.Lhs, allowLabel: false, strict, out lhs, out _);
        if (!rl.IsSuccess) return rl;
        return EvalInt(node.Rhs, allowLabel: false, strict, out rhs, out _);
    }

    /// <summary>Verify an expression is a side-effect-free constant, discarding its value.</summary>
    private EvalResult EvalConstDiscard(Node node)
    {
        _types.AddType(node);
        if (TypeSystem.IsFlonum(node.Ty))
            return EvalDouble(node, strict: false, out _);
        return EvalInt(node, allowLabel: false, strict: false, out _, out _);
    }

    private EvalResult TryIsTruthy(Node node, bool strict, out bool truthy)
    {
        truthy = false;
        _types.AddType(node);
        if (TypeSystem.IsFlonum(node.Ty))
        {
            EvalResult r = EvalDouble(node, strict, out double d);
            if (!r.IsSuccess) return r;
            truthy = d != 0;
            return EvalResult.Success;
        }
        EvalResult ri = EvalInt(node, allowLabel: false, strict, out long v, out _);
        if (!ri.IsSuccess) return ri;
        truthy = v != 0;
        return EvalResult.Success;
    }

    /// <summary>
    /// Normalize an integer value to its C integer-promoted type: types narrower
    /// than <c>int</c> promote to (signed) <c>int</c>; <c>int</c>/<c>unsigned</c>
    /// wrap at 32 bits; 8-byte types are unchanged. This mirrors the width of the
    /// value the runtime IL keeps on the evaluation stack (i4 / i8).
    /// </summary>
    private static long NarrowPromoted(long val, CType ty)
    {
        if (!TypeSystem.IsInteger(ty)) return val;
        int size = ty.Size;
        bool unsigned = ty.IsUnsigned;
        if (size < 4) { size = 4; unsigned = false; } // integer promotion
        return size == 4 ? (unsigned ? (uint)val : (int)val) : val;
    }

    /// <summary>Truncate an integer to an explicit cast target width/signedness.</summary>
    private static long TruncateCast(long val, CType ty)
    {
        if (TypeSystem.IsInteger(ty))
        {
            switch (ty.Size)
            {
                case 1: return ty.IsUnsigned ? (byte)val : (sbyte)val;
                case 2: return ty.IsUnsigned ? (ushort)val : (short)val;
                case 4: return ty.IsUnsigned ? (uint)val : (int)val;
            }
        }
        return val;
    }

    /// <summary>Convert a double to an integer, matching the runtime conv opcodes.</summary>
    private static long DoubleToInt(double d, CType ty)
    {
        if (ty.Size <= 4)
            return ty.IsUnsigned ? (uint)d : (int)d;
        return ty.IsUnsigned ? unchecked((long)(ulong)d) : (long)d;
    }

    private static long IntTypeMin(CType ty) => ty.Size <= 4 ? int.MinValue : long.MinValue;
}
