using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Chibil;

/// <summary>
/// Recursive descent parser — port of parse.c.
/// Parses tokens into an AST (Obj list for globals/functions, Node tree for bodies).
/// </summary>
public class Parser
{
    private Obj _locals;
    private Obj _globals;
    private Scope _scope;
    private Obj _currentFn;
    private Node _gotos;
    private Node _labels;
    private string _brkLabel;
    private string _contLabel;
    private Node _currentSwitch;
    private Obj _builtinAlloca;
    private int _uniqueId;
    private Dictionary<string, bool> _typenameMap;
    private readonly Tokenizer _tokenizer;
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;

    public Parser(Tokenizer tokenizer, CompilerOptions options, TypeSystem types)
    {
        _tokenizer = tokenizer;
        _options = options;
        _types = types;
        _scope = new Scope();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scope management
    // ═══════════════════════════════════════════════════════════════

    private void EnterScope() { var sc = new Scope { Next = _scope }; _scope = sc; }
    private void LeaveScope() { _scope = _scope.Next; }

    private VarScope FindVar(Token tok)
    {
        string name = Util.GetTokenText(tok);
        for (Scope sc = _scope; sc != null; sc = sc.Next)
            if (sc.Vars.TryGetValue(name, out VarScope vs))
                return vs;
        return null;
    }

    private CType FindTag(Token tok)
    {
        string name = Util.GetTokenText(tok);
        for (Scope sc = _scope; sc != null; sc = sc.Next)
            if (sc.Tags.TryGetValue(name, out CType ty))
                return ty;
        return null;
    }

    private VarScope PushScope(string name)
    {
        var sc = new VarScope();
        _scope.Vars[name] = sc;
        return sc;
    }

    private void PushTagScope(Token tok, CType ty)
    {
        _scope.Tags[Util.GetTokenText(tok)] = ty;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Node constructors
    // ═══════════════════════════════════════════════════════════════

    private static Node NewNode(NodeKind kind, Token tok) => new() { Kind = kind, Tok = tok };
    private static Node NewBinary(NodeKind kind, Node lhs, Node rhs, Token tok) => new() { Kind = kind, Lhs = lhs, Rhs = rhs, Tok = tok };
    private static Node NewUnary(NodeKind kind, Node expr, Token tok) => new() { Kind = kind, Lhs = expr, Tok = tok };
    private static Node NewNum(long val, Token tok) => new() { Kind = NodeKind.Num, Val = val, Tok = tok };
    private Node NewLong(long val, Token tok) => new() { Kind = NodeKind.Num, Val = val, Ty = _types.TyLong, Tok = tok };
    private Node NewUlong(long val, Token tok) => new() { Kind = NodeKind.Num, Val = val, Ty = _types.SizeType, Tok = tok };
    private static Node NewVarNode(Obj var, Token tok) => new() { Kind = NodeKind.Var, Var = var, Tok = tok };
    private static Node NewVlaPtr(Obj var, Token tok) => new() { Kind = NodeKind.VlaPtr, Var = var, Tok = tok };

    private string NewUniqueName() => $".L..{_uniqueId++}";
    private string GetIdent(Token tok) { if (tok.Kind != TokenKind.Ident) Util.ErrorTok(tok, "expected an identifier"); return Util.GetTokenText(tok); }

    // ═══════════════════════════════════════════════════════════════
    //  Variable constructors
    // ═══════════════════════════════════════════════════════════════

    private Obj NewVar(string name, CType ty)
    {
        var v = new Obj { Name = name, Ty = ty, Align = ty.Align };
        PushScope(name).Var = v;
        return v;
    }

    private Obj NewLvar(string name, CType ty)
    {
        Obj v = NewVar(name, ty);
        v.IsLocal = true;
        v.Next = _locals;
        _locals = v;
        return v;
    }

    private Obj NewGvar(string name, CType ty)
    {
        Obj v = NewVar(name, ty);
        v.Next = _globals;
        v.IsStatic = true;
        v.IsDefinition = true;
        _globals = v;
        return v;
    }

    private Obj NewAnonGvar(CType ty) => NewGvar(NewUniqueName(), ty);

    private Obj NewStringLiteral(byte[] str, CType ty)
    {
        Obj v = NewAnonGvar(ty);
        v.InitData = str;
        return v;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type helpers
    // ═══════════════════════════════════════════════════════════════

    private CType FindTypedef(Token tok)
    {
        if (tok.Kind == TokenKind.Ident) { VarScope sc = FindVar(tok); if (sc != null) return sc.TypeDef; }
        return null;
    }

    private bool IsTypename(Token tok)
    {
        if (_typenameMap == null)
        {
            _typenameMap = new();
            string[] kw = { "void", "_Bool", "char", "short", "int", "long", "struct", "union",
                "typedef", "enum", "static", "extern", "_Alignas", "signed", "unsigned",
                "const", "volatile", "auto", "register", "restrict", "__restrict",
                "__restrict__", "_Noreturn", "float", "double", "typeof", "inline",
                "_Thread_local", "__thread", "_Atomic" };
            foreach (string k in kw) _typenameMap[k] = true;
        }
        string text = Util.GetTokenText(tok);
        return _typenameMap.ContainsKey(text) || FindTypedef(tok) != null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Initializer
    // ═══════════════════════════════════════════════════════════════

    private Initializer NewInitializer(CType ty, bool isFlexible)
    {
        var init = new Initializer { Ty = ty };
        if (ty.Kind == TypeKind.Array)
        {
            if (isFlexible && ty.Size < 0) { init.IsFlexible = true; return init; }
            init.Children = new Initializer[ty.ArrayLen];
            for (int i = 0; i < ty.ArrayLen; i++) init.Children[i] = NewInitializer(ty.Base, false);
            return init;
        }
        if (ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union)
        {
            int len = 0;
            for (Member mem = ty.Members; mem != null; mem = mem.Next) len++;
            init.Children = new Initializer[len];
            for (Member mem = ty.Members; mem != null; mem = mem.Next)
            {
                if (isFlexible && ty.IsFlexible && mem.Next == null)
                {
                    init.Children[mem.Idx] = new Initializer { Ty = mem.Ty, IsFlexible = true };
                }
                else
                {
                    init.Children[mem.Idx] = NewInitializer(mem.Ty, false);
                }
            }
            return init;
        }
        return init;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Declaration specifier
    // ═══════════════════════════════════════════════════════════════

    private const int VOID = 1 << 0, BOOL = 1 << 2, CHAR = 1 << 4, SHORT = 1 << 6;
    private const int INT = 1 << 8, LONG = 1 << 10, FLOAT = 1 << 12, DOUBLE = 1 << 14;
    private const int OTHER = 1 << 16, SIGNED = 1 << 17, UNSIGNED = 1 << 18;

    private CType Declspec(ref Token rest, Token tok, VarAttr attr)
    {
        CType ty = _types.TyInt;
        int counter = 0;
        bool isAtomic = false;
        bool isConst = false;
        bool isVolatile = false;

        while (IsTypename(tok))
        {
            if (Util.Equal(tok, "typedef") || Util.Equal(tok, "static") || Util.Equal(tok, "extern") ||
                Util.Equal(tok, "inline") || Util.Equal(tok, "_Thread_local") || Util.Equal(tok, "__thread"))
            {
                if (attr == null) Util.ErrorTok(tok, "storage class specifier is not allowed in this context");
                if (Util.Equal(tok, "typedef")) attr.IsTypedef = true;
                else if (Util.Equal(tok, "static")) attr.IsStatic = true;
                else if (Util.Equal(tok, "extern")) attr.IsExtern = true;
                else if (Util.Equal(tok, "inline")) attr.IsInline = true;
                else { attr.IsTls = true; Util.ErrorTok(tok, "thread-local storage is not supported in MSIL mode"); }
                if (attr.IsTypedef && ((attr.IsStatic?1:0) + (attr.IsExtern?1:0) + (attr.IsInline?1:0) + (attr.IsTls?1:0) > 1))
                    Util.ErrorTok(tok, "typedef may not be used together with static, extern, inline, __thread or _Thread_local");
                tok = tok.Next; continue;
            }
            if (Util.Equal(tok, "const")) { isConst = true; tok = tok.Next; continue; }
            if (Util.Equal(tok, "volatile")) { isVolatile = true; tok = tok.Next; continue; }
            if (Util.Consume(ref tok, tok, "auto") || Util.Consume(ref tok, tok, "register") ||
                Util.Consume(ref tok, tok, "restrict") || Util.Consume(ref tok, tok, "__restrict") ||
                Util.Consume(ref tok, tok, "__restrict__") || Util.Consume(ref tok, tok, "_Noreturn"))
                continue;
            if (Util.Equal(tok, "_Atomic"))
            {
                tok = tok.Next;
                if (Util.Equal(tok, "(")) { ty = Typename(ref tok, tok.Next); tok = Util.Skip(tok, ")"); }
                isAtomic = true; continue;
            }
            if (Util.Equal(tok, "_Alignas"))
            {
                if (attr == null) Util.ErrorTok(tok, "_Alignas is not allowed in this context");
                tok = Util.Skip(tok.Next, "(");
                if (IsTypename(tok)) attr.Align = Typename(ref tok, tok).Align;
                else attr.Align = (int)ConstExpr(ref tok, tok);
                tok = Util.Skip(tok, ")"); continue;
            }

            CType ty2 = FindTypedef(tok);
            if (Util.Equal(tok, "struct") || Util.Equal(tok, "union") || Util.Equal(tok, "enum") ||
                Util.Equal(tok, "typeof") || ty2 != null)
            {
                if (counter != 0) break;
                if (Util.Equal(tok, "struct")) ty = StructDecl(ref tok, tok.Next);
                else if (Util.Equal(tok, "union")) ty = UnionDecl(ref tok, tok.Next);
                else if (Util.Equal(tok, "enum")) ty = EnumSpecifier(ref tok, tok.Next);
                else if (Util.Equal(tok, "typeof")) ty = TypeofSpecifier(ref tok, tok.Next);
                else { ty = ty2; tok = tok.Next; }
                counter += OTHER; continue;
            }

            if (Util.Equal(tok, "void")) counter += VOID;
            else if (Util.Equal(tok, "_Bool")) counter += BOOL;
            else if (Util.Equal(tok, "char")) counter += CHAR;
            else if (Util.Equal(tok, "short")) counter += SHORT;
            else if (Util.Equal(tok, "int")) counter += INT;
            else if (Util.Equal(tok, "long")) counter += LONG;
            else if (Util.Equal(tok, "float")) counter += FLOAT;
            else if (Util.Equal(tok, "double")) counter += DOUBLE;
            else if (Util.Equal(tok, "signed")) counter |= SIGNED;
            else if (Util.Equal(tok, "unsigned")) counter |= UNSIGNED;
            else Util.Unreachable();

            ty = counter switch
            {
                VOID => _types.TyVoid, BOOL => _types.TyBool,
                CHAR or (SIGNED + CHAR) => _types.TyChar, UNSIGNED + CHAR => _types.TyUchar,
                SHORT or (SHORT + INT) or (SIGNED + SHORT) or (SIGNED + SHORT + INT) => _types.TyShort,
                (UNSIGNED + SHORT) or (UNSIGNED + SHORT + INT) => _types.TyUshort,
                INT or SIGNED or (SIGNED + INT) => _types.TyInt,
                UNSIGNED or (UNSIGNED + INT) => _types.TyUint,
                LONG or (LONG + INT) or
                (SIGNED + LONG) or (SIGNED + LONG + INT) => _types.TyLong,
                (LONG + LONG) or (LONG + LONG + INT) or
                (SIGNED + LONG + LONG) or (SIGNED + LONG + LONG + INT) => _types.TyLongLong,
                (UNSIGNED + LONG) or (UNSIGNED + LONG + INT) => _types.TyUlong,
                (UNSIGNED + LONG + LONG) or (UNSIGNED + LONG + LONG + INT) => _types.TyUlongLong,
                FLOAT => _types.TyFloat, DOUBLE => _types.TyDouble,
                (LONG + DOUBLE) => _types.TyLdouble,
                _ => throw new ChibiException($"invalid type")
            };
            tok = tok.Next;
        }
        if (isAtomic || isConst || isVolatile)
        {
            ty = TypeSystem.CopyType(ty);
            // OR-merge: preserve qualifiers from typedef origin
            ty.IsAtomic |= isAtomic;
            ty.IsConst |= isConst;
            ty.IsVolatile |= isVolatile;
        }
        rest = tok;
        return ty;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Declarator and type suffix
    // ═══════════════════════════════════════════════════════════════

    private CType FuncParams(ref Token rest, Token tok, CType ty)
    {
        if (Util.Equal(tok, "void") && Util.Equal(tok.Next, ")"))
        {
            rest = tok.Next.Next;
            return TypeSystem.FuncType(ty);
        }
        CType head = new(), cur = head;
        bool isVariadic = false;
        while (!Util.Equal(tok, ")"))
        {
            if (cur != head) tok = Util.Skip(tok, ",");
            if (Util.Equal(tok, "...")) { isVariadic = true; tok = tok.Next; Util.Skip(tok, ")"); break; }
            CType ty2 = Declspec(ref tok, tok, null);
            ty2 = Declarator(ref tok, tok, ty2);
            Token name = ty2.Name;
            if (ty2.Kind == TypeKind.Array) { ty2 = _types.PointerTo(ty2.Base); ty2.Name = name; }
            else if (ty2.Kind == TypeKind.Func) { ty2 = _types.PointerTo(ty2); ty2.Name = name; }
            cur = cur.Next = TypeSystem.CopyType(ty2);
        }
        if (cur == head) isVariadic = true;
        ty = TypeSystem.FuncType(ty);
        ty.Params = head.Next;
        ty.IsVariadic = isVariadic;
        rest = tok.Next;
        return ty;
    }

    private CType ArrayDimensions(ref Token rest, Token tok, CType ty)
    {
        while (Util.Equal(tok, "static") || Util.Equal(tok, "restrict")) tok = tok.Next;
        if (Util.Equal(tok, "]")) { ty = TypeSuffix(ref rest, tok.Next, ty); return TypeSystem.ArrayOf(ty, -1); }
        Node expr = Conditional(ref tok, tok);
        tok = Util.Skip(tok, "]");
        ty = TypeSuffix(ref rest, tok, ty);
        if (ty.Kind == TypeKind.Vla || !IsConstExpr(expr)) return _types.VlaOf(ty, expr);
        return TypeSystem.ArrayOf(ty, (int)Eval(expr));
    }

    private CType TypeSuffix(ref Token rest, Token tok, CType ty)
    {
        if (Util.Equal(tok, "(")) return FuncParams(ref rest, tok.Next, ty);
        if (Util.Equal(tok, "[")) return ArrayDimensions(ref rest, tok.Next, ty);
        rest = tok; return ty;
    }

    private CType Pointers(ref Token rest, Token tok, CType ty)
    {
        while (Util.Consume(ref tok, tok, "*"))
        {
            ty = _types.PointerTo(ty);
            while (Util.Equal(tok, "const") || Util.Equal(tok, "volatile") || Util.Equal(tok, "restrict") ||
                   Util.Equal(tok, "__restrict") || Util.Equal(tok, "__restrict__"))
            {
                if (Util.Equal(tok, "const")) ty.IsConst = true;
                else if (Util.Equal(tok, "volatile")) ty.IsVolatile = true;
                tok = tok.Next;
            }
        }
        rest = tok; return ty;
    }

    private CallConv ParseCallConv(ref Token tok)
    {
        if (Util.Equal(tok, "__cdecl")) { tok = tok.Next; return CallConv.Cdecl; }
        if (Util.Equal(tok, "__clrcall")) { tok = tok.Next; return CallConv.Clrcall; }
        if (Util.Equal(tok, "__stdcall"))
        {
            tok = tok.Next;
            // On x64, __stdcall is silently treated as __cdecl (all CCs converge to MS-x64 ABI).
            // Normalize early so downstream code doesn't need special cases.
            return _options.DataModel.PointerSize == 4 ? CallConv.Stdcall : CallConv.Cdecl;
        }
        return CallConv.Cdecl;
    }

    private CType Declarator(ref Token rest, Token tok, CType ty)
    {
        CallConv callConv = ParseCallConv(ref tok);
        if (ty.Kind == TypeKind.Func) ty.CallConv = callConv;
        ty = Pointers(ref tok, tok, ty);
        // Calling convention can also appear after pointers (e.g., void* __stdcall fn())
        CallConv cc2 = ParseCallConv(ref tok);
        if (cc2 != CallConv.Cdecl) callConv = cc2;
        if (Util.Equal(tok, "("))
        {
            Token start = tok;
            CType dummy = new();
            Declarator(ref tok, start.Next, dummy);
            tok = Util.Skip(tok, ")");
            ty = TypeSuffix(ref rest, tok, ty);
            return Declarator(ref tok, start.Next, ty);
        }
        Token name = null, namePos = tok;
        if (tok.Kind == TokenKind.Ident) { name = tok; tok = tok.Next; }
        ty = TypeSuffix(ref rest, tok, ty);
        if (ty.Kind == TypeKind.Func) ty.CallConv = callConv;
        ty.Name = name; ty.NamePos = namePos;
        return ty;
    }

    private CType AbstractDeclarator(ref Token rest, Token tok, CType ty)
    {
        CallConv callConv = ParseCallConv(ref tok);
        if (ty.Kind == TypeKind.Func) ty.CallConv = callConv;
        ty = Pointers(ref tok, tok, ty);
        CallConv cc2 = ParseCallConv(ref tok);
        if (cc2 != CallConv.Cdecl) callConv = cc2;
        if (Util.Equal(tok, "("))
        {
            Token start = tok;
            CType dummy = new();
            AbstractDeclarator(ref tok, start.Next, dummy);
            tok = Util.Skip(tok, ")");
            ty = TypeSuffix(ref rest, tok, ty);
            return AbstractDeclarator(ref tok, start.Next, ty);
        }
        ty = TypeSuffix(ref rest, tok, ty);
        if (ty.Kind == TypeKind.Func) ty.CallConv = callConv;
        return ty;
    }

    private CType Typename(ref Token rest, Token tok)
    {
        CType ty = Declspec(ref tok, tok, null);
        return AbstractDeclarator(ref rest, tok, ty);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Enum, typeof, struct, union
    // ═══════════════════════════════════════════════════════════════

    private static bool IsEnd(Token tok) => Util.Equal(tok, "}") || (Util.Equal(tok, ",") && Util.Equal(tok.Next, "}"));

    private static bool ConsumeEnd(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, "}")) { rest = tok.Next; return true; }
        if (Util.Equal(tok, ",") && Util.Equal(tok.Next, "}")) { rest = tok.Next.Next; return true; }
        return false;
    }

    private CType EnumSpecifier(ref Token rest, Token tok)
    {
        CType ty = TypeSystem.EnumType();
        Token tag = null;
        if (tok.Kind == TokenKind.Ident) { tag = tok; tok = tok.Next; }
        if (tag != null && !Util.Equal(tok, "{"))
        {
            CType found = FindTag(tag);
            if (found == null) Util.ErrorTok(tag, "unknown enum type");
            if (found.Kind != TypeKind.Enum) Util.ErrorTok(tag, "not an enum tag");
            rest = tok; return found;
        }
        tok = Util.Skip(tok, "{");
        if (tag != null) ty.TagName = Util.GetTokenText(tag);
        int i = 0, val = 0;
        while (!ConsumeEnd(ref rest, tok))
        {
            if (i++ > 0) tok = Util.Skip(tok, ",");
            string name = GetIdent(tok); tok = tok.Next;
            if (Util.Equal(tok, "=")) val = (int)ConstExpr(ref tok, tok.Next);
            VarScope sc = PushScope(name);
            sc.EnumTy = ty; sc.EnumVal = val++;
        }
        if (tag != null) PushTagScope(tag, ty);
        return ty;
    }

    private CType TypeofSpecifier(ref Token rest, Token tok)
    {
        tok = Util.Skip(tok, "(");
        CType ty;
        if (IsTypename(tok)) ty = Typename(ref tok, tok);
        else { Node node = Expr(ref tok, tok); _types.AddType(node); ty = node.Ty; }
        rest = Util.Skip(tok, ")");
        return ty;
    }

    private Member GetStructMember(CType ty, Token tok)
    {
        string name = Util.GetTokenText(tok);
        for (Member mem = ty.Members; mem != null; mem = mem.Next)
        {
            if ((mem.Ty.Kind == TypeKind.Struct || mem.Ty.Kind == TypeKind.Union) && mem.Name == null)
            {
                if (GetStructMember(mem.Ty, tok) != null) return mem;
                continue;
            }
            if (mem.Name != null && Util.GetTokenText(mem.Name) == name) return mem;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Struct/union declarations
    // ═══════════════════════════════════════════════════════════════

    private void StructMembers(ref Token rest, Token tok, CType ty)
    {
        Member head = new(), cur = head;
        int idx = 0;
        while (!Util.Equal(tok, "}"))
        {
            VarAttr attr = new();
            CType basety = Declspec(ref tok, tok, attr);
            bool first = true;
            if ((basety.Kind == TypeKind.Struct || basety.Kind == TypeKind.Union) && Util.Consume(ref tok, tok, ";"))
            {
                var mem = new Member { Ty = basety, Idx = idx++, Align = attr.Align != 0 ? attr.Align : basety.Align };
                cur = cur.Next = mem; continue;
            }
            while (!Util.Consume(ref tok, tok, ";"))
            {
                if (!first) tok = Util.Skip(tok, ",");
                first = false;
                var mem = new Member();
                mem.Ty = Declarator(ref tok, tok, basety);
                mem.Name = mem.Ty.Name; mem.Idx = idx++;
                mem.Align = attr.Align != 0 ? attr.Align : mem.Ty.Align;
                if (Util.Consume(ref tok, tok, ":")) { mem.IsBitfield = true; mem.BitWidth = (int)ConstExpr(ref tok, tok); }
                cur = cur.Next = mem;
            }
        }
        if (cur != head && cur.Ty.Kind == TypeKind.Array && cur.Ty.ArrayLen < 0)
        {
            cur.Ty = TypeSystem.ArrayOf(cur.Ty.Base, 0); ty.IsFlexible = true;
        }
        rest = tok.Next;
        ty.Members = head.Next;
    }

    private Token AttributeList(Token tok, CType ty)
    {
        while (Util.Consume(ref tok, tok, "__attribute__"))
        {
            tok = Util.Skip(tok, "("); tok = Util.Skip(tok, "(");
            bool first = true;
            while (!Util.Consume(ref tok, tok, ")"))
            {
                if (!first) tok = Util.Skip(tok, ",");
                first = false;
                if (Util.Consume(ref tok, tok, "packed")) { ty.IsPacked = true; continue; }
                if (Util.Consume(ref tok, tok, "aligned"))
                {
                    tok = Util.Skip(tok, "("); ty.Align = (int)ConstExpr(ref tok, tok); tok = Util.Skip(tok, ")"); continue;
                }
                Util.ErrorTok(tok, "unknown attribute");
            }
            tok = Util.Skip(tok, ")");
        }
        return tok;
    }

    private CType StructUnionDecl(ref Token rest, Token tok)
    {
        CType ty = TypeSystem.StructType();
        tok = AttributeList(tok, ty);
        Token tag = null;
        if (tok.Kind == TokenKind.Ident) { tag = tok; tok = tok.Next; }
        if (tag != null && !Util.Equal(tok, "{"))
        {
            rest = tok;
            CType ty2 = FindTag(tag);
            if (ty2 != null) return ty2;
            ty.Size = -1; ty.TagName = Util.GetTokenText(tag); PushTagScope(tag, ty); return ty;
        }
        tok = Util.Skip(tok, "{");
        StructMembers(ref tok, tok, ty);
        rest = AttributeList(tok, ty);
        if (tag != null) ty.TagName = Util.GetTokenText(tag);
        if (tag != null)
        {
            string tagName = Util.GetTokenText(tag);
            if (_scope.Tags.TryGetValue(tagName, out CType existing))
            {
                // Overwrite existing definition
                existing.Kind = ty.Kind; existing.Size = ty.Size; existing.Align = ty.Align;
                existing.Members = ty.Members; existing.IsFlexible = ty.IsFlexible; existing.IsPacked = ty.IsPacked;
                return existing;
            }
            PushTagScope(tag, ty);
        }
        return ty;
    }

    private CType StructDecl(ref Token rest, Token tok)
    {
        CType ty = StructUnionDecl(ref rest, tok);
        ty.Kind = TypeKind.Struct;
        if (ty.Size < 0) return ty;
        int bits = 0;
        for (Member mem = ty.Members; mem != null; mem = mem.Next)
        {
            if (mem.IsBitfield && mem.BitWidth == 0) { bits = Util.AlignTo(bits, mem.Ty.Size * 8); }
            else if (mem.IsBitfield)
            {
                int sz = mem.Ty.Size;
                if (bits / (sz * 8) != (bits + mem.BitWidth - 1) / (sz * 8)) bits = Util.AlignTo(bits, sz * 8);
                mem.Offset = Util.AlignDown(bits / 8, sz);
                mem.BitOffset = bits % (sz * 8);
                bits += mem.BitWidth;
            }
            else
            {
                if (!ty.IsPacked) bits = Util.AlignTo(bits, mem.Align * 8);
                mem.Offset = bits / 8;
                bits += mem.Ty.Size * 8;
            }
            if (!ty.IsPacked && ty.Align < mem.Align) ty.Align = mem.Align;
        }
        ty.Size = Util.AlignTo(bits, ty.Align * 8) / 8;
        return ty;
    }

    private CType UnionDecl(ref Token rest, Token tok)
    {
        CType ty = StructUnionDecl(ref rest, tok);
        ty.Kind = TypeKind.Union;
        if (ty.Size < 0) return ty;
        for (Member mem = ty.Members; mem != null; mem = mem.Next)
        {
            if (ty.Align < mem.Align) ty.Align = mem.Align;
            if (ty.Size < mem.Ty.Size) ty.Size = mem.Ty.Size;
        }
        ty.Size = Util.AlignTo(ty.Size, ty.Align);
        return ty;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant expression evaluation
    // ═══════════════════════════════════════════════════════════════

    // C's eval(node) passes NULL for label → labels not allowed
    private long Eval(Node node) => Eval2(node, out Unsafe.NullRef<Func<string>>());

    private long Eval2(Node node, out Func<string> label)
    {
        Unsafe.SkipInit(out label);
        _types.AddType(node);
        if (TypeSystem.IsFlonum(node.Ty)) return (long)EvalDouble(node);

        switch (node.Kind)
        {
            case NodeKind.Add: { long l = Eval2(node.Lhs, out label); return l + Eval(node.Rhs); }
            case NodeKind.Sub: { long l = Eval2(node.Lhs, out label); return l - Eval(node.Rhs); }
            case NodeKind.Mul: return Eval(node.Lhs) * Eval(node.Rhs);
            case NodeKind.Div:
                if (node.Ty.IsUnsigned) return (long)((ulong)Eval(node.Lhs) / (ulong)Eval(node.Rhs));
                return Eval(node.Lhs) / Eval(node.Rhs);
            case NodeKind.Neg: return -Eval(node.Lhs);
            case NodeKind.Mod:
                if (node.Ty.IsUnsigned) return (long)((ulong)Eval(node.Lhs) % (ulong)Eval(node.Rhs));
                return Eval(node.Lhs) % Eval(node.Rhs);
            case NodeKind.BitAnd: return Eval(node.Lhs) & Eval(node.Rhs);
            case NodeKind.BitOr: return Eval(node.Lhs) | Eval(node.Rhs);
            case NodeKind.BitXor: return Eval(node.Lhs) ^ Eval(node.Rhs);
            case NodeKind.Shl: return Eval(node.Lhs) << (int)Eval(node.Rhs);
            case NodeKind.Shr:
                if (node.Ty.IsUnsigned && node.Ty.Size == 8) return (long)((ulong)Eval(node.Lhs) >> (int)Eval(node.Rhs));
                return Eval(node.Lhs) >> (int)Eval(node.Rhs);
            case NodeKind.Eq: return Eval(node.Lhs) == Eval(node.Rhs) ? 1 : 0;
            case NodeKind.Ne: return Eval(node.Lhs) != Eval(node.Rhs) ? 1 : 0;
            case NodeKind.Lt:
                if (node.Lhs.Ty.IsUnsigned) return (ulong)Eval(node.Lhs) < (ulong)Eval(node.Rhs) ? 1 : 0;
                return Eval(node.Lhs) < Eval(node.Rhs) ? 1 : 0;
            case NodeKind.Le:
                if (node.Lhs.Ty.IsUnsigned) return (ulong)Eval(node.Lhs) <= (ulong)Eval(node.Rhs) ? 1 : 0;
                return Eval(node.Lhs) <= Eval(node.Rhs) ? 1 : 0;
            case NodeKind.Cond: return Eval(node.Cond) != 0 ? Eval2(node.Then, out label) : Eval2(node.Els, out label);
            case NodeKind.Comma: return Eval2(node.Rhs, out label);
            case NodeKind.Not: return Eval(node.Lhs) == 0 ? 1 : 0;
            case NodeKind.BitNot: return ~Eval(node.Lhs);
            case NodeKind.LogAnd: return (Eval(node.Lhs) != 0 && Eval(node.Rhs) != 0) ? 1 : 0;
            case NodeKind.LogOr: return (Eval(node.Lhs) != 0 || Eval(node.Rhs) != 0) ? 1 : 0;
            case NodeKind.Cast:
            {
                long val = Eval2(node.Lhs, out label);
                if (TypeSystem.IsInteger(node.Ty))
                {
                    switch (node.Ty.Size)
                    {
                        case 1: return node.Ty.IsUnsigned ? (byte)val : (sbyte)val;
                        case 2: return node.Ty.IsUnsigned ? (ushort)val : (short)val;
                        case 4: return node.Ty.IsUnsigned ? (uint)val : (int)val;
                    }
                }
                return val;
            }
            case NodeKind.Addr: return EvalRval(node.Lhs, out label);
            case NodeKind.LabelVal:
                label = () => node.UniqueLabel;
                return 0;
            case NodeKind.Member:
                if (Unsafe.IsNullRef(ref label)) Util.ErrorTok(node.Tok, "not a compile-time constant");
                if (node.Ty.Kind != TypeKind.Array) Util.ErrorTok(node.Tok, "invalid initializer");
                return EvalRval(node.Lhs, out label) + node.Member.Offset;
            case NodeKind.Var:
                if (Unsafe.IsNullRef(ref label)) Util.ErrorTok(node.Tok, "not a compile-time constant");
                if (node.Var.Ty.Kind != TypeKind.Array && node.Var.Ty.Kind != TypeKind.Func) Util.ErrorTok(node.Tok, "invalid initializer");
                { Obj v = node.Var; label = () => v.Name; }
                return 0;
            case NodeKind.Deref:
                // Array subscript on global: *(arr + i) where result is array type (decays to pointer)
                if (Unsafe.IsNullRef(ref label)) Util.ErrorTok(node.Tok, "not a compile-time constant");
                if (node.Ty.Kind == TypeKind.Array || node.Ty.Kind == TypeKind.Func)
                    return Eval2(node.Lhs, out label);
                Util.ErrorTok(node.Tok, "not a compile-time constant");
                return 0;
            case NodeKind.Num: return node.Val;
        }
        Util.ErrorTok(node.Tok, $"not a compile-time constant (node={node.Kind})");
        return 0;
    }

    private long EvalRval(Node node, out Func<string> label)
    {
        label = null;
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.IsLocal) Util.ErrorTok(node.Tok, "not a compile-time constant");
                { Obj v = node.Var; label = () => v.Name; }
                return 0;
            case NodeKind.Deref: return Eval2(node.Lhs, out label);
            case NodeKind.Member: return EvalRval(node.Lhs, out label) + node.Member.Offset;
        }
        Util.ErrorTok(node.Tok, "invalid initializer");
        return 0;
    }

    private bool IsConstExpr(Node node)
    {
        _types.AddType(node);
        switch (node.Kind)
        {
            case NodeKind.Add: case NodeKind.Sub: case NodeKind.Mul: case NodeKind.Div:
            case NodeKind.BitAnd: case NodeKind.BitOr: case NodeKind.BitXor:
            case NodeKind.Shl: case NodeKind.Shr: case NodeKind.Eq: case NodeKind.Ne:
            case NodeKind.Lt: case NodeKind.Le: case NodeKind.LogAnd: case NodeKind.LogOr:
                return IsConstExpr(node.Lhs) && IsConstExpr(node.Rhs);
            case NodeKind.Cond: return IsConstExpr(node.Cond) && IsConstExpr(Eval(node.Cond) != 0 ? node.Then : node.Els);
            case NodeKind.Comma: return IsConstExpr(node.Rhs);
            case NodeKind.Neg: case NodeKind.Not: case NodeKind.BitNot: case NodeKind.Cast: return IsConstExpr(node.Lhs);
            case NodeKind.Num: return true;
        }
        return false;
    }

    public long ConstExpr(ref Token rest, Token tok)
    {
        Node node = Conditional(ref rest, tok);
        return Eval(node);
    }

    private double EvalDouble(Node node)
    {
        _types.AddType(node);
        if (TypeSystem.IsInteger(node.Ty))
        {
            if (node.Ty.IsUnsigned) return (ulong)Eval(node);
            return Eval(node);
        }
        switch (node.Kind)
        {
            case NodeKind.Add: return EvalDouble(node.Lhs) + EvalDouble(node.Rhs);
            case NodeKind.Sub: return EvalDouble(node.Lhs) - EvalDouble(node.Rhs);
            case NodeKind.Mul: return EvalDouble(node.Lhs) * EvalDouble(node.Rhs);
            case NodeKind.Div: return EvalDouble(node.Lhs) / EvalDouble(node.Rhs);
            case NodeKind.Neg: return -EvalDouble(node.Lhs);
            case NodeKind.Cond: return EvalDouble(node.Cond) != 0 ? EvalDouble(node.Then) : EvalDouble(node.Els);
            case NodeKind.Comma: return EvalDouble(node.Rhs);
            case NodeKind.Cast: return TypeSystem.IsFlonum(node.Lhs.Ty) ? EvalDouble(node.Lhs) : Eval(node.Lhs);
            case NodeKind.Num: return node.FVal;
        }
        Util.ErrorTok(node.Tok, "not a compile-time constant");
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression parsing (recursive descent)
    // ═══════════════════════════════════════════════════════════════

    private Node Expr(ref Token rest, Token tok)
    {
        Node node = Assign(ref tok, tok);
        if (Util.Equal(tok, ",")) return NewBinary(NodeKind.Comma, node, Expr(ref rest, tok.Next), tok);
        rest = tok; return node;
    }

    private Node Assign(ref Token rest, Token tok)
    {
        Node node = Conditional(ref tok, tok);
        if (Util.Equal(tok, "=")) return NewBinary(NodeKind.Assign, node, Assign(ref rest, tok.Next), tok);
        if (Util.Equal(tok, "+=")) return ToAssign(NewAdd(node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "-=")) return ToAssign(NewSub(node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "*=")) return ToAssign(NewBinary(NodeKind.Mul, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "/=")) return ToAssign(NewBinary(NodeKind.Div, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "%=")) return ToAssign(NewBinary(NodeKind.Mod, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "&=")) return ToAssign(NewBinary(NodeKind.BitAnd, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "|=")) return ToAssign(NewBinary(NodeKind.BitOr, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "^=")) return ToAssign(NewBinary(NodeKind.BitXor, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, "<<=")) return ToAssign(NewBinary(NodeKind.Shl, node, Assign(ref rest, tok.Next), tok));
        if (Util.Equal(tok, ">>=")) return ToAssign(NewBinary(NodeKind.Shr, node, Assign(ref rest, tok.Next), tok));
        rest = tok; return node;
    }

    private Node Conditional(ref Token rest, Token tok)
    {
        Node cond = LogOr(ref tok, tok);
        if (!Util.Equal(tok, "?")) { rest = tok; return cond; }
        if (Util.Equal(tok.Next, ":"))
        {
            _types.AddType(cond);
            Obj v = NewLvar("", cond.Ty);
            Node lhs = NewBinary(NodeKind.Assign, NewVarNode(v, tok), cond, tok);
            var rhs = NewNode(NodeKind.Cond, tok);
            rhs.Cond = NewVarNode(v, tok); rhs.Then = NewVarNode(v, tok); rhs.Els = Conditional(ref rest, tok.Next.Next);
            return NewBinary(NodeKind.Comma, lhs, rhs, tok);
        }
        var node = NewNode(NodeKind.Cond, tok);
        node.Cond = cond; node.Then = Expr(ref tok, tok.Next); tok = Util.Skip(tok, ":"); node.Els = Conditional(ref rest, tok);
        return node;
    }

    private Node LogOr(ref Token rest, Token tok) { Node n = LogAnd(ref tok, tok); while (Util.Equal(tok, "||")) { Token s = tok; n = NewBinary(NodeKind.LogOr, n, LogAnd(ref tok, tok.Next), s); } rest = tok; return n; }
    private Node LogAnd(ref Token rest, Token tok) { Node n = BitOr(ref tok, tok); while (Util.Equal(tok, "&&")) { Token s = tok; n = NewBinary(NodeKind.LogAnd, n, BitOr(ref tok, tok.Next), s); } rest = tok; return n; }
    private Node BitOr(ref Token rest, Token tok) { Node n = BitXor(ref tok, tok); while (Util.Equal(tok, "|")) { Token s = tok; n = NewBinary(NodeKind.BitOr, n, BitXor(ref tok, tok.Next), s); } rest = tok; return n; }
    private Node BitXor(ref Token rest, Token tok) { Node n = BitAnd(ref tok, tok); while (Util.Equal(tok, "^")) { Token s = tok; n = NewBinary(NodeKind.BitXor, n, BitAnd(ref tok, tok.Next), s); } rest = tok; return n; }
    private Node BitAnd(ref Token rest, Token tok) { Node n = Equality(ref tok, tok); while (Util.Equal(tok, "&")) { Token s = tok; n = NewBinary(NodeKind.BitAnd, n, Equality(ref tok, tok.Next), s); } rest = tok; return n; }

    private Node Equality(ref Token rest, Token tok)
    {
        Node n = Relational(ref tok, tok);
        for (;;)
        {
            Token s = tok;
            if (Util.Equal(tok, "==")) { n = NewBinary(NodeKind.Eq, n, Relational(ref tok, tok.Next), s); continue; }
            if (Util.Equal(tok, "!=")) { n = NewBinary(NodeKind.Ne, n, Relational(ref tok, tok.Next), s); continue; }
            rest = tok; return n;
        }
    }

    private Node Relational(ref Token rest, Token tok)
    {
        Node n = Shift(ref tok, tok);
        for (;;)
        {
            Token s = tok;
            if (Util.Equal(tok, "<")) { n = NewBinary(NodeKind.Lt, n, Shift(ref tok, tok.Next), s); continue; }
            if (Util.Equal(tok, "<=")) { n = NewBinary(NodeKind.Le, n, Shift(ref tok, tok.Next), s); continue; }
            if (Util.Equal(tok, ">")) { n = NewBinary(NodeKind.Lt, Shift(ref tok, tok.Next), n, s); continue; }
            if (Util.Equal(tok, ">=")) { n = NewBinary(NodeKind.Le, Shift(ref tok, tok.Next), n, s); continue; }
            rest = tok; return n;
        }
    }

    private Node Shift(ref Token rest, Token tok)
    {
        Node n = Add(ref tok, tok);
        for (;;)
        {
            Token s = tok;
            if (Util.Equal(tok, "<<")) { n = NewBinary(NodeKind.Shl, n, Add(ref tok, tok.Next), s); continue; }
            if (Util.Equal(tok, ">>")) { n = NewBinary(NodeKind.Shr, n, Add(ref tok, tok.Next), s); continue; }
            rest = tok; return n;
        }
    }

    private Node NewAdd(Node lhs, Node rhs, Token tok)
    {
        _types.AddType(lhs); _types.AddType(rhs);
        if (TypeSystem.IsNumeric(lhs.Ty) && TypeSystem.IsNumeric(rhs.Ty)) return NewBinary(NodeKind.Add, lhs, rhs, tok);
        if (lhs.Ty.Base != null && rhs.Ty.Base != null) Util.ErrorTok(tok, "invalid operands");
        if (lhs.Ty.Base == null && rhs.Ty.Base != null) { var tmp = lhs; lhs = rhs; rhs = tmp; }
        if (lhs.Ty.Base.Kind == TypeKind.Vla) { rhs = NewBinary(NodeKind.Mul, rhs, NewVarNode(lhs.Ty.Base.VlaSize, tok), tok); return NewBinary(NodeKind.Add, lhs, rhs, tok); }
        rhs = NewBinary(NodeKind.Mul, rhs, NewLong(lhs.Ty.Base.Size, tok), tok);
        return NewBinary(NodeKind.Add, lhs, rhs, tok);
    }

    private Node NewSub(Node lhs, Node rhs, Token tok)
    {
        _types.AddType(lhs); _types.AddType(rhs);
        if (TypeSystem.IsNumeric(lhs.Ty) && TypeSystem.IsNumeric(rhs.Ty)) return NewBinary(NodeKind.Sub, lhs, rhs, tok);
        if (lhs.Ty.Base != null && lhs.Ty.Base.Kind == TypeKind.Vla)
        {
            rhs = NewBinary(NodeKind.Mul, rhs, NewVarNode(lhs.Ty.Base.VlaSize, tok), tok);
            _types.AddType(rhs); var node = NewBinary(NodeKind.Sub, lhs, rhs, tok); node.Ty = lhs.Ty; return node;
        }
        if (lhs.Ty.Base != null && TypeSystem.IsInteger(rhs.Ty))
        {
            rhs = NewBinary(NodeKind.Mul, rhs, NewLong(lhs.Ty.Base.Size, tok), tok);
            _types.AddType(rhs); var node = NewBinary(NodeKind.Sub, lhs, rhs, tok); node.Ty = lhs.Ty; return node;
        }
        if (lhs.Ty.Base != null && rhs.Ty.Base != null)
        {
            var node = NewBinary(NodeKind.Sub, lhs, rhs, tok); node.Ty = _types.PtrdiffType;
            return NewBinary(NodeKind.Div, node, NewNum(lhs.Ty.Base.Size, tok), tok);
        }
        Util.ErrorTok(tok, "invalid operands"); return null;
    }

    private Node Add(ref Token rest, Token tok)
    {
        Node n = Mul(ref tok, tok);
        for (;;) { Token s = tok; if (Util.Equal(tok, "+")) { n = NewAdd(n, Mul(ref tok, tok.Next), s); continue; } if (Util.Equal(tok, "-")) { n = NewSub(n, Mul(ref tok, tok.Next), s); continue; } rest = tok; return n; }
    }

    private Node Mul(ref Token rest, Token tok)
    {
        Node n = CastExpr(ref tok, tok);
        for (;;) { Token s = tok; if (Util.Equal(tok, "*")) { n = NewBinary(NodeKind.Mul, n, CastExpr(ref tok, tok.Next), s); continue; } if (Util.Equal(tok, "/")) { n = NewBinary(NodeKind.Div, n, CastExpr(ref tok, tok.Next), s); continue; } if (Util.Equal(tok, "%")) { n = NewBinary(NodeKind.Mod, n, CastExpr(ref tok, tok.Next), s); continue; } rest = tok; return n; }
    }

    private Node CastExpr(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, "(") && IsTypename(tok.Next))
        {
            Token start = tok;
            CType ty = Typename(ref tok, tok.Next); tok = Util.Skip(tok, ")");
            if (Util.Equal(tok, "{")) return Unary(ref rest, start);
            var node = _types.NewCast(CastExpr(ref rest, tok), ty); node.Tok = start; return node;
        }
        return Unary(ref rest, tok);
    }

    private Node ToAssign(Node binary)
    {
        _types.AddType(binary.Lhs); _types.AddType(binary.Rhs);
        Token tok = binary.Tok;
        if (binary.Lhs.Kind == NodeKind.Member)
        {
            Obj v = NewLvar("", _types.PointerTo(binary.Lhs.Lhs.Ty));
            Node e1 = NewBinary(NodeKind.Assign, NewVarNode(v, tok), NewUnary(NodeKind.Addr, binary.Lhs.Lhs, tok), tok);
            Node e2 = NewUnary(NodeKind.Member, NewUnary(NodeKind.Deref, NewVarNode(v, tok), tok), tok); e2.Member = binary.Lhs.Member;
            Node e3 = NewUnary(NodeKind.Member, NewUnary(NodeKind.Deref, NewVarNode(v, tok), tok), tok); e3.Member = binary.Lhs.Member;
            Node e4 = NewBinary(NodeKind.Assign, e2, NewBinary(binary.Kind, e3, binary.Rhs, tok), tok);
            return NewBinary(NodeKind.Comma, e1, e4, tok);
        }
        if (binary.Lhs.Ty.IsAtomic)
        {
            // Simplified atomic op= handling
            Node head = new(), cur = head;
            Obj addr = NewLvar("", _types.PointerTo(binary.Lhs.Ty));
            Obj val = NewLvar("", binary.Rhs.Ty);
            Obj old = NewLvar("", binary.Lhs.Ty);
            Obj @new = NewLvar("", binary.Lhs.Ty);
            cur = cur.Next = NewUnary(NodeKind.ExprStmt, NewBinary(NodeKind.Assign, NewVarNode(addr, tok), NewUnary(NodeKind.Addr, binary.Lhs, tok), tok), tok);
            cur = cur.Next = NewUnary(NodeKind.ExprStmt, NewBinary(NodeKind.Assign, NewVarNode(val, tok), binary.Rhs, tok), tok);
            cur = cur.Next = NewUnary(NodeKind.ExprStmt, NewBinary(NodeKind.Assign, NewVarNode(old, tok), NewUnary(NodeKind.Deref, NewVarNode(addr, tok), tok), tok), tok);
            var loop = NewNode(NodeKind.Do, tok);
            loop.BrkLabel = NewUniqueName(); loop.ContLabel = NewUniqueName();
            Node body = NewBinary(NodeKind.Assign, NewVarNode(@new, tok), NewBinary(binary.Kind, NewVarNode(old, tok), NewVarNode(val, tok), tok), tok);
            loop.Then = NewNode(NodeKind.Block, tok); loop.Then.Body = NewUnary(NodeKind.ExprStmt, body, tok);
            var cas = NewNode(NodeKind.Cas, tok);
            cas.CasAddr = NewVarNode(addr, tok); cas.CasOld = NewUnary(NodeKind.Addr, NewVarNode(old, tok), tok); cas.CasNew = NewVarNode(@new, tok);
            loop.Cond = NewUnary(NodeKind.Not, cas, tok);
            cur = cur.Next = loop;
            cur = cur.Next = NewUnary(NodeKind.ExprStmt, NewVarNode(@new, tok), tok);
            var stmtExpr = NewNode(NodeKind.StmtExpr, tok); stmtExpr.Body = head.Next;
            return stmtExpr;
        }
        Obj v2 = NewLvar("", _types.PointerTo(binary.Lhs.Ty));
        Node x1 = NewBinary(NodeKind.Assign, NewVarNode(v2, tok), NewUnary(NodeKind.Addr, binary.Lhs, tok), tok);
        Node x2 = NewBinary(NodeKind.Assign, NewUnary(NodeKind.Deref, NewVarNode(v2, tok), tok), NewBinary(binary.Kind, NewUnary(NodeKind.Deref, NewVarNode(v2, tok), tok), binary.Rhs, tok), tok);
        return NewBinary(NodeKind.Comma, x1, x2, tok);
    }

    private Node Unary(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, "+")) return CastExpr(ref rest, tok.Next);
        if (Util.Equal(tok, "-")) return NewUnary(NodeKind.Neg, CastExpr(ref rest, tok.Next), tok);
        if (Util.Equal(tok, "&")) { Node lhs = CastExpr(ref rest, tok.Next); _types.AddType(lhs); if (lhs.Kind == NodeKind.Member && lhs.Member.IsBitfield) Util.ErrorTok(tok, "cannot take address of bitfield"); return NewUnary(NodeKind.Addr, lhs, tok); }
        if (Util.Equal(tok, "*")) { Node node = CastExpr(ref rest, tok.Next); _types.AddType(node); if (node.Ty.Kind == TypeKind.Func) return node; return NewUnary(NodeKind.Deref, node, tok); }
        if (Util.Equal(tok, "!")) return NewUnary(NodeKind.Not, CastExpr(ref rest, tok.Next), tok);
        if (Util.Equal(tok, "~")) return NewUnary(NodeKind.BitNot, CastExpr(ref rest, tok.Next), tok);
        if (Util.Equal(tok, "++")) return ToAssign(NewAdd(Unary(ref rest, tok.Next), NewNum(1, tok), tok));
        if (Util.Equal(tok, "--")) return ToAssign(NewSub(Unary(ref rest, tok.Next), NewNum(1, tok), tok));
        if (Util.Equal(tok, "&&")) { var node = NewNode(NodeKind.LabelVal, tok); node.Label = GetIdent(tok.Next); node.GotoNext = _gotos; _gotos = node; rest = tok.Next.Next; return node; }
        return Postfix(ref rest, tok);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Postfix and primary
    // ═══════════════════════════════════════════════════════════════

    private Node StructRef(Node node, Token tok)
    {
        _types.AddType(node);
        if (node.Ty.Kind != TypeKind.Struct && node.Ty.Kind != TypeKind.Union) Util.ErrorTok(node.Tok, "not a struct nor a union");
        CType ty = node.Ty;
        for (;;)
        {
            Member mem = GetStructMember(ty, tok);
            if (mem == null) Util.ErrorTok(tok, "no such member");
            node = NewUnary(NodeKind.Member, node, tok); node.Member = mem;
            if (mem.Name != null) break;
            ty = mem.Ty;
        }
        return node;
    }

    private Node NewIncDec(Node node, Token tok, int addend)
    {
        _types.AddType(node);
        return _types.NewCast(NewAdd(ToAssign(NewAdd(node, NewNum(addend, tok), tok)), NewNum(-addend, tok), tok), node.Ty);
    }

    private Node Postfix(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, "(") && IsTypename(tok.Next))
        {
            Token start = tok;
            CType ty = Typename(ref tok, tok.Next); tok = Util.Skip(tok, ")");
            if (_scope.Next == null) { Obj v = NewAnonGvar(ty); GvarInitializer(ref rest, tok, v); return NewVarNode(v, start); }
            Obj lv = NewLvar("", ty);
            Node lhs = LvarInitializer(ref rest, tok, lv);
            return NewBinary(NodeKind.Comma, lhs, NewVarNode(lv, tok), start);
        }
        Node node = Primary(ref tok, tok);
        for (;;)
        {
            if (Util.Equal(tok, "(")) { node = Funcall(ref tok, tok.Next, node); continue; }
            if (Util.Equal(tok, "[")) { Token s = tok; Node idx = Expr(ref tok, tok.Next); tok = Util.Skip(tok, "]"); node = NewUnary(NodeKind.Deref, NewAdd(node, idx, s), s); continue; }
            if (Util.Equal(tok, ".")) { node = StructRef(node, tok.Next); tok = tok.Next.Next; continue; }
            if (Util.Equal(tok, "->")) { node = NewUnary(NodeKind.Deref, node, tok); node = StructRef(node, tok.Next); tok = tok.Next.Next; continue; }
            if (Util.Equal(tok, "++")) { node = NewIncDec(node, tok, 1); tok = tok.Next; continue; }
            if (Util.Equal(tok, "--")) { node = NewIncDec(node, tok, -1); tok = tok.Next; continue; }
            rest = tok; return node;
        }
    }

    private Node Funcall(ref Token rest, Token tok, Node fn)
    {
        _types.AddType(fn);
        if (fn.Ty.Kind != TypeKind.Func && (fn.Ty.Kind != TypeKind.Ptr || fn.Ty.Base.Kind != TypeKind.Func))
            Util.ErrorTok(fn.Tok, "not a function");
        CType ty = fn.Ty.Kind == TypeKind.Func ? fn.Ty : fn.Ty.Base;
        CType paramTy = ty.Params;
        Node head = new(), cur = head;
        while (!Util.Equal(tok, ")"))
        {
            if (cur != head) tok = Util.Skip(tok, ",");
            Node arg = Assign(ref tok, tok);
            _types.AddType(arg);
            if (paramTy != null) { if (paramTy.Kind != TypeKind.Struct && paramTy.Kind != TypeKind.Union) arg = _types.NewCast(arg, paramTy); paramTy = paramTy.Next; }
            else if (arg.Ty.Kind == TypeKind.Float) arg = _types.NewCast(arg, _types.TyDouble);
            cur = cur.Next = arg;
        }
        if (paramTy != null) Util.ErrorTok(tok, "too few arguments");
        rest = Util.Skip(tok, ")");
        var node = NewUnary(NodeKind.FunCall, fn, tok);
        node.FuncTy = ty; node.Ty = ty.ReturnTy; node.Args = head.Next;
        if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union) node.RetBuffer = NewLvar("", node.Ty);
        return node;
    }

    private Node Primary(ref Token rest, Token tok)
    {
        Token start = tok;
        if (Util.Equal(tok, "(") && Util.Equal(tok.Next, "{"))
        {
            var node = NewNode(NodeKind.StmtExpr, tok);
            node.Body = CompoundStmt(ref tok, tok.Next.Next).Body;
            rest = Util.Skip(tok, ")"); return node;
        }
        if (Util.Equal(tok, "(")) { Node node = Expr(ref tok, tok.Next); rest = Util.Skip(tok, ")"); return node; }
        if (Util.Equal(tok, "sizeof") && Util.Equal(tok.Next, "(") && IsTypename(tok.Next.Next))
        {
            CType ty = Typename(ref tok, tok.Next.Next); rest = Util.Skip(tok, ")");
            if (ty.Kind == TypeKind.Vla) { if (ty.VlaSize != null) return NewVarNode(ty.VlaSize, tok); Node lhs = ComputeVlaSize(ty, tok); return NewBinary(NodeKind.Comma, lhs, NewVarNode(ty.VlaSize, tok), tok); }
            return NewUlong(ty.Size, start);
        }
        if (Util.Equal(tok, "sizeof")) { Node node = Unary(ref rest, tok.Next); _types.AddType(node); if (node.Ty.Kind == TypeKind.Vla) return NewVarNode(node.Ty.VlaSize, tok); return NewUlong(node.Ty.Size, tok); }
        if (Util.Equal(tok, "_Alignof") && Util.Equal(tok.Next, "(") && IsTypename(tok.Next.Next))
        { CType ty = Typename(ref tok, tok.Next.Next); rest = Util.Skip(tok, ")"); return NewUlong(ty.Align, tok); }
        if (Util.Equal(tok, "_Alignof")) { Node node = Unary(ref rest, tok.Next); _types.AddType(node); return NewUlong(node.Ty.Align, tok); }
        if (Util.Equal(tok, "_Generic")) return GenericSelection(ref rest, tok.Next);
        if (Util.Equal(tok, "__builtin_types_compatible_p"))
        { tok = Util.Skip(tok.Next, "("); CType t1 = Typename(ref tok, tok); tok = Util.Skip(tok, ","); CType t2 = Typename(ref tok, tok); rest = Util.Skip(tok, ")"); return NewNum(TypeSystem.IsCompatible(t1, t2) ? 1 : 0, start); }
        if (Util.Equal(tok, "__builtin_reg_class"))
        { tok = Util.Skip(tok.Next, "("); CType ty = Typename(ref tok, tok); rest = Util.Skip(tok, ")"); if (TypeSystem.IsInteger(ty) || ty.Kind == TypeKind.Ptr) return NewNum(0, start); if (TypeSystem.IsFlonum(ty)) return NewNum(1, start); return NewNum(2, start); }
        if (Util.Equal(tok, "__builtin_compare_and_swap"))
        { var node = NewNode(NodeKind.Cas, tok); tok = Util.Skip(tok.Next, "("); node.CasAddr = Assign(ref tok, tok); tok = Util.Skip(tok, ","); node.CasOld = Assign(ref tok, tok); tok = Util.Skip(tok, ","); node.CasNew = Assign(ref tok, tok); rest = Util.Skip(tok, ")"); return node; }
        if (Util.Equal(tok, "__builtin_atomic_exchange"))
        { var node = NewNode(NodeKind.Exch, tok); tok = Util.Skip(tok.Next, "("); node.Lhs = Assign(ref tok, tok); tok = Util.Skip(tok, ","); node.Rhs = Assign(ref tok, tok); rest = Util.Skip(tok, ")"); return node; }
        if (tok.Kind == TokenKind.Ident)
        {
            VarScope sc = FindVar(tok); rest = tok.Next;
            if (sc != null && sc.Var != null && sc.Var.IsFunction) { if (_currentFn != null) _currentFn.Refs.Add(sc.Var.Name); else sc.Var.IsRoot = true; }
            if (sc != null) { if (sc.Var != null) return NewVarNode(sc.Var, tok); if (sc.EnumTy != null) return NewNum(sc.EnumVal, tok); }
            if (Util.Equal(tok.Next, "(")) Util.ErrorTok(tok, "implicit declaration of a function");
            Util.ErrorTok(tok, "undefined variable");
        }
        if (tok.Kind == TokenKind.Str) { Obj v = NewStringLiteral(tok.Str, tok.Ty); rest = tok.Next; return NewVarNode(v, tok); }
        if (tok.Kind == TokenKind.Num)
        {
            Node node;
            if (TypeSystem.IsFlonum(tok.Ty)) { node = NewNode(NodeKind.Num, tok); node.FVal = tok.FVal; }
            else node = NewNum(tok.Val, tok);
            node.Ty = tok.Ty; rest = tok.Next; return node;
        }
        Util.ErrorTok(tok, "expected an expression"); return null;
    }

    private Node GenericSelection(ref Token rest, Token tok)
    {
        Token start = tok; tok = Util.Skip(tok, "(");
        Node ctrl = Assign(ref tok, tok); _types.AddType(ctrl);
        CType t1 = ctrl.Ty;
        if (t1.Kind == TypeKind.Func) t1 = _types.PointerTo(t1);
        else if (t1.Kind == TypeKind.Array) t1 = _types.PointerTo(t1.Base);
        Node ret = null;
        while (!Util.Consume(ref rest, tok, ")"))
        {
            tok = Util.Skip(tok, ",");
            if (Util.Equal(tok, "default")) { tok = Util.Skip(tok.Next, ":"); Node node = Assign(ref tok, tok); if (ret == null) ret = node; continue; }
            CType t2 = Typename(ref tok, tok); tok = Util.Skip(tok, ":"); Node node2 = Assign(ref tok, tok);
            if (TypeSystem.IsCompatible(t1, t2)) ret = node2;
        }
        if (ret == null) Util.ErrorTok(start, "controlling expression type not compatible with any generic association type");
        return ret;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statements
    // ═══════════════════════════════════════════════════════════════

    private Node Stmt(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, "return"))
        {
            var node = NewNode(NodeKind.Return, tok);
            if (Util.Consume(ref rest, tok.Next, ";")) return node;
            Node exp = Expr(ref tok, tok.Next); rest = Util.Skip(tok, ";");
            _types.AddType(exp);
            CType rty = _currentFn.Ty.ReturnTy;
            if (rty.Kind != TypeKind.Struct && rty.Kind != TypeKind.Union) exp = _types.NewCast(exp, rty);
            node.Lhs = exp; return node;
        }
        if (Util.Equal(tok, "if"))
        {
            var node = NewNode(NodeKind.If, tok);
            tok = Util.Skip(tok.Next, "("); node.Cond = Expr(ref tok, tok); tok = Util.Skip(tok, ")");
            node.Then = Stmt(ref tok, tok);
            if (Util.Equal(tok, "else")) node.Els = Stmt(ref tok, tok.Next);
            rest = tok; return node;
        }
        if (Util.Equal(tok, "switch"))
        {
            var node = NewNode(NodeKind.Switch, tok);
            tok = Util.Skip(tok.Next, "("); node.Cond = Expr(ref tok, tok); tok = Util.Skip(tok, ")");
            Node sw = _currentSwitch; _currentSwitch = node;
            string brk = _brkLabel; _brkLabel = node.BrkLabel = NewUniqueName();
            node.Then = Stmt(ref rest, tok);
            _currentSwitch = sw; _brkLabel = brk; return node;
        }
        if (Util.Equal(tok, "case"))
        {
            if (_currentSwitch == null) Util.ErrorTok(tok, "stray case");
            var node = NewNode(NodeKind.Case, tok);
            long begin = ConstExpr(ref tok, tok.Next);
            long end;
            if (Util.Equal(tok, "...")) { end = ConstExpr(ref tok, tok.Next); if (end < begin) Util.ErrorTok(tok, "empty case range specified"); }
            else end = begin;
            tok = Util.Skip(tok, ":"); node.Label = NewUniqueName(); node.Lhs = Stmt(ref rest, tok);
            node.Begin = begin; node.End = end; node.CaseNext = _currentSwitch.CaseNext; _currentSwitch.CaseNext = node; return node;
        }
        if (Util.Equal(tok, "default"))
        {
            if (_currentSwitch == null) Util.ErrorTok(tok, "stray default");
            var node = NewNode(NodeKind.Case, tok); tok = Util.Skip(tok.Next, ":");
            node.Label = NewUniqueName(); node.Lhs = Stmt(ref rest, tok); _currentSwitch.DefaultCase = node; return node;
        }
        if (Util.Equal(tok, "for"))
        {
            var node = NewNode(NodeKind.For, tok); tok = Util.Skip(tok.Next, "(");
            EnterScope();
            string brk = _brkLabel, cont = _contLabel;
            _brkLabel = node.BrkLabel = NewUniqueName(); _contLabel = node.ContLabel = NewUniqueName();
            if (IsTypename(tok)) { CType basety = Declspec(ref tok, tok, null); node.Init = Declaration(ref tok, tok, basety, null); }
            else node.Init = ExprStmt(ref tok, tok);
            if (!Util.Equal(tok, ";")) node.Cond = Expr(ref tok, tok);
            tok = Util.Skip(tok, ";");
            if (!Util.Equal(tok, ")")) node.Inc = Expr(ref tok, tok);
            tok = Util.Skip(tok, ")"); node.Then = Stmt(ref rest, tok);
            LeaveScope(); _brkLabel = brk; _contLabel = cont; return node;
        }
        if (Util.Equal(tok, "while"))
        {
            var node = NewNode(NodeKind.For, tok); tok = Util.Skip(tok.Next, "(");
            string brk = _brkLabel, cont = _contLabel;
            _brkLabel = node.BrkLabel = NewUniqueName(); _contLabel = node.ContLabel = NewUniqueName();
            node.Cond = Expr(ref tok, tok); tok = Util.Skip(tok, ")"); node.Then = Stmt(ref rest, tok);
            _brkLabel = brk; _contLabel = cont; return node;
        }
        if (Util.Equal(tok, "do"))
        {
            var node = NewNode(NodeKind.Do, tok);
            string brk = _brkLabel, cont = _contLabel;
            _brkLabel = node.BrkLabel = NewUniqueName(); _contLabel = node.ContLabel = NewUniqueName();
            node.Then = Stmt(ref tok, tok.Next);
            _brkLabel = brk; _contLabel = cont;
            tok = Util.Skip(tok, "while"); tok = Util.Skip(tok, "("); node.Cond = Expr(ref tok, tok);
            tok = Util.Skip(tok, ")"); rest = Util.Skip(tok, ";"); return node;
        }
        if (Util.Equal(tok, "asm")) return AsmStmt(ref rest, tok);
        if (Util.Equal(tok, "goto"))
        {
            if (Util.Equal(tok.Next, "*")) { var node = NewNode(NodeKind.GotoExpr, tok); node.Lhs = Expr(ref tok, tok.Next.Next); rest = Util.Skip(tok, ";"); return node; }
            var gn = NewNode(NodeKind.Goto, tok); gn.Label = GetIdent(tok.Next); gn.GotoNext = _gotos; _gotos = gn;
            rest = Util.Skip(tok.Next.Next, ";"); return gn;
        }
        if (Util.Equal(tok, "break")) { if (_brkLabel == null) Util.ErrorTok(tok, "stray break"); var node = NewNode(NodeKind.Goto, tok); node.UniqueLabel = _brkLabel; rest = Util.Skip(tok.Next, ";"); return node; }
        if (Util.Equal(tok, "continue")) { if (_contLabel == null) Util.ErrorTok(tok, "stray continue"); var node = NewNode(NodeKind.Goto, tok); node.UniqueLabel = _contLabel; rest = Util.Skip(tok.Next, ";"); return node; }
        if (tok.Kind == TokenKind.Ident && Util.Equal(tok.Next, ":"))
        {
            var node = NewNode(NodeKind.Label, tok); node.Label = Util.GetTokenText(tok);
            node.UniqueLabel = NewUniqueName(); node.Lhs = Stmt(ref rest, tok.Next.Next);
            node.GotoNext = _labels; _labels = node; return node;
        }
        if (Util.Equal(tok, "{")) return CompoundStmt(ref rest, tok.Next);
        return ExprStmt(ref rest, tok);
    }

