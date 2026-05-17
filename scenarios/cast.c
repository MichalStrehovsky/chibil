// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC cast.c
// LINK: link cast.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int cast_widen(char c, short s)
{
    int i = c;
    long long ll = s;
    return (int)(i + ll);
}

int cast_narrow(int i)
{
    char c = (char)i;
    short s = (short)i;
    return c + s;
}

int cast_unsigned(unsigned int u)
{
    int s = (int)u;
    unsigned long long ull = u;
    return s + (int)ull;
}

int cast_float(int i, float f, double d)
{
    float fi = (float)i;
    double di = (double)i;
    int fromf = (int)f;
    int fromd = (int)d;
    float df = (float)d;
    double fd = (double)f;
    return fromf + fromd + (int)fi + (int)di + (int)df + (int)fd;
}

int cast_bool(int x)
{
    _Bool b = x;
    return b;
}

int main()
{
    return cast_widen('A', 100) + cast_narrow(0x12345) + cast_unsigned(42u) + cast_float(10, 3.5f, 7.25) + cast_bool(42);
}
