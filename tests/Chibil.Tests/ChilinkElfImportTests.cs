using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkElfImportTests
{
    public ChilinkElfImportTests()
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("ELF import execution requires Linux.");
    }

    [Fact]
    public void StrippedLibrariesPreserveSonameAndFunctionExports()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int add(int, int);
            int read_text(const char *);
            int main(void) { return add(20, 20) + read_text("xx"); }
            """).NativeLinuxLibrary("""
            int data = 1;
            int add(int a, int b) { return a + b; }
            int read_text(const char *p) { return p[0] == 'x' && p[1] == 'x' ? 2 : 0; }
            """, "unrelated.so", "libactual.so.1", ["-Wl,-s"]);
        var result = builder.Link(gcSections: true);
        File.Delete(builder._objFiles[1]);
        result.RunAndCheck(42);
    }

    [Theory]
    [InlineData("libnative.so")]
    [InlineData("libnative.so.1.2")]
    public void PositionalInputsWithoutSonamePreserveTheirPath(string filename)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", filename, soname: null);
        string directory = Path.Combine(builder._tempDir, "outside output");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename);
        File.Move(builder._objFiles[1], path);
        builder._objFiles[1] = path;
        builder.Link().RunAndCheck(42);
    }

    [Fact]
    public async Task RelativePositionalInputWithoutSonameRemainsRelative()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", soname: null);
        string relative = Path.Combine("libraries with spaces", "libnative.so");
        Directory.CreateDirectory(Path.Combine(builder._tempDir, "libraries with spaces"));
        File.Move(builder._objFiles[1], Path.Combine(builder._tempDir, relative));
        var result = await ChilinkCommandLineTests.RunDriver(builder,
            ["-o", "output.dll", "-emain", builder._objFiles[0], "./" + relative]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        string runtime = Path.Combine(builder._tempDir, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "libraries with spaces"));
        File.Move(Path.Combine(builder._tempDir, relative), Path.Combine(runtime, relative));
        File.Move(Path.Combine(builder._tempDir, "output.dll"), Path.Combine(runtime, "output.dll"));
        new LinkResult(Path.Combine(runtime, "output.dll"), true).RunAndCheck(42, runtime);
    }

    [Theory]
    [InlineData("-lnative")]
    [InlineData("-l:libnative.so")]
    [InlineData("--library=:libnative.so")]
    public void SearchedInputsWithoutSonameUseFilenameRatherThanBuildPath(string option)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", soname: null);
        string library = builder._objFiles[1];
        string directory = Path.Combine(builder._tempDir, "build libraries");
        Directory.CreateDirectory(directory);
        File.Copy(library, Path.Combine(directory, "libnative.so"));
        builder._objFiles.RemoveAt(1);
        var result = builder.Chilink([option, "-L", directory]);
        Directory.Delete(directory, recursive: true);
        result.RunAndCheck(42);
    }

    [Theory]
    [InlineData("int data = 42;", "data")]
    [InlineData("__thread int data = 42;", "TLS")]
    public void SelectedDataImportsFail(string provider, string diagnostic)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int data; int main(void) { return data; }")
            .NativeLinuxLibrary(provider);
        Assert.Contains(diagnostic, Assert.Throws<InvalidOperationException>(() => builder.Link()).Message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Theory]
    [InlineData("__attribute__((visibility(\"hidden\"))) int target(void) { return 42; }")]
    [InlineData("int target(void); int other(void) { return target(); }")]
    [InlineData("static int target(void) { return 42; } int other(void) { return target(); }")]
    public void NonExportedAndUndefinedSymbolsCannotProvideImports(string provider)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary(provider);
        Assert.Contains("target", Assert.Throws<InvalidOperationException>(() => builder.Link()).Message);
    }

    [Theory]
    [InlineData("__attribute__((weak)) int target(void) { return 42; }")]
    [InlineData("""
        static int implementation(void) { return 42; }
        static void *resolve(void) { return implementation; }
        int target(void) __attribute__((ifunc("resolve")));
        """)]
    public void WeakAndIndirectFunctionsCanProvideImports(string provider)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary(provider).Link().RunAndCheck(42);
    }

    [Fact]
    public void ElfExportNamesDoNotAcquireCoffImportPointerAliases()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int target(void);
            int __imp_target(void);
            int main(void) { return target() + __imp_target(); }
            """).NativeLinuxLibrary("""
                int target(void) { return 17; }
                int __imp_target(void) { return 25; }
                """).Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyDefaultSymbolVersionsProvidePlainNameImports(bool defaultVersion)
    {
        using var builder = new CompilationBuilder(true);
        string versions = Path.Combine(builder._tempDir, "versions.map");
        File.WriteAllText(versions, "V1 {}; V2 {} V1;");
        string provider = """
            int old_target(void) { return 17; }
            __asm__(".symver old_target,target@V1");
            """;
        if (defaultVersion)
            provider += """

                int new_target(void) { return 42; }
                __asm__(".symver new_target,target@@V2");
                """;
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary(provider, extraArgs: ["-Wl,--version-script=" + versions]);
        if (defaultVersion)
            builder.Link().RunAndCheck(42);
        else
            Assert.Contains("target", Assert.Throws<InvalidOperationException>(() => builder.Link()).Message);
    }

    [Fact]
    public void LibrarySearchUsesDirectoryThenLinuxFilenamePrecedence()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "first.so", "libfirst.so.1")
            .NativeLinuxLibrary("int target(void) { return 17; }", "second.so", "libsecond.so.1");
        string first = Path.Combine(builder._tempDir, "first");
        string second = Path.Combine(builder._tempDir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.Move(builder._objFiles[1], Path.Combine(first, "libanswer.so"));
        File.Move(builder._objFiles[2], Path.Combine(second, "libanswer.so"));
        File.WriteAllText(Path.Combine(first, "libanswer.a"), "not an archive");
        builder._objFiles.RemoveRange(1, 2);
        builder.Chilink(["-lanswer", "-L", first, "-L", second]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedLinkerScriptCommandsFailInsteadOfFallingThrough(bool searched)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        string script = Path.Combine(builder._tempDir, "libscript.so");
        File.WriteAllText(script, "/* GNU ld script */\nSECTIONS { .text : { *(.text) } }");
        File.WriteAllText(Path.Combine(builder._tempDir, "libscript.a"), "not an archive");
        string[] args = searched ? ["-L", builder._tempDir, "-lscript"] : [script];
        Assert.Contains("linker script", Assert.Throws<InvalidOperationException>(() => builder.Chilink(args)).Message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void BuiltInDirectoriesFindVersionedGlibcWithoutExplicitPaths()
    {
        if (!File.Exists("/lib/x86_64-linux-gnu/libc.so.6"))
            throw Xunit.Sdk.SkipException.ForSkip("This smoke test requires x64 multiarch glibc.");
        using var builder = new CompilationBuilder(true);
        builder.Compile("int getpid(void); int main(void) { return getpid() > 0 ? 42 : 1; }")
            .Chilink(["-l:libc.so.6"]).RunAndCheck(42);
    }

    [Fact]
    public void ExplicitLibraryPathsPrecedeBuiltInDirectories()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int fixture(void); int main(void) { return fixture(); }")
            .NativeLinuxLibrary("int fixture(void) { return 42; }", "libc.so.6", "liboverride.so.1");
        builder._objFiles.RemoveAt(1);
        builder.Chilink(["-l:libc.so.6", "-L", builder._tempDir]).RunAndCheck(42);
    }

    [Fact]
    public async Task ResponseFilesSupportExactVersionedLibrariesAndQuotedDirectories()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLinuxLibrary("int target(void) { return 42; }", "libnative.so.1.2");
        string directory = Path.Combine(builder._tempDir, "libraries with spaces");
        Directory.CreateDirectory(directory);
        File.Move(builder._objFiles[1], Path.Combine(directory, "libnative.so.1.2"));
        string response = Path.Combine(builder._tempDir, "link.rsp");
        File.WriteAllText(response, $"""
            -e main -o output.dll "{builder._objFiles[0]}"
            --library=:libnative.so.1.2 --library-path="{directory}"
            """);
        var result = await ChilinkCommandLineTests.RunDriver(builder, ["@" + response]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }

    [Fact]
    public async Task CompilerDriverForwardsVersionedSharedLibraryInputs()
    {
        using var builder = new CompilationBuilder(true);
        builder.NativeLinuxLibrary("int target(void) { return 42; }", "libnative.so.1.2");
        string source = Path.Combine(builder._tempDir, "main.c");
        string output = Path.Combine(builder._tempDir, "output.dll");
        File.WriteAllText(source, "int target(void); int main(void) { return target(); }");
        var result = await ChilinkCommandLineTests.RunTool(builder, "Chibil",
            ["--target=x86_64-dotnet-linux", source, builder._objFiles[0], "-o", output, "-Wl,-e,main"]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(output, true).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedLibraryInputsCannotBeOverwritten(bool pdb)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }")
            .NativeLinuxLibrary("int target(void) { return 42; }");
        string path = builder._objFiles[1];
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => pdb
            ? builder.Chilink(["--pdb=" + path])
            : builder.Link(outputPath: path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
