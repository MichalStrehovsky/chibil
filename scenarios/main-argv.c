// COMPILE: cl /c /Z7 /Zl /clr:pure /TP main-argv.c
// LINK: link main-argv.obj /incremental:no /debug /subsystem:console minicrt.obj

// The compiler generates a `extern "C" int __CxxPureMSILEntry(int argc, char** argv, char** envp)` function
// automatically. The signature is always this. The function body will call user `main` depending on what
// signature the user specified. Unused parameters are dropped, so if user only specified `int main()` or
// `void main()`, the body of `__CxxPureMSILEntry` will adapt to it. This only happens when compiling as C++. C frontend of MSVC will not create the shim and we therefore can't have main with args!

int main(int argc, char** argv)
{
    if (argc > 1)
        return argv[1][0];
    return 0;
}
