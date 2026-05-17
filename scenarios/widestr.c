// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC widestr.c
// LINK: link widestr.obj mscoree.lib /incremental:no /debug /entry:main /subsystem:console

int main()
{
    unsigned short* w = L"Hi";
    return w[0] + w[1];
}
