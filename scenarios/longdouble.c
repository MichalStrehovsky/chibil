// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC longdouble.c
// LINK: link longdouble.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

long double ld_add(long double a, long double b)
{
    return a + b;
}

int main()
{
    long double x = 3.14L;
    long double y = 2.0L;
    long double z = ld_add(x, y);
    return (int)z;
}
