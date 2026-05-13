// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC alloca.c
// LINK: link alloca.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

void* _alloca(unsigned int);

int sum_dynamic(int n)
{
    int* arr = (int*)_alloca(n * 4);
    int i;
    for (i = 0; i < n; i = i + 1)
        arr[i] = i + 1;
    int sum = 0;
    for (i = 0; i < n; i = i + 1)
        sum = sum + arr[i];
    return sum;
}

int main()
{
    return sum_dynamic(5);
}
