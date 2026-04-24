// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC union.c
// LINK: link union.obj /incremental:no /debug /entry:main /subsystem:console

typedef union _Number
{
    int i;
    float f;
} Number;

int union_test(void)
{
    Number n;
    n.i = 0x41200000;
    float f = n.f;
    Number m;
    m.f = 3.14f;
    int i = m.i;
    return i + (int)f;
}

int main()
{
    return union_test();
}
