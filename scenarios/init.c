// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC init.c
// LINK: link init.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

char* str = "Hello!";

int main()
{
    return str[0];
}
