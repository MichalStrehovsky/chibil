// COMPILE: cl /c /Z7 /Zl /clr:pure /TP global.c
// LINK: link global.obj /incremental:no /debug /entry:main /subsystem:console minicrt.obj /include:?.cctor@@$$FYMXXZ
// NOTE: we pass /TP instead of /BC because the MSVC++ compiler rejects global initializers in /BC /clr:pure mode (error C2099).

int g_initialized = 42;
int g_uninitialized;
int g_array[4] = { 1, 2, 3, 4 };

int main()
{
    g_uninitialized = 10;
    int sum = g_initialized + g_uninitialized;
    int i;
    for (i = 0; i < 4; i = i + 1)
        sum = sum + g_array[i];
    return sum;
}
