using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Behavior tests for the <c>switch</c> IL jump-table lowering, which matches
/// MSVC /clr codegen: a jump table is emitted only when a switch has at least
/// six case labels and the covered value span (max - min + 1) is at most 255.
/// Otherwise a linear comparison chain is used. These tests exercise the
/// runtime behavior across in-range hits, gaps, defaults, and out-of-range
/// (below the base and above the max, including the unsigned-wrap edge).
/// </summary>
public class SwitchJumpTableTests : ChibiTestBase
{
    [Fact]
    public void ContiguousZeroBasedHitsEachCaseAndDefault()
    {
        // 6 contiguous cases starting at 0 -> jump table with no base subtraction.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 0: return 10;
                    case 1: return 11;
                    case 2: return 12;
                    case 3: return 13;
                    case 4: return 14;
                    case 5: return 15;
                }
                return 99;
            }
            int main() {
                if (f(0) != 10) return 1;
                if (f(3) != 13) return 2;
                if (f(5) != 15) return 3;
                if (f(6) != 99) return 4;    // above max -> default
                if (f(-1) != 99) return 5;   // below 0 (unsigned wrap) -> default
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void NonZeroBaseSubtractsBeforeSwitch()
    {
        // 6 contiguous cases starting at 10 -> jump table with (x - 10) base subtraction.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 10: return 1;
                    case 11: return 2;
                    case 12: return 3;
                    case 13: return 4;
                    case 14: return 5;
                    case 15: return 6;
                }
                return 99;
            }
            int main() {
                if (f(10) != 1) return 1;
                if (f(15) != 6) return 2;
                if (f(9) != 99) return 3;    // below base -> default
                if (f(16) != 99) return 4;   // above max -> default
                if (f(0) != 99) return 5;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void NegativeBaseIsHandled()
    {
        // Negative minimum -> base subtraction with a negative constant.
        Compile("""
            int f(int x) {
                switch (x) {
                    case -3: return 1;
                    case -2: return 2;
                    case -1: return 3;
                    case 0: return 4;
                    case 1: return 5;
                    case 2: return 6;
                }
                return 99;
            }
            int main() {
                if (f(-3) != 1) return 1;
                if (f(0) != 4) return 2;
                if (f(2) != 6) return 3;
                if (f(-4) != 99) return 4;   // below min -> default
                if (f(3) != 99) return 5;    // above max -> default
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void GapsInSpanFallToDefault()
    {
        // 6 labels spread across a span <= 255 -> jump table whose gap slots
        // target the default.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 0: return 1;
                    case 3: return 2;
                    case 6: return 3;
                    case 9: return 4;
                    case 12: return 5;
                    case 15: return 6;
                    default: return 99;
                }
            }
            int main() {
                if (f(0) != 1) return 1;
                if (f(6) != 3) return 2;
                if (f(15) != 6) return 3;
                if (f(1) != 99) return 4;    // gap -> default
                if (f(14) != 99) return 5;   // gap -> default
                if (f(16) != 99) return 6;   // out of range -> default
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void FallThroughBetweenCasesWorks()
    {
        // Cases without break must fall through into the following case bodies.
        Compile("""
            int f(int x) {
                int r = 0;
                switch (x) {
                    case 0: r += 1;
                    case 1: r += 2;
                    case 2: r += 4;
                    case 3: r += 8;
                    case 4: r += 16;
                    case 5: r += 32;
                }
                return r;
            }
            int main() {
                if (f(0) != 63) return 1;    // 1+2+4+8+16+32
                if (f(3) != 56) return 2;    // 8+16+32
                if (f(5) != 32) return 3;
                if (f(6) != 0) return 4;     // no match, no default
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LongLongSwitchUsesJumpTable()
    {
        // 64-bit switch value -> base subtraction as i8, then switch.
        Compile("""
            long long f(long long x) {
                switch (x) {
                    case 100: return 1;
                    case 101: return 2;
                    case 102: return 3;
                    case 103: return 4;
                    case 104: return 5;
                    case 105: return 6;
                }
                return 99;
            }
            int main() {
                if (f(100LL) != 1) return 1;
                if (f(105LL) != 6) return 2;
                if (f(99LL) != 99) return 3;
                if (f(106LL) != 99) return 4;
                if (f(0x100000000LL) != 99) return 5;  // large 64-bit -> default
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void CaseRangeExpandsIntoTable()
    {
        // GNU case ranges expand into contiguous table slots. Six labels here,
        // spanning [0, 9], so a jump table is used.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 0 ... 2: return 1;
                    case 3: return 2;
                    case 4: return 3;
                    case 5: return 4;
                    case 6: return 5;
                    case 7 ... 9: return 6;
                }
                return 99;
            }
            int main() {
                if (f(0) != 1) return 1;
                if (f(2) != 1) return 2;
                if (f(3) != 2) return 3;
                if (f(9) != 6) return 4;
                if (f(10) != 99) return 5;
                if (f(-1) != 99) return 6;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void FiveCasesUseComparisonChain()
    {
        // Fewer than six labels -> comparison chain (no jump table); must still
        // produce correct results.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 0: return 10;
                    case 1: return 11;
                    case 2: return 12;
                    case 3: return 13;
                    case 4: return 14;
                }
                return 99;
            }
            int main() {
                if (f(0) != 10) return 1;
                if (f(4) != 14) return 2;
                if (f(5) != 99) return 3;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void WideSpanUsesComparisonChain()
    {
        // Six labels but span > 255 -> comparison chain (no jump table); must
        // still produce correct results.
        Compile("""
            int f(int x) {
                switch (x) {
                    case 0: return 1;
                    case 100: return 2;
                    case 200: return 3;
                    case 300: return 4;
                    case 400: return 5;
                    case 500: return 6;
                }
                return 99;
            }
            int main() {
                if (f(0) != 1) return 1;
                if (f(300) != 4) return 2;
                if (f(500) != 6) return 3;
                if (f(250) != 99) return 4;
                if (f(501) != 99) return 5;
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }
}
