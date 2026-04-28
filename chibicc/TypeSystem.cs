namespace Chibicc;

/// <summary>
/// Type system — singleton types, constructors, and type annotation.
/// Port of type.c.
/// </summary>
public static class TypeSystem
{
    // Singleton type instances
    public static readonly CType TyVoid = new(TypeKind.Void, 1, 1);
    public static readonly CType TyBool = new(TypeKind.Bool, 1, 1);

    public static readonly CType TyChar = new(TypeKind.Char, 1, 1);
    public static readonly CType TyShort = new(TypeKind.Short, 2, 2);
    public static readonly CType TyInt = new(TypeKind.Int, 4, 4);
    public static readonly CType TyLong = new(TypeKind.Long, 8, 8);

    public static readonly CType TyUchar = new(TypeKind.Char, 1, 1, isUnsigned: true);
    public static readonly CType TyUshort = new(TypeKind.Short, 2, 2, isUnsigned: true);
    public static readonly CType TyUint = new(TypeKind.Int, 4, 4, isUnsigned: true);
    public static readonly CType TyUlong = new(TypeKind.Long, 8, 8, isUnsigned: true);

    public static readonly CType TyFloat = new(TypeKind.Float, 4, 4);
    public static readonly CType TyDouble = new(TypeKind.Double, 8, 8);
    public static readonly CType TyLdouble = new(TypeKind.LDouble, 16, 16);

    public static bool IsInteger(CType ty)
    {
        TypeKind k = ty.Kind;
        return k == TypeKind.Bool || k == TypeKind.Char || k == TypeKind.Short ||
               k == TypeKind.Int || k == TypeKind.Long || k == TypeKind.Enum;
    }

    public static bool IsFlonum(CType ty)
    {
        return ty.Kind == TypeKind.Float || ty.Kind == TypeKind.Double ||
               ty.Kind == TypeKind.LDouble;
    }

    public static bool IsNumeric(CType ty)
    {
        return IsInteger(ty) || IsFlonum(ty);
    }

    public static bool IsCompatible(CType t1, CType t2)
    {
        if (t1 == t2) return true;
        if (t1.Origin != null) return IsCompatible(t1.Origin, t2);
        if (t2.Origin != null) return IsCompatible(t1, t2.Origin);
        if (t1.Kind != t2.Kind) return false;

        switch (t1.Kind)
        {
            case TypeKind.Char:
            case TypeKind.Short:
            case TypeKind.Int:
            case TypeKind.Long:
                return t1.IsUnsigned == t2.IsUnsigned;
            case TypeKind.Float:
            case TypeKind.Double:
            case TypeKind.LDouble:
                return true;
            case TypeKind.Ptr:
                return IsCompatible(t1.Base, t2.Base);
            case TypeKind.Func:
            {
                if (!IsCompatible(t1.ReturnTy, t2.ReturnTy)) return false;
                if (t1.IsVariadic != t2.IsVariadic) return false;
                CType p1 = t1.Params, p2 = t2.Params;
                for (; p1 != null && p2 != null; p1 = p1.Next, p2 = p2.Next)
                    if (!IsCompatible(p1, p2)) return false;
                return p1 == null && p2 == null;
            }
            case TypeKind.Array:
                if (!IsCompatible(t1.Base, t2.Base)) return false;
                return t1.ArrayLen < 0 && t2.ArrayLen < 0 &&
                       t1.ArrayLen == t2.ArrayLen;
        }
        return false;
    }

