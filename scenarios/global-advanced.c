// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC global-advanced.c
// LINK: link global-advanced.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

char hello[] = "Hello!";
char* e = &hello[1];

int get()
{
    return 42;
}

int (*m)() = &get;

int main()
{
    return m() + hello[0] + *e;
}
