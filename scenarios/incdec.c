// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC incdec.c
// LINK: link incdec.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib
//
// Tests pre/post increment and compound assignment operators.
// Key IL pattern: starg.s instruction to write back to a parameter.
// post_inc:  dup old value, compute new, starg.s, return old
// pre_inc:   compute new, starg.s, return new
// compound:  ldarg, ldarg, op, starg.s (then ldarg for result)
// ptr_post_inc: ldc.i4.4 + add for int* pointer step

int post_inc(int x) { return x++; }
int pre_inc(int x) { return ++x; }
int compound_add(int a, int b) { a += b; return a; }
int compound_sub(int a, int b) { a -= b; return a; }
int compound_mul(int a, int b) { a *= b; return a; }
int compound_shl(int a, int b) { a <<= b; return a; }

void ptr_post_inc(int **pp)
{
    int *p = *pp;
    p++;
    *pp = p;
}

int main()
{
    int x = 5;
    int *p = &x;
    return post_inc(10) + pre_inc(10) + compound_add(3, 4);
}
