// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC negconst.c
// LINK: link negconst.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib
//
// Tests how MSVC loads various constant values into the IL eval stack.
// Shows every ldc.i4 variant: ldc.i4.m1 (-1), ldc.i4.0..8 (small),
// ldc.i4.s for int8 range (-128..127), ldc.i4 for full int32, and
// ldc.i8 for int64 constants.  Also shows UINT_MAX encoded as ldc.i4.m1
// (same bit pattern 0xFFFFFFFF = -1 in two's complement).

int neg_one(void) { return -1; }
int int_min(void) { return -2147483647 - 1; }
unsigned int uint_max(void) { return 4294967295U; }
long long ll_max(void) { return 9223372036854775807LL; }
long long ll_min(void) { return -9223372036854775807LL - 1; }
int small_neg(void) { return -42; }
int zero(void) { return 0; }
int small_pos(void) { return 8; }
int medium_pos(void) { return 127; }
int large_pos(void) { return 1000; }

int main()
{
    return neg_one() + int_min() + (int)uint_max() + small_neg()
         + zero() + small_pos() + medium_pos() + large_pos();
}
