// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC flexible-array.c
// LINK: link flexible-array.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

typedef struct _FlexBuf {
    int len;
    int data[];
} FlexBuf;

int sum_flex(FlexBuf* buf)
{
    int sum = 0;
    int i;
    for (i = 0; i < buf->len; i = i + 1)
        sum = sum + buf->data[i];
    return sum;
}

int main()
{
    return 0;
}
