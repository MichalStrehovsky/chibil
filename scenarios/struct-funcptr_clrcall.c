// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC struct-funcptr_clrcall.c
// LINK: link struct-funcptr_clrcall.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

typedef struct _Handler {
    int (__clrcall *callback)(int);
    int value;
} Handler;

int __clrcall double_it(int x) { return x * 2; }

int __clrcall invoke(Handler* h)
{
    return h->callback(h->value);
}

int __clrcall main()
{
    Handler h;
    h.callback = double_it;
    h.value = 21;
    return invoke(&h);
}