    private Node AsmStmt(ref Token rest, Token tok)
    {
        var node = NewNode(NodeKind.Asm, tok); tok = tok.Next;
        while (Util.Equal(tok, "volatile") || Util.Equal(tok, "inline")) tok = tok.Next;
        tok = Util.Skip(tok, "(");
        if (tok.Kind != TokenKind.Str || tok.Ty.Base.Kind != TypeKind.Char)
            Util.ErrorTok(tok, "expected string literal");
        node.AsmStr = Encoding.UTF8.GetString(tok.Str, 0, tok.Str.Length - 1);
        rest = Util.Skip(tok.Next, ")");
        return node;
    }

    private Node CompoundStmt(ref Token rest, Token tok)
    {
        var node = NewNode(NodeKind.Block, tok);
        Node head = new(), cur = head;
        EnterScope();
        while (!Util.Equal(tok, "}"))
        {
            if (IsTypename(tok) && !Util.Equal(tok.Next, ":"))
            {
                VarAttr attr = new();
                CType basety = Declspec(ref tok, tok, attr);
                if (attr.IsTypedef) { tok = ParseTypedef(tok, basety); continue; }
                if (IsFunction(tok)) { tok = Function(tok, basety, attr); continue; }
                if (attr.IsExtern) { tok = GlobalVariable(tok, basety, attr); continue; }
                cur = cur.Next = Declaration(ref tok, tok, basety, attr);
            }
            else cur = cur.Next = Stmt(ref tok, tok);
            _types.AddType(cur);
        }
        LeaveScope();
        node.Body = head.Next; rest = tok.Next; return node;
    }

