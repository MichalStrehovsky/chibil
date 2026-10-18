using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Behavioral coverage for the condition-context lowering (GenCondBranch), which
/// emits direct conditional branches (blt/ble/bge/bgt/beq/bne.un and .un variants)
/// instead of materializing a 0/1 boolean and branching on it. These tests pin down
/// correctness across signed/unsigned/float comparisons, pointer truth tests,
/// short-circuiting logical operators, ternaries, and negation — a wrong branch
/// opcode (signedness or ordered-vs-unordered) produces a non-zero exit code.
/// </summary>
public sealed class ConditionBranchTests : ChibiTestBase
{
    private static readonly string[] ConsoleMain = ["/entry:main", "/subsystem:console"];

    [Fact]
    public void ReducedCasesFromRequest()
    {
        Compile("""
            int f(int a, int b) { if (a < b) return a; return b; }
            int g(char *p) { int n = 0; while (*p) { p++; n++; } return n; }
            int main(void) {
                if (f(3, 7) != 3) return 1;
                if (f(7, 3) != 3) return 2;
                if (g("hello") != 5) return 3;
                if (g("") != 0) return 4;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void SignedIntegerComparisons()
    {
        Compile("""
            int t_lt(int a, int b) { if (a <  b) return 1; return 0; }
            int t_le(int a, int b) { if (a <= b) return 1; return 0; }
            int t_gt(int a, int b) { if (a >  b) return 1; return 0; }
            int t_ge(int a, int b) { if (a >= b) return 1; return 0; }
            int t_eq(int a, int b) { if (a == b) return 1; return 0; }
            int t_ne(int a, int b) { if (a != b) return 1; return 0; }
            int main(void) {
                if (t_lt(3, 7) != 1) return 1;
                if (t_lt(7, 3) != 0) return 2;
                if (t_lt(3, 3) != 0) return 3;
                if (t_le(3, 7) != 1) return 4;
                if (t_le(7, 3) != 0) return 5;
                if (t_le(3, 3) != 1) return 6;
                if (t_gt(7, 3) != 1) return 7;
                if (t_gt(3, 7) != 0) return 8;
                if (t_gt(3, 3) != 0) return 9;
                if (t_ge(7, 3) != 1) return 10;
                if (t_ge(3, 7) != 0) return 11;
                if (t_ge(3, 3) != 1) return 12;
                if (t_eq(3, 3) != 1) return 13;
                if (t_eq(3, 7) != 0) return 14;
                if (t_ne(3, 7) != 1) return 15;
                if (t_ne(3, 3) != 0) return 16;
                /* negatives must use signed branches */
                if (t_lt(-1, 1) != 1) return 17;
                if (t_lt(1, -1) != 0) return 18;
                if (t_le(-5, -5) != 1) return 19;
                if (t_gt(-1, -2) != 1) return 20;
                if (t_ge(-2, -1) != 0) return 21;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void UnsignedIntegerComparisons()
    {
        Compile("""
            int u_lt(unsigned a, unsigned b) { if (a <  b) return 1; return 0; }
            int u_le(unsigned a, unsigned b) { if (a <= b) return 1; return 0; }
            int u_gt(unsigned a, unsigned b) { if (a >  b) return 1; return 0; }
            int u_ge(unsigned a, unsigned b) { if (a >= b) return 1; return 0; }
            int main(void) {
                /* 0xFFFFFFFF is the largest unsigned, not -1 */
                if (u_lt(0xFFFFFFFFu, 1u) != 0) return 1;
                if (u_gt(0xFFFFFFFFu, 1u) != 1) return 2;
                if (u_le(1u, 0xFFFFFFFFu) != 1) return 3;
                if (u_ge(0xFFFFFFFFu, 0xFFFFFFFFu) != 1) return 4;
                if (u_lt(2u, 3u) != 1) return 5;
                if (u_le(3u, 3u) != 1) return 6;
                if (u_gt(2u, 3u) != 0) return 7;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void FloatComparisonsIncludingNaN()
    {
        Compile("""
            double zero(void) { return 0.0; }
            int f_lt(double a, double b) { if (a <  b) return 1; return 0; }
            int f_le(double a, double b) { if (a <= b) return 1; return 0; }
            int f_gt(double a, double b) { if (a >  b) return 1; return 0; }
            int f_ge(double a, double b) { if (a >= b) return 1; return 0; }
            int f_eq(double a, double b) { if (a == b) return 1; return 0; }
            int f_ne(double a, double b) { if (a != b) return 1; return 0; }
            int main(void) {
                double n = zero();
                n = n / zero();              /* 0.0 / 0.0 == NaN */
                /* ordered cases */
                if (f_lt(1.0, 2.0) != 1) return 1;
                if (f_lt(2.0, 1.0) != 0) return 2;
                if (f_le(2.0, 2.0) != 1) return 3;
                if (f_gt(2.0, 1.0) != 1) return 4;
                if (f_ge(2.0, 2.0) != 1) return 5;
                if (f_eq(2.0, 2.0) != 1) return 6;
                if (f_ne(2.0, 1.0) != 1) return 7;
                /* NaN: every ordered comparison is false, only != is true */
                if (f_lt(n, 1.0) != 0) return 8;
                if (f_le(n, 1.0) != 0) return 9;
                if (f_gt(n, 1.0) != 0) return 10;
                if (f_ge(n, 1.0) != 0) return 11;
                if (f_eq(n, n) != 0) return 12;
                if (f_ne(n, n) != 1) return 13;
                if (f_lt(1.0, n) != 0) return 14;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void PointerAndNullTruthTests()
    {
        Compile("""
            int is_set(int *p) { if (p) return 1; return 0; }
            int is_null(int *p) { if (!p) return 1; return 0; }
            int count_nonzero(int *p, int n) {
                int c = 0; int i = 0;
                while (i < n) { if (p[i]) c++; i++; }
                return c;
            }
            int main(void) {
                int x = 5;
                if (is_set(&x) != 1) return 1;
                if (is_set(0) != 0) return 2;
                if (is_null(0) != 1) return 3;
                if (is_null(&x) != 0) return 4;
                int arr[3];
                arr[0] = 0; arr[1] = 7; arr[2] = 0;
                if (count_nonzero(arr, 3) != 1) return 5;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LogicalOperatorsShortCircuit()
    {
        Compile("""
            int calls;
            int side(int v) { calls++; return v; }
            int main(void) {
                calls = 0;
                if (side(0) && side(1)) return 1;   /* false; rhs skipped */
                if (calls != 1) return 2;
                calls = 0;
                if (!(side(1) && side(1))) return 3; /* both eval, result true */
                if (calls != 2) return 4;
                calls = 0;
                if (side(1) || side(1)) { } else return 5; /* true; rhs skipped */
                if (calls != 1) return 6;
                calls = 0;
                if (side(0) || side(0)) return 7;   /* false; both eval */
                if (calls != 2) return 8;
                return 0;
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LogicalOperatorsMaterializeBooleanAndShortCircuit()
    {
        Compile("""
            int calls;
            int side(int v) { calls++; return v; }
            int main(void) {
                int r;
                calls = 0;
                r = side(0) && side(1);
                if (r != 0) return 1;
                if (calls != 1) return 2;
                calls = 0;
                r = side(2) && side(3);
                if (r != 1) return 3;
                if (calls != 2) return 4;
                calls = 0;
                r = side(4) || side(0);
                if (r != 1) return 5;
                if (calls != 1) return 6;
                calls = 0;
                r = side(0) || side(0);
                if (r != 0) return 7;
                if (calls != 2) return 8;
                return 0;
            }
            """)
        .MsvcLink(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void TernaryWithComparisonCondition()
    {
        Compile("""
            int mn(int a, int b) { return a < b ? a : b; }
            int mx(int a, int b) { return a < b ? b : a; }
            int main(void) {
                if (mn(3, 7) != 3) return 1;
                if (mn(7, 3) != 3) return 2;
                if (mx(3, 7) != 7) return 3;
                if (mx(7, 3) != 7) return 4;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void NegatedComparison()
    {
        Compile("""
            int t(int a, int b) { if (!(a < b)) return 100; return 200; }
            int main(void) {
                if (t(3, 7) != 200) return 1;   /* 3<7 true  -> !true  -> 200 */
                if (t(7, 3) != 100) return 2;   /* 7<3 false -> !false -> 100 */
                if (t(3, 3) != 100) return 3;   /* 3<3 false -> !false -> 100 */
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LongLongComparisons()
    {
        Compile("""
            int l_lt(long long a, long long b) { if (a <  b) return 1; return 0; }
            int l_ge(long long a, long long b) { if (a >= b) return 1; return 0; }
            int main(void) {
                if (l_lt(3LL, 7LL) != 1) return 1;
                if (l_lt(7LL, 3LL) != 0) return 2;
                if (l_lt(-1LL, 1LL) != 1) return 3;
                long long big = 0x100000000LL;        /* 2^32, needs 64-bit compare */
                if (l_lt(big, big + 1) != 1) return 4;
                if (l_lt(big + 1, big) != 0) return 5;
                if (l_ge(big, big) != 1) return 6;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LogicalAndInLoopCondition()
    {
        Compile("""
            int main(void) {
                int i = 0; int sum = 0;
                while (i < 10 && sum < 100) { sum += i; i++; }
                if (i != 10) return 1;
                if (sum != 45) return 2;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void FloatComparisonInDoWhileConditionWithNaN()
    {
        // do/while branches when the condition is TRUE, exercising the
        // branchIfTrue float comparison path (ordered Blt) with a NaN operand:
        // NaN < x is false, so the loop must terminate after one iteration.
        Compile("""
            double zero(void) { return 0.0; }
            int main(void) {
                double n = zero();
                n = n / zero();           /* NaN */
                int iters = 0;
                do { iters++; } while (n < 1.0);
                if (iters != 1) return 1;
                /* ordered values: loop until counter reaches the bound */
                double c = 0.0;
                iters = 0;
                do { iters++; c = c + 1.0; } while (c < 3.0);
                if (iters != 3) return 2;
                return 0;
            }
            """)
        .Link(ConsoleMain)
        .RunAndCheck(exitCode: 0);
    }
}
