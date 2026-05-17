// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC long-mod.c
// LINK: link long-mod.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

long add_long(long a, long b)
{
    return a + b;
}

unsigned long add_ulong(unsigned long a, unsigned long b)
{
    return a + b;
}

int main()
{
    long x = add_long(10, 20);
    unsigned long y = add_ulong(100, 200);
    return (int)(x + y);
}
