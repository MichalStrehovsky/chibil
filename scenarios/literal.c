// Compile with: cl /Zl /d1clrNoPureCRT /clr:pure /BC literal.c /link /entry:main /subsystem:console

int main()
{
    char* c = "Hello";
    return c[0];
}
