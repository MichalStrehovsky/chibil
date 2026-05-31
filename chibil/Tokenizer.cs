using System.Globalization;
using System.Text;

namespace Chibil;

/// <summary>
/// Tokenizer — port of tokenize.c.
/// Converts UTF-8 source bytes into a linked list of tokens.
/// </summary>
public class Tokenizer
{
    private CFile _currentFile;
    private readonly List<CFile> _inputFiles = new();
    private bool _atBol;
    private bool _hasSpace;
    private int _fileNo;

    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;

    public Tokenizer(CompilerOptions options, TypeSystem types)
    {
        _options = options;
        _types = types;
    }

    public CFile[] GetInputFiles() => _inputFiles.ToArray();

    // ═══════════════════════════════════════════════════════════════
    //  Token creation
    // ═══════════════════════════════════════════════════════════════

    private Token NewToken(TokenKind kind, byte[] buf, int start, int end)
    {
        var tok = new Token
        {
            Kind = kind,
            Buf = buf,
            Loc = start,
            Len = end - start,
            File = _currentFile,
            FileName = _currentFile.DisplayName,
            AtBol = _atBol,
            HasSpace = _hasSpace,
        };
        _atBol = false;
        _hasSpace = false;
        return tok;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helper functions
    // ═══════════════════════════════════════════════════════════════

    private static bool StartsWith(byte[] buf, int pos, string s)
    {
        return Util.StartsWith(buf, pos, s);
    }

    private static bool IsAsciiAlnum(byte b)
    {
        return (b >= '0' && b <= '9') || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z');
    }

    private static bool IsAsciiDigit(byte b) => b >= '0' && b <= '9';

    private static bool IsAsciiXDigit(byte b)
    {
        return (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');
    }

    private static bool IsAsciiSpace(byte b)
    {
        return b == ' ' || b == '\t' || b == '\f' || b == '\r' || b == '\v';
    }

    private static bool IsAsciiPunct(byte b)
    {
        return (b >= '!' && b <= '/') || (b >= ':' && b <= '@') ||
               (b >= '[' && b <= '`') || (b >= '{' && b <= '~');
    }

    private static int FromHex(byte c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return c - 'A' + 10;
    }

    // Read an identifier and return its length. 0 if not valid.
    private int ReadIdent(byte[] buf, int start)
    {
        int p = start;
        uint c = Unicode.DecodeUtf8(_currentFile, buf, p, out p);
        if (!Unicode.IsIdent1(c))
            return 0;

        for (;;)
        {
            c = Unicode.DecodeUtf8(buf, p, out int q);
            if (!Unicode.IsIdent2(c))
                return p - start;
            p = q;
        }
    }

    // Read a punctuator token and return its length.
    private static int ReadPunct(byte[] buf, int p)
    {
        string[] kw = {
            "<<=", ">>=", "...", "==", "!=", "<=", ">=", "->", "+=",
            "-=", "*=", "/=", "++", "--", "%=", "&=", "|=", "^=", "&&",
            "||", "<<", ">>", "##",
        };

        foreach (string k in kw)
            if (StartsWith(buf, p, k))
                return k.Length;

        return IsAsciiPunct(buf[p]) ? 1 : 0;
    }

    private Dictionary<string, bool> _keywordMap;

    private bool IsKeyword(Token tok)
    {
        if (_keywordMap == null)
        {
            _keywordMap = new Dictionary<string, bool>();
            string[] kw = {
                "return", "if", "else", "for", "while", "int", "sizeof", "char",
                "struct", "union", "short", "long", "void", "typedef", "_Bool",
                "enum", "static", "goto", "break", "continue", "switch", "case",
                "default", "extern", "_Alignof", "_Alignas", "do", "signed",
                "unsigned", "const", "volatile", "auto", "register", "restrict",
                "__restrict", "__restrict__", "_Noreturn", "float", "double",
                "typeof", "asm", "_Thread_local", "__thread", "_Atomic",
                "__attribute__", "__declspec", "__cdecl", "__clrcall", "__stdcall",
                "__int8", "__int16", "__int32", "__int64",
            };
            foreach (string k in kw)
                _keywordMap[k] = true;
        }
        string text = Util.GetTokenText(tok);
        return _keywordMap.ContainsKey(text);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Escape sequences
    // ═══════════════════════════════════════════════════════════════

    private int ReadEscapedChar(byte[] buf, ref int p)
    {
        if (buf[p] >= '0' && buf[p] <= '7')
        {
            int c = buf[p++] - '0';
            if (buf[p] >= '0' && buf[p] <= '7')
            {
                c = (c << 3) + (buf[p++] - '0');
                if (buf[p] >= '0' && buf[p] <= '7')
                    c = (c << 3) + (buf[p++] - '0');
            }
            return c;
        }

        if (buf[p] == 'x')
        {
            p++;
            if (!IsAsciiXDigit(buf[p]))
                Util.ErrorAt(_currentFile, buf, p, "invalid hex escape sequence");
            int c = 0;
            for (; IsAsciiXDigit(buf[p]); p++)
                c = (c << 4) + FromHex(buf[p]);
            return c;
        }

        byte ch = buf[p++];
        switch (ch)
        {
            case (byte)'a': return '\a';
            case (byte)'b': return '\b';
            case (byte)'t': return '\t';
            case (byte)'n': return '\n';
            case (byte)'v': return '\v';
            case (byte)'f': return '\f';
            case (byte)'r': return '\r';
            case (byte)'e': return 27; // GNU extension
            default: return ch;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  String literals
    // ═══════════════════════════════════════════════════════════════

    private int StringLiteralEnd(byte[] buf, int p)
    {
        int start = p;
        for (; buf[p] != '"'; p++)
        {
            if (buf[p] == '\n' || buf[p] == 0)
                Util.ErrorAt(_currentFile, buf, start, "unclosed string literal");
            if (buf[p] == '\\')
                p++;
        }
        return p;
    }

    private Token ReadStringLiteral(byte[] buf, int start, int quote)
    {
        int end = StringLiteralEnd(buf, quote + 1);
        var bytes = new List<byte>();

        for (int p = quote + 1; p < end;)
        {
            if (buf[p] == '\\')
            {
                p++;
                bytes.Add((byte)ReadEscapedChar(buf, ref p));
            }
            else
                bytes.Add(buf[p++]);
        }

        bytes.Add(0); // NUL terminator

        var tok = NewToken(TokenKind.Str, buf, start, end + 1);
        tok.Ty = TypeSystem.ArrayOf(_types.TyChar, bytes.Count);
        tok.Str = bytes.ToArray();
        return tok;
    }

    private Token ReadUtf16StringLiteral(byte[] buf, int start, int quote)
    {
        int end = StringLiteralEnd(buf, quote + 1);
        var shorts = new List<ushort>();

        for (int p = quote + 1; p < end;)
        {
            if (buf[p] == '\\')
            {
                p++;
                shorts.Add((ushort)ReadEscapedChar(buf, ref p));
                continue;
            }

            uint c = Unicode.DecodeUtf8(_currentFile, buf, p, out p);
            if (c < 0x10000)
            {
                shorts.Add((ushort)c);
            }
            else
            {
                c -= 0x10000;
                shorts.Add((ushort)(0xd800 + ((c >> 10) & 0x3ff)));
                shorts.Add((ushort)(0xdc00 + (c & 0x3ff)));
            }
        }

        shorts.Add(0);

        var tok = NewToken(TokenKind.Str, buf, start, end + 1);
        tok.Ty = TypeSystem.ArrayOf(_types.TyUshort, shorts.Count);
        // Convert to byte array
        byte[] strBytes = new byte[shorts.Count * 2];
        for (int i = 0; i < shorts.Count; i++)
        {
            strBytes[i * 2] = (byte)(shorts[i] & 0xFF);
            strBytes[i * 2 + 1] = (byte)(shorts[i] >> 8);
        }
        tok.Str = strBytes;
        return tok;
    }

    private Token ReadUtf32StringLiteral(byte[] buf, int start, int quote, CType ty)
    {
        int end = StringLiteralEnd(buf, quote + 1);
        var ints = new List<uint>();

        for (int p = quote + 1; p < end;)
        {
            if (buf[p] == '\\')
            {
                p++;
                ints.Add((uint)ReadEscapedChar(buf, ref p));
            }
            else
            {
                ints.Add(Unicode.DecodeUtf8(_currentFile, buf, p, out p));
            }
        }

        ints.Add(0);

        var tok = NewToken(TokenKind.Str, buf, start, end + 1);
        tok.Ty = TypeSystem.ArrayOf(ty, ints.Count);
        byte[] strBytes = new byte[ints.Count * 4];
        for (int i = 0; i < ints.Count; i++)
        {
            strBytes[i * 4] = (byte)(ints[i] & 0xFF);
            strBytes[i * 4 + 1] = (byte)((ints[i] >> 8) & 0xFF);
            strBytes[i * 4 + 2] = (byte)((ints[i] >> 16) & 0xFF);
            strBytes[i * 4 + 3] = (byte)((ints[i] >> 24) & 0xFF);
        }
        tok.Str = strBytes;
        return tok;
    }

    private Token ReadCharLiteral(byte[] buf, int start, int quote, CType ty)
    {
        int p = quote + 1;
        if (buf[p] == 0)
            Util.ErrorAt(_currentFile, buf, start, "unclosed char literal");

        int c;
        if (buf[p] == '\\')
        {
            p++;
            c = ReadEscapedChar(buf, ref p);
        }
        else
        {
            c = (int)Unicode.DecodeUtf8(_currentFile, buf, p, out p);
        }

        // Find closing '
        int endPos = p;
        while (buf[endPos] != '\'' && buf[endPos] != 0)
            endPos++;
        if (buf[endPos] != '\'')
            Util.ErrorAt(_currentFile, buf, p, "unclosed char literal");

        var tok = NewToken(TokenKind.Num, buf, start, endPos + 1);
        tok.Val = c;
        tok.Ty = ty;
        return tok;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Number conversion
    // ═══════════════════════════════════════════════════════════════

    private static int StrNCaseCompare(byte[] buf, int pos, string s, int n)
    {
        for (int i = 0; i < n; i++)
        {
            byte a = buf[pos + i];
            byte b = (byte)s[i];
            if ((a | 0x20) != (b | 0x20)) return 1;
        }
        return 0;
    }

    private bool ConvertPpInt(Token tok)
    {
        byte[] buf = tok.Buf;
        int p = tok.Loc;

        int @base = 10;
        if (StrNCaseCompare(buf, p, "0x", 2) == 0 && IsAsciiXDigit(buf[p + 2]))
        {
            p += 2;
            @base = 16;
        }
        else if (StrNCaseCompare(buf, p, "0b", 2) == 0 && (buf[p + 2] == '0' || buf[p + 2] == '1'))
        {
            p += 2;
            @base = 2;
        }
        else if (buf[p] == '0')
        {
            @base = 8;
        }

        // Parse the number
        ulong val = 0;
        while (p < tok.Loc + tok.Len)
        {
            byte c = buf[p];
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else break;
            if (digit >= @base) break;
            val = val * (ulong)@base + (ulong)digit;
            p++;
        }

        // Read U, L or LL suffixes
        bool l = false, ll = false, u = false;
        int remaining = tok.Loc + tok.Len - p;
        string suffix = remaining > 0 ? Encoding.ASCII.GetString(buf, p, remaining) : "";

        if (suffix.Equals("LLU", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("LLu", StringComparison.Ordinal) ||
            suffix.Equals("llU", StringComparison.Ordinal) ||
            suffix.Equals("llu", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("ULL", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("Ull", StringComparison.Ordinal) ||
            suffix.Equals("uLL", StringComparison.Ordinal) ||
            suffix.Equals("ull", StringComparison.OrdinalIgnoreCase))
        {
            p += 3; ll = true; u = true;
        }
        else if (StrNCaseCompare(buf, p, "lu", 2) == 0 || StrNCaseCompare(buf, p, "ul", 2) == 0)
        {
            if (remaining >= 2) { p += 2; l = true; u = true; }
        }
        else if (remaining >= 2 && ((buf[p] == 'L' || buf[p] == 'l') && (buf[p + 1] == 'L' || buf[p + 1] == 'l')))
        {
            p += 2; ll = true;
        }
        else if (remaining >= 1 && (buf[p] == 'L' || buf[p] == 'l'))
        {
            p++; l = true;
        }
        else if (remaining >= 1 && (buf[p] == 'U' || buf[p] == 'u'))
        {
            p++; u = true;
        }

        if (p != tok.Loc + tok.Len)
            return false;

        // Infer a type based on suffix, base, and value magnitude.
        // C11 §6.4.4.1: the type is the first in the candidate list that can hold the value.
        int longBits = _types.TyLong.Size * 8;
        // C# shifts mask the count to 0-63, so val>>64 is a no-op. Use a helper.
        bool fitsInLong = longBits >= 64 || (val >> longBits) == 0;
        bool fitsInSignedLong = longBits >= 64 ? (val >> 63) == 0 : (val >> (longBits - 1)) == 0;
        CType ty;
        if (ll && u) ty = _types.TyUlongLong;
        else if (ll) ty = (val >> 63) != 0 ? _types.TyUlongLong : _types.TyLongLong;
        else if (l && u) ty = fitsInLong ? _types.TyUlong : _types.TyUlongLong;
        else if (l)
        {
            if (@base == 10)
            {
                // decimal L: long → long long
                ty = fitsInSignedLong ? _types.TyLong : _types.TyLongLong;
            }
            else
            {
                // hex/oct L: long → unsigned long → long long → unsigned long long
                if (fitsInSignedLong) ty = _types.TyLong;
                else if (fitsInLong) ty = _types.TyUlong;
                else if ((val >> 63) == 0) ty = _types.TyLongLong;
                else ty = _types.TyUlongLong;
            }
        }
        else if (u)
        {
            // U suffix: unsigned int → unsigned long → unsigned long long
            if ((val >> 32) == 0) ty = _types.TyUint;
            else if (fitsInLong) ty = _types.TyUlong;
            else ty = _types.TyUlongLong;
        }
        else if (@base == 10)
        {
            // decimal, no suffix: int → long → long long
            if ((val >> 31) == 0) ty = _types.TyInt;
            else if (fitsInSignedLong) ty = _types.TyLong;
            else ty = _types.TyLongLong;
        }
        else
        {
            // hex/oct, no suffix: int → unsigned int → long → unsigned long → long long → unsigned long long
            if ((val >> 31) == 0) ty = _types.TyInt;
            else if ((val >> 32) == 0) ty = _types.TyUint;
            else if (fitsInSignedLong) ty = _types.TyLong;
            else if (fitsInLong) ty = _types.TyUlong;
            else if ((val >> 63) == 0) ty = _types.TyLongLong;
            else ty = _types.TyUlongLong;
        }

        tok.Kind = TokenKind.Num;
        tok.Val = (long)val;
        tok.Ty = ty;
        return true;
    }

    private void ConvertPpNumber(Token tok)
    {
        if (ConvertPpInt(tok))
            return;

        // Parse as floating point
        string text = Util.GetTokenText(tok);
        CType ty;
        string numText = text;

        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase))
        {
            ty = _types.TyFloat;
            numText = text[..^1];
        }
        else if (text.EndsWith("l", StringComparison.OrdinalIgnoreCase))
        {
            ty = _types.TyLdouble;
            numText = text[..^1];
        }
        else
        {
            ty = _types.TyDouble;
        }

        if (numText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            numText.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseHexFloat(numText, out double val2))
                Util.ErrorTok(tok, "invalid numeric constant");
            tok.Kind = TokenKind.Num;
            tok.FVal = val2;
            tok.Ty = ty;
            return;
        }

        if (!double.TryParse(numText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double val))
        {
            Util.ErrorTok(tok, "invalid numeric constant");
        }

        tok.Kind = TokenKind.Num;
        tok.FVal = val;
        tok.Ty = ty;
    }

    private static bool TryParseHexFloat(string s, out double result)
    {
        result = 0;
        // Parse hex float: 0xH.HpE format
        int i = 2; // skip "0x"
        double intPart = 0;
        while (i < s.Length && IsAsciiXDigit((byte)s[i]))
        {
            intPart = intPart * 16 + FromHex((byte)s[i]);
            i++;
        }
        double fracPart = 0;
        double fracDiv = 1;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && IsAsciiXDigit((byte)s[i]))
            {
                fracDiv *= 16;
                fracPart += FromHex((byte)s[i]) / fracDiv;
                i++;
            }
        }
        double value = intPart + fracPart;
        if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
        {
            i++;
            int sign = 1;
            if (i < s.Length && s[i] == '+') i++;
            else if (i < s.Length && s[i] == '-') { sign = -1; i++; }
            int exp = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                exp = exp * 10 + (s[i] - '0');
                i++;
            }
            value *= Math.Pow(2, sign * exp);
        }
        if (i != s.Length) return false;
        result = value;
        return true;
    }

    public void ConvertPpTokens(Token tok)
    {
        for (Token t = tok; t.Kind != TokenKind.Eof; t = t.Next)
        {
            if (IsKeyword(t))
                t.Kind = TokenKind.Keyword;
            else if (t.Kind == TokenKind.PPNum)
                ConvertPpNumber(t);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Line numbers
    // ═══════════════════════════════════════════════════════════════

    private void AddLineNumbers(Token tok)
    {
        byte[] buf = _currentFile.Contents;
        int p = 0;
        int n = 1;

        while (tok != null && tok.Kind != TokenKind.Eof)
        {
            while (p < tok.Loc)
            {
                if (buf[p] == '\n')
                    n++;
                p++;
            }
            tok.LineNo = n;
            tok = tok.Next;
        }
        // Handle EOF token
        if (tok != null)
        {
            while (p < tok.Loc)
            {
                if (buf[p] == '\n') n++;
                p++;
            }
            tok.LineNo = n;
        }
    }

    public Token TokenizeStringLiteral(Token tok, CType basety)
    {
        Token t;
        if (basety.Size == 2)
            t = ReadUtf16StringLiteral(tok.Buf, tok.Loc, tok.Loc);
        else
            t = ReadUtf32StringLiteral(tok.Buf, tok.Loc, tok.Loc, basety);
        t.Next = tok.Next;
        return t;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Main tokenize function
    // ═══════════════════════════════════════════════════════════════

    public Token Tokenize(CFile file)
    {
        _currentFile = file;
        byte[] buf = file.Contents;
        int p = 0;
        Token head = new Token();
        Token cur = head;

        _atBol = true;
        _hasSpace = false;

        while (buf[p] != 0)
        {
            // Skip line comments
            if (buf[p] == '/' && buf[p + 1] == '/')
            {
                p += 2;
                while (buf[p] != '\n')
                    p++;
                _hasSpace = true;
                continue;
            }

            // Skip block comments
            if (buf[p] == '/' && buf[p + 1] == '*')
            {
                int q = p + 2;
                while (!(buf[q] == '*' && buf[q + 1] == '/'))
                {
                    if (buf[q] == 0)
                        Util.ErrorAt(_currentFile, buf, p, "unclosed block comment");
                    q++;
                }
                p = q + 2;
                _hasSpace = true;
                continue;
            }

            // Skip newline
            if (buf[p] == '\n')
            {
                p++;
                _atBol = true;
                _hasSpace = false;
                continue;
            }

            // Skip whitespace
            if (IsAsciiSpace(buf[p]))
            {
                p++;
                _hasSpace = true;
                continue;
            }

            // Numeric literal
            if (IsAsciiDigit(buf[p]) || (buf[p] == '.' && IsAsciiDigit(buf[p + 1])))
            {
                int q = p++;
                for (;;)
                {
                    if (buf[p] != 0 && buf[p + 1] != 0 &&
                        (buf[p] == 'e' || buf[p] == 'E' || buf[p] == 'p' || buf[p] == 'P') &&
                        (buf[p + 1] == '+' || buf[p + 1] == '-'))
                        p += 2;
                    else if (IsAsciiAlnum(buf[p]) || buf[p] == '.')
                        p++;
                    else
                        break;
                }
                cur = cur.Next = NewToken(TokenKind.PPNum, buf, q, p);
                continue;
            }

            // String literal
            if (buf[p] == '"')
            {
                cur = cur.Next = ReadStringLiteral(buf, p, p);
                p += cur.Len;
                continue;
            }

            // UTF-8 string literal
            if (StartsWith(buf, p, "u8\""))
            {
                cur = cur.Next = ReadStringLiteral(buf, p, p + 2);
                p += cur.Len;
                continue;
            }

            // UTF-16 string literal
            if (StartsWith(buf, p, "u\""))
            {
                cur = cur.Next = ReadUtf16StringLiteral(buf, p, p + 1);
                p += cur.Len;
                continue;
            }

            // Wide string literal (wchar_t size depends on data model)
            if (StartsWith(buf, p, "L\""))
            {
                if (_options.DataModel.WcharSize == 2)
                    cur = cur.Next = ReadUtf16StringLiteral(buf, p, p + 1);
                else
                    cur = cur.Next = ReadUtf32StringLiteral(buf, p, p + 1, _types.TyInt);
                p += cur.Len;
                continue;
            }

            // UTF-32 string literal
            if (StartsWith(buf, p, "U\""))
            {
                cur = cur.Next = ReadUtf32StringLiteral(buf, p, p + 1, _types.TyUint);
                p += cur.Len;
                continue;
            }

            // Character literal
            if (buf[p] == '\'')
            {
                cur = cur.Next = ReadCharLiteral(buf, p, p, _types.TyInt);
                cur.Val = (sbyte)cur.Val;
                p += cur.Len;
                continue;
            }

            // UTF-16 character literal
            if (StartsWith(buf, p, "u'"))
            {
                cur = cur.Next = ReadCharLiteral(buf, p, p + 1, _types.TyUshort);
                cur.Val &= 0xffff;
                p += cur.Len;
                continue;
            }

            // Wide character literal
            if (StartsWith(buf, p, "L'"))
            {
                cur = cur.Next = ReadCharLiteral(buf, p, p + 1,
                    _options.DataModel.WcharSize == 2 ? _types.TyUshort : _types.TyInt);
                p += cur.Len;
                continue;
            }

            // UTF-32 character literal
            if (StartsWith(buf, p, "U'"))
            {
                cur = cur.Next = ReadCharLiteral(buf, p, p + 1, _types.TyUint);
                p += cur.Len;
                continue;
            }

            // Identifier or keyword
            int identLen = ReadIdent(buf, p);
            if (identLen > 0)
            {
                cur = cur.Next = NewToken(TokenKind.Ident, buf, p, p + identLen);
                p += cur.Len;
                continue;
            }

            // Punctuators
            int punctLen = ReadPunct(buf, p);
            if (punctLen > 0)
            {
                cur = cur.Next = NewToken(TokenKind.Punct, buf, p, p + punctLen);
                p += cur.Len;
                continue;
            }

            Util.ErrorAt(_currentFile, buf, p, "invalid token");
        }

        cur = cur.Next = NewToken(TokenKind.Eof, buf, p, p);
        AddLineNumbers(head.Next);
        return head.Next;
    }

    // ═══════════════════════════════════════════════════════════════
    //  File reading
    // ═══════════════════════════════════════════════════════════════

    private static byte[] ReadFile(string path)
    {
        byte[] raw;
        if (path == "-")
        {
            using var ms = new MemoryStream();
            Console.OpenStandardInput().CopyTo(ms);
            raw = ms.ToArray();
        }
        else
        {
            if (!System.IO.File.Exists(path))
                return null;
            raw = System.IO.File.ReadAllBytes(path);
        }

        int len = raw.Length;
        bool needsNewline = len == 0 || raw[len - 1] != (byte)'\n';
        byte[] buf = new byte[len + (needsNewline ? 1 : 0) + 1]; // +1 for NUL
        Array.Copy(raw, buf, len);
        int pos = len;
        if (needsNewline) buf[pos++] = (byte)'\n';
        buf[pos] = 0; // NUL terminator
        return buf;
    }

    public CFile NewFile(string name, int fileNo, byte[] contents)
    {
        var file = new CFile
        {
            Name = name,
            DisplayName = name,
            FileNo = fileNo,
            Contents = contents,
        };
        return file;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Source preprocessing (newline canonicalization, etc.)
    // ═══════════════════════════════════════════════════════════════

    private static void CanonicalizeNewline(byte[] buf)
    {
        int i = 0, j = 0;
        while (buf[i] != 0)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n')
            {
                i += 2;
                buf[j++] = (byte)'\n';
            }
            else if (buf[i] == '\r')
            {
                i++;
                buf[j++] = (byte)'\n';
            }
            else
            {
                buf[j++] = buf[i++];
            }
        }
        buf[j] = 0;
    }

    private static void RemoveBackslashNewline(byte[] buf)
    {
        int i = 0, j = 0, n = 0;

        while (buf[i] != 0)
        {
            if (buf[i] == '\\' && buf[i + 1] == '\n')
            {
                i += 2;
                n++;
            }
            else if (buf[i] == '\n')
            {
                buf[j++] = buf[i++];
                for (; n > 0; n--)
                    buf[j++] = (byte)'\n';
            }
            else
            {
                buf[j++] = buf[i++];
            }
        }

        for (; n > 0; n--)
            buf[j++] = (byte)'\n';
        buf[j] = 0;
    }

    private static uint ReadUniversalChar(byte[] buf, int p, int len)
    {
        uint c = 0;
        for (int i = 0; i < len; i++)
        {
            if (!IsAsciiXDigit(buf[p + i]))
                return 0;
            c = (c << 4) | (uint)FromHex(buf[p + i]);
        }
        return c;
    }

    private static void ConvertUniversalChars(byte[] buf)
    {
        int p = 0, q = 0;

        while (buf[p] != 0)
        {
            if (buf[p] == '\\' && buf[p + 1] == 'u')
            {
                uint c = ReadUniversalChar(buf, p + 2, 4);
                if (c != 0)
                {
                    p += 6;
                    q += Unicode.EncodeUtf8(buf, q, c);
                }
                else
                {
                    buf[q++] = buf[p++];
                }
            }
            else if (buf[p] == '\\' && buf[p + 1] == 'U')
            {
                uint c = ReadUniversalChar(buf, p + 2, 8);
                if (c != 0)
                {
                    p += 10;
                    q += Unicode.EncodeUtf8(buf, q, c);
                }
                else
                {
                    buf[q++] = buf[p++];
                }
            }
            else if (buf[p] == '\\')
            {
                buf[q++] = buf[p++];
                buf[q++] = buf[p++];
            }
            else
            {
                buf[q++] = buf[p++];
            }
        }

        buf[q] = 0;
    }

    public Token TokenizeFile(string path)
    {
        byte[] buf = ReadFile(path);
        if (buf == null)
            return null;

        // Skip UTF-8 BOM if present
        int start = 0;
        if (buf.Length >= 3 && buf[0] == 0xef && buf[1] == 0xbb && buf[2] == 0xbf)
            start = 3;

        if (start > 0)
        {
            // Shift buffer contents
            byte[] newBuf = new byte[buf.Length - start];
            Array.Copy(buf, start, newBuf, 0, buf.Length - start);
            buf = newBuf;
        }

        CanonicalizeNewline(buf);
        RemoveBackslashNewline(buf);
        ConvertUniversalChars(buf);

        CFile file = NewFile(path, _fileNo + 1, buf);

        // Track input files
        _inputFiles.Add(file);
        _fileNo++;

        return Tokenize(file);
    }
}
