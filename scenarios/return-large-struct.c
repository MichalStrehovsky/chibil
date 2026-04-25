// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC return-large-struct.c
// LINK: link return-large-struct.obj /incremental:no /debug /entry:main /subsystem:console

typedef struct _Large {
    int a, b, c, d, e;
} Large;

Large make_large(int v)
{
    Large s;
    s.a = v;
    s.b = v + 1;
    s.c = v + 2;
    s.d = v + 3;
    s.e = v + 4;
    return s;
}

int main()
{
    Large s = make_large(10);
    return s.a + s.b + s.c + s.d + s.e;
}
