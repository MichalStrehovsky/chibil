using System.Reflection.PortableExecutable;
using Asm2Obj;
using Xunit;

namespace Chibil.Tests;

/// <summary>
/// End-to-end interop tests for the Asm2Obj.Annotations attributes.
///
/// The harness builds a C# fixture assembly (Asm2ObjAssembly.dll, see
/// tests/Chibil.Tests/Asm2ObjAssembly/) that exercises every recognised
/// signature-modifier attribute on its method declarations, embeds it as
/// a managed resource, then at test time:
///
///   1. Extracts the .dll resource to a temp directory.
///   2. Runs asm2obj on it to produce a managed COFF .obj.
///   3. Compiles a chibil C translation unit that defines (or extern-
///      declares) the matching functions.
///   4. Links the two .objs together with link.exe.
///   5. Runs the resulting binary and asserts on the exit code.
///
/// link.exe enforces that both the COFF mangled symbol name AND the
/// ECMA signature blob match between objects, so a successful link
/// proves the rewriter and the mangler agree byte-for-byte with what
/// chibil emits for the same C signatures. Running the binary verifies
/// runtime correctness of the modifier-rewritten calls.
///
/// Both directions are tested in a single run:
///   - C# extern (ForwardRef) called from C# mainCRTStartup, body in C.
///   - C# body called from a C trampoline (which mainCRTStartup invokes
///     as another ForwardRef extern).
/// </summary>
public class Asm2ObjAnnotationsTests : ChibiTestBase
{
    [Fact]
    public void EndToEndInteropAllAttributes()
    {
        Compile("""
            // ── Direction 1: C bodies that the C# fixture calls into via ForwardRef.

            int c_basic(int a, int b) { return a + b; }
            char c_char(char c) { return c; }
            int c_charptr(char* s) { return *s; }
            int c_charptrptr(char** p) { return **p; }
            int c_const_charptr(const char* s) { return *s; }
            long c_long(long x) { return x; }
            int c_volatile_intptr(volatile int* p) { return *p; }
            int c_const_intptr(int* const p) { return *p; }
            int c_const_voidptr(const void* p) { return *(const char*)p; }

            // ── Direction 2: extern C declarations whose definitions live in the C#
            // fixture, plus trampolines so mainCRTStartup can reach them through
            // ForwardRef externs in C#. The extern declarations use __clrcall so
            // chibil's call emits via metadata token only (no /clr IJW NEP-thunk
            // machinery, which asm2obj does not yet produce on the defining side).

            extern int __clrcall cs_double(int x);
            extern int __clrcall cs_charptr_strlen(const char* s);
            extern long __clrcall cs_long_negate(long x);

            int call_cs_double(int x) { return cs_double(x); }
            int call_cs_charptr_strlen(const char* s) { return cs_charptr_strlen(s); }
            long call_cs_long_negate(long x) { return cs_long_negate(x); }
            """)
        .AddAsm2ObjAssembly("Asm2ObjAssembly.dll")
        .Link(["/subsystem:console"])
        // Expected checksum computed in Cases.mainCRTStartup:
        //   c_basic(2,3) + c_char('X') + c_charptr("A...") + c_charptrptr(&"A...")
        //   + c_const_charptr("A...") + c_const_voidptr("A...") + c_long(100)
        //   + c_volatile_intptr(&42) + c_const_intptr(&7)
        //   + call_cs_double(11) + call_cs_charptr_strlen("ABCDE") + call_cs_long_negate(-9)
        //   = 5 + 88 + 65 + 65 + 65 + 65 + 100 + 42 + 7 + 22 + 5 + 9 = 538
        .RunAndCheck(exitCode: 538);
    }
}

/// <summary>
/// Extension methods that compose with <see cref="CompilationBuilder"/>
/// for the asm2obj annotation interop tests. Lives in the test class's
/// file (rather than CompilationBuilder.cs) since this asm2obj-specific
/// helper isn't a general harness primitive.
/// </summary>
internal static class Asm2ObjAnnotationsTestExtensions
{
    /// <summary>
    /// Extract the named embedded .NET assembly resource (e.g.
    /// <c>Asm2ObjAssembly.dll</c>) from the test assembly, run asm2obj
    /// on it for the surrounding vcvars-environment architecture, and
    /// add the resulting managed COFF object to the link.
    /// </summary>
    public static CompilationBuilder AddAsm2ObjAssembly(this CompilationBuilder builder, string resourceName)
    {
        var asmPath = Path.Combine(builder._tempDir, resourceName);
        var objName = Path.GetFileNameWithoutExtension(resourceName) + ".obj";
        var objPath = Path.Combine(builder._tempDir, objName);

        var hostAsm = typeof(Asm2ObjAnnotationsTestExtensions).Assembly;
        using (var stream = hostAsm.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
                throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found in test assembly.");
            using var file = File.Create(asmPath);
            stream.CopyTo(file);
        }

        // Mirror AddCrt's architecture-selection: vcvarsall.bat sets the
        // Platform environment variable to x86 / x64 / arm64.
        string platform = Environment.GetEnvironmentVariable("Platform") ?? "x64";
        Machine machine = platform switch
        {
            "x86" => Machine.I386,
            "arm64" => Machine.Arm64,
            _ => Machine.Amd64,
        };

        byte[] objBytes = AsmToObjConverter.Convert(asmPath, machine, objName);
        File.WriteAllBytes(objPath, objBytes);
        builder._objFiles.Add(objPath);
        return builder;
    }
}
