// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC logic.c
// LINK: link logic.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int logic(int a, int b)
{
    int land = a && b;
    int lor = a || b;
    int lnot = !a;
    return land + lor + lnot;
}

int main()
{
    return logic(1, 0);
}
