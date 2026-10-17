using System.Diagnostics;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkLibraryTests : ChibiTestBase
{
    [Fact]
    public void MissingRealLibraryIsAnErrorEvenWhenUnused()
    {
        var builder = Compile("int main(void) { return 42; }");
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/entry:main", Path.Combine(builder._tempDir, "missing.lib")]));
        Assert.Contains("missing.lib", error.Message);
    }

    [Fact]
    public void BareMscoreeCompatibilityExceptionDoesNotApplyToQualifiedPaths()
    {
        var builder = Compile("int main(void) { return 42; }");
        builder.Link(["/entry:main", "MSCOREE.LIB"]).RunAndCheck(42);
        Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/entry:main", Path.Combine(builder._tempDir, "mscoree.lib")]));
    }

    [Theory]
    [InlineData("LIB")]
    [InlineData("LIBPATH")]
    [InlineData("cwd")]
    public async Task LibrarySearchSupportsPrecedenceAndQuotedResponseFiles(string source)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target");
        string directory = Path.Combine(builder._tempDir, "libraries with spaces");
        Directory.CreateDirectory(directory);
        if (source != "cwd")
            File.Move(builder._objFiles[1], Path.Combine(directory, "native.lib"));
        string alternate = Path.Combine(builder._tempDir, "alternate libraries");
        Directory.CreateDirectory(alternate);
        builder.NativeLibrary("int target(void) { return 17; }", "target", "alternate.dll", "alternate.lib");
        File.Move(builder._objFiles[2], Path.Combine(alternate, "native.lib"));
        string output = Path.Combine(builder._tempDir, "output.dll");
        string response = Path.Combine(builder._tempDir, "arguments.rsp");
        File.WriteAllText(response, $"""
            /entry:main /out:"{output}"
            "{builder._objFiles[0]}" native.lib
            {(source == "LIB" ? "" : "/LiBpAtH:\"" + (source == "cwd" ? alternate : directory) + "\"")}
            """);
        var start = new ProcessStartInfo(Path.Combine(
            Path.GetDirectoryName(typeof(Chilink.Driver).Assembly.Location), "chilink.exe"))
        {
            WorkingDirectory = builder._tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["LIB"] = source == "LIB" ? directory : alternate;
        start.ArgumentList.Add("@" + response);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
        new LinkResult(output, true).RunAndCheck(42);
    }
}
