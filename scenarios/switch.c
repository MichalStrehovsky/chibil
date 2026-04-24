// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC switch.c
// LINK: link switch.obj /incremental:no /debug /entry:main /subsystem:console

int classify(int x)
{
    int result;
    switch (x)
    {
        case 0:
            result = 10;
            break;
        case 1:
            result = 20;
            break;
        case 2:
        case 3:
            result = 30;
            break;
        default:
            result = -1;
            break;
    }
    return result;
}

int main()
{
    return classify(0) + classify(1) + classify(2) + classify(5);
}
