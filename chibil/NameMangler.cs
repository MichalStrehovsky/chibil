using System.Diagnostics;
using System.Text;

namespace Chibil;

public readonly struct NameMangler
{
    private readonly TypeSystem _types;
    private readonly List<string> _nameBackRefs;
    private readonly Dictionary<string, int> _argBackRefs;

    private NameMangler(TypeSystem ts, string nameBackref)
    {
        _types = ts;
        _nameBackRefs = new List<string> { nameBackref };
        _argBackRefs = new Dictionary<string, int>();
    }

    private NameMangler(TypeSystem ts, bool useArgBackrefs)
    {
        _types = ts;
        _nameBackRefs = null;
        _argBackRefs = useArgBackrefs ? new Dictionary<string, int>() : null;
    }

    /// <summary>
    /// Produce an MSVC-compatible decorated name for a C function.
    /// Format: ?name@@$$J0YA(ret)(params)@Z  for cdecl
    ///         ?name@@$$J0YM(ret)(params)@Z  for __clrcall
    /// </summary>
    public static string MangleFunctionName(TypeSystem ts, string tuHash, Obj fn)
    {
        CType funcTy = fn.Ty;
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only reaches here on x86 (normalized to Cdecl on x64)
            _ => "A", // cdecl
        };
        // Static functions get TU-hash-scoped names to avoid cross-TU collisions
        string name = fn.IsStatic ? $"{fn.Name}_?A0x{tuHash}" : fn.Name;
        var sb = new StringBuilder();
        sb.Append($"?{name}@@$$J0Y{cc}");

        NameMangler mangler = new NameMangler(ts, fn.Name); // function name = slot 0

        // Return type: uses name backrefs but does NOT participate in arg backref table
        mangler.MangleType(sb, funcTy.ReturnTy, isReturn: true);

        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            mangler.MangleArgType(sb, p);
            paramCount++;
        }
        if (paramCount == 0)
            sb.Append("XZ"); // void params: X = no params, Z = terminator
        else if (funcTy.IsVariadic)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
        return sb.ToString();
    }

    /// <summary>
    /// Mangle a function argument type with backreference support.
    /// If the full mangled type string was seen before, emit a digit (0-9).
    /// Otherwise emit the full type and register it for future backrefs.
    /// </summary>
    private void MangleArgType(StringBuilder sb, CType ty)
    {
        // Mangle into a temp buffer with name backrefs disabled and with an isolated
        // arg-backref table, so we get a deterministic canonical arg-type key without
        // polluting the outer function's arg-backref slots.
        NameMangler nestedMangler = new NameMangler(_types, useArgBackrefs: true);
        var tmp = new StringBuilder();
        nestedMangler.MangleType(tmp, ty, isReturn: false);
        string canonical = tmp.ToString();

        // Check arg-type backref table using the canonical (no-backref) key
        if (_argBackRefs.TryGetValue(canonical, out int slot))
        {
            sb.Append((char)('0' + slot));
            return;
        }

        // No arg-type match — mangle again WITH name backrefs for final output
        MangleType(sb, ty, isReturn: false);

        // Register the canonical key if multi-char and slots available
        if (canonical.Length > 1 && _argBackRefs.Count < 10)
            _argBackRefs[canonical] = _argBackRefs.Count;
    }

    private void MangleType(StringBuilder sb, CType ty, bool isReturn)
    {
        // Strip qualifiers for mangling
        switch (ty.Kind)
        {
            case TypeKind.Void: sb.Append('X'); break;
            case TypeKind.Bool: sb.Append("_N"); break;
            case TypeKind.Char:
                if (ty.IsUnsigned) sb.Append('E');
                else if (ty.Origin?.Kind == TypeKind.Char && !ty.IsUnsigned) sb.Append('D'); // plain char
                else sb.Append('D'); // default char is plain char
                break;
            case TypeKind.Short:
                sb.Append(ty.IsUnsigned ? 'G' : 'F');
                break;
            case TypeKind.Int:
                sb.Append(ty.IsUnsigned ? 'I' : 'H');
                break;
            case TypeKind.Enum:
                {
                    string enumName = TypeSystem.GetTagName(ty);
                    if (enumName != null)
                    {
                        if (isReturn) sb.Append("?A");
                        sb.Append("W4");
                        MangleTagName(sb, enumName);
                    }
                    else
                    {
                        // Anonymous enum with no tag or typedef — mangle as underlying int
                        sb.Append(ty.IsUnsigned ? 'I' : 'H');
                    }
                    break;
                }
            case TypeKind.Long when _types.TyLong.Size == 4:
                sb.Append(ty.IsUnsigned ? 'K' : 'J');
                break;
            case TypeKind.Long:
            case TypeKind.LLong:
                sb.Append(ty.IsUnsigned ? "_K" : "_J");
                break;
            case TypeKind.Float: sb.Append('M'); break;
            case TypeKind.Double: sb.Append('N'); break;
            case TypeKind.LDouble: sb.Append("O"); break; // long double in MSVC mangling
            case TypeKind.Ptr:
                ManglePointer(sb, ty);
                break;
            case TypeKind.Array:
                // Array parameter decays to pointer (handled by ManglePointer
                // when the parser produces Ptr(Array) via FuncParams decay).
                // This branch handles the 1D case; multi-dim is caught by
                // ManglePointer's baseTy.Kind == Array check.
                ManglePointer(sb, _types.PointerTo(ty.Base));
                break;
            case TypeKind.Struct:
                if (isReturn) sb.Append("?A");
                sb.Append('U');
                MangleTagName(sb, _types.GetStructName(ty));
                break;
            case TypeKind.Union:
                if (isReturn) sb.Append("?A");
                sb.Append('T');
                MangleTagName(sb, _types.GetStructName(ty));
                break;
            case TypeKind.Func:
                // Function pointer type
                MangleFuncPtr(sb, ty);
                break;
        }
    }

    /// <summary>
    /// Emit a struct/union/enum tag name with name-backref support.
    /// First occurrence: emit name + "@@" (global scope) and register in name table.
    /// Subsequent: emit digit + "@" (backref + scope terminator).
    /// </summary>
    private void MangleTagName(StringBuilder sb, string name)
    {
        if (_nameBackRefs != null)
        {
            int idx = _nameBackRefs.IndexOf(name);
            if (idx >= 0)
            {
                // Name backref: digit replaces name@, then @ for scope
                sb.Append((char)('0' + idx));
                sb.Append('@');
                return;
            }
            if (_nameBackRefs.Count < 10)
                _nameBackRefs.Add(name);
        }
        // First occurrence: name + @@ (global scope)
        sb.Append(name);
        sb.Append("@@");
    }

    private void ManglePointer(StringBuilder sb, CType ty)
    {
        string e = _types.PointerSize == 4 ? "" : "E"; // __ptr64 on 64-bit
        CType baseTy = ty.Base;

        if (baseTy.Kind == TypeKind.Func)
        {
            // Function pointer: P6/Q6/R6/S6 depending on pointer-self qualifiers
            MangleFuncPtr(sb, baseTy, ty.IsConst, ty.IsVolatile);
            return;
        }

        if (baseTy.Kind == TypeKind.Array)
        {
            // Pointer to array (from multi-dim array param decay):
            // emit pointer qualifiers + Y-encoded array dimensions
            char ptrQualArr;
            if (ty.IsConst && ty.IsVolatile) ptrQualArr = 'S';
            else if (ty.IsConst) ptrQualArr = 'Q';
            else if (ty.IsVolatile) ptrQualArr = 'R';
            else ptrQualArr = 'P';

            char pteeQualArr;
            if (baseTy.IsConst && baseTy.IsVolatile) pteeQualArr = 'D';
            else if (baseTy.IsConst) pteeQualArr = 'B';
            else if (baseTy.IsVolatile) pteeQualArr = 'C';
            else pteeQualArr = 'A';

            sb.Append($"{ptrQualArr}{e}{pteeQualArr}");
            MangleArrayDims(sb, baseTy);
            return;
        }

        // Pointer-self qualifiers: P=none, Q=const, R=volatile, S=const volatile
        char ptrQual;
        if (ty.IsConst && ty.IsVolatile) ptrQual = 'S';
        else if (ty.IsConst) ptrQual = 'Q';
        else if (ty.IsVolatile) ptrQual = 'R';
        else ptrQual = 'P';

        // Pointee qualifiers: A=none, B=const, C=volatile, D=const volatile
        char pteeQual;
        if (baseTy.IsConst && baseTy.IsVolatile) pteeQual = 'D';
        else if (baseTy.IsConst) pteeQual = 'B';
        else if (baseTy.IsVolatile) pteeQual = 'C';
        else pteeQual = 'A';

        sb.Append($"{ptrQual}{e}{pteeQual}");

        MangleType(sb, baseTy, isReturn: false);
    }

    private void MangleFuncPtr(StringBuilder sb, CType funcTy, bool ptrIsConst = false, bool ptrIsVolatile = false)
    {
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only on x86
            _ => "A",
        };
        // Pointer-self qualifiers: P=none, Q=const, R=volatile, S=const volatile
        char ptrQual;
        if (ptrIsConst && ptrIsVolatile) ptrQual = 'S';
        else if (ptrIsConst) ptrQual = 'Q';
        else if (ptrIsVolatile) ptrQual = 'R';
        else ptrQual = 'P';
        sb.Append($"{ptrQual}6{cc}");
        MangleType(sb, funcTy.ReturnTy, isReturn: false);
        int count = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            // Func ptr params share the outer function's backref tables
            // (only when called from MangleFunctionName context)
            if (_argBackRefs != null)
                MangleArgType(sb, p);
            else
                MangleType(sb, p, isReturn: false);
            count++;
        }
        if (count == 0)
            sb.Append("XZ"); // void params inside func ptr also use XZ
        else if (funcTy.IsVariadic && funcTy.Params != null)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
    }

    /// <summary>
    /// Emit MSVC Y-encoding for inner array dimensions in multi-dim array parameter decay.
    /// Format: Y<ndims><bound1>...<boundN><elemtype>
    /// </summary>
    private void MangleArrayDims(StringBuilder sb, CType ty)
    {
        sb.Append('Y');
        // Count inner dimensions and collect bounds
        int ndims = 0;
        var dims = new List<int>();
        CType cur = ty;
        while (cur.Kind == TypeKind.Array)
        {
            ndims++;
            dims.Add(cur.ArrayLen);
            cur = cur.Base;
        }
        sb.Append(EncodeNumber(ndims));
        foreach (int dim in dims)
            sb.Append(EncodeNumber(dim));
        MangleType(sb, cur, isReturn: false);
    }

    /// <summary>MSVC number encoding for array dimensions.</summary>
    private static string EncodeNumber(int value)
    {
        if (value == 0) return "A@";
        if (value >= 1 && value <= 10) return ((char)('0' + value - 1)).ToString();
        // Hex encoding: nibbles A-P (A=0, P=15), MSB first, terminated by @
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, (char)('A' + (value & 0xF)));
            value >>= 4;
        }
        sb.Append('@');
        return sb.ToString();
    }

    /// <summary>
    /// Generate array TypeDef name: $ArrayType$$$BY(ndims)(bounds)(elemtype)
    /// </summary>
    public static string MangleArrayTypeName(TypeSystem ts, CType ty)
    {
        Debug.Assert(ty.Kind == TypeKind.Array);

        var sb = new StringBuilder("$ArrayType$$$BY");

        // Count dimensions
        int ndims = 0;
        var dims = new List<int>();
        CType cur = ty;
        while (cur.Kind == TypeKind.Array)
        {
            ndims++;
            dims.Add(cur.ArrayLen);
            cur = cur.Base;
        }
        sb.Append(EncodeNumber(ndims));
        foreach (int dim in dims)
            sb.Append(EncodeNumber(dim));

        // Element type code
        NameMangler mangler = new NameMangler(ts, useArgBackrefs: false);
        mangler.MangleType(sb, cur, isReturn: false);

        return sb.ToString();
    }

    public static string MangleStaticLocalName(string tuHash, Obj var)
    {
        return $"?A0x{tuHash}.{var.Name}";
    }
}
