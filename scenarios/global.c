// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC global.c
// LINK: link global.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

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
