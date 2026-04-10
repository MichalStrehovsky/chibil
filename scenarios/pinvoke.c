// Compile with: cl /Zl /d1clrNoPureCRT /clr:pure /BC pinvoke.c /link /entry:main /subsystem:console user32.lib

int __stdcall MessageBoxW(void* a, void* b, void* c, int d);

int main()
{
    return MessageBoxW(0, 0, 0, 0);
}
