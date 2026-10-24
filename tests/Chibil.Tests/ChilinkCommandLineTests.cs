using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkCommandLineTests : ChibiTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OutputAndEntryOptionFormsProduceRunnableImage(int form)
    {
        var builder = Compile("int start(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, "program with spaces.exe");
        string[] options = form switch
        {
            0 => ["-o", output, "-e", "start"],
            1 => ["-o" + output, "-estart"],
            2 => ["--output", output, "--entry", "start", "--subsystem", "console"],
            _ => ["--output=" + output, "--entry=start", "--subsystem=console"],
        };

        new Chilink.Driver().Run([.. options, .. builder._objFiles]);

        new LinkResult(output).RunAndCheck(42);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("-s", true)]
    [InlineData("--gc-sections", false)]
    [InlineData("--gc-sections -s", false)]
    [InlineData("-s --gc-sections", false)]
    [InlineData("--gc-sections --no-gc-sections", true)]
    [InlineData("--no-gc-sections --gc-sections", false)]
    public void SectionGcIsOptInAndIndependentOfDebug(string options, bool retainsUnused)
    {
        var builder = Compile("""
            int probe_methods(void);
            int frag_unused(void) { return 99; }
            int main(void) { return 42 + probe_methods(); }
            """, ["-ffunction-sections"])
            .AddAsm2ObjAssembly("ReflectionProbe.dll");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);

        new Chilink.Driver().Run([
            "-o", output, "-e", "main",
            .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            .. builder._objFiles,
        ]);

        new LinkResult(output).RunAndCheck(retainsUnused ? 43 : 42);
        Assert.False(File.Exists(Path.ChangeExtension(output, ".pdb")));
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("--output=")]
    [InlineData("-e")]
    [InlineData("--entry=")]
    [InlineData("-L")]
    [InlineData("-l")]
    [InlineData("-u")]
    [InlineData("--pdb")]
    [InlineData("--debug=bogus")]
    [InlineData("--GC-SECTIONS")]
    [InlineData("-entry:main")]
    [InlineData("/entry:main")]
    public void InvalidOptionsFailWithoutWritingOutput(string option)
    {
        using var builder = new CompilationBuilder();
        string output = Path.Combine(builder._tempDir, "output.exe");

        Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["-o", output, option]));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("--e=main")]
    [InlineData("--u=main")]
    [InlineData("--L=.")]
    public void LongOptionsDoNotAcceptShortOptionNames(string option)
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);

        Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["-o", output, "-emain", option, .. builder._objFiles]));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SlashPrefixedInputIsNotParsedAsAnOption()
    {
        var error = Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["/missing-chilink-input.obj"]));

        Assert.DoesNotContain("option", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing-chilink-input.obj", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultOutputNameAndOptionTerminatorWork(bool defaultEntry)
    {
        var builder = Compile($"int {(defaultEntry ? "mainCRTStartup" : "main")}(void) {{ return 42; }}");
        File.Move(builder._objFiles.Single(), Path.Combine(builder._tempDir, "-input.obj"));

        var result = await RunDriver(builder, defaultEntry
            ? ["--", "-input.obj"] : ["-emain", "--", "-input.obj"]);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "a.exe")).RunAndCheck(42);
    }

    [Fact]
    public void ObjectDirectivesHaveNoEffect()
    {
        using var builder = new CompilationBuilder(false);
        builder.MsvcCompile("""
            #pragma comment(linker, "/include:missing_directive_root")
            #pragma comment(lib, "missing_directive_library.lib")
            int main(void) { return 42; }
            """);
        string output = Path.Combine(builder._tempDir, "output.exe");

        new Chilink.Driver().Run(["-o", output, "-emain", .. builder._objFiles]);

        new LinkResult(output, false).RunAndCheck(42);
    }

    [Theory]
    [InlineData("libanswer.dll.a", 0)]
    [InlineData("answer.dll.a", 1)]
    [InlineData("libanswer.a", 2)]
    [InlineData("answer.lib", 3)]
    [InlineData("libanswer.lib", 4)]
    public void LibraryNamesAndOptionFormsFindSupportedArchives(string filename, int form)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target");
        string directory = Path.Combine(builder._tempDir, "libraries with spaces");
        Directory.CreateDirectory(directory);
        File.Move(builder._objFiles[1], Path.Combine(directory, filename));
        string[] options = form switch
        {
            0 => ["-lanswer", "-L" + directory],
            1 => ["-l", "answer", "-L", directory],
            2 => ["--library", "answer", "--library-path", directory],
            3 => ["--library=answer", "--library-path=" + directory],
            _ => ["-l:" + filename, "-L", directory],
        };
        string output = Path.Combine(builder._tempDir, "output.dll");

        new Chilink.Driver().Run(["-o", output, "-emain", .. options, builder._objFiles[0]]);

        new LinkResult(output, true).RunAndCheck(42);
    }

    [Theory]
    [InlineData("-lanswer", 42)]
    [InlineData("-l:answer.lib", 17)]
    [InlineData("--library=:answer.lib", 17)]
    public void LibrarySearchUsesDirectoryThenFilenamePrecedence(string library, int expected)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target", "winning.dll", "winning.lib")
            .NativeLibrary("int target(void) { return 17; }", "target", "losing.dll", "losing.lib");
        string first = Path.Combine(builder._tempDir, "first");
        string second = Path.Combine(builder._tempDir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.Copy(builder._objFiles[1], Path.Combine(first, "libanswer.a"));
        File.Copy(builder._objFiles[2], Path.Combine(first, "answer.lib"));
        File.Copy(builder._objFiles[2], Path.Combine(second, "libanswer.dll.a"));
        string output = Path.Combine(builder._tempDir, "output.dll");

        new Chilink.Driver().Run([
            "-o", output, "-emain", library, "-L", first, "-L", second, builder._objFiles[0],
        ]);

        new LinkResult(output, true).RunAndCheck(expected);
    }

    [Fact]
    public async Task LibrarySearchRequiresExplicitDirectoryEvenWithLibEnvironment()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target");
        string[] args = ["-o", "output.dll", "-emain", builder._objFiles[0], "-lnative"];

        var missing = await RunDriver(builder, args, builder._tempDir);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("native", missing.Diagnostics);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));

        var found = await RunDriver(builder, [.. args, "-L."], builder._tempDir);
        Assert.True(found.ExitCode == 0, found.Diagnostics);
        new LinkResult(Path.Combine(builder._tempDir, "output.dll"), true).RunAndCheck(42);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SharedLibrariesRetainDefaultOrExplicitRoots(int form)
    {
        using var library = new CompilationBuilder();
        library.Compile("int helper(void) { return 42; }", ["-ffunction-sections"]);
        string output = Path.Combine(library._tempDir, "library.dll");
        const string symbol = "?helper@@$$J0YAHXZ";
        string[] roots = form switch
        {
            -1 => [],
            0 => ["--gc-sections", "-u", symbol],
            1 => ["--gc-sections", "-u" + symbol],
            2 => ["--gc-sections", "--undefined", symbol],
            _ => ["--gc-sections", "--undefined=" + symbol],
        };
        new Chilink.Driver().Run(["--shared", "-o", output, .. roots, .. library._objFiles]);

        Compile($$"""
            int managed_dll_helper(char *path);
            int main(void) { return managed_dll_helper("{{output.Replace("\\", "\\\\")}}"); }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link()
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-s")]
    [InlineData("-S")]
    [InlineData("--strip-all")]
    [InlineData("--strip-debug")]
    public void PdbIsEmbeddedByDefaultUnlessStripped(string strip)
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);
        string[] options = strip == "" ? [] : [strip];

        new Chilink.Driver().Run(["-o", output, "-emain", .. options, .. builder._objFiles]);

        new LinkResult(output).RunAndCheck(42);
        using var image = new PEReader(File.OpenRead(output));
        if (strip == "")
            Assert.Contains(image.ReadDebugDirectory(),
                entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        else
            Assert.Empty(image.ReadDebugDirectory());
        Assert.False(File.Exists(Path.ChangeExtension(output, ".pdb")));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("-s", false)]
    [InlineData("-s", true)]
    [InlineData("-S", false)]
    [InlineData("-S", true)]
    [InlineData("--strip-all", false)]
    [InlineData("--strip-all", true)]
    [InlineData("--strip-debug", false)]
    [InlineData("--strip-debug", true)]
    public void ExplicitPdbIsExternalEvenWhenStripping(string strip, bool pdbFirst)
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);
        string pdb = Path.Combine(builder._tempDir, "custom symbols.pdb");
        string[] stripping = strip == "" ? [] : [strip];
        string[] options = pdbFirst ? ["--pdb=" + pdb, .. stripping] : [.. stripping, "--pdb", pdb];

        new Chilink.Driver().Run(["-o", output, "-emain", .. options, .. builder._objFiles]);

        new LinkResult(output).RunAndCheck(42);
        Assert.True(File.Exists(pdb));
        Assert.False(File.Exists(Path.ChangeExtension(output, ".pdb")));
        using var image = new PEReader(File.OpenRead(output));
        Assert.DoesNotContain(image.ReadDebugDirectory(),
            entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        var codeView = Assert.Single(image.ReadDebugDirectory(),
            entry => entry.Type == DebugDirectoryEntryType.CodeView);
        Assert.Equal(pdb, image.ReadCodeViewDebugDirectoryData(codeView).Path);
    }

    [Fact]
    public void EmptyPdbOptionUsesDefaultExternalFilename()
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);

        new Chilink.Driver().Run(["-o", output, "-emain", "--pdb=", .. builder._objFiles]);

        new LinkResult(output).RunAndCheck(42);
        Assert.True(File.Exists(Path.ChangeExtension(output, ".pdb")));
        using var image = new PEReader(File.OpenRead(output));
        Assert.DoesNotContain(image.ReadDebugDirectory(),
            entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    }

    [Theory]
    [InlineData("--debug")]
    [InlineData("--debug=embedded")]
    public void LegacyDebugOptionsAreRejected(string option)
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);

        Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["-o", output, "-emain", option, .. builder._objFiles]));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalPdbCannotOverwriteInputOrImage(bool input)
    {
        var builder = Compile("int main(void) { return 42; }");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);

        Assert.Throws<Chilink.LinkException>(() => new Chilink.Driver().Run([
            "-o", output, "-emain", "--pdb=" + (input ? builder._objFiles[0] : output),
            .. builder._objFiles,
        ]));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-s")]
    [InlineData("-Wl,-S")]
    [InlineData("external")]
    public async Task CompilerUsesLinkerPdbDefaultsAndForwardsOverrides(string mode)
    {
        using var builder = new CompilationBuilder();
        string source = Path.Combine(builder._tempDir, "main.c");
        string output = Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName);
        string pdb = Path.Combine(builder._tempDir, "external symbols.pdb");
        File.WriteAllText(source, "int main(void) { return 42; }");
        string[] options = mode switch
        {
            "" => [],
            "external" => ["-Wl,--pdb=" + pdb],
            _ => [mode],
        };

        var result = await RunTool(builder, "Chibil", [
            CompilationBuilder.DefaultNetCore ? "--target=x86_64-dotnet-windows" :
                "--target=x86_64-netfx-windows-chibil",
            source, "-o", output, "-Wl,-e,main", "-###", .. options,
        ]);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.DoesNotContain("--debug", result.Diagnostics);
        new LinkResult(output).RunAndCheck(42);
        Assert.Equal(mode == "external", File.Exists(pdb));
        using var image = new PEReader(File.OpenRead(output));
        Assert.Equal(mode == "", image.ReadDebugDirectory()
            .Any(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb));
        if (mode is "-s" or "-Wl,-S")
            Assert.Empty(image.ReadDebugDirectory());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompilerLibraryOptionsWorkWithBothLinkers(bool msvc, bool separated)
    {
        using var builder = new CompilationBuilder(false);
        builder.NativeLibrary("int target(void) { return 42; }", "target");
        string source = Path.Combine(builder._tempDir, "source with spaces.c");
        string output = Path.Combine(builder._tempDir, "program with spaces.exe");
        File.WriteAllText(source, "int target(void); int main(void) { return target(); }");
        string[] library = separated
            ? ["-L", builder._tempDir, "-l", "native"] : ["-L" + builder._tempDir, "-lnative"];

        var result = await RunTool(builder, "Chibil", [
            "--target=x86_64-netfx-windows",
            msvc ? "-fuse-ld=LINK.EXE" : "--target=x86_64-netfx-windows-chibil",
            source, "-o", output, .. library, msvc ? "-Wl,/entry:main" : "-Wl,-e,main", "-###",
        ]);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Equal(msvc, result.Diagnostics.Contains("mscoree.lib", StringComparison.OrdinalIgnoreCase));
        new LinkResult(output, false).RunAndCheck(42);
    }

    [Fact]
    public async Task CompilerSharedOptionProducesLoadableManagedLibrary()
    {
        using var builder = new CompilationBuilder();
        string source = Path.Combine(builder._tempDir, "helper.c");
        string output = Path.Combine(builder._tempDir, "library.dll");
        File.WriteAllText(source, "int helper(void) { return 42; }");
        var result = await RunTool(builder, "Chibil", [
            CompilationBuilder.DefaultNetCore ? "--target=x86_64-dotnet-windows" :
                "--target=x86_64-netfx-windows-chibil",
            source, "-shared", "-o", output, "-Wl,--gc-sections,--undefined=?helper@@$$J0YAHXZ",
        ]);
        Assert.True(result.ExitCode == 0, result.Diagnostics);

        Compile($$"""
            int managed_dll_helper(char *path);
            int main(void) { return managed_dll_helper("{{output.Replace("\\", "\\\\")}}"); }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link()
            .RunAndCheck(42);
    }

    internal static Task<(int ExitCode, string Diagnostics)> RunDriver(
        CompilationBuilder builder, string[] args, string libraryEnvironment = null)
        => RunTool(builder, "chilink", args, libraryEnvironment);

    private static async Task<(int ExitCode, string Diagnostics)> RunTool(
        CompilationBuilder builder, string tool, string[] args, string libraryEnvironment = null)
    {
        string tools = Path.GetDirectoryName(typeof(Chilink.Driver).Assembly.Location);
        var start = new ProcessStartInfo(Path.Combine(tools, tool))
        {
            WorkingDirectory = builder._tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = tools + Path.PathSeparator + start.Environment["PATH"];
        if (libraryEnvironment != null)
            start.Environment["LIB"] = libraryEnvironment;
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, $"{await stdout}\n{await stderr}");
    }
}
