// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC arith.c
// LINK: link arith.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int arith(int a, int b)
{
    int sum = a + b;
    int diff = a - b;
    int prod = a * b;
    int quot = a / b;
    int rem = a % b;
    int neg = -a;
    return sum + diff + prod + quot + rem + neg;
}

int main()
{
    return arith(10, 3);
}
