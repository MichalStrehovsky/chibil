using System.Buffers.Binary;
using Coff;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkImportTests
{
    private const string AddressConsumer = """
        int target(int);
        void *native_address(void);
        int (*pointer)(int) = target;
        int (*array[2])(int) = { target, target };
        struct Holder { int before; int (*callback)(int); int after; };
        struct Holder holder = { 11, target, 22 };
        int main(void) {
            int (*local)(int) = target;
            if ((void *)local != native_address() || pointer != local ||
                array[0] != local || array[1] != local || holder.callback != local ||
                holder.before != 11 || holder.after != 22) return 1;
            return pointer(10) + array[1](10) + holder.callback(10) + local(8);
        }
        """;
    private const string AddressProvider = """
        int target(int value) { return value + 1; }
        void *native_address(void) { return target; }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFunctionImportsUseCallerSignaturesAndActualDllName(bool netCore)
    {
        using var builder = new CompilationBuilder(netCore);
        builder.Compile("""
            int add(int, int);
            int read(const char *);
            int main(void) { return add(20, 20) + read("xx"); }
            """).NativeLibrary("""
            int add(int a, int b) { return a + b; }
            int read(const char *p) { return p[0] == 'x' && p[1] == 'x' ? 2 : 0; }
            """, "add\nread", "actual-library.dll", "unrelated-name.lib")
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAddressesAreExportAddressesRatherThanDllImportThunks(bool sections)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(AddressConsumer, sections ? ["-ffunction-sections", "-fdata-sections"] : null)
            .NativeLibrary(AddressProvider, "target\nnative_address")
            .AddCrt()
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void NativeAddressRequiresAnExplicitResolverDefinition()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(AddressConsumer).NativeLibrary(AddressProvider, "target\nnative_address");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(["/entry:main"]));
        Assert.Contains("__chilink_get_native_export", error.Message);
    }

    [Fact]
    public void CrtStartupAndNativeExportResolverWorkFromTheSameObject()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int target(int);
            int (*pointer)(int) = target;
            int main(int argc, char **argv) {
                if (argc < 1 || argv[argc] != 0) return 1;
                return pointer(41);
            }
            """)
            .NativeLibrary("int target(int value) { return value + 1; }", "target")
            .AddCrt()
            .Link(["/subsystem:console"]).RunAndCheck(42);
    }

    [Fact]
    public void FrameworkCrtDoesNotDefineTheNativeExportResolver()
    {
        using var builder = new CompilationBuilder(false);
        builder.Compile("int main(void) { return 42; }").AddCrt();
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.Link(["/entry:main", "/include:__chilink_get_native_export"]));
        Assert.Contains("__chilink_get_native_export", error.Message);
    }

    [Fact]
    public void DiscardedNativeAddressSlotsDoNotRequireResolverOrLoadTheLibrary()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int target(int);
            int (*unused)(int) = target;
            int main(void) { return 42; }
            """, ["-ffunction-sections", "-fdata-sections"])
            .NativeLibrary("int target(int value) { return value; }", "target");
        File.Delete(Path.Combine(builder._tempDir, "native.dll"));
        builder.Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlittableAggregateImportsPreserveTheSuppliedLayout(bool netCore)
    {
        using var builder = new CompilationBuilder(netCore);
        builder.Compile("""
            struct Value { long long left; long long right; };
            struct Value adjust(struct Value);
            int main(void) {
                struct Value value = { 20, 20 };
                struct Value result = adjust(value);
                return result.left + result.right;
            }
            """, ["-fmanaged-aggregate-fields"])
            .NativeLibrary("""
                struct Value { long long left; long long right; };
                struct Value adjust(struct Value value) { value.left++; value.right++; return value; }
                """, "adjust")
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData("ordinal", "ordinal @1 NONAME", "ordinal import")]
    [InlineData("data", "data DATA", "imported data")]
    public void SelectedUnsupportedImportKindsFailExplicitly(string symbol, string export, string diagnostic)
    {
        using var builder = new CompilationBuilder(true);
        string source = symbol == "data"
            ? "extern int data; int main(void) { return data; }"
            : "int ordinal(void); int main(void) { return ordinal(); }";
        builder.Compile(source).NativeLibrary("int data = 42; int ordinal(void) { return 42; }", export);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(["/entry:main"]));
        Assert.Contains(diagnostic, error.Message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void UnusedOrdinalAndDataImportsDoNotPreventNamedFunctionImports()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int data = 1; int ordinal(void) { return 1; } int target(void) { return 42; }",
                "data DATA\nordinal @1 NONAME\ntarget")
            .Link(["/entry:main"]).RunAndCheck(42);
    }

    [Fact]
    public void ConflictingSignaturesForTheSameNativeImportAreErrors()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(int); int helper(void); int main(void) { return target(20) + helper(); }")
            .Compile("long long target(long long); int helper(void) { return target(20); }")
            .NativeLibrary("int target(int value) { return value + 1; }", "target");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(["/entry:main"]));
        Assert.Contains("incompatible signatures", error.Message);
    }

    [Fact]
    public void IncorrectResolverSignatureFailsBeforeOutputIsWritten()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(AddressConsumer)
            .Compile("int __chilink_get_native_export(void) { return 0; }")
            .NativeLibrary(AddressProvider, "target\nnative_address");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(["/entry:main"]));
        Assert.Contains("accepting (string, string)", error.Message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, "output.dll")));
    }

    [Fact]
    public void ExplicitManagedDefinitionWinsOverUnusedImportLibrary()
    {
        using var builder = new CompilationBuilder(true);
        builder.NativeLibrary("int target(void) { return 17; }", "target")
            .Compile("int target(void); int main(void) { return target(); }")
            .Compile("int target(void) { return 42; }");
        File.Delete(Path.Combine(builder._tempDir, "native.dll"));
        builder.Link(["/entry:main"]).RunAndCheck(42);
    }

    [Fact]
    public void MissingNativeLibrarySurfacesAtRuntime()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int target(void); int main(void) { return target(); }")
            .NativeLibrary("int target(void) { return 42; }", "target");
        File.Delete(Path.Combine(builder._tempDir, "native.dll"));
        var result = builder.Link(["/entry:main"]);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => result.RunAndCheck(42));
        Assert.Contains("DllNotFoundException", error.Message);
    }

    [Fact]
    public void NormalizedExportNamesAreUsedForBothDirectCallsAndAddresses()
    {
        using var builder = new CompilationBuilder(true);
        const string provider = "int actual(int v) { return v + 1; } void *native_address(void) { return actual; }";
        builder.Compile("""
            int _actual(int);
            void *native_address(void);
            int (*pointer)(int) = _actual;
            int main(void) {
                if ((void *)pointer != native_address() || pointer != _actual) return 1;
                return _actual(20) + pointer(20);
            }
            """).NativeLibrary(provider, "actual\n_actual=actual\nnative_address", libraryName: "aliases.lib");
        string path = builder._objFiles[1];
        byte[] bytes = File.ReadAllBytes(path);
        bool changed = false;
        using (var archive = new CoffArchiveReader(new MemoryStream(bytes)))
        {
            var members = archive.GetMemberTableReader();
            var strings = archive.GetStringTableReader();
            foreach (var handle in members.Members)
            {
                var member = members.GetCoffArchiveMember(handle);
                var import = archive.GetImportObject(member);
                if (import.IsDefault || strings.GetString(import.SymbolName) != "_actual")
                    continue;
                var flags = bytes.AsSpan(member.DataOffset + 18, 2);
                BinaryPrimitives.WriteUInt16LittleEndian(flags, (ushort)(
                    (BinaryPrimitives.ReadUInt16LittleEndian(flags) & ~(7 << 2)) |
                    ((int)CoffImportNameType.NoPrefix << 2)));
                changed = true;
            }
        }
        Assert.True(changed, "The fixture must contain the aliased short import.");
        File.WriteAllBytes(path, bytes);
        // Replace the DLL with one that exports actual, but not the COFF alias _actual.
        builder.NativeLibrary(provider, "actual\nnative_address", libraryName: "unused.lib")
            .AddCrt()
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void MissingNativeExportSurfacesFromAddressInitialization()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(AddressConsumer).NativeLibrary(AddressProvider, "target\nnative_address", libraryName: "original.lib")
            .NativeLibrary("void *native_address(void) { return 0; }", "native_address", libraryName: "replacement.lib")
            .AddCrt();
        var result = builder.Link(["/entry:main", "/OPT:REF"]);
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => result.RunAndCheck(42));
        Assert.Contains("EntryPointNotFoundException", error.Message);
    }
}
