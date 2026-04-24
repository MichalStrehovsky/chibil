// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC local-array.c
// LINK: link local-array.obj /incremental:no /debug /entry:main /subsystem:console

int array_sum(int* arr, int len)
{
    int sum = 0;
    int i;
    for (i = 0; i < len; i = i + 1)
        sum = sum + arr[i];
    return sum;
}

int main()
{
    int arr[5] = { 10, 20, 30, 40, 50 };
    int sum = array_sum(arr, 5);
    int* p = arr + 2;
    int val = *p;
    return sum + val;
}
