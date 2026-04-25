// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC struct-funcptr.c
// LINK: link struct-funcptr.obj /incremental:no /debug /entry:main /subsystem:console

typedef struct _Handler {
    int (*callback)(int);
    int value;
} Handler;

int double_it(int x) { return x * 2; }

int invoke(Handler* h)
{
    return h->callback(h->value);
}

int main()
{
    Handler h;
    h.callback = double_it;
    h.value = 21;
    return invoke(&h);
}