    private Node ExprStmt(ref Token rest, Token tok)
    {
        if (Util.Equal(tok, ";")) { rest = tok.Next; return NewNode(NodeKind.Block, tok); }
        var node = NewNode(NodeKind.ExprStmt, tok); node.Lhs = Expr(ref tok, tok);
        rest = Util.Skip(tok, ";"); return node;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Declarations and initializers (simplified)
    // ═══════════════════════════════════════════════════════════════

    private Node ComputeVlaSize(CType ty, Token tok)
    {
        Node node = NewNode(NodeKind.NullExpr, tok);
        if (ty.Base != null) node = NewBinary(NodeKind.Comma, node, ComputeVlaSize(ty.Base, tok), tok);
        if (ty.Kind != TypeKind.Vla) return node;
        Node baseSz = ty.Base.Kind == TypeKind.Vla ? NewVarNode(ty.Base.VlaSize, tok) : NewNum(ty.Base.Size, tok);
        ty.VlaSize = NewLvar("", _types.SizeType);
        Node expr = NewBinary(NodeKind.Assign, NewVarNode(ty.VlaSize, tok), NewBinary(NodeKind.Mul, ty.VlaLen, baseSz, tok), tok);
        return NewBinary(NodeKind.Comma, node, expr, tok);
    }

    private Node NewAlloca(Node sz)
    {
        var node = NewUnary(NodeKind.FunCall, NewVarNode(_builtinAlloca, sz.Tok), sz.Tok);
        node.FuncTy = _builtinAlloca.Ty; node.Ty = _builtinAlloca.Ty.ReturnTy; node.Args = sz;
        _types.AddType(sz); return node;
    }

    private Node Declaration(ref Token rest, Token tok, CType basety, VarAttr attr)
    {
        Node head = new(), cur = head;
        int i = 0;
        while (!Util.Equal(tok, ";"))
        {
            if (i++ > 0) tok = Util.Skip(tok, ",");
            CType ty = Declarator(ref tok, tok, basety);
            if (ty.Kind == TypeKind.Void) Util.ErrorTok(tok, "variable declared void");
            if (ty.Name == null) Util.ErrorTok(ty.NamePos, "variable name omitted");
            if (attr != null && attr.IsStatic)
            {
                Obj v = NewAnonGvar(ty);
                PushScope(GetIdent(ty.Name)).Var = v;
                if (Util.Equal(tok, "=")) GvarInitializer(ref tok, tok.Next, v);
                continue;
            }
            cur = cur.Next = NewUnary(NodeKind.ExprStmt, ComputeVlaSize(ty, tok), tok);
            if (ty.Kind == TypeKind.Vla)
            {
                if (Util.Equal(tok, "=")) Util.ErrorTok(tok, "variable-sized object may not be initialized");
                Obj v = NewLvar(GetIdent(ty.Name), ty);
                Token vtok = ty.Name;
                Node expr = NewBinary(NodeKind.Assign, NewVlaPtr(v, vtok), NewAlloca(NewVarNode(ty.VlaSize, vtok)), vtok);
                cur = cur.Next = NewUnary(NodeKind.ExprStmt, expr, vtok); continue;
            }
            Obj var = NewLvar(GetIdent(ty.Name), ty);
            if (attr != null && attr.Align != 0) var.Align = attr.Align;
            if (Util.Equal(tok, "=")) { Node expr = LvarInitializer(ref tok, tok.Next, var); cur = cur.Next = NewUnary(NodeKind.ExprStmt, expr, tok); }
            if (var.Ty.Size < 0) Util.ErrorTok(ty.Name, "variable has incomplete type");
            if (var.Ty.Kind == TypeKind.Void) Util.ErrorTok(ty.Name, "variable declared void");
        }
        var block = NewNode(NodeKind.Block, tok); block.Body = head.Next; rest = tok.Next; return block;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Initializer helpers
    // ═══════════════════════════════════════════════════════════════

    private static void CopyInitializer(Initializer dst, Initializer src)
    {
        dst.Ty = src.Ty; dst.Tok = src.Tok; dst.IsFlexible = src.IsFlexible;
        dst.Expr = src.Expr; dst.Children = src.Children; dst.Mem = src.Mem;
    }

    private Token SkipExcessElement(Token tok)
    {
        if (Util.Equal(tok, "{")) { tok = SkipExcessElement(tok.Next); return Util.Skip(tok, "}"); }
        Assign(ref tok, tok);
        return tok;
    }

    // array-designator = "[" const-expr "]"
    private void ArrayDesignator(ref Token rest, Token tok, CType ty, out int begin, out int end)
    {
        begin = (int)ConstExpr(ref tok, tok.Next);
        if (begin >= ty.ArrayLen)
            Util.ErrorTok(tok, "array designator index exceeds array bounds");
        if (Util.Equal(tok, "..."))
        {
            end = (int)ConstExpr(ref tok, tok.Next);
            if (end >= ty.ArrayLen)
                Util.ErrorTok(tok, "array designator index exceeds array bounds");
            if (end < begin)
                Util.ErrorTok(tok, "array designator range is empty");
        }
        else
        {
            end = begin;
        }
        rest = Util.Skip(tok, "]");
    }

    // struct-designator = "." ident
    private Member StructDesignator(ref Token rest, Token tok, CType ty)
    {
        Token start = tok;
        tok = Util.Skip(tok, ".");
        if (tok.Kind != TokenKind.Ident)
            Util.ErrorTok(tok, "expected a field designator");
        for (Member mem = ty.Members; mem != null; mem = mem.Next)
        {
            if ((mem.Ty.Kind == TypeKind.Struct || mem.Ty.Kind == TypeKind.Union) && mem.Name == null)
            {
                if (GetStructMember(mem.Ty, tok) != null) { rest = start; return mem; }
                continue;
            }
            if (mem.Name != null && Util.GetTokenText(mem.Name) == Util.GetTokenText(tok))
            { rest = tok.Next; return mem; }
        }
        Util.ErrorTok(tok, "struct has no such member");
        return null;
    }

    // designation = ("[" const-expr "]" | "." ident)* "="? initializer
    private void Designation(ref Token rest, Token tok, Initializer init)
    {
        if (Util.Equal(tok, "["))
        {
            if (init.Ty.Kind != TypeKind.Array)
                Util.ErrorTok(tok, "array index in non-array initializer");
            ArrayDesignator(ref tok, tok, init.Ty, out int begin, out int end);
            Token tok2 = tok;
            for (int i = begin; i <= end; i++)
                Designation(ref tok2, tok, init.Children[i]);
            ArrayInitializer2(ref rest, tok2, init, begin + 1);
            return;
        }
        if (Util.Equal(tok, ".") && init.Ty.Kind == TypeKind.Struct)
        {
            Member mem = StructDesignator(ref tok, tok, init.Ty);
            Designation(ref tok, tok, init.Children[mem.Idx]);
            init.Expr = null;
            StructInitializer2(ref rest, tok, init, mem.Next);
            return;
        }
        if (Util.Equal(tok, ".") && init.Ty.Kind == TypeKind.Union)
        {
            Member mem = StructDesignator(ref tok, tok, init.Ty);
            init.Mem = mem;
            Designation(ref rest, tok, init.Children[mem.Idx]);
            return;
        }
        if (Util.Equal(tok, "."))
            Util.ErrorTok(tok, "field name not in struct or union initializer");
        if (Util.Equal(tok, "="))
            tok = tok.Next;
        Initializer2(ref rest, tok, init);
    }

    private void Initializer2(ref Token rest, Token tok, Initializer init)
    {
        if (init.Ty.Kind == TypeKind.Array && tok.Kind == TokenKind.Str) { StringInitializer(ref rest, tok, init); return; }
        if (init.Ty.Kind == TypeKind.Array) { if (Util.Equal(tok, "{")) ArrayInitializer1(ref rest, tok, init); else ArrayInitializer2(ref rest, tok, init, 0); return; }
        if (init.Ty.Kind == TypeKind.Struct)
        {
            if (Util.Equal(tok, "{")) { StructInitializer1(ref rest, tok, init); return; }
            Node expr = Assign(ref rest, tok); _types.AddType(expr);
            if (expr.Ty.Kind == TypeKind.Struct) { init.Expr = expr; return; }
            StructInitializer2(ref rest, tok, init, init.Ty.Members); return;
        }
        if (init.Ty.Kind == TypeKind.Union) { UnionInitializer(ref rest, tok, init); return; }
        if (Util.Equal(tok, "{")) { Initializer2(ref tok, tok.Next, init); rest = Util.Skip(tok, "}"); return; }
        init.Expr = Assign(ref rest, tok);
    }

    private void StringInitializer(ref Token rest, Token tok, Initializer init)
    {
        if (init.IsFlexible) CopyInitializer(init, NewInitializer(TypeSystem.ArrayOf(init.Ty.Base, tok.Ty.ArrayLen), false));
        int len = Math.Min(init.Ty.ArrayLen, tok.Ty.ArrayLen);
        switch (init.Ty.Base.Size)
        {
            case 1: for (int j = 0; j < len; j++) init.Children[j].Expr = NewNum((sbyte)tok.Str[j], tok); break;
            case 2: for (int j = 0; j < len; j++) { ushort v = BitConverter.ToUInt16(tok.Str, j * 2); init.Children[j].Expr = NewNum(v, tok); } break;
            case 4: for (int j = 0; j < len; j++) { uint v = BitConverter.ToUInt32(tok.Str, j * 4); init.Children[j].Expr = NewNum(v, tok); } break;
        }
        rest = tok.Next;
    }

    private void ArrayInitializer1(ref Token rest, Token tok, Initializer init)
    {
        tok = Util.Skip(tok, "{");
        if (init.IsFlexible) { int len = CountArrayInitElements(tok, init.Ty); CopyInitializer(init, NewInitializer(TypeSystem.ArrayOf(init.Ty.Base, len), false)); }
        bool first = true;
        for (int i = 0; !ConsumeEnd(ref rest, tok); i++)
        {
            if (!first) tok = Util.Skip(tok, ","); first = false;
            if (Util.Equal(tok, "["))
            {
                ArrayDesignator(ref tok, tok, init.Ty, out int begin, out int end);
                Token tok2 = tok;
                for (int j = begin; j <= end; j++) Designation(ref tok2, tok, init.Children[j]);
                tok = tok2; i = end; continue;
            }
            if (i < init.Ty.ArrayLen) Initializer2(ref tok, tok, init.Children[i]);
            else tok = SkipExcessElement(tok);
        }
    }

    private void ArrayInitializer2(ref Token rest, Token tok, Initializer init, int i)
    {
        if (init.IsFlexible) { int len = CountArrayInitElements(tok, init.Ty); CopyInitializer(init, NewInitializer(TypeSystem.ArrayOf(init.Ty.Base, len), false)); }
        for (; i < init.Ty.ArrayLen && !IsEnd(tok); i++)
        {
            Token start = tok;
            if (i > 0) tok = Util.Skip(tok, ",");
            if (Util.Equal(tok, "[") || Util.Equal(tok, ".")) { rest = start; return; }
            Initializer2(ref tok, tok, init.Children[i]);
        }
        rest = tok;
    }

    private int CountArrayInitElements(Token tok, CType ty)
    {
        bool first = true; Initializer dummy = NewInitializer(ty.Base, true);
        int i = 0, max = 0;
        while (!ConsumeEnd(ref tok, tok))
        {
            if (!first) tok = Util.Skip(tok, ","); first = false;
            if (Util.Equal(tok, "["))
            {
                i = (int)ConstExpr(ref tok, tok.Next);
                if (Util.Equal(tok, "...")) i = (int)ConstExpr(ref tok, tok.Next);
                tok = Util.Skip(tok, "]");
                Designation(ref tok, tok, dummy);
            }
            else { Initializer2(ref tok, tok, dummy); }
            i++; max = Math.Max(max, i);
        }
        return max;
    }

    private void StructInitializer1(ref Token rest, Token tok, Initializer init)
    {
        tok = Util.Skip(tok, "{"); Member mem = init.Ty.Members; bool first = true;
        while (!ConsumeEnd(ref rest, tok))
        {
            if (!first) tok = Util.Skip(tok, ","); first = false;
            if (Util.Equal(tok, "."))
            {
                mem = StructDesignator(ref tok, tok, init.Ty);
                Designation(ref tok, tok, init.Children[mem.Idx]);
                mem = mem.Next; continue;
            }
            if (mem != null) { Initializer2(ref tok, tok, init.Children[mem.Idx]); mem = mem.Next; }
            else tok = SkipExcessElement(tok);
        }
    }

    private void StructInitializer2(ref Token rest, Token tok, Initializer init, Member mem)
    {
        bool first = true;
        for (; mem != null && !IsEnd(tok); mem = mem.Next)
        {
            Token start = tok; if (!first) tok = Util.Skip(tok, ","); first = false;
            if (Util.Equal(tok, "[") || Util.Equal(tok, ".")) { rest = start; return; }
            Initializer2(ref tok, tok, init.Children[mem.Idx]);
        }
        rest = tok;
    }

    private void UnionInitializer(ref Token rest, Token tok, Initializer init)
    {
        if (Util.Equal(tok, "{") && Util.Equal(tok.Next, "."))
        {
            Member mem = StructDesignator(ref tok, tok.Next, init.Ty);
            init.Mem = mem;
            Designation(ref tok, tok, init.Children[mem.Idx]);
            rest = Util.Skip(tok, "}"); return;
        }
        init.Mem = init.Ty.Members;
        if (Util.Equal(tok, "{"))
        { Initializer2(ref tok, tok.Next, init.Children[0]); Util.Consume(ref tok, tok, ","); rest = Util.Skip(tok, "}"); }
        else Initializer2(ref rest, tok, init.Children[0]);
    }

    private Initializer InitializerEntry(ref Token rest, Token tok, CType ty, out CType newTy)
    {
        Initializer init = NewInitializer(ty, true);
        Initializer2(ref rest, tok, init);
        if ((ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union) && ty.IsFlexible)
        {
            ty = CopyStructType(ty);
            Member mem = ty.Members; while (mem.Next != null) mem = mem.Next;
            mem.Ty = init.Children[mem.Idx].Ty;
            ty.Size += mem.Ty.Size;
            newTy = ty; return init;
        }
        newTy = init.Ty; return init;
    }

    private static CType CopyStructType(CType ty)
    {
        ty = TypeSystem.CopyType(ty);
        Member head = new(), cur = head;
        for (Member mem = ty.Members; mem != null; mem = mem.Next)
        {
            var m = new Member { Ty = mem.Ty, Name = mem.Name, Idx = mem.Idx, Align = mem.Align, Offset = mem.Offset, IsBitfield = mem.IsBitfield, BitOffset = mem.BitOffset, BitWidth = mem.BitWidth, Tok = mem.Tok };
            cur = cur.Next = m;
        }
        ty.Members = head.Next; return ty;
    }

    private Node InitDesgExpr(InitDesg desg, Token tok)
    {
        if (desg.Var != null) return NewVarNode(desg.Var, tok);
        if (desg.Member != null) { Node node = NewUnary(NodeKind.Member, InitDesgExpr(desg.Next, tok), tok); node.Member = desg.Member; return node; }
        return NewUnary(NodeKind.Deref, NewAdd(InitDesgExpr(desg.Next, tok), NewNum(desg.Idx, tok), tok), tok);
    }

    private Node CreateLvarInit(Initializer init, CType ty, InitDesg desg, Token tok)
    {
        if (ty.Kind == TypeKind.Array) { Node node = NewNode(NodeKind.NullExpr, tok); for (int i = 0; i < ty.ArrayLen; i++) { var d2 = new InitDesg { Next = desg, Idx = i }; node = NewBinary(NodeKind.Comma, node, CreateLvarInit(init.Children[i], ty.Base, d2, tok), tok); } return node; }
        if (ty.Kind == TypeKind.Struct && init.Expr == null) { Node node = NewNode(NodeKind.NullExpr, tok); for (Member mem = ty.Members; mem != null; mem = mem.Next) { var d2 = new InitDesg { Next = desg, Member = mem }; node = NewBinary(NodeKind.Comma, node, CreateLvarInit(init.Children[mem.Idx], mem.Ty, d2, tok), tok); } return node; }
        if (ty.Kind == TypeKind.Union) { Member mem = init.Mem ?? ty.Members; var d2 = new InitDesg { Next = desg, Member = mem }; return CreateLvarInit(init.Children[mem.Idx], mem.Ty, d2, tok); }
        if (init.Expr == null) return NewNode(NodeKind.NullExpr, tok);
        return NewBinary(NodeKind.Assign, InitDesgExpr(desg, tok), init.Expr, tok);
    }

    private Node LvarInitializer(ref Token rest, Token tok, Obj var)
    {
        Initializer init = InitializerEntry(ref rest, tok, var.Ty, out var.Ty);
        var desg = new InitDesg { Var = var };
        Node lhs = NewNode(NodeKind.MemZero, tok); lhs.Var = var;
        Node rhs = CreateLvarInit(init, var.Ty, desg, tok);
        return NewBinary(NodeKind.Comma, lhs, rhs, tok);
    }

    private void GvarInitializer(ref Token rest, Token tok, Obj var)
    {
        Initializer init = InitializerEntry(ref rest, tok, var.Ty, out var.Ty);
        Relocation head = new();
        byte[] buf = new byte[var.Ty.Size];
        WriteGvarData(head, init, var.Ty, buf, 0);
        var.InitData = buf;
        var.Rel = head.Next;
    }

    private Relocation WriteGvarData(Relocation cur, Initializer init, CType ty, byte[] buf, int offset)
    {
        if (ty.Kind == TypeKind.Array) { for (int i = 0; i < ty.ArrayLen; i++) cur = WriteGvarData(cur, init.Children[i], ty.Base, buf, offset + ty.Base.Size * i); return cur; }
        if (ty.Kind == TypeKind.Struct) { for (Member mem = ty.Members; mem != null; mem = mem.Next) { if (mem.IsBitfield) { Node expr = init.Children[mem.Idx].Expr; if (expr == null) break; ulong oldval = ReadBuf(buf, offset + mem.Offset, mem.Ty.Size); ulong newval = (ulong)Eval(expr); ulong mask = (1UL << mem.BitWidth) - 1; ulong combined = oldval | ((newval & mask) << mem.BitOffset); Util.WriteBuf(buf, offset + mem.Offset, (long)combined, mem.Ty.Size); } else cur = WriteGvarData(cur, init.Children[mem.Idx], mem.Ty, buf, offset + mem.Offset); } return cur; }
        if (ty.Kind == TypeKind.Union) { if (init.Mem == null) return cur; return WriteGvarData(cur, init.Children[init.Mem.Idx], init.Mem.Ty, buf, offset); }
        if (init.Expr == null) return cur;
        if (ty.Kind == TypeKind.Float) { Util.WriteFloat(buf, offset, (float)EvalDouble(init.Expr)); return cur; }
        if (ty.Kind == TypeKind.Double) { Util.WriteDouble(buf, offset, EvalDouble(init.Expr)); return cur; }
        if (ty.Kind == TypeKind.LDouble)
        {
            if (ty.Size == 8)
            {
                // LLP64: long double is same as double
                Util.WriteDouble(buf, offset, EvalDouble(init.Expr));
            }
            else
            {
                // LP64: 80-bit x87 extended precision, padded to 16 bytes
                byte[] f80 = Util.DoubleToF80Bytes(EvalDouble(init.Expr));
                Array.Copy(f80, 0, buf, offset, Math.Min(f80.Length, ty.Size));
            }
            return cur;
        }

        long val = Eval2(init.Expr, out Func<string> label);
        if (label == null) { Util.WriteBuf(buf, offset, val, ty.Size); return cur; }
        var rel = new Relocation { Offset = offset, Label = label, Addend = val };
        cur.Next = rel; return cur.Next;
    }

    private static ulong ReadBuf(byte[] buf, int offset, int sz)
    {
        if (sz == 1) return buf[offset];
        if (sz == 2) return BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));
        if (sz == 4) return BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
        if (sz == 8) return BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(offset));
        Util.Unreachable(); return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Top-level declarations
    // ═══════════════════════════════════════════════════════════════

