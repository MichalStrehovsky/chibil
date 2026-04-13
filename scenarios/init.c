// COMPILE: cl /c /Z7 /TP /Zl /clr:pure init.c
// LINK: link init.obj /incremental:no /debug /entry:main /subsystem:console minicrt.obj /include:?.cctor@@$$FYMXXZ
// NOTE: we pass /TP instead of /BC because the MSVC++ compiler cannot compile the below C code as C with /clr:pure. It is however valid C and we expect a C compiler to compile it.

char* str = "Hello!";

int main()
{
    return str[0];
}
