using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Behavioral tests for -ffunction-sections / -fdata-sections (MSVC /Gy / /Gw, plus
/// /GF string pooling under -fdata-sections). Rather than inspect COFF section
/// tables, these observe the linked program at runtime:
///
///   * The flags must never change a program's result.
///   * String pooling is observable through literal addresses — identical literals
///     share an address within a TU and (being content-keyed COMDATs) fold across
///     TUs at link time.
///   * Per-function / per-data COMDATs let /OPT:REF dead-strip unreferenced members.
///     That is observed at runtime via the <c>ReflectionProbe</c> fixture, which
///     reflects over the linked module and counts the surviving <c>frag_*</c>
///     globals — zero once the unused ones are stripped, otherwise still present.
/// </summary>
public sealed class FunctionDataSectionsTests : ChibiTestBase
{
    private const string MultiFuncMultiData = """
        int g_init = 100;
        const int g_ro = 5;
        char* msg = "hi";
        int g_zero = 0;
        int g_uninit;
        static int s_init = 9;
        static int s_uninit;
        static int s_helper(int x) { return x + g_init; }
        int helper(int x) { return s_helper(x) + g_ro; }
        int main(void) {
            return helper(g_zero) + msg[0] - 209 + s_init + g_uninit + s_uninit;
        }
        """;

    // Two translation units that each take the address of an identical literal.
    private const string CrossTuMain = """
        extern char* other_tu_literal(void);
        char* this_tu_literal = "cross-tu-shared";
        int main(void) {
            return this_tu_literal == other_tu_literal() ? 0 : 1;
        }
        """;

    private const string CrossTuOther = """
        char* shared = "cross-tu-shared";
        char* other_tu_literal(void) { return shared; }
        """;

    [Theory]
    [InlineData("")]
    [InlineData("-ffunction-sections")]
    [InlineData("-fdata-sections")]
    [InlineData("-ffunction-sections,-fdata-sections")]
    public void Sections_DoNotChangeProgramResult(string flagsCsv)
    {
        string[] flags = flagsCsv.Length == 0 ? null : flagsCsv.Split(',');

        // helper(0)=s_helper(0)+5 = 100+5; +msg[0]('h'=104) -209 +s_init(9) +0 +0
        // = 105 + 104 - 209 + 9 = 9.
        Compile(MultiFuncMultiData, flags)
            .MsvcLink(["/entry:main", "/subsystem:console"])
            .RunAndCheck(9);
    }

    // ── String pooling ─────────────────────────────────────────────────────────

    [Fact]
    public void DataSections_PoolsIdenticalLiteralsWithinTranslationUnit()
    {
        const string src = """
            char* a = "pooled";
            char* b = "pooled";
            char* c = "other";
            int main(void) {
                if (a != b) return 1;   /* identical literals fold to one address */
                if (a == c) return 2;   /* distinct literals stay separate */
                return 0;
            }
            """;

        Compile(src, ["-fdata-sections"])
            .MsvcLink(["/entry:main", "/subsystem:console"])
            .RunAndCheck(0);
    }

    [Fact]
    public void Default_DoesNotPoolIdenticalLiteralsWithinTranslationUnit()
    {
        const string src = """
            char* a = "pooled";
            char* b = "pooled";
            int main(void) {
                /* Without -fdata-sections, each occurrence is its own definition. */
                return a == b ? 1 : 0;
            }
            """;

        Compile(src)
            .MsvcLink(["/entry:main", "/subsystem:console"])
            .RunAndCheck(0);
    }

    [Fact]
    public void DataSections_FoldsIdenticalLiteralsAcrossTranslationUnits()
    {
        // The pooled literal is a COMDAT with selection Any, so the linker folds the
        // two TUs' copies into one — the addresses compare equal (main returns 0).
        Compile(CrossTuMain, ["-fdata-sections"])
            .Compile(CrossTuOther, ["-fdata-sections"])
            .MsvcLink(["/entry:main", "/subsystem:console"])
            .RunAndCheck(0);
    }

    [Fact]
    public void Default_DoesNotFoldIdenticalLiteralsAcrossTranslationUnits()
    {
        // Without -fdata-sections the literals are merged, non-COMDAT data, so each
        // TU keeps its own copy and the addresses differ (main returns 1).
        Compile(CrossTuMain)
            .Compile(CrossTuOther)
            .MsvcLink(["/entry:main", "/subsystem:console"])
            .RunAndCheck(1);
    }

    // ── /OPT:REF dead-stripping, observed via reflection over the linked module ──

    private const string UnusedFunctions = """
        extern int probe_methods(void);
        int frag_one(int x) { return x + 1; }
        int frag_two(int x) { return x + 2; }
        int main(void) { return probe_methods(); }
        """;

    private const string UnusedGlobals = """
        extern int probe_fields(void);
        int frag_data1 = 11;
        int frag_data2 = 22;
        int main(void) { return probe_fields(); }
        """;

    [Fact]
    public void FunctionSections_LetOptRefDeadStripUnusedFunctions()
    {
        // Each function is its own COMDAT, so /OPT:REF removes the two unreferenced
        // frag_* functions: the probe finds none of them in the linked module.
        Compile(UnusedFunctions, ["-ffunction-sections"])
            .AddAsm2ObjAssembly("ReflectionProbe.dll")
            .MsvcLink(["/entry:main", "/subsystem:console", "/OPT:REF"])
            .RunAndCheck(0);
    }

    [Fact]
    public void Default_KeepsUnusedFunctionsUnderOptRef()
    {
        // Without -ffunction-sections every function shares one .text COMDAT, so
        // /OPT:REF cannot strip them — the probe still finds both frag_* functions.
        Compile(UnusedFunctions)
            .AddAsm2ObjAssembly("ReflectionProbe.dll")
            .MsvcLink(["/entry:main", "/subsystem:console", "/OPT:REF"])
            .RunAndCheck(2);
    }

    [Fact]
    public void DataSections_LetOptRefDeadStripUnusedGlobals()
    {
        // Each initialized global is its own COMDAT, so /OPT:REF removes the two
        // unreferenced frag_data globals: the probe finds none of them.
        Compile(UnusedGlobals, ["-fdata-sections"])
            .AddAsm2ObjAssembly("ReflectionProbe.dll")
            .MsvcLink(["/entry:main", "/subsystem:console", "/OPT:REF"])
            .RunAndCheck(0);
    }

    [Fact]
    public void Default_KeepsUnusedGlobalsUnderOptRef()
    {
        // Without -fdata-sections the globals share one merged .data section, so
        // /OPT:REF cannot strip them — the probe still finds both frag_data globals.
        Compile(UnusedGlobals)
            .AddAsm2ObjAssembly("ReflectionProbe.dll")
            .MsvcLink(["/entry:main", "/subsystem:console", "/OPT:REF"])
            .RunAndCheck(2);
    }
}
