// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC funcptr-array_clrcall.c
// LINK: link funcptr-array_clrcall.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int __clrcall add(int a, int b) { return a + b; }
int __clrcall sub_fn(int a, int b) { return a - b; }

int __clrcall main()
{
    int (__clrcall *ops[2])(int, int);
    ops[0] = add;
    ops[1] = sub_fn;
    return ops[0](10, 3) + ops[1](10, 3);
}
