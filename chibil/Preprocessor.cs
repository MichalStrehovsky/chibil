using System.Text;

namespace Chibil;

/// <summary>
/// C preprocessor — port of preprocess.c.
/// Takes a list of tokens and returns a new list with macros expanded
/// and directives processed.
/// </summary>
public class Preprocessor
{
    private readonly Tokenizer _tokenizer;
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
    private Parser _parser; // Set before preprocessing to enable #if evaluation

    private Dictionary<string, Macro> _macros = new();
    private CondIncl _condIncl;
    private Dictionary<string, bool> _pragmaOnce = new();
    private int _includeNextIdx;
    private int _counterValue;

    public void SetParser(Parser parser) { _parser = parser; }

    public Preprocessor(Tokenizer tokenizer, CompilerOptions options, TypeSystem types)
    {
        _tokenizer = tokenizer;
        _options = options;
        _types = types;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Hideset operations
    // ═══════════════════════════════════════════════════════════════

    private static Hideset NewHideset(string name) => new() { Name = name };

    private static Hideset HidesetUnion(Hideset hs1, Hideset hs2)
    {
        Hideset head = new(), cur = head;
        for (; hs1 != null; hs1 = hs1.Next)
            cur = cur.Next = NewHideset(hs1.Name);
        cur.Next = hs2;
        return head.Next;
    }

    private static bool HidesetContains(Hideset hs, string name)
    {
        for (; hs != null; hs = hs.Next)
            if (hs.Name == name || hs.Name.Length == name.Length && hs.Name == name)
                return true;
        return false;
    }

    private static bool HidesetContains(Hideset hs, byte[] buf, int loc, int len)
    {
        string name = Encoding.UTF8.GetString(buf, loc, len);
        return HidesetContains(hs, name);
    }

    private static Hideset HidesetIntersection(Hideset hs1, Hideset hs2)
    {
        Hideset head = new(), cur = head;
        for (; hs1 != null; hs1 = hs1.Next)
            if (HidesetContains(hs2, hs1.Name))
                cur = cur.Next = NewHideset(hs1.Name);
        return head.Next;
    }

    private static Token AddHideset(Token tok, Hideset hs)
    {
        Token head = new(), cur = head;
        for (; tok != null; tok = tok.Next)
        {
            Token t = CopyToken(tok);
            t.Hideset = HidesetUnion(t.Hideset, hs);
            cur = cur.Next = t;
        }
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Token operations
    // ═══════════════════════════════════════════════════════════════

    private static bool IsHash(Token tok) => tok.AtBol && Util.Equal(tok, "#");

    private static Token CopyToken(Token tok)
    {
        var t = new Token
        {
            Kind = tok.Kind, Buf = tok.Buf, Loc = tok.Loc, Len = tok.Len,
            Val = tok.Val, FVal = tok.FVal, Ty = tok.Ty, Str = tok.Str,
            File = tok.File, FileName = tok.FileName, LineNo = tok.LineNo,
            LineDelta = tok.LineDelta, AtBol = tok.AtBol, HasSpace = tok.HasSpace,
            Hideset = tok.Hideset, Origin = tok.Origin,
            AttrNext = tok.AttrNext, AttrArgs = tok.AttrArgs,
        };
        return t;
    }

    private static Token NewEof(Token tok)
    {
        Token t = CopyToken(tok);
        t.Kind = TokenKind.Eof;
        t.Len = 0;
        t.Next = null;
        return t;
    }

    private Token SkipLine(Token tok)
    {
        if (tok.AtBol) return tok;
        Util.WarnTok(tok, "extra token");
        while (!tok.AtBol)
            tok = tok.Next;
        return tok;
    }

    // Append tok2 to end of tok1
    private static Token Append(Token tok1, Token tok2)
    {
        if (tok1.Kind == TokenKind.Eof) return tok2;
        Token head = new(), cur = head;
        for (; tok1.Kind != TokenKind.Eof; tok1 = tok1.Next)
            cur = cur.Next = CopyToken(tok1);
        cur.Next = tok2;
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Conditional inclusion skip
    // ═══════════════════════════════════════════════════════════════

    private Token SkipCondIncl2(Token tok)
    {
        while (tok.Kind != TokenKind.Eof)
        {
            if (IsHash(tok) && (Util.Equal(tok.Next, "if") || Util.Equal(tok.Next, "ifdef") || Util.Equal(tok.Next, "ifndef")))
            {
                tok = SkipCondIncl2(tok.Next.Next);
                continue;
            }
            if (IsHash(tok) && Util.Equal(tok.Next, "endif"))
                return tok.Next.Next;
            tok = tok.Next;
        }
        return tok;
    }

    private Token SkipCondIncl(Token tok)
    {
        while (tok.Kind != TokenKind.Eof)
        {
            if (IsHash(tok) && (Util.Equal(tok.Next, "if") || Util.Equal(tok.Next, "ifdef") || Util.Equal(tok.Next, "ifndef")))
            {
                tok = SkipCondIncl2(tok.Next.Next);
                continue;
            }
            if (IsHash(tok) && (Util.Equal(tok.Next, "elif") || Util.Equal(tok.Next, "else") || Util.Equal(tok.Next, "endif")))
                break;
            tok = tok.Next;
        }
        return tok;
    }

    // ═══════════════════════════════════════════════════════════════
    //  String and token operations
    // ═══════════════════════════════════════════════════════════════

    private static string QuoteString(string str)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in str)
        {
            if (c == '\\' || c == '"') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private Token NewStrToken(string str, Token tmpl)
    {
        string buf = QuoteString(str);
        byte[] bytes = Util.StringToBytes(buf);
        return _tokenizer.Tokenize(_tokenizer.NewFile(tmpl.File.Name, tmpl.File.FileNo, bytes));
    }

    private Token CopyLine(ref Token rest, Token tok)
    {
        Token head = new(), cur = head;
        for (; !tok.AtBol; tok = tok.Next)
            cur = cur.Next = CopyToken(tok);
        cur.Next = NewEof(tok);
        rest = tok;
        return head.Next;
    }

    private Token NewNumToken(int val, Token tmpl)
    {
        string buf = $"{val}\n";
        byte[] bytes = Util.StringToBytes(buf);
        return _tokenizer.Tokenize(_tokenizer.NewFile(tmpl.File.Name, tmpl.File.FileNo, bytes));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Macro operations
    // ═══════════════════════════════════════════════════════════════

    private Macro FindMacro(Token tok)
    {
        if (tok.Kind != TokenKind.Ident) return null;
        string name = Util.GetTokenText(tok);
        _macros.TryGetValue(name, out Macro m);
        return m;
    }

    private Macro AddMacro(string name, bool isObjlike, Token body)
    {
        var m = new Macro { Name = name, IsObjlike = isObjlike, Body = body };
        _macros[name] = m;
        return m;
    }

    public void DefineMacro(string name, string buf)
    {
        byte[] bytes = Util.StringToBytes(buf);
        Token tok = _tokenizer.Tokenize(_tokenizer.NewFile("<built-in>", 1, bytes));
        AddMacro(name, true, tok);
    }

    public void UndefMacro(string name)
    {
        _macros.Remove(name);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Macro parameter reading
    // ═══════════════════════════════════════════════════════════════

    private MacroParam ReadMacroParams(ref Token rest, Token tok, out string vaArgsName)
    {
        vaArgsName = null;
        MacroParam head = new(), cur = head;

        while (!Util.Equal(tok, ")"))
        {
            if (cur != head)
                tok = Util.Skip(tok, ",");

            if (Util.Equal(tok, "..."))
            {
                vaArgsName = "__VA_ARGS__";
                rest = Util.Skip(tok.Next, ")");
                return head.Next;
            }

            if (tok.Kind != TokenKind.Ident)
                Util.ErrorTok(tok, "expected an identifier");

            if (Util.Equal(tok.Next, "..."))
            {
                vaArgsName = Util.GetTokenText(tok);
                rest = Util.Skip(tok.Next.Next, ")");
                return head.Next;
            }

            var m = new MacroParam { Name = Util.GetTokenText(tok) };
            cur = cur.Next = m;
            tok = tok.Next;
        }

        rest = tok.Next;
        return head.Next;
    }

    private void ReadMacroDefinition(ref Token rest, Token tok)
    {
        if (tok.Kind != TokenKind.Ident)
            Util.ErrorTok(tok, "macro name must be an identifier");
        string name = Util.GetTokenText(tok);
        tok = tok.Next;

        if (!tok.HasSpace && Util.Equal(tok, "("))
        {
            MacroParam @params = ReadMacroParams(ref tok, tok.Next, out string vaArgsName);
            Token body = CopyLine(ref rest, tok);
            Macro m = AddMacro(name, false, body);
            m.Params = @params;
            m.VaArgsName = vaArgsName;
        }
        else
        {
            AddMacro(name, true, CopyLine(ref rest, tok));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Macro argument reading
    // ═══════════════════════════════════════════════════════════════

    private MacroArg ReadMacroArgOne(ref Token rest, Token tok, bool readRest)
    {
        Token head = new(), cur = head;
        int level = 0;

        for (;;)
        {
            if (level == 0 && Util.Equal(tok, ")")) break;
            if (level == 0 && !readRest && Util.Equal(tok, ",")) break;
            if (tok.Kind == TokenKind.Eof)
                Util.ErrorTok(tok, "premature end of input");
            if (Util.Equal(tok, "(")) level++;
            else if (Util.Equal(tok, ")")) level--;
            cur = cur.Next = CopyToken(tok);
            tok = tok.Next;
        }

        cur.Next = NewEof(tok);
        var arg = new MacroArg { Tok = head.Next };
        rest = tok;
        return arg;
    }

    private MacroArg ReadMacroArgs(ref Token rest, Token tok, MacroParam @params, string vaArgsName)
    {
        Token start = tok;
        tok = tok.Next.Next; // skip macro name and '('

        MacroArg head = new(), cur = head;
        MacroParam pp = @params;
        for (; pp != null; pp = pp.Next)
        {
            if (cur != head)
                tok = Util.Skip(tok, ",");
            cur = cur.Next = ReadMacroArgOne(ref tok, tok, false);
            cur.Name = pp.Name;
        }

        if (vaArgsName != null)
        {
            MacroArg arg;
            if (Util.Equal(tok, ")"))
            {
                arg = new MacroArg { Tok = NewEof(tok) };
            }
            else
            {
                if (pp != @params)
                    tok = Util.Skip(tok, ",");
                arg = ReadMacroArgOne(ref tok, tok, true);
            }
            arg.Name = vaArgsName;
            arg.IsVaArgs = true;
            cur = cur.Next = arg;
        }
        else if (pp != null)
        {
            Util.ErrorTok(start, "too many arguments");
        }

        Util.Skip(tok, ")");
        rest = tok;
        return head.Next;
    }

    private static MacroArg FindArg(MacroArg args, Token tok)
    {
        string name = Util.GetTokenText(tok);
        for (MacroArg ap = args; ap != null; ap = ap.Next)
            if (ap.Name == name)
                return ap;
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Token joining and stringizing
    // ═══════════════════════════════════════════════════════════════

    private static string JoinTokens(Token tok, Token end)
    {
        var sb = new StringBuilder();
        bool first = true;
        for (Token t = tok; t != end && t.Kind != TokenKind.Eof; t = t.Next)
        {
            if (!first && t.HasSpace) sb.Append(' ');
            first = false;
            sb.Append(Encoding.UTF8.GetString(t.Buf, t.Loc, t.Len));
        }
        return sb.ToString();
    }

    private Token Stringize(Token hash, Token arg)
    {
        string s = JoinTokens(arg, null);
        return NewStrToken(s, hash);
    }

    private Token Paste(Token lhs, Token rhs)
    {
        string buf = Encoding.UTF8.GetString(lhs.Buf, lhs.Loc, lhs.Len) +
                     Encoding.UTF8.GetString(rhs.Buf, rhs.Loc, rhs.Len);
        byte[] bytes = Util.StringToBytes(buf);
        Token tok = _tokenizer.Tokenize(_tokenizer.NewFile(lhs.File.Name, lhs.File.FileNo, bytes));
        if (tok.Next.Kind != TokenKind.Eof)
            Util.ErrorTok(lhs, $"pasting forms '{buf}', an invalid token");
        return tok;
    }

    private static bool HasVarargs(MacroArg args)
    {
        for (MacroArg ap = args; ap != null; ap = ap.Next)
            if (ap.Name == "__VA_ARGS__")
                return ap.Tok.Kind != TokenKind.Eof;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Macro substitution
    // ═══════════════════════════════════════════════════════════════

    private Token Subst(Token tok, MacroArg args)
    {
        Token head = new(), cur = head;

        while (tok.Kind != TokenKind.Eof)
        {
            // "#" followed by a parameter → stringize
            if (Util.Equal(tok, "#"))
            {
                MacroArg arg = FindArg(args, tok.Next);
                if (arg == null)
                    Util.ErrorTok(tok.Next, "'#' is not followed by a macro parameter");
                cur = cur.Next = Stringize(tok, arg.Tok);
                tok = tok.Next.Next;
                continue;
            }

            // [GNU] `,##__VA_ARGS__`
            if (Util.Equal(tok, ",") && Util.Equal(tok.Next, "##"))
            {
                MacroArg arg = FindArg(args, tok.Next.Next);
                if (arg != null && arg.IsVaArgs)
                {
                    if (arg.Tok.Kind == TokenKind.Eof)
                        tok = tok.Next.Next.Next;
                    else
                    {
                        cur = cur.Next = CopyToken(tok);
                        tok = tok.Next.Next;
                    }
                    continue;
                }
            }

            if (Util.Equal(tok, "##"))
            {
                if (cur == head)
                    Util.ErrorTok(tok, "'##' cannot appear at start of macro expansion");
                if (tok.Next.Kind == TokenKind.Eof)
                    Util.ErrorTok(tok, "'##' cannot appear at end of macro expansion");

                MacroArg arg = FindArg(args, tok.Next);
                if (arg != null)
                {
                    if (arg.Tok.Kind != TokenKind.Eof)
                    {
                        Token pasted = Paste(cur, arg.Tok);
                        cur.Kind = pasted.Kind; cur.Buf = pasted.Buf; cur.Loc = pasted.Loc;
                        cur.Len = pasted.Len; cur.Val = pasted.Val; cur.FVal = pasted.FVal;
                        cur.Ty = pasted.Ty; cur.Str = pasted.Str; cur.File = pasted.File;
                        cur.FileName = pasted.FileName; cur.LineNo = pasted.LineNo;
                        for (Token t = arg.Tok.Next; t.Kind != TokenKind.Eof; t = t.Next)
                            cur = cur.Next = CopyToken(t);
                    }
                    tok = tok.Next.Next;
                    continue;
                }

                Token pasted2 = Paste(cur, tok.Next);
                cur.Kind = pasted2.Kind; cur.Buf = pasted2.Buf; cur.Loc = pasted2.Loc;
                cur.Len = pasted2.Len; cur.Val = pasted2.Val; cur.FVal = pasted2.FVal;
                cur.Ty = pasted2.Ty; cur.Str = pasted2.Str; cur.File = pasted2.File;
                cur.FileName = pasted2.FileName; cur.LineNo = pasted2.LineNo;
                tok = tok.Next.Next;
                continue;
            }

            MacroArg arg2 = FindArg(args, tok);

            if (arg2 != null && Util.Equal(tok.Next, "##"))
            {
                Token rhs = tok.Next.Next;
                if (arg2.Tok.Kind == TokenKind.Eof)
                {
                    MacroArg arg3 = FindArg(args, rhs);
                    if (arg3 != null)
                    {
                        for (Token t = arg3.Tok; t.Kind != TokenKind.Eof; t = t.Next)
                            cur = cur.Next = CopyToken(t);
                    }
                    else
                    {
                        cur = cur.Next = CopyToken(rhs);
                    }
                    tok = rhs.Next;
                    continue;
                }

                for (Token t = arg2.Tok; t.Kind != TokenKind.Eof; t = t.Next)
                    cur = cur.Next = CopyToken(t);
                tok = tok.Next;
                continue;
            }

            // __VA_OPT__
            if (Util.Equal(tok, "__VA_OPT__") && Util.Equal(tok.Next, "("))
            {
                MacroArg vaOptArg = ReadMacroArgOne(ref tok, tok.Next.Next, true);
                if (HasVarargs(args))
                    for (Token t = vaOptArg.Tok; t.Kind != TokenKind.Eof; t = t.Next)
                        cur = cur.Next = t;
                tok = Util.Skip(tok, ")");
                continue;
            }

            // Expand macro argument
            if (arg2 != null)
            {
                Token t = Preprocess2(arg2.Tok);
                t.AtBol = tok.AtBol;
                t.HasSpace = tok.HasSpace;
                for (; t.Kind != TokenKind.Eof; t = t.Next)
                    cur = cur.Next = CopyToken(t);
                tok = tok.Next;
                continue;
            }

            cur = cur.Next = CopyToken(tok);
            tok = tok.Next;
        }

        cur.Next = tok;
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Macro expansion
    // ═══════════════════════════════════════════════════════════════

    private bool ExpandMacro(ref Token rest, Token tok)
    {
        if (HidesetContains(tok.Hideset, tok.Buf, tok.Loc, tok.Len))
            return false;

        Macro m = FindMacro(tok);
        if (m == null) return false;

        if (Util.Equal(tok, "__attribute__") && !m.IsObjlike && m.Body.Kind == TokenKind.Eof &&
            Util.Equal(tok.Next, "("))
            return false;

        // Built-in dynamic macro
        if (m.Handler != null)
        {
            rest = m.Handler(tok);
            rest.Next = tok.Next;
            return true;
        }

        // Object-like macro
        if (m.IsObjlike)
        {
            Hideset hs = HidesetUnion(tok.Hideset, NewHideset(m.Name));
            Token body = AddHideset(m.Body, hs);
            for (Token t = body; t != null && t.Kind != TokenKind.Eof; t = t.Next)
                t.Origin = tok;
            rest = Append(body, tok.Next);
            if (rest != null)
            {
                rest.AtBol = tok.AtBol;
                rest.HasSpace = tok.HasSpace;
            }
            return true;
        }

        // Function-like macro: not followed by '(' → treat as identifier
        if (!Util.Equal(tok.Next, "("))
            return false;

        Token macroToken = tok;
        MacroArg args = ReadMacroArgs(ref tok, tok, m.Params, m.VaArgsName);
        Token rparen = tok;

        Hideset hs2 = HidesetIntersection(macroToken.Hideset, rparen.Hideset);
        hs2 = HidesetUnion(hs2, NewHideset(m.Name));

        Token body2 = Subst(m.Body, args);
        body2 = AddHideset(body2, hs2);
        for (Token t = body2; t != null && t.Kind != TokenKind.Eof; t = t.Next)
            t.Origin = macroToken;
        rest = Append(body2, tok.Next);
        if (rest != null)
        {
            rest.AtBol = macroToken.AtBol;
            rest.HasSpace = macroToken.HasSpace;
        }
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Include path searching
    // ═══════════════════════════════════════════════════════════════

    public string SearchIncludePaths(string filename)
    {
        if (filename.StartsWith("/")) return filename;

        for (int i = 0; i < _options.IncludePaths.Count; i++)
        {
            string path = $"{_options.IncludePaths[i]}/{filename}";
            if (System.IO.File.Exists(path))
            {
                _includeNextIdx = i + 1;
                return path;
            }
        }
        return null;
    }

    private string SearchIncludeNext(string filename)
    {
        for (; _includeNextIdx < _options.IncludePaths.Count; _includeNextIdx++)
        {
            string path = $"{_options.IncludePaths[_includeNextIdx]}/{filename}";
            if (System.IO.File.Exists(path))
                return path;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Include file handling
    // ═══════════════════════════════════════════════════════════════

    private string ReadIncludeFilename(ref Token rest, Token tok, out bool isDquote)
    {
        if (tok.Kind == TokenKind.Str)
        {
            isDquote = true;
            rest = SkipLine(tok.Next);
            return Encoding.UTF8.GetString(tok.Buf, tok.Loc + 1, tok.Len - 2);
        }

        if (Util.Equal(tok, "<"))
        {
            Token start = tok;
            for (; !Util.Equal(tok, ">"); tok = tok.Next)
                if (tok.AtBol || tok.Kind == TokenKind.Eof)
                    Util.ErrorTok(tok, "expected '>'");
            isDquote = false;
            rest = SkipLine(tok.Next);
            return JoinTokens(start.Next, tok);
        }

        if (tok.Kind == TokenKind.Ident)
        {
            Token tok2 = Preprocess2(CopyLine(ref rest, tok));
            return ReadIncludeFilename(ref tok2, tok2, out isDquote);
        }

        Util.ErrorTok(tok, "expected a filename");
        isDquote = false;
        return null;
    }

    private Dictionary<string, string> _includeGuards = new();

    private Token IncludeFile(Token tok, string path, Token filenameTok)
    {
        if (_pragmaOnce.ContainsKey(path))
            return tok;

        if (_includeGuards.TryGetValue(path, out string guardName) && _macros.ContainsKey(guardName))
            return tok;

        Token tok2 = _tokenizer.TokenizeFile(path);
        if (tok2 == null)
            Util.ErrorTok(filenameTok, $"{path}: cannot open file");

        string guard = DetectIncludeGuard(tok2);
        if (guard != null)
            _includeGuards[path] = guard;

        return Append(tok2, tok);
    }

    private string DetectIncludeGuard(Token tok)
    {
        if (!IsHash(tok) || !Util.Equal(tok.Next, "ifndef"))
            return null;
        tok = tok.Next.Next;
        if (tok.Kind != TokenKind.Ident)
            return null;
        string macro = Util.GetTokenText(tok);
        tok = tok.Next;
        if (!IsHash(tok) || !Util.Equal(tok.Next, "define") || !Util.Equal(tok.Next.Next, macro))
            return null;

        while (tok.Kind != TokenKind.Eof)
        {
            if (!IsHash(tok)) { tok = tok.Next; continue; }
            if (Util.Equal(tok.Next, "endif") && tok.Next.Next.Kind == TokenKind.Eof)
                return macro;
            if (Util.Equal(tok.Next, "if") || Util.Equal(tok.Next, "ifdef") || Util.Equal(tok.Next, "ifndef"))
                tok = SkipCondIncl(tok.Next);
            else
                tok = tok.Next;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant expression evaluation for #if
    // ═══════════════════════════════════════════════════════════════

    private Token ReadConstExpr(ref Token rest, Token tok)
    {
        tok = CopyLine(ref rest, tok);
        Token head = new(), cur = head;

        while (tok.Kind != TokenKind.Eof)
        {
            if (Util.Equal(tok, "defined"))
            {
                Token start = tok;
                bool hasParen = Util.Consume(ref tok, tok.Next, "(");
                if (tok.Kind != TokenKind.Ident)
                    Util.ErrorTok(start, "macro name must be an identifier");
                Macro m = FindMacro(tok);
                tok = tok.Next;
                if (hasParen) tok = Util.Skip(tok, ")");
                cur = cur.Next = NewNumToken(m != null ? 1 : 0, start);
                continue;
            }
            cur = cur.Next = tok;
            tok = tok.Next;
        }
        cur.Next = tok;
        return head.Next;
    }

    private long EvalConstExpr(ref Token rest, Token tok)
    {
        Token start = tok;
        Token expr = ReadConstExpr(ref rest, tok.Next);
        expr = Preprocess2(expr);

        if (expr.Kind == TokenKind.Eof)
            Util.ErrorTok(start, "no expression");

        for (Token t = expr; t.Kind != TokenKind.Eof; t = t.Next)
        {
            if (t.Kind == TokenKind.Ident)
            {
                Token next = t.Next;
                Token zero = NewNumToken(0, t);
                t.Kind = zero.Kind; t.Val = zero.Val; t.FVal = zero.FVal;
                t.Ty = zero.Ty; t.Buf = zero.Buf; t.Loc = zero.Loc; t.Len = zero.Len;
                t.Next = next;
            }
        }

        _tokenizer.ConvertPpTokens(expr);
        // We need a minimal parser for constant expressions here.
        // In the C code, this calls const_expr from parse.c.
        // We'll call into the parser's ConstExpr method.
        return _constExprEvaluator(ref expr, expr);
    }

    private long _constExprEvaluator(ref Token rest, Token tok)
    {
        if (_parser != null)
        {
            return _parser.ConstExpr(ref rest, tok);
        }
        // Fallback: simple integer parsing for basic #if expressions
        if (tok.Kind == TokenKind.Num)
        {
            rest = tok.Next;
            return tok.Val;
        }
        Util.ErrorTok(tok, "expected a constant expression");
        return 0;
    }

    private CondIncl PushCondIncl(Token tok, bool included)
    {
        var ci = new CondIncl { Next = _condIncl, Ctx = CondIncl.Context.InThen, Tok = tok, Included = included };
        _condIncl = ci;
        return ci;
    }

    // ═══════════════════════════════════════════════════════════════
    //  #line directive
    // ═══════════════════════════════════════════════════════════════

    private void ReadLineMarker(ref Token rest, Token tok)
    {
        Token start = tok;
        Token processed = Preprocess(CopyLine(ref rest, tok));
        if (processed.Kind != TokenKind.Num || processed.Ty.Kind != TypeKind.Int)
            Util.ErrorTok(processed, "invalid line marker");
        start.File.LineDelta = (int)processed.Val - start.LineNo;

        processed = processed.Next;
        if (processed.Kind == TokenKind.Eof) return;
        if (processed.Kind != TokenKind.Str)
            Util.ErrorTok(processed, "filename expected");
        start.File.DisplayName = Encoding.UTF8.GetString(processed.Str, 0, processed.Str.Length - 1); // remove NUL
    }

    // ═══════════════════════════════════════════════════════════════
    //  Main preprocessor loop
    // ═══════════════════════════════════════════════════════════════

    private Token Preprocess2(Token tok)
    {
        Token head = new(), cur = head;

        while (tok.Kind != TokenKind.Eof)
        {
            if (ExpandMacro(ref tok, tok))
                continue;

            if (!IsHash(tok))
            {
                tok.LineDelta = tok.File.LineDelta;
                tok.FileName = tok.File.DisplayName;
                cur = cur.Next = tok;
                tok = tok.Next;
                continue;
            }

            Token start = tok;
            tok = tok.Next;

            if (Util.Equal(tok, "include"))
            {
                string filename = ReadIncludeFilename(ref tok, tok.Next, out bool isDquote);
                if (!filename.StartsWith("/") && isDquote)
                {
                    string dir = Path.GetDirectoryName(start.File.Name);
                    string path = $"{dir}/{filename}";
                    if (System.IO.File.Exists(path))
                    {
                        tok = IncludeFile(tok, path, start.Next.Next);
                        continue;
                    }
                }
                string searchPath = SearchIncludePaths(filename);
                tok = IncludeFile(tok, searchPath ?? filename, start.Next.Next);
                continue;
            }

            if (Util.Equal(tok, "include_next"))
            {
                string filename = ReadIncludeFilename(ref tok, tok.Next, out _);
                string path = SearchIncludeNext(filename);
                tok = IncludeFile(tok, path ?? filename, start.Next.Next);
                continue;
            }

            if (Util.Equal(tok, "define"))
            {
                ReadMacroDefinition(ref tok, tok.Next);
                continue;
            }

            if (Util.Equal(tok, "undef"))
            {
                tok = tok.Next;
                if (tok.Kind != TokenKind.Ident)
                    Util.ErrorTok(tok, "macro name must be an identifier");
                UndefMacro(Util.GetTokenText(tok));
                tok = SkipLine(tok.Next);
                continue;
            }

            if (Util.Equal(tok, "if"))
            {
                long val = EvalConstExpr(ref tok, tok);
                PushCondIncl(start, val != 0);
                if (val == 0)
                    tok = SkipCondIncl(tok);
                continue;
            }

            if (Util.Equal(tok, "ifdef"))
            {
                bool defined = FindMacro(tok.Next) != null;
                PushCondIncl(tok, defined);
                tok = SkipLine(tok.Next.Next);
                if (!defined)
                    tok = SkipCondIncl(tok);
                continue;
            }

            if (Util.Equal(tok, "ifndef"))
            {
                bool defined = FindMacro(tok.Next) != null;
                PushCondIncl(tok, !defined);
                tok = SkipLine(tok.Next.Next);
                if (defined)
                    tok = SkipCondIncl(tok);
                continue;
            }

            if (Util.Equal(tok, "elif"))
            {
                if (_condIncl == null || _condIncl.Ctx == CondIncl.Context.InElse)
                    Util.ErrorTok(start, "stray #elif");
                _condIncl.Ctx = CondIncl.Context.InElif;
                if (!_condIncl.Included && EvalConstExpr(ref tok, tok) != 0)
                    _condIncl.Included = true;
                else
                    tok = SkipCondIncl(tok);
                continue;
            }

            if (Util.Equal(tok, "else"))
            {
                if (_condIncl == null || _condIncl.Ctx == CondIncl.Context.InElse)
                    Util.ErrorTok(start, "stray #else");
                _condIncl.Ctx = CondIncl.Context.InElse;
                tok = SkipLine(tok.Next);
                if (_condIncl.Included)
                    tok = SkipCondIncl(tok);
                continue;
            }

            if (Util.Equal(tok, "endif"))
            {
                if (_condIncl == null)
                    Util.ErrorTok(start, "stray #endif");
                _condIncl = _condIncl.Next;
                tok = SkipLine(tok.Next);
                continue;
            }

            if (Util.Equal(tok, "line"))
            {
                ReadLineMarker(ref tok, tok.Next);
                continue;
            }

            if (tok.Kind == TokenKind.PPNum)
            {
                ReadLineMarker(ref tok, tok);
                continue;
            }

            if (Util.Equal(tok, "pragma") && Util.Equal(tok.Next, "once"))
            {
                _pragmaOnce[tok.File.Name] = true;
                tok = SkipLine(tok.Next.Next);
                continue;
            }

            if (Util.Equal(tok, "pragma"))
            {
                do { tok = tok.Next; } while (!tok.AtBol);
                continue;
            }

            if (Util.Equal(tok, "error"))
                Util.ErrorTok(tok, "error");

            if (tok.AtBol) continue;

            Util.ErrorTok(tok, "invalid preprocessor directive");
        }

        cur.Next = tok;
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Built-in macros
    // ═══════════════════════════════════════════════════════════════

    private Macro AddBuiltin(string name, Func<Token, Token> fn)
    {
        Macro m = AddMacro(name, true, null);
        m.Handler = fn;
        return m;
    }

    private Token FileMacro(Token tmpl)
    {
        while (tmpl.Origin != null) tmpl = tmpl.Origin;
        return NewStrToken(tmpl.File.DisplayName, tmpl);
    }

    private Token LineMacro(Token tmpl)
    {
        while (tmpl.Origin != null) tmpl = tmpl.Origin;
        int i = tmpl.LineNo + tmpl.File.LineDelta;
        return NewNumToken(i, tmpl);
    }

    private Token CounterMacro(Token tmpl) => NewNumToken(_counterValue++, tmpl);

    private Token TimestampMacro(Token tmpl)
    {
        try
        {
            var fi = new System.IO.FileInfo(tmpl.File.Name);
            if (fi.Exists)
            {
                string ts = fi.LastWriteTime.ToString("ddd MMM dd HH:mm:ss yyyy",
                    System.Globalization.CultureInfo.InvariantCulture);
                return NewStrToken(ts, tmpl);
            }
        }
        catch { }
        return NewStrToken("??? ??? ?? ??:??:?? ????", tmpl);
    }

    private Token BaseFileMacro(Token tmpl) => NewStrToken(_options.BaseFile, tmpl);

    public void InitMacros()
    {
        var dm = _options.DataModel;
        bool isLP64 = dm.LongSize == 8;

        if (isLP64) { DefineMacro("_LP64", "1"); DefineMacro("__LP64__", "1"); }
        DefineMacro("__C99_MACRO_WITH_VA_ARGS", "1");
        DefineMacro("__ELF__", "1");
        DefineMacro("__SIZEOF_DOUBLE__", "8");
        DefineMacro("__SIZEOF_FLOAT__", "4");
        DefineMacro("__SIZEOF_INT__", "4");
        DefineMacro("__SIZEOF_LONG_DOUBLE__", $"{dm.LDoubleSize}");
        DefineMacro("__SIZEOF_LONG_LONG__", "8");
        DefineMacro("__SIZEOF_LONG__", $"{dm.LongSize}");
        DefineMacro("__SIZEOF_POINTER__", $"{dm.PointerSize}");
        DefineMacro("__SIZEOF_PTRDIFF_T__", $"{dm.PointerSize}");
        DefineMacro("__SIZEOF_SHORT__", "2");
        DefineMacro("__SIZEOF_SIZE_T__", $"{dm.PointerSize}");
        DefineMacro("__SIZEOF_WCHAR_T__", $"{dm.WcharSize}");
        DefineMacro("__SIZE_TYPE__", isLP64 ? "unsigned long" : "unsigned long long");
        DefineMacro("__PTRDIFF_TYPE__", isLP64 ? "long" : "long long");
        DefineMacro("__STDC_HOSTED__", "1");
        DefineMacro("__STDC_NO_COMPLEX__", "1");
        DefineMacro("__STDC_UTF_16__", "1");
        DefineMacro("__STDC_UTF_32__", "1");
        DefineMacro("__STDC_VERSION__", "201112L");
        DefineMacro("__STDC__", "1");
        DefineMacro("__USER_LABEL_PREFIX__", "");
        DefineMacro("__alignof__", "_Alignof");
        DefineMacro("__amd64", "1");
        DefineMacro("__amd64__", "1");
        DefineMacro("__chibil__", "1");
        DefineMacro("__const__", "const");
        DefineMacro("__gnu_linux__", "1");
        DefineMacro("__inline__", "inline");
        DefineMacro("__inline", "inline");
        DefineMacro("__forceinline", "inline");
        DefineMacro("__cdecl", "__attribute__((cdecl))");
        DefineMacro("__clrcall", "__attribute__((clrcall))");
        DefineMacro("__stdcall", "__attribute__((stdcall))");
        DefineMacro("__linux", "1");
        DefineMacro("__linux__", "1");
        DefineMacro("__signed__", "signed");
        DefineMacro("__typeof__", "typeof");
        DefineMacro("__unix", "1");
        DefineMacro("__unix__", "1");
        DefineMacro("__volatile__", "volatile");
        DefineMacro("__x86_64", "1");
        DefineMacro("__x86_64__", "1");
        DefineMacro("linux", "1");
        DefineMacro("unix", "1");

        AddBuiltin("__FILE__", FileMacro);
        AddBuiltin("__LINE__", LineMacro);
        AddBuiltin("__COUNTER__", CounterMacro);
        AddBuiltin("__TIMESTAMP__", TimestampMacro);
        AddBuiltin("__BASE_FILE__", BaseFileMacro);

        var now = DateTime.Now;
        string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        DefineMacro("__DATE__", $"\"{months[now.Month - 1]} {now.Day,2} {now.Year}\"");
        DefineMacro("__TIME__", $"\"{now.Hour:D2}:{now.Minute:D2}:{now.Second:D2}\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  String literal concatenation
    // ═══════════════════════════════════════════════════════════════

    private enum StringKind { None, Utf8, Utf16, Utf32, Wide }

    private static StringKind GetStringKind(Token tok)
    {
        // Check first bytes of token
        if (tok.Len >= 2 && tok.Buf[tok.Loc] == 'u' && tok.Buf[tok.Loc + 1] == '8')
            return StringKind.Utf8;
        switch (tok.Buf[tok.Loc])
        {
            case (byte)'"': return StringKind.None;
            case (byte)'u': return StringKind.Utf16;
            case (byte)'U': return StringKind.Utf32;
            case (byte)'L': return StringKind.Wide;
        }
        Util.Unreachable();
        return StringKind.None;
    }

    private void JoinAdjacentStringLiterals(Token tok)
    {
        // First pass: promote regular string literals to wide types
        for (Token tok1 = tok; tok1.Kind != TokenKind.Eof;)
        {
            if (tok1.Kind != TokenKind.Str || tok1.Next.Kind != TokenKind.Str)
            {
                tok1 = tok1.Next;
                continue;
            }

            StringKind kind = GetStringKind(tok1);
            CType basety = tok1.Ty.Base;

            for (Token t = tok1.Next; t.Kind == TokenKind.Str; t = t.Next)
            {
                StringKind k = GetStringKind(t);
                if (kind == StringKind.None)
                {
                    kind = k;
                    basety = t.Ty.Base;
                }
                else if (k != StringKind.None && kind != k)
                {
                    Util.ErrorTok(t, "unsupported non-standard concatenation of string literals");
                }
            }

            if (basety.Size > 1)
            {
                for (Token t = tok1; t.Kind == TokenKind.Str; t = t.Next)
                {
                    if (t.Ty.Base.Size == 1)
                    {
                        Token converted = _tokenizer.TokenizeStringLiteral(t, basety);
                        // C does *t = *tokenize_string_literal(t, basety) — full struct copy
                        t.Kind = converted.Kind; t.Ty = converted.Ty; t.Str = converted.Str;
                        t.Buf = converted.Buf; t.Loc = converted.Loc; t.Len = converted.Len;
                        t.File = converted.File; t.FileName = converted.FileName;
                        t.LineNo = converted.LineNo; t.LineDelta = converted.LineDelta;
                    }
                }
            }

            while (tok1.Kind == TokenKind.Str)
                tok1 = tok1.Next;
        }

        // Second pass: concatenate
        for (Token tok1 = tok; tok1.Kind != TokenKind.Eof;)
        {
            if (tok1.Kind != TokenKind.Str || tok1.Next.Kind != TokenKind.Str)
            {
                tok1 = tok1.Next;
                continue;
            }

            Token tok2 = tok1.Next;
            while (tok2.Kind == TokenKind.Str) tok2 = tok2.Next;

            int len = tok1.Ty.ArrayLen;
            for (Token t = tok1.Next; t != tok2; t = t.Next)
                len = len + t.Ty.ArrayLen - 1;

            byte[] buf = new byte[tok1.Ty.Base.Size * len];
            int pos = 0;
            for (Token t = tok1; t != tok2; t = t.Next)
            {
                Array.Copy(t.Str, 0, buf, pos, t.Ty.Size);
                pos = pos + t.Ty.Size - t.Ty.Base.Size;
            }

            Token copied = CopyToken(tok1);
            tok1.Kind = copied.Kind; tok1.AtBol = copied.AtBol; tok1.HasSpace = copied.HasSpace;
            tok1.Origin = copied.Origin; tok1.Hideset = copied.Hideset;
            tok1.Ty = TypeSystem.ArrayOf(tok1.Ty.Base, len);
            tok1.Str = buf;
            tok1.Next = tok2;
            tok1 = tok2;
        }
    }

    private Token ReadAttributeList(Token tok, Token end)
    {
        Token head = null;
        bool first = true;

        while (tok != end)
        {
            if (!first)
                tok = Util.Skip(tok, ",");
            first = false;

            if (tok.Kind != TokenKind.Ident)
                Util.ErrorTok(tok, "expected attribute name");

            Token name = tok;
            tok = tok.Next;

            Token args = null;
            if (Util.Equal(tok, "("))
            {
                Token rparen = Util.FindMatchingParen(tok);
                Token argHead = new(), argCur = argHead;
                for (Token t = tok.Next; t != rparen && t.Kind != TokenKind.Eof; t = t.Next)
                    argCur = argCur.Next = CopyToken(t);
                argCur.Next = NewEof(rparen);
                args = argHead.Next;
                tok = rparen.Next;
            }

            Token attr = CopyToken(name);
            attr.Next = null;
            attr.AttrNext = null;
            attr.AttrArgs = args;
            if (attr.AttrArgs != null)
                _tokenizer.ConvertPpTokens(attr.AttrArgs);

            AppendAttributes(ref head, attr);
        }

        return head;
    }

    private static void AppendAttributes(ref Token list, Token attrs)
    {
        while (list != null)
            list = ref list.AttrNext;
        list = attrs;
    }

    private Token PreprocessAttributes(Token tok)
    {
        Token head = new(), cur = head;

        while (tok.Kind != TokenKind.Eof)
        {
            if (Util.Equal(tok, "__attribute__"))
            {
                Token innerOpen = Util.Skip(tok.Next, "(");
                Token attrsStart = Util.Skip(innerOpen, "(");
                Token innerClose = Util.FindMatchingParen(innerOpen);
                Token rest = Util.Skip(innerClose.Next, ")");

                Token attrs = tok.AttrNext;
                tok.AttrNext = null;
                AppendAttributes(ref attrs, ReadAttributeList(attrsStart, innerClose));
                tok = rest;
                AppendAttributes(ref tok.AttrNext, attrs);
                continue;
            }

            cur = cur.Next = tok;
            tok = tok.Next;
        }

        cur.Next = tok;
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public Token Preprocess(Token tok)
    {
        tok = Preprocess2(tok);
        if (_condIncl != null)
            Util.ErrorTok(_condIncl.Tok, "unterminated conditional directive");
        tok = PreprocessAttributes(tok);
        _tokenizer.ConvertPpTokens(tok);
        JoinAdjacentStringLiterals(tok);

        for (Token t = tok; t != null; t = t.Next)
            t.LineNo += t.LineDelta;
        return tok;
    }
}
