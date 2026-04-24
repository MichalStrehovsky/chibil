// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC varargs.c
// LINK: link varargs.obj /incremental:no /debug /entry:main /subsystem:console

int __cdecl sum(int count, ...);

int main()
{
    return sum(3, 10, 20, 30);
}
