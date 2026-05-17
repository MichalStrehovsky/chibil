// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC static-local.c
// LINK: link static-local.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int counter(void)
{
    static int count;
    count = count + 1;
    return count;
}

int main()
{
    counter();
    counter();
    return counter();
}