    private Token ParseTypedef(Token tok, CType basety)
    {
        bool first = true;
        while (!Util.Consume(ref tok, tok, ";"))
        {
            if (!first) tok = Util.Skip(tok, ","); first = false;
            CType ty = Declarator(ref tok, tok, basety);
            if (ty.Name == null) Util.ErrorTok(ty.NamePos, "typedef name omitted");
            // For anonymous enum/struct/union, use the typedef name as the tag
            if (ty.TagName == null && (ty.Kind == TypeKind.Enum || ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union))
            {
                string tdName = GetIdent(ty.Name);
                // Set on this type and all Origin ancestors that lack a tag
                for (CType c = ty; c != null; c = c.Origin)
                {
                    if (c.TagName != null) break;
                    c.TagName = tdName;
                }
            }
            PushScope(GetIdent(ty.Name)).TypeDef = ty;
        }
        return tok;
    }

    private void CreateParamLvars(CType param)
    {
        if (param != null) { CreateParamLvars(param.Next); if (param.Name == null) Util.ErrorTok(param.NamePos, "parameter name omitted"); NewLvar(GetIdent(param.Name), param); }
    }

    private void ResolveGotoLabels()
    {
        for (Node x = _gotos; x != null; x = x.GotoNext)
        {
            for (Node y = _labels; y != null; y = y.GotoNext)
                if (x.Label == y.Label) { x.UniqueLabel = y.UniqueLabel; break; }
            if (x.UniqueLabel == null) Util.ErrorTok(x.Tok.Next, "use of undeclared label");
        }
        _gotos = _labels = null;
    }

