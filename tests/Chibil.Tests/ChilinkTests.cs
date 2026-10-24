using System.Diagnostics;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkTests : ChibiTestBase
{
    [Fact]
    public void ExplicitEntryPointProducesRunnableImage()
    {
        Compile("int main(void) { return 42; }")
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
            .RunAndCheck(42);
    }

    [Theory]
    [InlineData(true, "--gc-sections", false)]
    [InlineData(true, "--no-gc-sections", true)]
    [InlineData(false, "--gc-sections", true)]
    [InlineData(true, "", true)]
    [InlineData(true, "-s", true)]
    [InlineData(true, "-s --gc-sections", false)]
    [InlineData(true, "--gc-sections -s", false)]
    [InlineData(true, "-s --no-gc-sections", true)]
    [InlineData(true, "--no-gc-sections -s", true)]
    public void DeadStrippingRespectsFunctionSectionGranularity(
        bool functionSections, string options, bool keepsUnused)
    {
        var builder = Compile("""
            int probe_methods(void);
            int frag_unused(void) { return 99; }
            int helper(int value) { return value + 1; }
            int main(void) { return helper(41) + probe_methods(); }
            """, functionSections ? ["-ffunction-sections"] : null)
            .AddAsm2ObjAssembly("ReflectionProbe.dll");
        builder
            .Chilink(options.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
            builder.Link(gcSections: true));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedLibraryHasNoEntryAndOnlyExplicitManagedRootIsRetained(bool include)
    {
        using var builder = new CompilationBuilder();
        var library = Path.Combine(builder._tempDir, "library.dll");
        builder.Compile("""
            int helper(void) { return 42; }
            int unused(void) { return 99; }
            """, ["-ffunction-sections"]);
        builder.Chilink(include
            ? ["--gc-sections", "--undefined=?helper@@$$J0YAHXZ"]
            : ["--gc-sections"], shared: true, outputPath: library);

        Compile($$"""
            int managed_dll_helper(char *path);
            int main(void) {
                return managed_dll_helper("{{library.Replace("\\", "\\\\")}}");
            }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link()
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
            .Link(entryPoint: "mainCRTStartup")
            .RunAndCheck(42);
    }

    [Fact]
    public void MsvcManagedInputRunsWithoutNativeLibraries()
    {
        MsvcCompile("int main(void) { return 42; }")
            .Link()
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
        builder.Link().RunAndCheck(42);
    }

    [Fact]
    public void DllRetainsUncalledPublicManagedTypeAndItsBody()
    {
        using var builder = new CompilationBuilder();
        var library = Path.Combine(builder._tempDir, "library.dll");
        builder.AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(shared: true, outputPath: library, gcSections: true);

        Compile($$"""
            int managed_dll_public_root(char *path);
            int main(void) {
                return managed_dll_public_root("{{library.Replace("\\", "\\\\")}}");
            }
            """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link()
            .RunAndCheck(42);
    }

    [Fact]
    public void UnsupportedNativeFunctionPointerFails()
    {
        var builder = Compile("""
            int target(int value) { return value + 1; }
            int main(void) { int (*function)(int) = target; return function(41); }
            """, ["-O0", "-fno-uco-entrypoints"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link());
        Assert.IsType<Chilink.LinkException>(error.InnerException);
    }

    [Fact]
    public void InvalidEntryPointFails()
    {
        var builder = Compile("int main(int argc, char **argv) { return argc; }");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link());
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains("entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "no input")]
    [InlineData("--debug=bogus", "debug")]
    [InlineData("--entry=", "entry")]
    public void DriverRejectsMissingInputAndUnsupportedOrIncompleteOptions(
        string option, string diagnostic)
    {
        var error = Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(option == null ? [] : [option]));
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriverHelpDoesNotRequireInputs()
    {
        new Chilink.Driver().Run(["--help"]);
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public async Task CompilerDriverLaunchesChilinkFromChildPathWithSpacedPaths(
        bool fixedAddresses, bool explicitOutput, bool linkerPath, bool targetPreset)
    {
        using var workspace = new CompilationBuilder();
        string compiler = Path.Combine(
            Path.GetDirectoryName(typeof(Chibil.Driver).Assembly.Location), "Chibil");
        string linkerDirectory = Path.GetDirectoryName(typeof(Chilink.Driver).Assembly.Location);

        string source = Path.Combine(workspace._tempDir, "source with spaces.c");
        string output = Path.Combine(workspace._tempDir,
            (explicitOutput ? "program with spaces" : "a") + Path.GetExtension(CompilationBuilder.ExecutableName));
        File.WriteAllText(source, "int value = 42; int main(void) { int *pointer = &value; return *pointer; }");
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
        if (!CompilationBuilder.DefaultNetCore)
            start.ArgumentList.Add("--target=x86_64-netfx-windows");
        string selection = targetPreset
            ? "--target=x86_64-netfx-windows-chibil"
            : "-fuse-ld=" + (linkerPath ? Path.Combine(linkerDirectory, "chilink") : "chilink");
        foreach (var argument in new[] { selection, source, "-###" })
            start.ArgumentList.Add(argument);
        if (linkerPath)
        {
            start.ArgumentList.Add("-Xlinker");
            start.ArgumentList.Add("--entry=main");
        }
        else
            start.ArgumentList.Add("-Wl,-e,main");
        if (explicitOutput)
        {
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(output);
        }
        start.ArgumentList.Add(fixedAddresses ? "-ffixed-address-statics" : "-fno-fixed-address-statics");
        if (CompilationBuilder.DefaultNetCore)
        {
            start.ArgumentList.Add("-fnetcore");
            start.ArgumentList.Add("-fuco-entrypoints");
        }

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
        Assert.DoesNotContain("mscoree.lib", diagnostics, StringComparison.OrdinalIgnoreCase);

        new LinkResult(output).RunAndCheck(42);
    }

    [Fact]
    public void DriverRejectsSharedLibraryWithExplicitEntryPoint()
    {
        var builder = Compile("int main(void) { return 42; }");
        var error = Assert.Throws<Chilink.LinkException>(() =>
            new Chilink.Driver().Run(["-shared", "-emain", .. builder._objFiles]));
        Assert.Contains("entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriverResponseFilePreservesQuotedPaths()
    {
        var builder = Compile("int main(void) { return 42; }");
        var directory = Path.Combine(builder._tempDir, "directory with spaces");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input with spaces.obj");
        var output = Path.Combine(directory, "output with spaces" + Path.GetExtension(CompilationBuilder.ExecutableName));
        var response = Path.Combine(directory, "arguments with spaces.rsp");
        File.Copy(builder._objFiles.Single(), input);
        File.WriteAllText(response, $"""
            --entry=main
            --output="{output}"
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
}
