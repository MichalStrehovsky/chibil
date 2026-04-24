// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC voidptr.c
// LINK: link voidptr.obj /incremental:no /debug /entry:main /subsystem:console
//
// Tests void pointer patterns.  void* in MSIL signatures encodes as
// Ptr Void (ELEMENT_TYPE_PTR ELEMENT_TYPE_VOID).  Casting void* to
// a typed pointer and dereferencing generates ldind/stind without any
// explicit conversion instruction.  Byte-level memory copy uses
// ldind.i1 / stind.i1 (for char*).

void* identity(void *p) { return p; }
int deref_via_cast(void *p) { return *(int*)p; }
void write_via_cast(void *p, int val) { *(int*)p = val; }

void copy_bytes(void *dst, void *src, int n)
{
    char *d = (char*)dst;
    char *s = (char*)src;
    int i;
    for (i = 0; i < n; i++) {
        d[i] = s[i];
    }
}

int main()
{
    int x = 42;
    int y = 0;
    write_via_cast(&y, deref_via_cast(&x));
    return y;
}
