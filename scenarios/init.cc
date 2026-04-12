// First compile minicrt.cc (instructions in the file)
// Compile with: cl /Zl /clr:pure init.cc /link /entry:main /subsystem:console minicrt.obj /include:?.cctor@@$$FYMXXZ

char* str = "Hello!";

int main()
{
    return str[0];
}
