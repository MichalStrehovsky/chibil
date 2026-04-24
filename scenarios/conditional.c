// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC conditional.c
// LINK: link conditional.obj /incremental:no /debug /entry:main /subsystem:console

int abs_val(int x)
{
    return x >= 0 ? x : -x;
}

int comma_test(int a, int b)
{
    int x;
    x = (a = a + 1, b = b + 2, a + b);
    return x;
}

int main()
{
    return abs_val(-5) + comma_test(10, 20);
}
