// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC funcptr-array.c
// LINK: link funcptr-array.obj /incremental:no /debug /entry:main /subsystem:console

int add(int a, int b) { return a + b; }
int sub_fn(int a, int b) { return a - b; }

int main()
{
    int (*ops[2])(int, int);
    ops[0] = add;
    ops[1] = sub_fn;
    return ops[0](10, 3) + ops[1](10, 3);
}
