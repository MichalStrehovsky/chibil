// Reflection fixture for the -ffunction-sections / -fdata-sections tests. asm2obj
// converts this assembly to a managed COFF object that the Chibil.Tests harness
// links against a chibil-compiled C translation unit; the C `main` calls one of
// these probes and returns its result as the process exit code.
//
// The probes make the linker's dead-stripping observable at RUNTIME: they walk the
// global members of the *linked* module via reflection and count those whose name
// begins with "frag_". The test C unit defines a couple of unreferenced
// `frag_*` functions/globals — with -ffunction-sections / -fdata-sections each lands
// in its own COMDAT, so /OPT:REF discards the unused ones and the count drops to
// zero; without the flags they share a merged section, survive, and the count
// stays put.
//
// Note: the module is obtained via Assembly.GetExecutingAssembly().ManifestModule
// rather than typeof(Probe).Module. asm2obj hoists a [CompilerGlobalScope] class's
// methods onto <Module>, which leaves a `typeof(Probe)` ldtoken dangling and
// corrupts the merged metadata at link time.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Asm2Obj;

[CompilerGlobalScope]
static class Probe
{
    [return: CallConvCdecl]
    static int probe_methods()
    {
        Module module = Assembly.GetExecutingAssembly().ManifestModule;
        int count = 0;
        foreach (MethodInfo m in module.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.Name.StartsWith("frag_", StringComparison.Ordinal))
                count++;
        return count;
    }

    [return: CallConvCdecl]
    static int probe_fields()
    {
        Module module = Assembly.GetExecutingAssembly().ManifestModule;
        int count = 0;
        foreach (FieldInfo f in module.GetFields(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (f.Name.StartsWith("frag_", StringComparison.Ordinal))
                count++;
        return count;
    }
}
