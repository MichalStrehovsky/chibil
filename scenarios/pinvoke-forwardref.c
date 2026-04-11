// Compile with: cl /Zl /d1clrNoPureCRT /clr:pure /BC pinvoke-forwardref.c /link /entry:main /subsystem:console user32.lib

struct Mine;

int __stdcall MessageBoxW(struct Mine* a, void* b, void* c, int d);

int main()
{
    return MessageBoxW(0, 0, 0, 0);
}
