Rename:

- how about chibil


Issues:
- is long double implemented correctly?
- Static initialization order fiasco possible
  * File format does have provisions for relocs in data, including vtfixup, however this is likely not implemented on non-Windows in the CLR. Also native AOT doesn't support it.
  * If we keep initializers, might need our own linker so we can generate a sane module constructor instead of something that needs to resolve tokens at runtime (no native AOT is possible as long as this is there)
- Distinguish native vs managed function pointers
- Varargs
- Setjmp/longjmp should be possible to implement via throw/catch
- UnmanagedCallersOnly could be useful, but no desktop CLR compat
- We define a bunch of #defines we probably shouldn't be defining
  * Have a CL and GCC compatible driver that acts like Windows or Linux? Potentially with MSVC/GCC language extensions.
- Computed goto can probably be emulated but would be slow
- Way to call into .NET methods (`void __clrimport("mscorlib", "System.Console::WriteLine") WriteInt32(int x)` could work)
- All structs compiling into a sequential layout struct might have bad ABI implications on interop boundaries (e.g. SysV).
- Way to export things for .NET to consume:
  * Specify owning type. Static class makes most sense.
  * What about fields on structs? Any drawbacks for just adding ref returning properties? (Or add real fields? Why didn't MSVC do it?)

