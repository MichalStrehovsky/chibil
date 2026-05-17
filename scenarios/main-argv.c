// COMPILE: cl /c /Z7 /Zl /clr:pure /TP main-argv.c
// LINK: link main-argv.obj /incremental:no /debug /subsystem:console minicrt.obj
// NOTE: this scenario intentionally stays on /clr:pure /TP, unlike our other /clr
// scenarios. The MSVC C++ frontend (/TP) under /clr:pure auto-generates an
// `extern "C" int __CxxPureMSILEntry(int argc, char** argv, char** envp)` shim
// whose body adapts to whatever main signature the user wrote (dropping unused
// argc/argv/envp for `int main()` / `void main()`). chibil is expected to handle
// entry points the same way — emitting that shim — even when main takes args.
// The MSVC C frontend (/BC) never generates the shim, and under /clr the runtime
// can't deliver argc/argv to a managed main without it, so this scenario keeps
// the /clr:pure entry path on purpose.

int main(int argc, char** argv)
{
    if (argc > 1)
        return argv[1][0];
    return 0;
}
