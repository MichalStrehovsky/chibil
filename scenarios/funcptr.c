// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC funcptr.c
// LINK: link funcptr.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int add(int a, int b) { return a + b; }
int sub_fn(int a, int b) { return a - b; }

int apply(int (*fn)(int, int), int x, int y)
{
    return fn(x, y);
}

int main()
{
    int (*fp)(int, int);
    fp = add;
    int a = fp(10, 3);
    int b = apply(sub_fn, 10, 3);
    return a + b;
}