    public static CType CopyType(CType ty)
    {
        var ret = new CType
        {
            Kind = ty.Kind,
            Size = ty.Size,
            Align = ty.Align,
            IsUnsigned = ty.IsUnsigned,
            IsAtomic = ty.IsAtomic,
            Base = ty.Base,
            Name = ty.Name,
            NamePos = ty.NamePos,
            ArrayLen = ty.ArrayLen,
            VlaLen = ty.VlaLen,
            VlaSize = ty.VlaSize,
            Members = ty.Members,
            IsFlexible = ty.IsFlexible,
            IsPacked = ty.IsPacked,
            ReturnTy = ty.ReturnTy,
            Params = ty.Params,
            IsVariadic = ty.IsVariadic,
            IsNativeCallConv = ty.IsNativeCallConv,
            Next = ty.Next,
            Origin = ty,
            TypeId = ty.TypeId, // preserve identity for metadata lookup
        };
        return ret;
    }

    public static CType PointerTo(CType @base)
    {
        var ty = new CType(TypeKind.Ptr, 8, 8);
        ty.Base = @base;
        ty.IsUnsigned = true;
        return ty;
    }

    public static CType FuncType(CType returnTy)
    {
        var ty = new CType(TypeKind.Func, 1, 1);
        ty.ReturnTy = returnTy;
        return ty;
    }

    public static CType ArrayOf(CType @base, int len)
    {
        var ty = new CType(TypeKind.Array, @base.Size * len, @base.Align);
        ty.Base = @base;
        ty.ArrayLen = len;
        return ty;
    }

    public static CType VlaOf(CType @base, Node len)
    {
        var ty = new CType(TypeKind.Vla, 8, 8);
        ty.Base = @base;
        ty.VlaLen = len;
        return ty;
    }

    public static CType EnumType()
    {
        return new CType(TypeKind.Enum, 4, 4);
    }

    public static CType StructType()
    {
        return new CType(TypeKind.Struct, 0, 1);
    }

    private static CType GetCommonType(CType ty1, CType ty2)
    {
        if (ty1.Base != null)
            return PointerTo(ty1.Base);

        if (ty1.Kind == TypeKind.Func) return PointerTo(ty1);
        if (ty2.Kind == TypeKind.Func) return PointerTo(ty2);

        if (ty1.Kind == TypeKind.LDouble || ty2.Kind == TypeKind.LDouble) return TyLdouble;
        if (ty1.Kind == TypeKind.Double || ty2.Kind == TypeKind.Double) return TyDouble;
        if (ty1.Kind == TypeKind.Float || ty2.Kind == TypeKind.Float) return TyFloat;

        if (ty1.Size < 4) ty1 = TyInt;
        if (ty2.Size < 4) ty2 = TyInt;

        if (ty1.Size != ty2.Size)
            return (ty1.Size < ty2.Size) ? ty2 : ty1;

        if (ty2.IsUnsigned) return ty2;
        return ty1;
    }

    public static void UsualArithConv(ref Node lhs, ref Node rhs)
    {
        CType ty = GetCommonType(lhs.Ty, rhs.Ty);
        lhs = NewCast(lhs, ty);
        rhs = NewCast(rhs, ty);
    }

    public static Node NewCast(Node expr, CType ty)
    {
        AddType(expr);
        var node = new Node
        {
            Kind = NodeKind.Cast,
            Tok = expr.Tok,
            Lhs = expr,
            Ty = CopyType(ty),
        };
        return node;
    }

