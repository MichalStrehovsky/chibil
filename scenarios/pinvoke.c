// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC pinvoke.c
// LINK: link pinvoke.obj mscoree.lib user32.lib /incremental:no /debug /entry:main /subsystem:console

int __stdcall MessageBoxW(void* a, void* b, void* c, int d);

int main()
{
    return MessageBoxW(0, 0, 0, 0);
}
