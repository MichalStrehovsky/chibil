// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC compare.c
// LINK: link compare.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int compare(int a, int b)
{
    int eq = (a == b);
    int ne = (a != b);
    int lt = (a < b);
    int le = (a <= b);
    int gt = (a > b);
    int ge = (a >= b);
    return eq + ne + lt + le + gt + ge;
}

int main()
{
    return compare(10, 20);
}
