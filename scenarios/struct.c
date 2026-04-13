// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC struct.c
// LINK: link struct.obj /incremental:no /debug /entry:main /subsystem:console


typedef struct _MyStruct
{
    int x, y, z;
} MyStruct;

int sum_struct(MyStruct* pS)
{
    return pS->x + pS->y + pS->z;
}

int main()
{
    int s = 0;

    {
        MyStruct m = { 10, 20, 30 };
        int i = sum_struct(&m);
        s += i;
    }

    {
        MyStruct m = { 20, 30, 40 };
        int j = sum_struct(&m);
        s += j;
    }

    return s;
}
