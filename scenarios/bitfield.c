// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC bitfield.c
// LINK: link bitfield.obj /incremental:no /debug /entry:main /subsystem:console

typedef struct _Flags
{
    unsigned int a : 3;
    unsigned int b : 5;
    unsigned int c : 8;
    unsigned int d : 16;
} Flags;

int bitfield_test(void)
{
    Flags f;
    f.a = 5;
    f.b = 17;
    f.c = 200;
    f.d = 1000;
    return f.a + f.b + f.c + f.d;
}

int main()
{
    return bitfield_test();
}
