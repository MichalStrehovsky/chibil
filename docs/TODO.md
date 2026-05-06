Issues:
- is long double implemented correctly?
- Static initialization order fiasco possible
  * File format does have provisions for relocs in data, including vtfixup, however this is likely not implemented on non-Windows in the CLR. Also native AOT doesn't support it.
  * If we keep initializers, might need our own linker so we can generate a sane module constructor instead of something that needs to resolve tokens at runtime (no native AOT is possible as long as this is there)
  * Our own linker could potentially also do something about the static initializer fiasco!
- Distinguish native vs managed function pointers
- Varargs
- Setjmp/longjmp should be possible to implement via throw/catch
- UnmanagedCallersOnly could be useful, but no desktop CLR compat at that point (we probably don't care)
- We define a bunch of #defines we probably shouldn't be defining
  * Should we have a CL and GCC compatible driver that acts like Windows or Linux? Potentially with MSVC/GCC language extensions.
- Computed goto can probably be emulated but would be slow
- Way to call into .NET methods (`void __clrimport("mscorlib", "System.Console::WriteLine") WriteInt32(int x)` could work)
- Way for .NET to call C methods (specify owning type in C? specify owning type at link-time?)
- All structs compiling into a sequential layout struct might have bad ABI implications on interop boundaries (e.g. SysV).
  - Why did MSVC choose this instead of exposing fields as they are?
- Way to export things for .NET to consume:
  * Specify owning type. Static class makes most sense.
  * What about fields on structs? Any drawbacks for just adding ref returning properties? (Or add real fields? Why didn't MSVC do it?)

