// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC literal.c
// LINK: link literal.obj /incremental:no /debug /entry:main /subsystem:console

int main()
{
    char* c = "Hello";
    char* d = "World!";
    return c[0] + d[0];
}
