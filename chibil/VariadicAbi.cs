namespace Chibil;

internal static class VariadicAbi
{
    public static IEnumerable<CType> Parameters(CType function, TypeSystem types)
    {
        for (CType p = function.Params; p != null; p = p.Next)
            yield return p;
        if (function.IsVariadic)
            yield return types.PointerTo(types.TyVoid);
    }

    public static int FixedParameterCount(CType function)
    {
        int count = 0;
        for (CType p = function.Params; p != null; p = p.Next)
            count++;
        return count;
    }

    public static void ValidateAddress(CType type, Token token, CompilerOptions options)
    {
        if (!type.IsVariadic)
            return;
        if (!options.EmulatedVarargs)
            Util.ErrorTok(token, "variadic function addresses require -femulated-varargs");
        if (type.CallConv != CallConv.Clrcall)
            Util.ErrorTok(token, "variadic function addresses require __clrcall");
    }

    public static CType ArgumentType(CType type) =>
        type.Kind is TypeKind.Struct or TypeKind.Union ? type.Canonicalize() : type;

    public static void ValidateObjectType(CType type, Token token)
    {
        if (type.Kind is TypeKind.Void or TypeKind.Func or TypeKind.Array or TypeKind.Vla ||
            type.Size < 0 || type.Align <= 0)
            Util.ErrorTok(token, "variadic argument requires a complete object type");
    }

    public static int Advance(int cursor, CType type, Token token, out int offset)
    {
        ValidateObjectType(type, token);
        long aligned = ((long)cursor + type.Align - 1) & -(long)type.Align;
        long end = aligned + type.Size;
        if (end > int.MaxValue)
            Util.ErrorTok(token, "variadic argument area is too large");
        offset = (int)aligned;
        return (int)end;
    }
}
