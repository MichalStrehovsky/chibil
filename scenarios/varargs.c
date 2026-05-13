// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC varargs.c
// LINK: link varargs.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int __cdecl sum(int count, ...);

int main()
{
    return sum(3, 10, 20, 30);
}
