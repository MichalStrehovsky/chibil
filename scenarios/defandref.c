// COMPILE: cl /DBUILD_DEFINITION /c /Z7 /Zl /d1clrNoPureCRT /clr /BC defandref.c /Fodef.obj
// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC defandref.c /Foref.obj
// LINK: link def.obj ref.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int arith(int a, int b);

#ifdef BUILD_DEFINITION
int arith(int a, int b)
{
    return a + b;
}
#else
int main()
{
    return arith(10, 3);
}
#endif