    private Obj FindFunc(string name)
    {
        Scope sc = _scope; while (sc.Next != null) sc = sc.Next;
        if (sc.Vars.TryGetValue(name, out VarScope vs) && vs.Var != null && vs.Var.IsFunction) return vs.Var;
        return null;
    }

    private void MarkLive(Obj var)
    {
        if (!var.IsFunction || var.IsLive) return;
        var.IsLive = true;
        foreach (string name in var.Refs) { Obj fn = FindFunc(name); if (fn != null) MarkLive(fn); }
    }

    private bool IsFunction(Token tok)
    {
        if (Util.Equal(tok, ";")) return false;
        CType dummy = new();
        CType ty = Declarator(ref tok, tok, dummy);
        return ty.Kind == TypeKind.Func;
    }

    private Token Function(Token tok, CType basety, VarAttr attr)
    {
        CType ty = Declarator(ref tok, tok, basety);
        if (ty.Name == null) Util.ErrorTok(ty.NamePos, "function name omitted");
        string nameStr = GetIdent(ty.Name);
        Obj fn = FindFunc(nameStr);
        if (fn != null)
        {
            if (!fn.IsFunction) Util.ErrorTok(tok, "redeclared as a different kind of symbol");
            if (fn.IsDefinition && Util.Equal(tok, "{")) Util.ErrorTok(tok, $"redefinition of {nameStr}");
            if (!fn.IsStatic && attr.IsStatic) Util.ErrorTok(tok, "static declaration follows a non-static declaration");
            fn.IsDefinition = fn.IsDefinition || Util.Equal(tok, "{");

            // MSIL unprototyped function handling:
            //
            // In old C, `void f()` declares a function with *unspecified* parameters
            // (not "no parameters"). The parser sets IsVariadic=true, Params=null for
            // these. In native code this works because the caller pushes args onto the
            // stack and the callee pops what it expects — the ABI doesn't enforce
            // signature matching.
            //
            // In MSIL, call-site signatures must exactly match the callee's MethodDef
            // signature. There is no way to emit a correct call to an unprototyped
            // function without knowing its actual parameters. This makes cross-TU
            // unprototyped calls fundamentally unsupportable:
            //
            //   // tu1.c: void f(); void g() { f(42); }  — can't emit matching sig
            //   // tu2.c: void f(int x) { ... }           — definition expects int
            //
            // For the same-TU case (forward decl followed by definition), we update
            // fn.Ty here so the MethodDef gets the correct signature from the
            // definition. For cross-TU, the linker will reject mismatched signatures.
            if (Util.Equal(tok, "{"))
            {
                // Check calling convention compatibility (MSVC rejects clrcall vs cdecl)
                if (fn.Ty.CallConv != ty.CallConv)
                    Util.ErrorTok(ty.Name, "conflicting calling conventions in redeclaration");

                if (fn.Ty.IsVariadic && fn.Ty.Params == null)
                    fn.Ty = ty; // unprototyped K&R → update from definition
            }
        }
        else
        {
            fn = NewGvar(nameStr, ty);
            fn.IsFunction = true; fn.IsDefinition = Util.Equal(tok, "{");
            fn.IsStatic = attr.IsStatic || (attr.IsInline && !attr.IsExtern);
            fn.IsInline = attr.IsInline;
        }
        fn.IsRoot = !(fn.IsStatic && fn.IsInline);
        if (Util.Consume(ref tok, tok, ";")) return tok;
        _currentFn = fn; _locals = null; EnterScope();
        CreateParamLvars(ty.Params);
        CType rty = ty.ReturnTy;
        if ((rty.Kind == TypeKind.Struct || rty.Kind == TypeKind.Union) && rty.Size > 16) NewLvar("", _types.PointerTo(rty));
        fn.Params = _locals;
        if (ty.IsVariadic && ty.Params != null)
            Util.ErrorTok(ty.Name, "variadic function definitions are not supported in MSIL mode");
        if (ty.IsVariadic) fn.VaArea = NewLvar("__va_area__", TypeSystem.ArrayOf(_types.TyChar, 136));
        fn.AllocaBottom = NewLvar("__alloca_size__", _types.PointerTo(_types.TyChar));
        tok = Util.Skip(tok, "{");
        byte[] nameBytes = Encoding.UTF8.GetBytes(fn.Name);
        byte[] nameBytesNul = new byte[nameBytes.Length + 1];
        Array.Copy(nameBytes, nameBytesNul, nameBytes.Length);
        PushScope("__func__").Var = NewStringLiteral(nameBytesNul, TypeSystem.ArrayOf(_types.TyChar, nameBytes.Length + 1));
        PushScope("__FUNCTION__").Var = NewStringLiteral(nameBytesNul, TypeSystem.ArrayOf(_types.TyChar, nameBytes.Length + 1));
        fn.Body = CompoundStmt(ref tok, tok);
        fn.Locals = _locals; LeaveScope(); ResolveGotoLabels();
        return tok;
    }

