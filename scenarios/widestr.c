// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC widestr.c
// LINK: link widestr.obj /incremental:no /debug /entry:main /subsystem:console

int main()
{
    unsigned short* w = L"Hi";
    return w[0] + w[1];
}
