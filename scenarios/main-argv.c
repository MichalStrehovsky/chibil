// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC main-argv.c
// LINK: link main-argv.obj /incremental:no /debug /entry:main /subsystem:console

int main(int argc, char** argv)
{
    if (argc > 1)
        return argv[1][0];
    return 0;
}
