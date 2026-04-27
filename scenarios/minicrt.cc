// Compile with: cl /c /Z7 /Zl /clr:pure minicrt.cc

// Allow __identifier
#pragma warning(disable:4483)


// A functional module initializer that works for link.exe for testing
 
using namespace System;
 
typedef void const* (__clrcall* _PVFVM)(void);
 
#pragma const_seg(".CRTMA$XCA")
__declspec(process) const __declspec(allocate(".CRTMA$XCA"))
    _PVFVM __xc_ma_a[] = { nullptr };
 
#pragma const_seg(".CRTMA$XCZ")
__declspec(process) const __declspec(allocate(".CRTMA$XCZ"))
    _PVFVM __xc_ma_z[] = { nullptr };
 
#pragma const_seg()
#pragma comment(linker, "/merge:.CRTMA=.rdata")
 
ref class ModuleToken {};
 
[System::Diagnostics::DebuggerStepThroughAttribute]
[System::Security::SecurityCriticalAttribute]
void __clrcall __identifier(".cctor")()
{
    auto h = ModuleToken::typeid->Module->ModuleHandle;

    for (const _PVFVM* p = __xc_ma_a; p < __xc_ma_z; ++p)
    {
        if (*p != nullptr)
        {
            auto pfn = (_PVFVM)h.ResolveMethodHandle((int)(size_t)*p)
                                 .GetFunctionPointer()
                                 .ToPointer();
            pfn();
        }
    }
}

extern "C" int __CxxPureMSILEntry(int, char**, char**);

char arg0[] = "NotImplemented";
char* argv[] = { arg0, nullptr };
char* envp[] = { nullptr };

int __clrcall mainCRTStartup(cli::array<System::String^>^)
{
    return __CxxPureMSILEntry(1, argv, envp);
}
