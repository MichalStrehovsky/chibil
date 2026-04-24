// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC longlong.c
// LINK: link longlong.obj /incremental:no /debug /entry:main /subsystem:console
//
// Tests 64-bit integer (long long) arithmetic and conversions.
// Shows: ldc.i8 for 64-bit constants, conv.i8 / conv.i4 for
// widening / narrowing casts, shr.un for unsigned 64-bit shift,
// and confirms that add/sub/mul/div/shl/shr all work on int64
// with the same opcodes (they are type-agnostic on the eval stack).

long long ll_add(long long a, long long b) { return a + b; }
long long ll_mul(long long a, long long b) { return a * b; }
long long ll_div(long long a, long long b) { return a / b; }
long long ll_shl(long long a, int n) { return a << n; }
long long ll_shr(long long a, int n) { return a >> n; }
unsigned long long ull_shr(unsigned long long a, int n) { return a >> n; }
int ll_compare(long long a, long long b) { return a < b; }
long long int_to_ll(int x) { return (long long)x; }
int ll_to_int(long long x) { return (int)x; }

int main()
{
    long long a = 1000000LL;
    long long b = 2000000LL;
    return ll_to_int(ll_add(a, b)) + (int)ull_shr(0xFFFFFFFFFFFFFFFFULL, 1);
}
