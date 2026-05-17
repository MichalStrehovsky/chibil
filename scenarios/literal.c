// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC literal.c
// LINK: link literal.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int main()
{
    char* c = "Hello";
    char* d = "World!";
    return c[0] + d[0];
}
