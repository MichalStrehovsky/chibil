// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC const-param.c
// LINK: link const-param.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int sum_array(const int* arr, int len)
{
    int sum = 0;
    int i;
    for (i = 0; i < len; i = i + 1)
        sum = sum + arr[i];
    return sum;
}

int read_volatile(volatile int* p)
{
    return *p;
}

int main()
{
    int arr[3] = { 10, 20, 30 };
    volatile int v = 42;
    return sum_array(arr, 3) + read_volatile(&v);
}
