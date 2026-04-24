// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC ptrsub.c
// LINK: link ptrsub.obj /incremental:no /debug /entry:main /subsystem:console
//
// Tests pointer subtraction and pointer comparison.
// Pointer subtraction uses:  sub + shr (by log2(sizeof(element)))
//   int*:    sub, ldc.i4.2, shr        (divide by 4)
//   double*: sub, ldc.i4.3, shr        (divide by 8)
//   char*:   sub                        (no shift needed, size 1)
// Pointer comparison uses unsigned branches: bge.un.s, bne.un.s

int ptr_subtract_int(int *p, int *q) { return (int)(p - q); }
int ptr_subtract_char(char *p, char *q) { return (int)(p - q); }
long long ptr_subtract_double(double *p, double *q) { return (long long)(p - q); }
int ptr_less(int *p, int *q) { return p < q; }
int ptr_equal(int *p, int *q) { return p == q; }

int main()
{
    int arr[4];
    return ptr_subtract_int(&arr[3], &arr[0])
         + ptr_less(&arr[0], &arr[3])
         + ptr_equal(&arr[1], &arr[1]);
}
