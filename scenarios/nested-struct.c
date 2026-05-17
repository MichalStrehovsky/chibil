// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC nested-struct.c
// LINK: link nested-struct.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

struct Outer {
    struct Inner {
        int a;
        int b;
    } inner;
    int z;
};

int main()
{
    struct Outer o;
    o.inner.a = 10;
    o.inner.b = 20;
    o.z = 30;
    return o.inner.a + o.inner.b + o.z;
}
