// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC char-types.c
// LINK: link char-types.obj /incremental:no /debug /entry:main /subsystem:console

int char_func(char a, signed char b, unsigned char c)
{
    return a + b + c;
}

int main()
{
    char x = 'A';
    signed char y = -1;
    unsigned char z = 255;
    return char_func(x, y, z);
}
