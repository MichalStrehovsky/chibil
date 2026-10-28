using Xunit;

namespace Chibil.Tests;

public sealed class NativeVarargsTests
{
    private const string Structs = """
        typedef struct { char a; } S1;
        typedef struct { short a; } S2;
        typedef struct { char a, b, c; } S3;
        typedef struct { int a; } S4;
        typedef struct { int a, b; } S8;
        typedef struct { long long a, b; } S16;
        typedef struct { double a, b; } D2;
        typedef struct { long long a, b, c; } S24;
        typedef struct { long long a; double b; } M16;

        """;

    private const string NativeSource = Structs + """
        #include <stdarg.h>
        #ifdef _WIN32
        int _fltused;
        #endif
        static char *put_int(char *out, long long value) {
            char digits[24];
            int count = 0;
            unsigned long long magnitude = value < 0 ? 0ull - (unsigned long long)value : (unsigned long long)value;
            if (value < 0) *out++ = '-';
            do { digits[count++] = (char)('0' + magnitude % 10); magnitude /= 10; } while (magnitude);
            while (count) *out++ = digits[--count];
            return out;
        }
        #define PAIR(x, y) out = put_int(out, x); *out++ = ':'; out = put_int(out, y)
        int vformat(char *out, const char *spec, va_list ap) {
            char *start = out;
            const char *first = spec;
            for (; *spec; spec++) {
                if (spec != first) *out++ = ',';
                switch (*spec) {
                case 'i': out = put_int(out, va_arg(ap, int)); break;
                case 'u': out = put_int(out, va_arg(ap, unsigned)); break;
                case 'l': out = put_int(out, va_arg(ap, long long)); break;
                case 'd': out = put_int(out, (long long)(va_arg(ap, double) * 100)); break;
                case 'p': out = put_int(out, *va_arg(ap, int *)); break;
                case 'c': *out++ = (char)va_arg(ap, int); break;
                case 's': { const char *s = va_arg(ap, const char *); while (*s) *out++ = *s++; break; }
                case '1': { S1 v = va_arg(ap, S1); out = put_int(out, v.a); break; }
                case '2': { S2 v = va_arg(ap, S2); out = put_int(out, v.a); break; }
                case '3': { S3 v = va_arg(ap, S3); PAIR(v.a, v.b); *out++ = ':'; out = put_int(out, v.c); break; }
                case '4': { S4 v = va_arg(ap, S4); out = put_int(out, v.a); break; }
                case '8': { S8 v = va_arg(ap, S8); PAIR(v.a, v.b); break; }
                case 'q': { S16 v = va_arg(ap, S16); PAIR(v.a, v.b); break; }
                case 'f': { D2 v = va_arg(ap, D2); PAIR((long long)(v.a * 100), (long long)(v.b * 100)); break; }
                case 't': { S24 v = va_arg(ap, S24); PAIR(v.a, v.b); *out++ = ':'; out = put_int(out, v.c); break; }
                case 'm': { M16 v = va_arg(ap, M16); PAIR(v.a, (long long)(v.b * 100)); break; }
                default: *out++ = '?'; break;
                }
            }
            *out = 0;
            return (int)(out - start);
        }
        int format(char *out, const char *spec, ...) {
            va_list ap;
            va_start(ap, spec);
            int result = vformat(out, spec, ap);
            va_end(ap);
            return result;
        }
        void *get_format(void) { return (void *)format; }
        int call_with(int (*callback)(const char *, va_list), const char *spec, ...) {
            va_list ap;
            va_start(ap, spec);
            int result = callback(spec, ap);
            va_end(ap);
            return result;
        }
        """;

    private const string NativeExports = "vformat\nformat\nget_format\ncall_with";

    private const string Prelude = Structs + """
        int format(char *out, const char *spec, ...);
        void *get_format(void);
        static int streq(const char *a, const char *b) {
            while (*a && *a == *b) a++, b++;
            return *a == *b;
        }

        """;

