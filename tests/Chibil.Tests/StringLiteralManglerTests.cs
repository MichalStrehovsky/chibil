using System.Text;
using Chibil;
using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Verifies the `??_C@` string-literal COMDAT name mangler byte-for-byte against
/// names produced by `cl.exe /clr /BC /GF` (captured as ground truth). This is the
/// content-keyed name that lets identical literals fold across translation units.
/// </summary>
public class StringLiteralManglerTests
{
    [Theory]
    [InlineData("", "??_C@_00CNPNBAHC@@")]
    [InlineData("a", "??_C@_01MCMALHOG@a@")]
    [InlineData("ab", "??_C@_02BOGAIONP@ab@")]
    [InlineData("abc", "??_C@_03FIKCJHKP@abc@")]
    [InlineData("abcdefghi", "??_C@_09GLBJAHID@abcdefghi@")]
    [InlineData("abcdefghij", "??_C@_0L@OLMIEILA@abcdefghij@")]
    [InlineData("abcdefghijk", "??_C@_0M@ENIDMJKI@abcdefghijk@")]
    [InlineData("hello world", "??_C@_0M@LACCCNMM@hello?5world@")]
    [InlineData("different", "??_C@_09BEJCKEDA@different@")]
    [InlineData("a b", "??_C@_03DCKHPPEA@a?5b@")]
    [InlineData("A.B", "??_C@_03NIPFJNG@A?4B@")]
    [InlineData("x/y", "??_C@_03LNNMGLPC@x?1y@")]
    public void NarrowStrings_MatchMsvc(string text, string expected)
    {
        byte[] bytes = new byte[Encoding.ASCII.GetByteCount(text) + 1];
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, 0);

        var mangler = new MsvcNameMangler(new TypeSystem(DataModel.LLP64), "deadbeef");
        Assert.Equal(expected, mangler.MangleStringLiteralName(bytes, 1));
    }

    [Fact]
    public void BackslashAndTab_MatchMsvc()
    {
        var mangler = new MsvcNameMangler(new TypeSystem(DataModel.LLP64), "deadbeef");
        Assert.Equal("??_C@_03OGAGBPBM@p?2q@",
            mangler.MangleStringLiteralName(new byte[] { (byte)'p', (byte)'\\', (byte)'q', 0 }, 1));
        Assert.Equal("??_C@_08HJJLMNMI@tab?7here@",
            mangler.MangleStringLiteralName(
                new byte[] { (byte)'t', (byte)'a', (byte)'b', (byte)'\t',
                             (byte)'h', (byte)'e', (byte)'r', (byte)'e', 0 }, 1));
    }

    [Fact]
    public void JamCrc32_MatchesKnownValue()
    {
        // JamCRC of a single null byte (no final XOR).
        Assert.Equal(0x2DFD1072u, Util.JamCrc32(new byte[] { 0 }));
    }
}
