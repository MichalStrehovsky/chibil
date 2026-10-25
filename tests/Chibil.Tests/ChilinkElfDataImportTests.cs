using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Coff;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkElfDataImportTests
{
    public ChilinkElfDataImportTests()
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("ELF data imports require Linux.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeStorageSupportsScalarAggregateAndManagedAccesses(bool fieldBacked)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter;
            extern int arr[4];
            struct S { int left; int right; };
            extern struct S s;
            int matches(int *);
            int read_counter(void);
            void set_counter(int);
            int managed_mutate_native(int *);
            int main(void) {
                if (counter != 5 || arr[3] || s.left != 1 || s.right != 2) return 1;
                if (!matches(&counter)) return 2;
                int assigned = (counter = 20);
                if (assigned != 20 || read_counter() != 20) return 3;
                counter += 2;
                if (counter++ != 22 || ++counter != 24) return 4;
                if (counter-- != 24 || --counter != 22) return 5;
                if (managed_mutate_native(&counter) != 22 || read_counter() != 23) return 6;
                set_counter(42);
                if (counter != 42) return 7;
                arr[1] = counter;
                arr[1] += 1;
                if (arr[1]++ != 43 || arr[1] != 44) return 8;
                struct S replacement = { 20, 22 };
                s = replacement;
                struct S copy = s;
                if (copy.left != 20 || copy.right != 22) return 9;
                s.left += s.right;
                return s.left;
            }
            """, [fieldBacked ? "-fmanaged-aggregate-fields" : "-fno-managed-aggregate-fields"])
            .NativeLinuxLibrary("""
                int counter = 5;
                int arr[4];
                struct S { int left; int right; } s = { 1, 2 };
                int matches(int *p) { return p == &counter; }
                int read_counter(void) { return counter; }
                void set_counter(int value) { counter = value; }
                """)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .AddCrt()
            .Link(gcSections: true).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataOnlyInitializersResolveImportsAndAddends(bool gcSections)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter;
            extern int arr[4];
            int *p = &counter;
            int *q = &arr[1];
            int main(void) { *p += 1; return *p + *q; }
            """, ["-fdata-sections"])
            .NativeLinuxLibrary("int counter = 5; int arr[4] = { 0, 36, 0, 0 };")
            .AddCrt().Link(gcSections: gcSections).RunAndCheck(42);
    }

    [Fact]
    public void CodeAndInitializersAcrossTranslationUnitsShareNativeStorage()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter;
            int *pointer = &counter;
            int update(void);
            int main(void) {
                counter = 40;
                if (update() != 41 || *pointer != 41) return 1;
                return ++*pointer;
            }
            """).Compile("""
                extern int counter;
                int update(void) { return ++counter; }
                """).NativeLinuxLibrary("int counter = 5;")
            .AddCrt().Link(gcSections: true).RunAndCheck(42);
    }

    [Fact]
    public void FunctionAndDataImportsUseTheirRespectiveResolvers()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter;
            int read_counter(void);
            int (*callback)(void) = read_counter;
            int main(void) { counter = 42; return callback(); }
            """).NativeLinuxLibrary("int counter = 5; int read_counter(void) { return counter; }")
            .AddCrt().Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedDefinitionsWinWithoutLoadingNativeProvider(bool reverse)
    {
        using var builder = new CompilationBuilder(true);
        const string consumer = "extern int counter; int main(void) { return ++counter; }";
        const string definition = "int counter = 41;";
        builder.Compile(reverse ? definition : consumer).Compile(reverse ? consumer : definition)
            .NativeLinuxLibrary("int counter = 1;");
        File.Delete(builder.NativeRuntimePath);
        builder.Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData("return counter;", "ldsfld")]
    [InlineData("counter = 42; return 42;", "stsfld")]
    public void ValueAccessWithoutFixedAddressesHasActionableDiagnostic(string body, string opcode)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { " + body + " }",
                ["-fno-fixed-address-statics"])
            .NativeLinuxLibrary("int counter = 5;").AddCrt();
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("counter", error);
        Assert.Contains("libnative.so.1", error);
        Assert.Contains("input0.obj", error);
        Assert.Contains(opcode, error);
        Assert.Contains("-ffixed-address-statics", error);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void AddressOnlyOptOutAccessSatisfiesTheOpcodeProtocol()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return *(&counter); }",
                ["-fno-fixed-address-statics"])
            .NativeLinuxLibrary("int counter = 42;").AddCrt().Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData("int counter(void); int main(void) { return counter(); }", "int counter = 5;")]
    [InlineData("extern int counter; int main(void) { return counter; }", "int counter(void) { return 42; }")]
    public void ImportKindMismatchesFail(string consumer, string provider)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(consumer).NativeLinuxLibrary(provider);
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("counter", error);
        Assert.Contains("function", error);
        Assert.Contains("data", error);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void LiveDataImportRequiresExplicitResolver()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .NativeLinuxLibrary("int counter = 42;");
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("data import 'counter' requires a native export resolver", error);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void IncorrectDataResolverSignatureFailsBeforeWritingOutput()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .Compile("int __chilink_get_native_data_export(void) { return 0; }")
            .NativeLinuxLibrary("int counter = 42;");
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("__chilink_get_native_data_export", error);
        Assert.Contains("accepting (string, string)", error);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardedDataImportsDoNotRequireResolverOrRuntimeLibrary(bool code)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(code
                ? "extern int counter; int unused(void) { return counter; } int main(void) { return 42; }"
                : "extern int counter; int *unused = &counter; int main(void) { return 42; }",
                ["-ffunction-sections", "-fdata-sections", "-fno-fixed-address-statics"])
            .NativeLinuxLibrary("int counter = 5;");
        File.Delete(builder.NativeRuntimePath);
        builder.Link(gcSections: true).RunAndCheck(42);
    }

    [Fact]
    public void SlotsAloneConflictWithAnExistingModuleInitializer()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .NativeLinuxLibrary("int counter = 42;")
            .AddCrt().AddAsm2ObjAssembly("ChilinkInitializerAssembly.dll");
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("module initializer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlibcStreamsUseExportedPointersAndRedirectedInput()
    {
        if (!File.Exists("/lib/x86_64-linux-gnu/libc.so.6"))
            throw Xunit.Sdk.SkipException.ForSkip("This smoke test requires x64 multiarch glibc.");
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            typedef struct FILE FILE;
            extern FILE *stdin, *stdout;
            FILE **output = &stdout;
            int fputs(const char *, FILE *);
            int fflush(FILE *);
            int fgetc(FILE *);
            int main(void) {
                if (*output != stdout) return 1;
                if (fputs("hi\n", stdout) < 0 || fflush(stdout)) return 2;
                return fgetc(stdin) == 'x' ? 42 : 3;
            }
            """).AddCrt().Chilink(["-l:libc.so.6"])
            .RunAndCheck(42, standardInput: "x\n", expectedOutput: "hi\n");
    }

    [Fact]
    public void LdtokenAccessIsRejectedBeforeOutputIsWritten()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .NativeLinuxLibrary("int counter = 42;").AddCrt();
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        int changed = 0;
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var md = coff.GetMetadataReader();
            var reference = md.MemberReferences.Single(h =>
                md.GetString(md.GetMemberReference(h).Name) == "counter");
            string token = MetadataTokens.GetToken(reference).ToString("X8");
            var sections = coff.GetSectionTableReader();
            var symbols = coff.GetSymbolTableReader();
            var strings = coff.GetStringTableReader();
            foreach (var handle in sections.Sections)
            {
                var section = sections.GetCoffSection(handle);
                if ((section.SectionCharacteristics & SectionCharacteristics.ContainsCode) == 0)
                    continue;
                var relocations = coff.GetRelocationTableReader(section);
                for (int i = 0; i < relocations.NumberOfRelocations; i++)
                {
                    var relocation = relocations.GetRelocation(i);
                    if (strings.GetString(symbols.GetCoffSymbol(relocation.Symbol).Name) != token)
                        continue;
                    bytes[section.PointerToRawData + relocation.Offset - 1] = 0xd0;
                    changed++;
                }
            }
        }
        Assert.Equal(1, changed);
        File.WriteAllBytes(builder._objFiles[0], bytes);
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("ldtoken", error);
        Assert.Contains("-ffixed-address-statics", error);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void FrameworkCrtDoesNotDefineADataResolver()
    {
        using var builder = new CompilationBuilder(false);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .NativeLinuxLibrary("int counter = 42;").AddCrt();
        string error = Assert.Throws<InvalidOperationException>(() => builder.Link()).Message;
        Assert.Contains("data import 'counter' requires a native export resolver", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeLibraryAndExportFailuresAreExplicit(bool missingExport)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return counter; }")
            .NativeLinuxLibrary("int counter = 42;").AddCrt();
        var result = builder.Link();
        if (missingExport)
            builder.NativeLinuxLibrary("int other = 42;");
        else
            File.Delete(builder.NativeRuntimePath);
        string error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => result.RunAndCheck(42)).Message;
        Assert.Contains(missingExport ? "EntryPointNotFoundException" : "DllNotFoundException", error);
    }

    [Fact]
    public void ElfDataNamesDoNotAcquireCoffPointerAliases()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter, __imp_counter;
            int main(void) { return counter + __imp_counter; }
            """).NativeLinuxLibrary("int counter = 17; int __imp_counter = 25;")
            .AddCrt().Link().RunAndCheck(42);
    }

    [Fact]
    public void CommonDynamicSymbolsProvideDataImports()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("extern int counter; int main(void) { return ++counter; }")
            .NativeLinuxLibrary("int counter = 41;").AddCrt();
        byte[] bytes = File.ReadAllBytes(builder._objFiles[1]);
        int sectionTable = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40)));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(60));
        int changed = 0;
        for (int i = 0; i < sectionCount; i++)
        {
            int section = sectionTable + i * 64;
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 4)) != 11)
                continue;
            int symbols = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(section + 24)));
            int size = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(section + 32)));
            int stringsSection = sectionTable + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 40))) * 64;
            int strings = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(stringsSection + 24)));
            for (int symbol = symbols; symbol < symbols + size; symbol += 24)
            {
                int name = strings + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(symbol)));
                int length = bytes.AsSpan(name).IndexOf((byte)0);
                if (Encoding.UTF8.GetString(bytes, name, length) != "counter")
                    continue;
                Assert.Equal(1, bytes[symbol + 4] & 15);
                bytes[symbol + 4] = (byte)((bytes[symbol + 4] & 0xf0) | 5);
                changed++;
            }
        }
        Assert.Equal(1, changed);
        File.WriteAllBytes(builder._objFiles[1], bytes);
        File.WriteAllBytes(builder.NativeRuntimePath, bytes);
        builder.Link().RunAndCheck(42);
    }

    [Fact]
    public void ImportedBitfieldsRetainAddressBasedReadModifyWrite()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            struct Bits { unsigned int low:4; unsigned int high:4; };
            extern struct Bits bits;
            int native_bits(void);
            int main(void) {
                if (bits.low != 1 || bits.high != 2) return 1;
                bits.low += 2;
                if (bits.high++ != 2) return 2;
                if ((bits.low = 10) != 10 || bits.high != 3) return 3;
                return native_bits();
            }
            """).NativeLinuxLibrary("""
                struct Bits { unsigned int low:4; unsigned int high:4; } bits = { 1, 2 };
                int native_bits(void) { return bits.low + bits.high + 29; }
                """).AddCrt().Link().RunAndCheck(42);
    }

    [Fact]
    public void ArchiveMembersAndResolverSelectionRebuildDataBindings()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int counter;
            int *pointer = &counter;
            int helper(void);
            int main(void) { return helper() + *pointer; }
            """).Compile("extern int counter; int helper(void) { return counter = 21; }")
            .AddCrt();
        ArchiveObject(builder, 1);
        ArchiveObject(builder, 2);
        builder.NativeLinuxLibrary("int counter = 5;").Link(gcSections: true).RunAndCheck(42);
    }

    private static void ArchiveObject(CompilationBuilder builder, int index)
    {
        byte[] bytes = File.ReadAllBytes(builder._objFiles[index]);
        string[] names;
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var symbols = coff.GetSymbolTableReader();
            var strings = coff.GetStringTableReader();
            names = symbols.Symbols.Select(symbols.GetCoffSymbol)
                .Where(s => s.StorageClass == CoffSymbolStorageClass.External && s.SectionNumber.Number > 0)
                .Select(s => strings.GetString(s.Name)).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        byte[] symbolNames = Encoding.UTF8.GetBytes(string.Join('\0', names) + '\0');
        int indexSize = 12 + names.Length * 2 + symbolNames.Length;
        int memberOffset = 8 + 64 + 60 + ((indexSize + 1) & ~1);
        string archive = Path.ChangeExtension(builder._objFiles[index], ".lib");
        using (var writer = new BinaryWriter(File.Create(archive)))
        {
            void Header(string name, int size) => writer.Write(Encoding.ASCII.GetBytes(
                $"{name,-16}{0,-12}{0,-6}{0,-6}{"100644",-8}{size,-10}`\n"));
            writer.Write("!<arch>\n"u8);
            Header("/", 4);
            writer.Write(0);
            Header("/", indexSize);
            writer.Write(1);
            writer.Write(memberOffset);
            writer.Write(names.Length);
            foreach (string name in names)
                writer.Write((ushort)1);
            writer.Write(symbolNames);
            if ((indexSize & 1) != 0)
                writer.Write((byte)'\n');
            Header("member.obj/", bytes.Length);
            writer.Write(bytes);
            if ((bytes.Length & 1) != 0)
                writer.Write((byte)'\n');
        }
        builder._objFiles[index] = archive;
    }

    [Fact]
    public async Task DataLookupUsesTheNativeHostsCopyRelocation()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            extern int copy_counter;
            int *copy_address(void);
            void set_copy_counter(int);
            int main(void) {
                if (copy_counter != 40 || &copy_counter != copy_address()) return 1;
                if (++copy_counter != 41) return 2;
                set_copy_counter(42);
                return copy_counter;
            }
            """).NativeLinuxLibrary("""
                int copy_counter = 5;
                int *copy_address(void) { return &copy_counter; }
                void set_copy_counter(int value) { copy_counter = value; }
                """).AddCrt().Link();
        string output = Path.Combine(builder._tempDir, "output.dll");
        File.WriteAllText(Path.ChangeExtension(output, ".runtimeconfig.json"), """
            {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
            """);
        string source = Path.Combine(builder._tempDir, "host.c");
        string host = Path.Combine(builder._tempDir, "host");
        File.WriteAllText(source, """
            #include <dlfcn.h>
            #include <stdio.h>
            #include <stdlib.h>
            extern int copy_counter;
            int *copy_address(void);
            static void require(int condition, const char *message) {
                if (!condition) { fprintf(stderr, "%s\n", message); exit(1); }
            }
            int main(int argc, char **argv) {
                require(argc == 4, "expected hostfxr, assembly, and native provider paths");
                void *provider = dlopen(argv[3], RTLD_NOW);
                require(provider != NULL, "could not load native provider");
                void *original = dlsym(provider, "copy_counter");
                require(original != NULL && original != &copy_counter, "COPY storage must differ from library storage");
                require(copy_address() == &copy_counter, "native code must use the host COPY storage");
                copy_counter = 40;
                void *fxr = dlopen(argv[1], RTLD_NOW);
                require(fxr != NULL, "could not load hostfxr");
                int (*initialize)(int, const char **, const void *, void **) =
                    dlsym(fxr, "hostfxr_initialize_for_dotnet_command_line");
                int (*run)(void *) = dlsym(fxr, "hostfxr_run_app");
                int (*close)(void *) = dlsym(fxr, "hostfxr_close");
                require(initialize != NULL && run != NULL && close != NULL, "missing hostfxr APIs");
                const char *args[] = { argv[2] };
                void *context = NULL;
                require(initialize(1, args, NULL, &context) >= 0 && context != NULL, "hostfxr initialization failed");
                int result = run(context);
                require(close(context) == 0, "hostfxr close failed");
                require(result == 42, "managed code did not use the COPY address");
                require(copy_counter == 42, "host did not observe the managed/native writes");
                return 42;
            }
            """);
        await RunNativeProcess("cc", [source, "-o", host, "-fno-pie", "-no-pie",
            builder._objFiles[1], "-Wl,-rpath,$ORIGIN", "-ldl"]);
        string relocations = await RunNativeProcess("readelf", ["-rW", host]);
        Assert.Contains(relocations.Split('\n'), line =>
            line.Contains("R_X86_64_COPY", StringComparison.Ordinal) && line.Contains("copy_counter", StringComparison.Ordinal));
        string runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string root = Directory.GetParent(runtime).Parent.Parent.FullName;
        string[] hosts = Directory.GetFiles(Path.Combine(root, "host", "fxr"), "libhostfxr.so", SearchOption.AllDirectories)
            .OrderByDescending(path => Version.Parse(Path.GetFileName(Path.GetDirectoryName(path)).Split('-')[0]))
            .ToArray();
        Assert.NotEmpty(hosts);
        string fxr = hosts[0];
        await RunNativeProcess(host, [fxr, output, builder._objFiles[1]], expectedExitCode: 42);
    }

    private static async Task<string> RunNativeProcess(string file, string[] args, int expectedExitCode = 0)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string output = await stdout;
        Assert.True(process.ExitCode == expectedExitCode,
            $"{file} exited with {process.ExitCode}, expected {expectedExitCode}.\n{output}\n{await stderr}");
        return output;
    }
}
