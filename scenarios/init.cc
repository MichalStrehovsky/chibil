// Compile with: cl /Zl /clr:pure init.cc /link /entry:main /subsystem:console

char* str = "Hello!";

int main()
{
    return str[0];
}

// Below is some C++ that will be in a standard library (or a linker feature)
// in the future. We don't need to concern ourselves with it in a C compiler.

// Allow __identifier
#pragma warning(disable:4483)

#if 1

// Very simplified module initializer, just enough so we can link this.
void __clrcall __identifier(".cctor")()
{
}


#else

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

#endif