using System.Diagnostics;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkTests : ChibiTestBase
{
    private static readonly string[] ConsoleMain = ["/entry:main", "/subsystem:console"];

    [Fact]
    public void ExplicitEntryPointProducesRunnableImage()
    {
        Compile("int main(void) { return 42; }")
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectCallsAcrossTranslationUnitsWorkInBothOrders(bool reverse)
    {
        const string main = """
            int add(int, int);
            int main(void) { return add(30, 12); }
            """;
        const string helper = "int add(int left, int right) { return left + right; }";
        Compile(reverse ? helper : main)
            .Compile(reverse ? main : helper)
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void RecursiveCallsResolveToTheFinalMethod()
    {
        Compile("""
            int factorial(int value) {
                return value <= 1 ? 1 : value * factorial(value - 1);
            }
            int main(void) { return factorial(5); }
            """)
            .Link(ConsoleMain)
            .RunAndCheck(120);
    }

    [Fact]
    public void StringLiteralPreservesCharactersIncludingEmbeddedNull()
    {
        Compile("""
            int read(char *text) {
                if (text[1] != 0) return 1;
                return text[0] + text[2] + text[3];
            }
            int main(void) { return read("A\0*\0"); }
            """)
            .Link(ConsoleMain)
            .RunAndCheck(107);
    }

    [Theory]
    [InlineData("unsigned char", 1)]
    [InlineData("unsigned short", 2)]
    [InlineData("unsigned int", 4)]
    [InlineData("unsigned long long", 8)]
    public void ReadOnlyConstantsPreserveAlignmentUpToEightBytes(string type, int alignment)
    {
        // Without data sections, chibil places const C globals in writable merged storage.
        Compile($$"""
            const unsigned char prefix = 7;
            const {{type}} aligned = 35;
            int main(void) {
                if ((unsigned long long)&aligned % {{alignment}} != 0) return 1;
                return prefix + aligned;
            }
            """, ["-fdata-sections"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void StaticReadOnlyComdatCanBeReadThroughAPointer()
    {
        Compile("""
            static const int answer = 42;
            int read(const int *p) { return *p; }
            int main(void) { return read(&answer); }
            """, ["-fdata-sections"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticReadOnlyComdatsWithTheSameNameRemainDistinct(bool reverse)
    {
        const string main = """
            static const int answer = 13;
            const int *other_address(void);
            int read(const int *p) { return *p; }
            int main(void) {
                const int *other = other_address();
                if (&answer == other) return 1;
                return read(&answer) + read(other);
            }
            """;
        const string other = """
            static const int answer = 29;
            const int *other_address(void) { return &answer; }
            """;
        Compile(reverse ? other : main, ["-fdata-sections"])
            .Compile(reverse ? main : other, ["-fdata-sections"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ReadOnlyConstantsPreserveExplicitOverAlignment(int alignment)
    {
        Compile($$"""
            _Alignas({{alignment}}) const unsigned long long aligned = 42;
            int main(void) {
                if (((unsigned long long)&aligned & {{alignment - 1}}) != 0) return 1;
                return aligned;
            }
            """, ["-fdata-sections"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData("char", "\"hi\"", 1)]
    [InlineData("unsigned short", "u\"hi\"", 2)]
    [InlineData("unsigned int", "U\"hi\"", 4)]
    public void StringLiteralsPreserveElementAlignmentAndTerminator(
        string type, string literal, int alignment)
    {
        Compile($$"""
            int main(void) {
                char *prefix = "x";
                {{type}} *text = {{literal}};
                if ((unsigned long long)text % {{alignment}} != 0) return 1;
                return prefix[0] == 'x' && text[0] == 'h' &&
                    text[1] == 'i' && text[2] == 0 ? 42 : 2;
            }
            """)
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(true, "/OPT:REF", false)]
    [InlineData(true, "/OPT:NOREF", true)]
    [InlineData(false, "/OPT:REF", true)]
    [InlineData(true, "", false)]
    [InlineData(true, "/debug", true)]
    [InlineData(true, "/debug /OPT:REF", false)]
    [InlineData(true, "/OPT:REF /debug", false)]
    [InlineData(true, "/debug /OPT:NOREF", true)]
    [InlineData(true, "/OPT:NOREF /debug", true)]
    [InlineData(true, "/debug /OPT:NOICF", true)]
    public void DeadStrippingRespectsFunctionSectionGranularity(
        bool functionSections, string options, bool keepsUnused)
    {
        Compile("""
            int probe_methods(void);
            int frag_unused(void) { return 99; }
            int helper(int value) { return value + 1; }
            int main(void) { return helper(41) + probe_methods(); }
            """, functionSections ? ["-ffunction-sections"] : null)
            .AddAsm2ObjAssembly("ReflectionProbe.dll")
            .Link(["/entry:main", .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries)])
            .RunAndCheck(keepsUnused ? 43 : 42);
    }

    [Fact]
    public void UnresolvedManagedExternFailsEvenInDeadComdat()
    {
        var builder = Compile("""
            int missing(void);
            int unused(void) { return missing(); }
            int main(void) { return 42; }
            """, ["-ffunction-sections"]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/entry:main", "/OPT:REF"]));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DllImpliesNoEntryAndOnlyExplicitManagedRootIsRetained(bool include)
    {
        using var builder = new CompilationBuilder();
        var library = Path.Combine(builder._tempDir, "library.dll");
        builder.Compile("""
            int helper(void) { return 42; }
            int unused(void) { return 99; }
            """, ["-ffunction-sections"]);
        builder.Link(include
            ? ["/dll", "/out:" + library, "/OPT:REF", "/include:?helper@@$$J0YAHXZ"]
            : ["/dll", "/out:" + library, "/OPT:REF"]);

        Compile($$"""
            int managed_dll_helper(char *path);
            int main(void) {
                return managed_dll_helper("{{library.Replace("\\", "\\\\")}}");
            }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck(include ? 42 : 0);
    }

    [Fact]
    public void NestedManagedAggregateFieldsWorkWithFunctionSections()
    {
        Compile("""
            struct Outer { int prefix; struct { int value; } inner; int suffix; };
            int update(struct Outer *value) { return value->inner.value + value->suffix; }
            int main(void) {
                struct Outer value;
                value.prefix = 1;
                value.inner.value = 40;
                value.suffix = 2;
                return update(&value);
            }
            """, ["-fmanaged-aggregate-fields", "-ffunction-sections"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void PackedManagedAggregatePreservesFieldOffsetsAndSize()
    {
        Compile("""
            struct __attribute__((packed)) Packed { char tag; int value; };
            int main(void) {
                struct Packed value;
                value.tag = 2;
                value.value = 40;
                if (sizeof(struct Packed) != 5) return 1;
                if ((char *)&value.value - (char *)&value != 1) return 2;
                return value.tag + value.value;
            }
            """, ["-fmanaged-aggregate-fields"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void UnionManagedAggregatePreservesOverlappingStorage()
    {
        Compile("""
            union Number { int value; unsigned char bytes[4]; };
            int main(void) {
                union Number number;
                number.value = 0x0102032a;
                if (sizeof(union Number) != 4) return 1;
                return number.bytes[0];
            }
            """, ["-fmanaged-aggregate-fields"])
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void Asm2ObjUserStringsWorkAlongsideCStrings()
    {
        Compile("""
            int managed_string(void);
            int main(void) {
                char *text = "C string";
                if (text[0] != 'C' || text[7] != 'g' || text[8] != 0) return 1;
                return managed_string();
            }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck("chilink user string".Length + 'c');
    }

    [Fact]
    public void Asm2ObjOrdinaryStaticFieldAndInitializerRun()
    {
        Compile("""
            int managed_static(void);
            int main(void) { return managed_static(); }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck(41);
    }

    [Fact]
    public void Asm2ObjGenericMethodAndConstructedGenericTypeRun()
    {
        Compile("""
            int managed_generic(void);
            int main(void) { return managed_generic(); }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void Asm2ObjCatchAndFinallyPreserveBothPaths()
    {
        Compile("""
            int managed_eh(int);
            int main(void) { return managed_eh(1) + managed_eh(-1); }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck(74);
    }

    [Fact]
    public void DefaultCrtStartupHandlesArgvPinnedLocalsAndFinally()
    {
        Compile("""
            int main(int argc, char **argv, char **envp) {
                if (argc != 1 || argv == 0 || argv[0] == 0 || argv[0][0] == 0)
                    return 1;
                return 42;
            }
            """)
            .AddCrt()
            .Link(["/subsystem:console"])
            .RunAndCheck(42);
    }

    [Fact]
    public void MsvcManagedInputRunsWithoutNativeLibraries()
    {
        MsvcCompile("int main(void) { return 42; }")
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedMsvcAndChibilDirectCallsWorkInBothDirections(bool msvcDefines)
    {
        const string helper = "int multiply(int a, int b) { return a * b; }";
        const string main = """
            int multiply(int, int);
            int main(void) { return multiply(6, 7); }
            """;
        var builder = msvcDefines
            ? MsvcCompile(helper).Compile(main)
            : Compile(helper).MsvcCompile(main);
        builder.Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void DllRetainsUncalledPublicManagedTypeAndItsBody()
    {
        using var builder = new CompilationBuilder();
        var library = Path.Combine(builder._tempDir, "library.dll");
        builder.AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(["/dll", "/out:" + library, "/OPT:REF"]);

        Compile($$"""
            int managed_dll_public_root(char *path);
            int main(void) {
                return managed_dll_public_root("{{library.Replace("\\", "\\\\")}}");
            }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain)
            .RunAndCheck(42);
    }

    [Fact]
    public void UnsupportedNativeFunctionPointerFails()
    {
        var builder = Compile("""
            int target(int value) { return value + 1; }
            int main(void) { int (*function)(int) = target; return function(41); }
            """, ["-O0"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
    }

    [Fact]
    public void InvalidEntryPointFails()
    {
        var builder = Compile("int main(int argc, char **argv) { return argc; }");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "no input")]
    [InlineData("/debug:bogus", "debug")]
    [InlineData("/machine:arm64", "x64")]
    [InlineData("/entry:", "entry")]
    public void DriverRejectsMissingInputAndUnsupportedOrIncompleteOptions(
        string option, string diagnostic)
    {
        var error = Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(option == null ? [] : [option]));
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/?")]
    [InlineData("-?")]
    public void DriverHelpAliasesDoNotRequireInputs(string option)
    {
        new Chilink.Driver().Run([option]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerDriverLaunchesChilinkFromChildPathWithSpacedPaths(bool fixedAddresses)
    {
        using var workspace = new CompilationBuilder();
        string compiler = Path.Combine(
            Path.GetDirectoryName(typeof(Chibil.Driver).Assembly.Location), "Chibil.exe");
        string linkerDirectory = Path.GetDirectoryName(typeof(Chilink.Driver).Assembly.Location);
        Assert.True(File.Exists(compiler), $"Compiler apphost not copied to test output: {compiler}");
        Assert.True(File.Exists(Path.Combine(linkerDirectory, "chilink.exe")),
            $"Linker apphost not copied to test output: {linkerDirectory}");

        string source = Path.Combine(workspace._tempDir, "source with spaces.c");
        string output = Path.Combine(workspace._tempDir, "program with spaces.exe");
        File.WriteAllText(source, "int value = 42; int main(void) { return value; }");
        var start = new ProcessStartInfo(compiler)
        {
            WorkingDirectory = workspace._tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = linkerDirectory + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["TEMP"] = workspace._tempDir;
        start.Environment["TMP"] = workspace._tempDir;
        foreach (var argument in new[] { "-fuse-ld=chilink", source, "-o", output, "-Wl,/entry:main", "-###" })
            start.ArgumentList.Add(argument);
        if (fixedAddresses)
            start.ArgumentList.Add("-ffixed-address-statics");

        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        string diagnostics = $"stdout:\n{await stdout}\nstderr:\n{await stderr}";
        if (!fixedAddresses)
        {
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("FixedAddressValueTypeAttribute", diagnostics);
            Assert.False(File.Exists(output));
            return;
        }
        Assert.True(process.ExitCode == 0, $"Compiler exited with {process.ExitCode}.\n{diagnostics}");
        Assert.Contains("chilink", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mscoree.lib", diagnostics, StringComparison.OrdinalIgnoreCase);

        new LinkResult(output).RunAndCheck(42);
    }

    [Fact]
    public void DriverRejectsDllWithExplicitEntryPoint()
    {
        var builder = Compile("int main(void) { return 42; }");
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/dll", "/entry:main"]));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriverResponseFilePreservesQuotedPathsAndMixedCaseOptions()
    {
        var builder = Compile("int main(void) { return 42; }");
        var directory = Path.Combine(builder._tempDir, "directory with spaces");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input with spaces.obj");
        var output = Path.Combine(directory, "output with spaces.exe");
        var response = Path.Combine(directory, "arguments with spaces.rsp");
        File.Copy(builder._objFiles.Single(), input);
        File.WriteAllText(response, $"""
            -NoLoGo /MaChInE:x64 /EnTrY:main
            /OuT:"{output}"
            "{input}"
            """);

        new Chilink.Driver().Run(["@" + response]);

        new LinkResult(output).RunAndCheck(42);
    }

    [Fact]
    public void DriverRejectsResponseFileCycles()
    {
        using var builder = new CompilationBuilder();
        var first = Path.Combine(builder._tempDir, "first.rsp");
        var second = Path.Combine(builder._tempDir, "second.rsp");
        File.WriteAllText(first, $"@\"{second}\"");
        File.WriteAllText(second, $"@\"{first}\"");

        var error = Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["@" + first]));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DriverDefaultLibrarySuppressionIsOrderIndependent(
        bool namedSuppression, bool suppressionFirst)
    {
        var builder = Compile("int main(void) { return 42; }");
        var output = Path.Combine(builder._tempDir, "output.exe");
        const string library = "/defaultlib:chilink-nonexistent.lib";
        string suppression = namedSuppression
            ? "/nodefaultlib:CHILINK-NONEXISTENT.LIB" : "/nodefaultlib";

        new Chilink.Driver().Run([
            "/out:" + output, "/entry:main",
            suppressionFirst ? suppression : library,
            builder._objFiles.Single(),
            suppressionFirst ? library : suppression,
        ]);

        new LinkResult(output).RunAndCheck(42);
    }

    [Fact]
    public void DriverRejectsUnsuppressedDefaultLibrary()
    {
        var builder = Compile("int main(void) { return 42; }");
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/entry:main", "/defaultlib:chilink-nonexistent.lib"]));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("chilink-nonexistent.lib", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
