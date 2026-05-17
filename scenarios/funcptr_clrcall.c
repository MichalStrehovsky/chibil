// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC funcptr_clrcall.c
// LINK: link funcptr_clrcall.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int __clrcall add(int a, int b) { return a + b; }
int __clrcall sub_fn(int a, int b) { return a - b; }

int __clrcall apply(int (__clrcall *fn)(int, int), int x, int y)
{
    return fn(x, y);
}

int __clrcall main()
{
    int (__clrcall *fp)(int, int);
    fp = add;
    int a = fp(10, 3);
    int b = apply(sub_fn, 10, 3);
    return a + b;
}
