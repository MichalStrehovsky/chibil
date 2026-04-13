// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC pinvoke.c
// LINK: link pinvoke.obj /incremental:no /debug /entry:main /subsystem:console user32.lib

int __stdcall MessageBoxW(void* a, void* b, void* c, int d);

int main()
{
    return MessageBoxW(0, 0, 0, 0);
}
