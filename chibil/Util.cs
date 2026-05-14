using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Chibicc;

public static class Util
{
    // ═══════════════════════════════════════════════════════════════
    //  Token comparison helpers
    // ═══════════════════════════════════════════════════════════════

    public static bool Equal(Token tok, string op)
    {
        if (tok.Len != op.Length) return false;
        byte[] buf = tok.Buf;
        int loc = tok.Loc;
        for (int i = 0; i < op.Length; i++)
            if (buf[loc + i] != (byte)op[i])
                return false;
        return true;
    }

    public static Token Skip(Token tok, string op)
    {
        if (!Equal(tok, op))
            ErrorTok(tok, $"expected '{op}'");
        return tok.Next;
    }

    public static bool Consume(ref Token rest, Token tok, string str)
    {
        if (Equal(tok, str)) { rest = tok.Next; return true; }
        rest = tok;
        return false;
    }

    // Extract token text as a C# string
    public static string GetTokenText(Token tok)
    {
        return Encoding.UTF8.GetString(tok.Buf, tok.Loc, tok.Len);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Alignment
    // ═══════════════════════════════════════════════════════════════

    public static int AlignTo(int n, int align)
    {
        return (n + align - 1) / align * align;
    }

    public static int AlignDown(int n, int align)
    {
        return AlignTo(n - align + 1, align);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error reporting
    // ═══════════════════════════════════════════════════════════════

    [DoesNotReturn]
    public static void Error(string msg)
    {
        throw new ChibiccException(msg);
    }

    [DoesNotReturn]
    public static void ErrorAt(CFile file, byte[] buf, int loc, string msg)
    {
        // Find line number
        int lineNo = 1;
        for (int i = 0; i < loc; i++)
            if (buf[i] == (byte)'\n')
                lineNo++;

        string formatted = FormatError(file.DisplayName ?? file.Name, buf, lineNo, loc, msg);
        throw new ChibiccException(formatted);
    }

    [DoesNotReturn]
    public static void ErrorTok(Token tok, string msg)
    {
        string formatted = FormatError(
            tok.FileName ?? tok.File?.Name ?? "<unknown>",
            tok.Buf, tok.LineNo, tok.Loc, msg);
        throw new ChibiccException(formatted);
    }

    public static void WarnTok(Token tok, string msg)
    {
        string formatted = FormatError(
            tok.FileName ?? tok.File?.Name ?? "<unknown>",
            tok.Buf, tok.LineNo, tok.Loc, msg);
        Console.Error.Write(formatted);
    }

    [DoesNotReturn]
    public static void Unreachable()
    {
        throw new ChibiccException("internal error: unreachable");
    }

    private static string FormatError(string filename, byte[] buf, int lineNo, int loc, string msg)
    {
        if (buf == null)
            return $"{filename}: {msg}\n";

        // Find the start and end of the line containing loc
        int lineStart = loc;
        while (lineStart > 0 && buf[lineStart - 1] != (byte)'\n')
            lineStart--;

        int lineEnd = loc;
        while (lineEnd < buf.Length && buf[lineEnd] != 0 && buf[lineEnd] != (byte)'\n')
            lineEnd++;

        var sb = new StringBuilder();

        // Print the line
        string prefix = $"{filename}:{lineNo}: ";
        sb.Append(prefix);
        sb.AppendLine(Encoding.UTF8.GetString(buf, lineStart, lineEnd - lineStart));

        // Print the caret
        int pos = Unicode.DisplayWidth(buf, lineStart, loc - lineStart) + prefix.Length;
        sb.Append(new string(' ', pos));
        sb.Append("^ ");
        sb.AppendLine(msg);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  BinaryPrimitives helpers for init_data writes
    // ═══════════════════════════════════════════════════════════════

    public static void WriteBuf(byte[] buf, int offset, long val, int size)
    {
        switch (size)
        {
            case 1:
                buf[offset] = (byte)val;
                break;
            case 2:
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(offset), (short)val);
                break;
            case 4:
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), (int)val);
                break;
            case 8:
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), val);
                break;
            default:
                Unreachable();
                break;
        }
    }

    public static void WriteFloat(byte[] buf, int offset, float val)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), val);
    }

    public static void WriteDouble(byte[] buf, int offset, double val)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset), val);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SoftFloat80 — convert double to 80-bit x87 extended precision
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert a double to its 80-bit x87 extended precision byte
    /// representation (10 bytes, little-endian). Used for emitting
    /// long double constants in .data sections.
    /// </summary>
    public static byte[] DoubleToF80Bytes(double value)
    {
        byte[] result = new byte[16]; // 16 bytes for alignment (padded)

        if (double.IsNaN(value))
        {
            // Quiet NaN
            result[7] = 0xC0;
            result[8] = 0xFF;
            result[9] = 0x7F;
            return result;
        }

        if (double.IsPositiveInfinity(value))
        {
            result[7] = 0x80;
            result[8] = 0xFF;
            result[9] = 0x7F;
            return result;
        }

        if (double.IsNegativeInfinity(value))
        {
            result[7] = 0x80;
            result[8] = 0xFF;
            result[9] = 0xFF;
            return result;
        }

        if (value == 0.0)
        {
            // Check for -0.0
            if (double.IsNegative(value))
                result[9] = 0x80;
            return result;
        }

        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int sign = (int)(bits >> 63);
        int exponent = (int)((bits >> 52) & 0x7FF);
        ulong mantissa = bits & 0x000FFFFFFFFFFFFF;

        if (exponent == 0)
        {
            // Denormalized double → denormalized f80
            // Rebias: f80 exponent = double exponent - 1023 + 16383
            // For denorms, effective exponent is -1022
            // The mantissa has no implicit leading 1
            ulong f80Mantissa = mantissa << 11;
            // Store as f80 denorm (exponent = 0)
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0), f80Mantissa);
            result[8] = 0;
            result[9] = (byte)(sign << 7);
        }
        else
        {
            // Normalized double
            // f80 exponent = double_exponent - 1023 + 16383
            int f80Exp = exponent - 1023 + 16383;
            // f80 has explicit integer bit (bit 63 of significand)
            ulong f80Mantissa = (1UL << 63) | (mantissa << 11);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0), f80Mantissa);
            ushort expWord = (ushort)((sign << 15) | f80Exp);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), expWord);
        }

        return result;
    }

    /// <summary>
    /// Get the two uint64 values representing an f80 value laid out
    /// in memory (little-endian, 16 bytes padded), matching the C
    /// union { long double f80; uint64_t u64[2]; } pattern.
    /// </summary>
    public static (ulong u64_0, ulong u64_1) DoubleToF80Words(double value)
    {
        byte[] bytes = DoubleToF80Bytes(value);
        ulong u0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0));
        ulong u1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        return (u0, u1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  String helpers
    // ═══════════════════════════════════════════════════════════════

    public static bool StartsWith(byte[] buf, int pos, string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (buf[pos + i] != (byte)s[i])
                return false;
        return true;
    }

    /// <summary>
    /// Convert a C-style byte string (from source buffer) to a C# string.
    /// </summary>
    public static string BytesToString(byte[] buf, int offset, int len)
    {
        return Encoding.UTF8.GetString(buf, offset, len);
    }

    /// <summary>
    /// Convert a C# string to a UTF-8 byte array with trailing NUL.
    /// </summary>
    public static byte[] StringToBytes(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        byte[] result = new byte[utf8.Length + 1];
        Array.Copy(utf8, result, utf8.Length);
        return result;
    }
}
