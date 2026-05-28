using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Asm2Obj;
using Chibil;
using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Fluent builder that accumulates compiled translation units (.obj files).
/// Call <see cref="Compile"/> one or more times, then <see cref="Link"/> to
/// produce an executable.
/// </summary>
public sealed class CompilationBuilder : IDisposable
{
    internal readonly string _tempDir;
    internal readonly List<string> _objFiles = new();
    private int _fileCounter;
    private bool _disposed;

    internal CompilationBuilder()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"chibil-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Extract the embedded <c>crt.dll</c> resource from this test assembly,
    /// run asm2obj on it to produce a managed COFF <c>crt.obj</c>, and add
    /// the obj to this builder. The CRT shim defines the
    /// <c>mainCRTStartup</c> entry point and the <c>__CxxPureMSILEntry</c>
    /// extern that chibil-generated main objects expect.
    /// </summary>
    public CompilationBuilder AddCrt()
    {
        var crtAsmPath = Path.Combine(_tempDir, "crt.dll");
        var crtObjPath = Path.Combine(_tempDir, "crt.obj");

        var asm = typeof(CompilationBuilder).Assembly;
        using (var stream = asm.GetManifestResourceStream("crt.dll"))
        {
            if (stream == null)
                throw new InvalidOperationException(
                    "Embedded resource 'crt.dll' not found in test assembly. " +
                    "Check the ProjectReference to crt.csproj in Chibil.Tests.csproj.");
            using var file = File.Create(crtAsmPath);
            stream.CopyTo(file);
        }

        // Match the target architecture of the surrounding vcvars environment.
        // vcvarsall.bat sets the Platform variable to x86 / x64 / arm64.
        string platform = Environment.GetEnvironmentVariable("Platform") ?? "x64";
        Machine machine = platform switch
        {
            "x86" => Machine.I386,
            "arm64" => Machine.Arm64,
            _ => Machine.Amd64,
        };

        byte[] objBytes = AsmToObjConverter.Convert(crtAsmPath, machine, "crt.obj");
        File.WriteAllBytes(crtObjPath, objBytes);
        _objFiles.Add(crtObjPath);
        return this;
    }

    /// <summary>
    /// Compile a C source string into an object file and add it to this builder.
    /// </summary>
    /// <param name="source">C source code.</param>
    /// <param name="extraArgs">
    /// Additional compiler flags forwarded before the -cc1 args (e.g. "-DFOO", "-I/some/path").
    /// </param>
    public CompilationBuilder Compile(string source, string[] extraArgs = null)
    {
        var name = $"input{_fileCounter++}";
        var sourceFile = Path.Combine(_tempDir, $"{name}.c");
        var objFile = Path.Combine(_tempDir, $"{name}.obj");

        File.WriteAllText(sourceFile, source);

        var args = new List<string>();
        if (extraArgs != null)
            args.AddRange(extraArgs);
        args.AddRange(["-cc1", "-cc1-input", sourceFile, "-cc1-output", objFile]);

        new Driver().Run(args.ToArray());

        _objFiles.Add(objFile);
        return this;
    }

    /// <summary>
    /// Compile a C source string into an object file using MSVC (cl.exe)
    /// with <c>/clr /BC</c> to produce a mixed-mode assembly, and add it
    /// to this builder.
    /// </summary>
    /// <param name="source">C source code.</param>
    /// <param name="extraArgs">
    /// Additional cl.exe flags (e.g. "/O2", "/DFOO").
    /// </param>
    public CompilationBuilder MsvcCompile(string source, string[] extraArgs = null)
    {
        var name = $"input{_fileCounter++}";
        var sourceFile = Path.Combine(_tempDir, $"{name}.c");
        var objFile = Path.Combine(_tempDir, $"{name}.obj");

        File.WriteAllText(sourceFile, source);

        var psi = new ProcessStartInfo("cl.exe")
        {
            WorkingDirectory = _tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("/clr");
        psi.ArgumentList.Add("/BC");
        psi.ArgumentList.Add($"/Fo{objFile}");
        if (extraArgs != null)
            foreach (var arg in extraArgs)
                psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(sourceFile);

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        var stderr = stderrTask.Result;
        var stdout = stdoutTask.Result;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"cl.exe failed with exit code {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        _objFiles.Add(objFile);
        return this;
    }

    /// <summary>
    /// Link all accumulated object files into an executable using link.exe.
    /// <c>mscoree.lib</c> is included automatically (required for CLR/IJW objects).
    /// </summary>
    /// <param name="extraArgs">
    /// Additional linker flags (e.g. "/entry:main", "/subsystem:console").
    /// </param>
    public LinkResult Link(string[] extraArgs = null)
    {
        if (_objFiles.Count == 0)
            throw new InvalidOperationException("No object files to link. Call Compile() first.");

        var exePath = Path.Combine(_tempDir, "output.exe");

        var psi = new ProcessStartInfo("link.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"/out:{exePath}");
        psi.ArgumentList.Add("mscoree.lib");
        if (extraArgs != null)
            foreach (var arg in extraArgs)
                psi.ArgumentList.Add(arg);
        foreach (var obj in _objFiles)
            psi.ArgumentList.Add(obj);

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        var stderr = stderrTask.Result;
        var stdout = stdoutTask.Result;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"link.exe failed with exit code {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        return new LinkResult(exePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(_tempDir, true); return; }
            catch { Thread.Sleep(100 + i * 200); }
        }
    }
}

/// <summary>
/// Result of a successful link. Call <see cref="RunAndCheck"/> to execute the
/// linked binary and assert on its exit code.
/// </summary>
public sealed class LinkResult
{
    private readonly string _exePath;

    internal LinkResult(string exePath) => _exePath = exePath;

    /// <summary>
    /// Run the linked executable and assert that it exits with the expected code.
    /// </summary>
    public void RunAndCheck(int exitCode)
    {
        var psi = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        var stderr = stderrTask.Result;
        var stdout = stdoutTask.Result;

        Assert.True(proc.ExitCode == exitCode,
            $"Expected exit code {exitCode} but got {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }
}

/// <summary>
/// Captures a compilation failure for assertion. Returned by
/// <see cref="ChibiTestBase.CompileExpectingError"/>.
/// </summary>
public sealed class CompilationError
{
    /// <summary>The error message from the compiler.</summary>
    public string ErrorMessage { get; }

    internal CompilationError(string errorMessage) => ErrorMessage = errorMessage;

    /// <summary>Assert that the error message contains the expected substring.</summary>
    public CompilationError AssertErrorContains(string expected)
    {
        Assert.Contains(expected, ErrorMessage);
        return this;
    }
}