    public static void AddType(Node node)
    {
        if (node == null || node.Ty != null) return;

        AddType(node.Lhs);
        AddType(node.Rhs);
        AddType(node.Cond);
        AddType(node.Then);
        AddType(node.Els);
        AddType(node.Init);
        AddType(node.Inc);

        for (Node n = node.Body; n != null; n = n.Next)
            AddType(n);
        for (Node n = node.Args; n != null; n = n.Next)
            AddType(n);

        switch (node.Kind)
        {
            case NodeKind.Num:
                node.Ty = TyInt;
                return;
            case NodeKind.Add:
            case NodeKind.Sub:
            case NodeKind.Mul:
            case NodeKind.Div:
            case NodeKind.Mod:
            case NodeKind.BitAnd:
            case NodeKind.BitOr:
            case NodeKind.BitXor:
                UsualArithConv(ref node.Lhs, ref node.Rhs);
                node.Ty = node.Lhs.Ty;
                return;
            case NodeKind.Neg:
            {
                CType ty = GetCommonType(TyInt, node.Lhs.Ty);
                node.Lhs = NewCast(node.Lhs, ty);
                node.Ty = ty;
                return;
            }
            case NodeKind.Assign:
                if (node.Lhs.Ty.Kind == TypeKind.Array)
                    Util.ErrorTok(node.Lhs.Tok, "not an lvalue");
                if (node.Lhs.Ty.Kind != TypeKind.Struct)
                    node.Rhs = NewCast(node.Rhs, node.Lhs.Ty);
                node.Ty = node.Lhs.Ty;
                return;
            case NodeKind.Eq:
            case NodeKind.Ne:
            case NodeKind.Lt:
            case NodeKind.Le:
                UsualArithConv(ref node.Lhs, ref node.Rhs);
                node.Ty = TyInt;
                return;
            case NodeKind.FunCall:
                node.Ty = node.FuncTy.ReturnTy;
                return;
            case NodeKind.Not:
            case NodeKind.LogOr:
            case NodeKind.LogAnd:
                node.Ty = TyInt;
                return;
            case NodeKind.BitNot:
            case NodeKind.Shl:
            case NodeKind.Shr:
                node.Ty = node.Lhs.Ty;
                return;
            case NodeKind.Var:
            case NodeKind.VlaPtr:
                node.Ty = node.Var.Ty;
                return;
            case NodeKind.Cond:
                if (node.Then.Ty.Kind == TypeKind.Void || node.Els.Ty.Kind == TypeKind.Void)
                    node.Ty = TyVoid;
                else
                {
                    UsualArithConv(ref node.Then, ref node.Els);
                    node.Ty = node.Then.Ty;
                }
                return;
            case NodeKind.Comma:
                node.Ty = node.Rhs.Ty;
                return;
            case NodeKind.Member:
                node.Ty = node.Member.Ty;
                return;
            case NodeKind.Addr:
            {
                CType ty = node.Lhs.Ty;
                if (ty.Kind == TypeKind.Array)
                    node.Ty = PointerTo(ty.Base);
                else
                    node.Ty = PointerTo(ty);
                return;
            }
            case NodeKind.Deref:
                if (node.Lhs.Ty.Base == null)
                    Util.ErrorTok(node.Tok, "invalid pointer dereference");
                if (node.Lhs.Ty.Base.Kind == TypeKind.Void)
                    Util.ErrorTok(node.Tok, "dereferencing a void pointer");
                node.Ty = node.Lhs.Ty.Base;
                return;
            case NodeKind.StmtExpr:
                if (node.Body != null)
                {
                    Node stmt = node.Body;
                    while (stmt.Next != null) stmt = stmt.Next;
                    if (stmt.Kind == NodeKind.ExprStmt)
                    {
                        node.Ty = stmt.Lhs.Ty;
                        return;
                    }
                }
                Util.ErrorTok(node.Tok, "statement expression returning void is not supported");
                return;
            case NodeKind.LabelVal:
                node.Ty = PointerTo(TyVoid);
                return;
            case NodeKind.Cas:
                AddType(node.CasAddr);
                AddType(node.CasOld);
                AddType(node.CasNew);
                node.Ty = TyBool;
                if (node.CasAddr.Ty.Kind != TypeKind.Ptr)
                    Util.ErrorTok(node.CasAddr.Tok, "pointer expected");
                if (node.CasOld.Ty.Kind != TypeKind.Ptr)
                    Util.ErrorTok(node.CasOld.Tok, "pointer expected");
                return;
            case NodeKind.Exch:
                if (node.Lhs.Ty.Kind != TypeKind.Ptr)
                    Util.ErrorTok(node.CasAddr.Tok, "pointer expected");
                node.Ty = node.Lhs.Ty.Base;
                return;
        }
    }
}
