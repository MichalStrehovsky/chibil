// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC bitwise.c
// LINK: link bitwise.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int bitwise(int a, int b)
{
    int band = a & b;
    int bor = a | b;
    int bxor = a ^ b;
    int bnot = ~a;
    int shl = a << 2;
    int shr = a >> 1;
    return band + bor + bxor + bnot + shl + shr;
}

int main()
{
    return bitwise(0x55, 0x33);
}
