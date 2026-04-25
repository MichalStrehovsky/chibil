// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC enum.c
// LINK: link enum.obj /incremental:no /debug /entry:main /subsystem:console

enum Color { RED, GREEN = 5, BLUE };

int use_enum(enum Color c)
{
    return c + 1;
}

int main()
{
    enum Color c = GREEN;
    return use_enum(c);
}
