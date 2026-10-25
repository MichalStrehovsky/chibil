using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkLinkerScriptTests
{
    public ChilinkLinkerScriptTests()
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("ELF import execution requires Linux.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlibcStyleScriptsProvideCallsAndNativeAddresses(bool searched)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int first(int);
            int second(int);
            int (*pointer)(int) = second;
            int main(void) { return first(19) + pointer(21); }
            """).AddCrt()
            .NativeLinuxLibrary("int first(int n) { return n + 1; }", "libfirst.so", "libfirst.so.1")
            .NativeLinuxLibrary("int second(int n) { return n + 1; }", "libsecond.so", "libsecond.so.1");
        builder._objFiles.RemoveRange(2, 2);
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, """
            /* GNU ld script */
            OUTPUT_FORMAT("ignored-default", "ignored-big", "ignored-little")
            GROUP ( libfirst.so AS_NEEDED ( libsecond.so ) )
            """);
        var result = builder.Chilink(searched ? ["-L", builder._tempDir, "-lscript"] : [script]);
        File.Delete(script);
        result.RunAndCheck(42);
    }

    [Theory]
    [InlineData("INPUT", " ")]
    [InlineData("GROUP", ",")]
    public void QuotedPathsCommentsAndSeparatorsWork(string command, string separator)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int first(void); int second(void); int main(void) { return first() + second(); }")
            .NativeLinuxLibrary("int first(void) { return 20; }", "first, library.so", "libfirst.so.1")
            .NativeLinuxLibrary("int second(void) { return 22; }", "second library.so", "libsecond.so.1");
        builder._objFiles.RemoveRange(1, 2);
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, $$"""
            OUTPUT_FORMAT("elf64-x86-64");
            {{command}} /* comment */ ("first, library.so"{{separator}}"second library.so");
            """);
        builder.Chilink([script]).RunAndCheck(42);
    }

    [Fact]
    public void NestedScriptsAndLibrarySearchUseTheUnderlyingProvider()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "libnative.so", "libnative.so.1");
        builder._objFiles.RemoveAt(1);
        string scripts = Path.Combine(builder._tempDir, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "libouter.so"), "GROUP ( libinner.so )");
        File.WriteAllText(Path.Combine(scripts, "libinner.so"), "INPUT ( -lnative )");
        var result = builder.Chilink(["-L", scripts, "-L", builder._tempDir, "-louter"]);
        Directory.Delete(scripts, recursive: true);
        result.RunAndCheck(42);
    }

    [Fact]
    public void ScriptProvidersRetainTheirListedOrder()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "libfirst.so", "libfirst.so.1")
            .NativeLinuxLibrary("int target(void) { return 17; }", "libsecond.so", "libsecond.so.1");
        builder._objFiles.RemoveRange(1, 2);
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, "GROUP ( AS_NEEDED(libfirst.so) libsecond.so )");
        builder.Chilink([script]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptFilesSearchTheirDirectoryThenWorkingDirectoryBeforeLibraryPaths(bool inScriptDirectory)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "libfirst.so", "libfirst.so.1")
            .NativeLinuxLibrary("int target(void) { return 17; }", "libsecond.so", "libsecond.so.1");
        string scripts = Path.Combine(builder._tempDir, "scripts");
        string libraries = Path.Combine(builder._tempDir, "libraries");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(libraries);
        File.Move(builder._objFiles[1], Path.Combine(inScriptDirectory ? scripts : builder._tempDir, "libnative.so"));
        File.Move(builder._objFiles[2], Path.Combine(libraries, "libnative.so"));
        if (inScriptDirectory)
            File.Copy(Path.Combine(libraries, "libnative.so"), Path.Combine(builder._tempDir, "libnative.so"));
        File.WriteAllText(Path.Combine(scripts, "libscript.so"), "INPUT ( libnative.so )");
        var result = await ChilinkCommandLineTests.RunDriver(builder,
            ["-o", "output.dll", "-emain", builder._objFiles[0], "scripts/libscript.so", "-L", libraries]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }

    [Fact]
    public async Task ExactLibraryReferencesWithoutSonameRetainTheRequestedFilename()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "libnative.so.7", soname: null);
        string libraries = Path.Combine(builder._tempDir, "libraries");
        Directory.CreateDirectory(libraries);
        File.Copy(builder._objFiles[1], Path.Combine(libraries, "libnative.so.7"));
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, "INPUT ( -l:libnative.so.7 )");
        var result = await ChilinkCommandLineTests.RunDriver(builder,
            ["-o", "output.dll", "-emain", builder._objFiles[0], script, "-L", libraries]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Directory.Delete(libraries, recursive: true);
        File.Delete(script);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }

    [Fact]
    public void ScriptFileReferencesWithoutSonameUseTheResolvedLibraryPath()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", soname: null);
        string libraries = Path.Combine(builder._tempDir, "libraries");
        Directory.CreateDirectory(libraries);
        File.Move(builder._objFiles[1], Path.Combine(libraries, "libnative.so"));
        builder._objFiles.RemoveAt(1);
        string script = Path.Combine(libraries, "libscript.so");
        File.WriteAllText(script, "INPUT ( libnative.so )");
        var result = builder.Chilink([script]);
        File.Delete(script);
        result.RunAndCheck(42);
    }

    [Fact]
    public async Task StaticArchivesInScriptsAreSkippedWithAWarning()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }");
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(Path.Combine(builder._tempDir, "libnonshared.a"), "!<arch>\n");
        File.WriteAllText(script, "GROUP ( libnative.so libnonshared.a )");
        var result = await ChilinkCommandLineTests.RunDriver(builder,
            ["-o", "output.dll", "-emain", builder._objFiles[0], script]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Contains("warning", result.Diagnostics);
        Assert.Contains("libnonshared.a", result.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }

    [Fact]
    public void SkippedScriptArchivesDoNotHideExplicitArchiveInputs()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        string archive = Path.Combine(builder._tempDir, "libempty.a");
        File.WriteAllText(archive, "!<arch>\n");
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, "INPUT ( libempty.a )");
        Assert.Contains("archive", Assert.Throws<InvalidOperationException>(() => builder.Chilink([script, archive])).Message);
    }

    [Theory]
    [InlineData("/* unfinished", "unterminated comment")]
    [InlineData("INPUT ( \"unfinished )", "unterminated quoted filename")]
    [InlineData("INPUT ()", "empty input list")]
    [InlineData("INPUT ( libc.so.6", "expected ')'")]
    [InlineData("INPUT ( libc.so.6, )", "after ','")]
    [InlineData("INPUT ( \"\" )", "empty library filename")]
    [InlineData("OUTPUT_FORMAT(elf32-i386", "expected ')'")]
    [InlineData("OUTPUT_FORMAT(elf64-x86-64)", "at least one input")]
    [InlineData("SEARCH_DIR(/tmp) INPUT(libc.so.6)", "unsupported command")]
    [InlineData("INPUT ( =/lib/libc.so.6 )", "sysroot")]
    [InlineData("INPUT ( $SYSROOT/lib/libc.so.6 )", "sysroot")]
    [InlineData("INPUT ( -static )", "unsupported input option")]
    [InlineData("INPUT ( libchilink_missing.so )", "cannot find input")]
    public void InvalidOrUnsupportedScriptsFailWithoutWritingOutput(string contents, string diagnostic)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, contents);
        string message = Assert.Throws<InvalidOperationException>(() => builder.Chilink([script])).Message;
        Assert.Contains(script, message);
        Assert.Contains(diagnostic, message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursiveScriptReferencesFail(bool indirect)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        string script = Path.Combine(builder._tempDir, "libfirst.so");
        File.WriteAllText(script, indirect ? "INPUT(libsecond.so)" : "INPUT(libfirst.so)");
        File.WriteAllText(Path.Combine(builder._tempDir, "libsecond.so"), "GROUP(libfirst.so)");
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => builder.Chilink([script])).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcessiveNestingFailsExplicitly(bool asNeeded)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        string script = Path.Combine(builder._tempDir, "lib0.so");
        if (asNeeded)
            File.WriteAllText(script, "INPUT(" +
                string.Concat(Enumerable.Repeat("AS_NEEDED(", 65)) + "libc.so.6" + new string(')', 66));
        else
            for (int i = 0; i < 66; i++)
                File.WriteAllText(Path.Combine(builder._tempDir, $"lib{i}.so"), $"INPUT(lib{i + 1}.so)");
        Assert.Contains("nesting limit", Assert.Throws<InvalidOperationException>(() => builder.Chilink([script])).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllScriptInputsAreProtectedFromOverwrite(bool pdb)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }")
            .NativeLinuxLibrary("int unused(void) { return 0; }");
        builder._objFiles.RemoveAt(1);
        string inner = Path.Combine(builder._tempDir, "libinner.so");
        string outer = Path.Combine(builder._tempDir, "libouter.so");
        string archive = Path.Combine(builder._tempDir, "libnonshared.a");
        File.WriteAllText(outer, "GROUP(libinner.so)");
        File.WriteAllText(inner, "INPUT(libnative.so libnonshared.a)");
        File.WriteAllText(archive, "!<arch>\n");
        foreach (string path in new[] { outer, inner, archive, Path.Combine(builder._tempDir, "libnative.so") })
        {
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() => pdb
                ? builder.Chilink([outer, "--pdb=" + path])
                : builder.Chilink([outer], outputPath: path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void InstalledLibcAndLibmScriptsProvideImports()
    {
        if (!File.Exists("/usr/lib/x86_64-linux-gnu/libc.so") ||
            !File.Exists("/usr/lib/x86_64-linux-gnu/libm.so"))
            throw Xunit.Sdk.SkipException.ForSkip("This smoke test requires x64 multiarch glibc development libraries.");
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int getpid(void);
            double cos(double);
            int main(void) { return getpid() > 0 && cos(0) == 1 ? 42 : 1; }
            """).Chilink(["-lc", "-lm"]).RunAndCheck(42);
    }

    [Fact]
    public async Task CompilerDriverCanLinkThroughInstalledGlibcScripts()
    {
        if (!File.Exists("/usr/lib/x86_64-linux-gnu/libc.so") ||
            !File.Exists("/usr/lib/x86_64-linux-gnu/libm.so"))
            throw Xunit.Sdk.SkipException.ForSkip("This smoke test requires x64 multiarch glibc development libraries.");
        using var builder = new CompilationBuilder(true);
        string source = Path.Combine(builder._tempDir, "main.c");
        File.WriteAllText(source, """
            int getpid(void);
            double cos(double);
            int main(void) { return getpid() > 0 && cos(0) == 1 ? 42 : 1; }
            """);
        var result = await ChilinkCommandLineTests.RunTool(builder, "Chibil",
            ["--target=x86_64-dotnet-linux", source, "-lc", "-lm", "-o", "output.dll", "-Wl,-e,main"]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }
}