    private Token GlobalVariable(Token tok, CType basety, VarAttr attr)
    {
        bool first = true;
        while (!Util.Consume(ref tok, tok, ";"))
        {
            if (!first) tok = Util.Skip(tok, ","); first = false;
            CType ty = Declarator(ref tok, tok, basety);
            if (ty.Name == null) Util.ErrorTok(ty.NamePos, "variable name omitted");
            Obj v = NewGvar(GetIdent(ty.Name), ty);
            v.IsDefinition = !attr.IsExtern; v.IsStatic = attr.IsStatic;
            v.IsTls = attr.IsTls;
            if (attr.Align != 0) v.Align = attr.Align;
            if (Util.Equal(tok, "=")) GvarInitializer(ref tok, tok.Next, v);
            else if (!attr.IsExtern && !attr.IsTls) v.IsTentative = true;
        }
        return tok;
    }

    private void ScanGlobals()
    {
        Obj head = new(); Obj cur = head;
        for (Obj v = _globals; v != null; v = v.Next)
        {
            if (!v.IsTentative) { cur = cur.Next = v; continue; }
            bool found = false;
            for (Obj v2 = _globals; v2 != null; v2 = v2.Next)
                if (v != v2 && v2.IsDefinition && v.Name == v2.Name) { found = true; break; }
            if (!found) cur = cur.Next = v;
        }
        cur.Next = null; _globals = head.Next;
    }

    private void DeclareBuiltinFunctions()
    {
        CType ty = TypeSystem.FuncType(_types.PointerTo(_types.TyVoid));
        ty.Params = TypeSystem.CopyType(_types.TyInt);
        _builtinAlloca = NewGvar("alloca", ty);
        _builtinAlloca.IsDefinition = false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public Obj Parse(Token tok)
    {
        DeclareBuiltinFunctions();
        _globals = null;
        while (tok.Kind != TokenKind.Eof)
        {
            VarAttr attr = new();
            CType basety = Declspec(ref tok, tok, attr);
            if (attr.IsTypedef) { tok = ParseTypedef(tok, basety); continue; }
            if (IsFunction(tok)) { tok = Function(tok, basety, attr); continue; }
            tok = GlobalVariable(tok, basety, attr);
        }
        for (Obj v = _globals; v != null; v = v.Next)
            if (v.IsRoot) MarkLive(v);
        ScanGlobals();
        return _globals;
    }
}
