// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC pinvoke-forwardref.c
// LINK: link pinvoke-forwardref.obj mscoree.lib user32.lib /incremental:no /debug /entry:main /subsystem:console

struct Mine;

int __stdcall MessageBoxW(struct Mine* a, void* b, void* c, int d);

int main()
{
    return MessageBoxW(0, 0, 0, 0);
}