    private static CompilationBuilder Native(bool netCore, string source, string[] extraArgs = null)
    {
        var builder = new CompilationBuilder(netCore);
        builder.Compile(source, extraArgs).NativeLibrary(NativeSource, NativeExports);
        if (OperatingSystem.IsLinux())
            builder.AddCrt();
        return builder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectCallsToNativeVariadicFunctionsPassScalars(bool netCore)
    {
        using var builder = Native(netCore, Prelude + """
            int main(void) {
                char buffer[256];
                int pointee = 77;
                char c = 'q';
                short s = -3;
                float f = 0.5f;
                if (format(buffer, "") != 0 || buffer[0]) return 1;
                if (format(buffer, "iiiiiiiiii", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10) != 20 ||
                    !streq(buffer, "1,2,3,4,5,6,7,8,9,10")) return 2;
                format(buffer, "ddddddddddi", 1.5, 2.5, -0.75, 1.25, 0.5, 4.0, 8.0, 16.0, 0.25, -1.0, 7);
                if (!streq(buffer, "150,250,-75,125,50,400,800,1600,25,-100,7")) return 3;
                format(buffer, "lupcs", -5000000000LL, 4000000000u, &pointee, 'z', "str");
                if (!streq(buffer, "-5000000000,4000000000,77,z,str")) return 4;
                format(buffer, "cid", c, s, f);
                if (!streq(buffer, "q,-3,50")) return 5;
                format(buffer, "idlidi", 1, 2.5, 3LL, 4, -0.75, 5);
                if (!streq(buffer, "1,250,3,4,-75,5")) return 6;
                return 42;
            }
            """);
        builder.Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectCallsToNativeVariadicFunctionsPassAggregates(bool netCore)
    {
        if (OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("Native SysV aggregate passing is not supported.");
        using var builder = Native(netCore, Prelude + """
            int main(void) {
                char buffer[256];
                S1 s1 = { 7 }; S2 s2 = { -300 }; S3 s3 = { 1, 2, 3 }; S4 s4 = { 100000 };
                S8 s8 = { 5, -6 }; S16 s16 = { 1LL << 40, -2 }; D2 d2 = { 2.5, -0.75 };
                S24 s24 = { 1, 2, 3 }; M16 m16 = { -9, 1.25 };
                format(buffer, "12348qftm", s1, s2, s3, s4, s8, s16, d2, s24, m16);
                if (!streq(buffer, "7,-300,1:2:3,100000,5:-6,1099511627776:-2,250:-75,1:2:3,-9:125")) return 1;
                format(buffer, "iiiiiqf", 1, 2, 3, 4, 5, s16, d2);
                if (!streq(buffer, "1,2,3,4,5,1099511627776:-2,250:-75")) return 2;
                format(buffer, "dddddddfi", 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, d2, 3);
                if (!streq(buffer, "50,50,50,50,50,50,50,250:-75,3")) return 3;
                return 42;
            }
            """);
        builder.Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectCallsToNativeVariadicFunctions(bool netCore)
    {
        using var builder = Native(netCore, Prelude + """
            typedef int (*Format)(char *, const char *, ...);
            int main(void) {
                char buffer[256];
                Format pointer = (Format)get_format();
                if (pointer(buffer, "") != 0 || buffer[0]) return 1;
                pointer(buffer, "isdl", 5, "x", 2.5, -7LL);
                if (!streq(buffer, "5,x,250,-7")) return 2;
                pointer(buffer, "ddddddddddi", 1.5, 2.5, -0.75, 1.25, 0.5, 4.0, 8.0, 16.0, 0.25, -1.0, 7);
                if (!streq(buffer, "150,250,-75,125,50,400,800,1600,25,-100,7")) return 3;
                pointer(buffer, "iiiiiiiiii", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                if (!streq(buffer, "1,2,3,4,5,6,7,8,9,10")) return 4;
                return 42;
            }
            """);
        builder.Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectCallsToNativeVariadicFunctionsPassAggregates(bool netCore)
    {
        if (OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("Native SysV aggregate passing is not supported.");
        using var builder = Native(netCore, Prelude + """
            typedef int (*Format)(char *, const char *, ...);
            int main(void) {
                char buffer[256];
                Format pointer = (Format)get_format();
                S1 s1 = { 7 }; S2 s2 = { -300 }; S3 s3 = { 1, 2, 3 }; S4 s4 = { 100000 };
                S8 s8 = { 5, -6 }; S16 s16 = { 1LL << 40, -2 }; D2 d2 = { 2.5, -0.75 };
                S24 s24 = { 1, 2, 3 };
                pointer(buffer, "12348qft", s1, s2, s3, s4, s8, s16, d2, s24);
                if (!streq(buffer, "7,-300,1:2:3,100000,5:-6,1099511627776:-2,250:-75,1:2:3")) return 1;
                return 42;
            }
            """);
        builder.Link().RunAndCheck(42);
    }

    [Fact]
    public void StaticPointersToNativeVariadicFunctions()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(Prelude + """
            int (*pointer)(char *, const char *, ...) = format;
            int main(void) {
                char buffer[64];
                pointer(buffer, "ids", 9, 1.25, "ok");
                return streq(buffer, "9,125,ok") ? 42 : 1;
            }
            """).NativeLibrary(NativeSource, NativeExports).AddCrt().Link().RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmulatedDefinitionsForwardTheirListToNativeCode(bool netCore)
    {
        using var builder = Native(netCore, Prelude + """
            #include <stdarg.h>
            int vformat(char *out, const char *spec, va_list ap);
            static int mine(char *out, const char *spec, ...) {
                va_list ap;
                va_start(ap, spec);
                int result = vformat(out, spec, ap);
                va_end(ap);
                return result;
            }
            int main(void) {
                char buffer[256];
                int pointee = 77;
                S3 s3 = { 1, 2, 3 }; S8 s8 = { 5, -6 }; S24 s24 = { 1, 2, 3 };
                mine(buffer, "iiiiiiiiii", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                if (!streq(buffer, "1,2,3,4,5,6,7,8,9,10")) return 1;
                mine(buffer, "ddddddddddi", 1.5, 2.5, -0.75, 1.25, 0.5, 4.0, 8.0, 16.0, 0.25, -1.0, 7);
                if (!streq(buffer, "150,250,-75,125,50,400,800,1600,25,-100,7")) return 2;
                mine(buffer, "lupcs", -5000000000LL, 4000000000u, &pointee, 'z', "str");
                if (!streq(buffer, "-5000000000,4000000000,77,z,str")) return 3;
                mine(buffer, "38t", s3, s8, s24);
                if (!streq(buffer, "1:2:3,5:-6,1:2:3")) return 4;
                return 42;
            }
            """, ["-femulated-varargs"]);
        builder.Link().RunAndCheck(42);
    }

    // Native callers of managed code need unmanaged-callable thunks, which chilink only supports on .NET.
    [Fact]
    public void NativeListsCanBeReadCopiedAndForwarded()
    {
        using var builder = Native(true, Prelude + """
            #include <stdarg.h>
            int vformat(char *out, const char *spec, va_list ap);
            int call_with(int (*callback)(const char *, va_list), const char *spec, ...);
            static int callback(const char *spec, va_list ap) {
                char buffer[64];
                va_list copy;
                va_copy(copy, ap);
                for (int i = 1; i <= 7; i++)
                    if (va_arg(ap, int) != i) return i;
                for (int i = 1; i <= 9; i++)
                    if (va_arg(ap, double) != i * 0.5) return 10 + i;
                if (va_arg(ap, long long) != -5000000000LL) return 20;
                if (!streq(va_arg(ap, const char *), "str")) return 21;
                S3 s3 = va_arg(ap, S3);
                if (s3.a != 1 || s3.b != 2 || s3.c != 3) return 22;
                S24 s24 = va_arg(ap, S24);
                if (s24.a != 4 || s24.b != 5 || s24.c != 6) return 23;
                if (va_arg(ap, int) != 99) return 24;
                if (vformat(buffer, "iii", copy) != 5 || !streq(buffer, "1,2,3")) return 25;
                va_end(copy);
                return streq(spec, "spec") ? 42 : 26;
            }
            int main(void) {
                S3 s3 = { 1, 2, 3 }; S24 s24 = { 4, 5, 6 };
                return call_with(callback, "spec", 1, 2, 3, 4, 5, 6, 7,
                    0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5,
                    -5000000000LL, "str", s3, s24, 99);
            }
            """, ["-femulated-varargs"]);
        builder.Link().RunAndCheck(42);
    }

    [Fact]
    public void WindowsPrintfFromTheSystemCrt()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("msvcrt.dll requires Windows.");
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int printf(const char *, ...);
            int sprintf(char *, const char *, ...);
            int fflush(void *);
            int strcmp(const char *, const char *);
            int main(void) {
                char buffer[64];
                sprintf(buffer, "%d %s %.2f %c", -12, "abc", 2.5, 'x');
                if (strcmp(buffer, "-12 abc 2.50 x")) return 1;
                printf("hi|%d|%.1f|%s", 7, 0.5, buffer);
                fflush(0);
                return 42;
            }
            """);
        string definition = Path.Combine(builder._tempDir, "msvcrt.def");
        string library = Path.Combine(builder._tempDir, "msvcrt.lib");
        File.WriteAllText(definition, "LIBRARY msvcrt.dll\nEXPORTS\nprintf\nsprintf\nfflush\nstrcmp\n");
        var psi = new System.Diagnostics.ProcessStartInfo("lib.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/nologo", "/def:" + definition, "/machine:x64", "/out:" + library })
            psi.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(psi))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        builder._objFiles.Add(library);
        builder.Link().RunAndCheck(42, expectedOutput: "hi|7|0.5|-12 abc 2.50 x");
    }

    [Fact]
    public void LinuxPrintfFromLibc()
    {
        if (!File.Exists("/lib/x86_64-linux-gnu/libc.so.6"))
            throw Xunit.Sdk.SkipException.ForSkip("glibc is not available.");
        using var builder = new CompilationBuilder(true);
        builder.Compile("""
            int printf(const char *, ...);
            int sprintf(char *, const char *, ...);
            int fflush(void *);
            int strcmp(const char *, const char *);
            int main(void) {
                char buffer[64];
                sprintf(buffer, "%d %s %.2f %c", -12, "abc", 2.5, 'x');
                if (strcmp(buffer, "-12 abc 2.50 x")) return 1;
                printf("hi|%d|%.1f|%s", 7, 0.5, buffer);
                fflush(0);
                return 42;
            }
            """).AddCrt().Chilink(["-l:libc.so.6"])
            .RunAndCheck(42, expectedOutput: "hi|7|0.5|-12 abc 2.50 x");
    }

    [Fact]
    public void LinuxVariadicCallsRequireTheCrtStubResolver()
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("Linux native imports require ELF shared libraries.");
        using var builder = new CompilationBuilder(true);
        builder.Compile(Prelude + """
            int main(void) { char buffer[8]; return format(buffer, "i", 1); }
            """).NativeLibrary(NativeSource, NativeExports);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link());
        Assert.Contains("__chilink_get_vararg_stub", error.Message);
    }

    [Fact]
    public void MsvcLinkerBindsVariadicImports()
    {
        using var builder = new CompilationBuilder(false);
        builder.Compile("""
            int wsprintfA(char *, const char *, ...);
            static int streq(const char *a, const char *b) {
                while (*a && *a == *b) a++, b++;
                return *a == *b;
            }
            int main(void) {
                char buffer[64];
                int length = wsprintfA(buffer, "%d %s %c %u", -12, "abc", 'x', 7u);
                return length == 11 && streq(buffer, "-12 abc x 7") ? 42 : 1;
            }
            """);
        builder.MsvcLink(["user32.lib"]).RunAndCheck(42);
    }

    private const string CrossTargetSource = Structs + """
        #include <stdarg.h>
        static int streq(const char *a, const char *b) {
            while (*a && *a == *b) a++, b++;
            return *a == *b;
        }
        static int __clrcall check(int first, ...) {
            va_list ap, copy;
            va_start(ap, first);
            va_copy(copy, ap);
            for (int i = 2; i <= 9; i++)
                if (va_arg(ap, int) != i) return i;
            for (int i = 1; i <= 10; i++)
                if (va_arg(ap, double) != i * 0.25) return 10 + i;
            S3 s3 = va_arg(ap, S3);
            if (s3.a != 1 || s3.b != 2 || s3.c != 3) return 21;
            S16 s16 = va_arg(ap, S16);
            if (s16.a != 4 || s16.b != 5) return 22;
            D2 d2 = va_arg(ap, D2);
            if (d2.a != 2.5 || d2.b != -0.75) return 23;
            S24 s24 = va_arg(ap, S24);
            if (s24.a != 6 || s24.b != 7 || s24.c != 8) return 24;
            M16 m16 = va_arg(ap, M16);
            if (m16.a != 9 || m16.b != 1.25) return 25;
            if (!streq(va_arg(ap, const char *), "end")) return 26;
            va_end(ap);
            if (va_arg(copy, int) != 2) return 27;
            va_end(copy);
            return first;
        }
        typedef int (__clrcall *Check)(int, ...);
        int main(void) {
            S3 s3 = { 1, 2, 3 }; S16 s16 = { 4, 5 }; D2 d2 = { 2.5, -0.75 };
            S24 s24 = { 6, 7, 8 }; M16 m16 = { 9, 1.25 };
            Check pointer = check;
            int direct = check(42, 2, 3, 4, 5, 6, 7, 8, 9,
                0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5,
                s3, s16, d2, s24, m16, "end");
            if (direct != 42) return direct;
            return pointer(42, 2, 3, 4, 5, 6, 7, 8, 9,
                0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5,
                s3, s16, d2, s24, m16, "end");
        }
        """;

    [Theory]
    [InlineData("x86_64-dotnet-windows")]
    [InlineData("x86_64-dotnet-linux")]
    public void EmulatedListsFollowTheTargetLayout(string target)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(CrossTargetSource, ["--target=" + target, "-femulated-varargs"])
            .Chilink(["--target=" + target]).RunAndCheck(42);
    }

    [Fact]
    public void SystemVListsReadRegisterSaveAreasAndOverflowArguments()
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(Structs + """
            #include <stdarg.h>
            static int read(va_list ap) {
                if (va_arg(ap, int) != 10) return 1;
                if (va_arg(ap, double) != 1.5) return 2;
                S16 s16 = va_arg(ap, S16);
                if (s16.a != 20 || s16.b != 30) return 3;
                D2 d2 = va_arg(ap, D2);
                if (d2.a != 2.5 || d2.b != 3.5) return 4;
                M16 m16 = va_arg(ap, M16);
                if (m16.a != 40 || m16.b != 4.5) return 5;
                // Memory-class aggregates always use the overflow area; D2 needs two XMM registers but one remains.
                S24 s24 = va_arg(ap, S24);
                if (s24.a != 1 || s24.b != 2 || s24.c != 3) return 6;
                D2 spilled = va_arg(ap, D2);
                if (spilled.a != 0.25 || spilled.b != 0.75) return 7;
                if (va_arg(ap, long long) != 50) return 8;
                if (va_arg(ap, long long) != 70) return 9;
                if (va_arg(ap, double) != 5.5) return 10;
                if (va_arg(ap, long long) != 4) return 11;
                if (va_arg(ap, double) != 6.5) return 12;
                if (va_arg(ap, int) != 60) return 13;
                return 42;
            }
            int main(void) {
                union { long long gp[6]; double fp[22]; } area = { 0 };
                long long overflow[8] = { 1, 2, 3, 0, 0, 4, 0, 0 };
                double *spill = (double *)&overflow[3];
                spill[0] = 0.25; spill[1] = 0.75;
                ((double *)&overflow[6])[0] = 6.5;
                overflow[7] = 60;
                area.gp[0] = 10; area.gp[1] = 20; area.gp[2] = 30; area.gp[3] = 40; area.gp[4] = 50; area.gp[5] = 70;
                area.fp[12] = 1.5; area.fp[14] = 2.5; area.fp[16] = 3.5; area.fp[18] = 4.5; area.fp[20] = 5.5;
                va_list ap;
                ap[0].gp_offset = 0;
                ap[0].fp_offset = 96;
                ap[0].reg_save_area = &area;
                ap[0].overflow_arg_area = overflow;
                return read(ap);
            }
            """, ["--target=x86_64-dotnet-linux", "-femulated-varargs"])
            .Chilink(["--target=x86_64-dotnet-linux"]).RunAndCheck(42);
    }

    [Fact]
    public void CompilerDriverForwardsTheTargetToChilink()
    {
        using var builder = new CompilationBuilder(true);
        string source = Path.Combine(builder._tempDir, "main.c");
        string output = Path.Combine(builder._tempDir, "output.dll");
        File.WriteAllText(source, """
            #include <stdarg.h>
            static int read(int first, ...) {
                va_list ap;
                va_start(ap, first);
                int result = ap[0].gp_offset == 48 && ap[0].fp_offset == 176 && va_arg(ap, int) == 2 ? first : 1;
                va_end(ap);
                return result;
            }
            int main(void) { return read(42, 2); }
            """);
        var result = ChilinkCommandLineTests.RunTool(builder, "Chibil",
            ["--target=x86_64-dotnet-linux", source, "-o", output, "-Wl,-e,main"]).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        new LinkResult(output, true).RunAndCheck(42);
    }

    [Theory]
    [InlineData("--target=x86_64-dotnet-linux", null)]
    [InlineData("--target=x86_64-dotnet-windows-chibil", null)]
    [InlineData("--target=x86_64-netfx-windows", null)]
    [InlineData("--target=aarch64-dotnet-linux", "unsupported target")]
    [InlineData("--target=", "missing value")]
    public void ChilinkTargetOption(string option, string error)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile("int main(void) { return 42; }");
        if (error == null)
        {
            builder.Chilink([option]).RunAndCheck(42);
            return;
        }
        Assert.Contains(error, Assert.Throws<InvalidOperationException>(() => builder.Chilink([option])).Message);
    }

    [Theory]
    [InlineData("int target(int, ...); int (*pointer)(int, ...) = target; int main(void) { return pointer(1); }")]
    [InlineData("int target(int, ...); int main(void) { int (*pointer)(int, ...) = target; return pointer(1); }")]
    public void NativeAddressesOfEmulatedDefinitionsAreRejected(string consumer)
    {
        using var builder = new CompilationBuilder(true);
        builder.Compile(consumer)
            .Compile("int target(int value, ...) { return value; }", ["-femulated-varargs"])
            .AddCrt();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link());
        Assert.Contains("target", error.Message);
        Assert.Contains("variadic", error.Message);
    }
}
