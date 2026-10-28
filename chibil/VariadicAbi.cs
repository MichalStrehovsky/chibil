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

    /// <summary>
    /// Emulated variadic definitions only accept managed vararg call sites, so they have no
    /// native entry point. Addresses of external declarations are checked by chilink.
    /// </summary>
    public static void ValidateAddress(Obj function, Token token)
    {
        if (function.Ty.IsVariadic && function.IsDefinition && function.Ty.CallConv != CallConv.Clrcall)
            Util.ErrorTok(token, "native address of an emulated variadic function; declare it __clrcall");
    }

    public static CType ArgumentType(CType type) =>
        type.Kind is TypeKind.Struct or TypeKind.Union ? type.Canonicalize() : type;

    public static void ValidateObjectType(CType type, Token token)
    {
        if (type.Kind is TypeKind.Void or TypeKind.Func or TypeKind.Array or TypeKind.Vla ||
            type.Size < 0 || type.Align <= 0)
            Util.ErrorTok(token, "variadic argument requires a complete object type");
    }

    /// <summary>
    /// Windows x64 passes aggregates whose size is not 1, 2, 4 or 8 bytes as a pointer to a copy.
    /// </summary>
    public static bool IsPassedByReferenceOnWindows(CType type) =>
        type.Kind is TypeKind.Struct or TypeKind.Union && type.Size is not (1 or 2 or 4 or 8);

    /// <summary>
    /// Classifies each eightbyte of a SysV x86-64 argument: true for SSE, false for INTEGER.
    /// Returns null for the MEMORY class.
    /// </summary>
    public static bool[] ClassifySysV(CType type)
    {
        if (type.Size > 16 || type.Size <= 0)
            return null;
        var classes = new SysVClass[(type.Size + 7) / 8];
        if (!Classify(type, 0, classes))
            return null;
        var result = new bool[classes.Length];
        for (int i = 0; i < classes.Length; i++)
            result[i] = classes[i] == SysVClass.Sse;
        return result;
    }

    private enum SysVClass { None, Integer, Sse }

    private static bool Classify(CType type, int offset, SysVClass[] classes)
    {
        switch (type.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                for (Member member = type.Members; member != null; member = member.Next)
                {
                    if (member.IsBitfield)
                    {
                        if (member.BitWidth != 0)
                            Merge(classes, offset + member.Offset, SysVClass.Integer);
                        continue;
                    }
                    if (member.Ty.Size == 0)
                        continue;
                    if ((member.Offset % Math.Max(1, member.Ty.Align)) != 0)
                        return false;
                    if (!Classify(member.Ty, offset + member.Offset, classes))
                        return false;
                }
                return true;
            case TypeKind.Array:
                for (int i = 0; i < type.ArrayLen; i++)
                    if (!Classify(type.Base, offset + i * type.Base.Size, classes))
                        return false;
                return true;
            case TypeKind.Float:
            case TypeKind.Double:
                Merge(classes, offset, SysVClass.Sse);
                return true;
            case TypeKind.LDouble:
                if (type.Size > 8)
                    return false;
                Merge(classes, offset, SysVClass.Sse);
                return true;
            default:
                Merge(classes, offset, SysVClass.Integer);
                return true;
        }
    }

    private static void Merge(SysVClass[] classes, int offset, SysVClass kind)
    {
        int index = offset / 8;
        if (index >= classes.Length)
            return;
        if (classes[index] != SysVClass.Integer)
            classes[index] = kind;
    }
}
