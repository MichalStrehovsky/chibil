using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkLibraryTests : ChibiTestBase
{
    [Fact]
    public void MissingRealLibraryIsAnErrorEvenWhenUnused()
    {
        var builder = Compile("int main(void) { return 42; }");
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Chilink([Path.Combine(builder._tempDir, "missing.lib")]));
        Assert.Contains("missing.lib", error.Message);
    }

    [Fact]
    public async Task BareMscoreeIsNotIgnored()
    {
        var builder = Compile("int main(void) { return 42; }");
        var result = await ChilinkCommandLineTests.RunDriver(builder,
            ["-o", "output.exe", "-emain", .. builder._objFiles, "MSCOREE.LIB"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MSCOREE.LIB", result.Diagnostics);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.exe")));
    }

    [Fact]
    public async Task LibrarySearchSupportsQuotedResponseFiles()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target");
        string directory = Path.Combine(builder._tempDir, "libraries with spaces");
        Directory.CreateDirectory(directory);
        File.Move(builder._objFiles[1], Path.Combine(directory, "native.lib"));
        string output = Path.Combine(builder._tempDir, "output.dll");
        string response = Path.Combine(builder._tempDir, "arguments.rsp");
        File.WriteAllText(response, $"""
            --entry=main --output="{output}"
            "{builder._objFiles[0]}" -l:native.lib
            -L "{directory}"
            """);
        var result = await ChilinkCommandLineTests.RunDriver(builder, ["@" + response]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(output, true).RunAndCheck(42);
    }
}
