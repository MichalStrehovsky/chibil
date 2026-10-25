using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Asm2Obj;
using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Fluent builder that accumulates compiled translation units (.obj files).
/// Call <see cref="Compile"/> one or more times, then <see cref="Link"/> to
/// produce an executable.
/// </summary>
public sealed class CompilationBuilder : IDisposable
{
#if NETCORE_TESTS
    internal const bool DefaultNetCore = true;
#else
    internal const bool DefaultNetCore = false;
#endif
    internal const string ExecutableName = DefaultNetCore ? "output.dll" : "output.exe";
    internal readonly bool _netCore;
    internal readonly string _tempDir;
    internal readonly List<string> _objFiles = new();
    internal string NativeRuntimePath { get; private set; }
    private int _fileCounter;
    private bool _disposed;

    internal CompilationBuilder(bool netCore = DefaultNetCore)
    {
        _netCore = netCore;
        _tempDir = Path.GetFullPath(Path.Combine("artifacts", $"chibil-test-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Extract the target-matching CRT resource from this test assembly,
    /// run asm2obj on it to produce a managed COFF <c>crt.obj</c>, and add
    /// the obj to this builder. The CRT shim defines the
    /// <c>mainCRTStartup</c> entry point and the <c>__CxxPureMSILEntry</c>
    /// extern that chibil-generated main objects expect.
    /// </summary>
    public CompilationBuilder AddCrt()
    {
        var crtAsmPath = Path.Combine(_tempDir, "crt.dll");
        var crtObjPath = Path.Combine(_tempDir, "crt.obj");
        string resourceName = _netCore ? "crt.net10.dll" : "crt.net481.dll";

        var asm = typeof(CompilationBuilder).Assembly;
        using (var stream = asm.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
                throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found in test assembly. " +
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

        var args = new List<string> {
            "-ffixed-address-statics", "-fno-managed-aggregate-fields", "-fno-emulated-varargs",
        };
        if (_netCore)
        {
            args.Add("-fnetcore");
            args.Add("-fuco-entrypoints");
        }
        else
            args.Add("--target=x86_64-netfx-windows");
#if FIELD_BACKED_AGGREGATES
        args.Add("-fmanaged-aggregate-fields");
#endif
#if OPTIMIZED_TESTS
        args.Add("-O");
#endif
        if (extraArgs != null)
            args.AddRange(extraArgs);
        args.AddRange(["-cc1", "-cc1-input", sourceFile, "-cc1-output", objFile]);

        new Chibil.Driver().Run(args.ToArray());

        _objFiles.Add(objFile);
        return this;
    }

    /// <summary>
    /// Compile a C source string into an object file using MSVC (cl.exe)
    /// with <c>/clr /BC /Zl /d1clrNoPureCRT</c> to omit default-library
    /// directives and native CRT initialization, and add it to this builder.
    /// </summary>
    /// <param name="source">C source code.</param>
    /// <param name="extraArgs">
    /// Additional cl.exe flags (e.g. "/O2", "/DFOO").
    /// </param>
    public CompilationBuilder MsvcCompile(string source, string[] extraArgs = null)
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("MSVC compilation requires Windows.");
        if (_netCore)
            throw Xunit.Sdk.SkipException.ForSkip("MSVC's /BC C frontend cannot compile with /clr:netcore.");

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
        psi.ArgumentList.Add("/Zl");
        psi.ArgumentList.Add("/d1clrNoPureCRT");
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
    /// Additional link.exe flags.
    /// </param>
    public LinkResult MsvcLink(string[] extraArgs = null, string entryPoint = "main")
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("MSVC linking requires Windows.");
        if (_objFiles.Count == 0)
            throw new InvalidOperationException("No object files to link. Call Compile() first.");

        var exePath = Path.Combine(_tempDir, _netCore ? "output.dll" : "output.exe");

        var psi = new ProcessStartInfo("link.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"/out:{exePath}");
        psi.ArgumentList.Add("/subsystem:console");
        if (entryPoint != null)
            psi.ArgumentList.Add("/entry:" + entryPoint);
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

        return new LinkResult(exePath, _netCore);
    }

    /// <summary>
    /// Link all accumulated object and library files using chilink.
    /// </summary>
    public LinkResult Link(string entryPoint = "main", bool shared = false, string outputPath = null,
        bool gcSections = false)
        => Chilink(gcSections ? ["--gc-sections"] : null,
            entryPoint: entryPoint, shared: shared, outputPath: outputPath);

    /// <summary>
    /// Link using chilink with additional native linker arguments.
    /// </summary>
    public LinkResult Chilink(string[] extraArgs = null, string entryPoint = "main",
        bool shared = false, string outputPath = null)
    {
        if (_objFiles.Count == 0)
            throw new InvalidOperationException("No object files to link. Call Compile() first.");

        var imagePath = outputPath ?? Path.Combine(_tempDir, shared || _netCore ? "output.dll" : "output.exe");
        var args = new List<string> { "-o", imagePath };
        if (shared)
            args.Add("-shared");
        else if (entryPoint != null)
            args.AddRange(["-e", entryPoint]);
        if (extraArgs != null)
            args.AddRange(extraArgs);
        args.AddRange(_objFiles);

        try
        {
            new Chilink.Driver().Run(args.ToArray());
        }
        catch (Chilink.LinkException ex)
        {
            throw new InvalidOperationException($"chilink failed: {ex.Message}", ex);
        }

        return new LinkResult(imagePath, _netCore);
    }

    public LinkResult LinkOnNetCoreOrMsvcLinkOnFramework(string entryPoint = "main")
        => _netCore ? Link(entryPoint) : MsvcLink(entryPoint: entryPoint);

    /// <summary>Build a native library exporting the supplied plain symbol names on the current OS.</summary>
    public CompilationBuilder NativeLibrary(string source, string exports, string name = "native")
    {
        if (OperatingSystem.IsWindows())
            return NativeWindowsLibrary(source, exports, dllName: name + ".dll");
        if (OperatingSystem.IsLinux())
            return NativeLinuxLibrary(source, soname: "lib" + name + ".so.1", exports: exports);
        throw Xunit.Sdk.SkipException.ForSkip("Native library fixtures require Windows or Linux.");
    }

    public CompilationBuilder NativeLinuxLibrary(string source, string filename = "libnative.so",
        string soname = "libnative.so.1", string[] extraArgs = null, string exports = null)
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip("Building ELF shared libraries requires Linux.");
        string sourcePath = Path.Combine(_tempDir, $"native{_fileCounter++}.c");
        string libraryPath = Path.Combine(_tempDir, filename);
        File.WriteAllText(sourcePath, source);
        var psi = new ProcessStartInfo("cc")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-shared", "-fPIC", sourcePath, "-o", libraryPath })
            psi.ArgumentList.Add(argument);
        if (soname != null)
            psi.ArgumentList.Add("-Wl,-soname," + soname);
        if (extraArgs != null)
            foreach (string argument in extraArgs)
                psi.ArgumentList.Add(argument);
        if (exports != null)
        {
            string mapPath = Path.ChangeExtension(sourcePath, ".map");
            string[] names = exports.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            File.WriteAllText(mapPath, "{ global: " + string.Join("; ", names) + "; local: *; };");
            psi.ArgumentList.Add("-Wl,--version-script=" + mapPath);
        }
        RunNativeCompiler(psi);
        if (soname != null && soname != filename)
            File.Copy(libraryPath, Path.Combine(_tempDir, soname), overwrite: true);
        NativeRuntimePath = soname == null ? libraryPath : Path.Combine(_tempDir, soname);
        _objFiles.Add(libraryPath);
        return this;
    }

    public CompilationBuilder NativeWindowsLibrary(string source, string exports,
        string dllName = "native.dll", string libraryName = "native.lib")
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Building native libraries with MSVC requires Windows.");
        string name = $"native{_fileCounter++}";
        string sourcePath = Path.Combine(_tempDir, name + ".c");
        string definitionPath = Path.Combine(_tempDir, name + ".def");
        File.WriteAllText(sourcePath, source);
        File.WriteAllText(definitionPath, "EXPORTS\n" + exports);
        var psi = new ProcessStartInfo("cl.exe")
        {
            WorkingDirectory = _tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] {
            "/nologo", "/LD", "/GS-", "/Fo" + Path.Combine(_tempDir, name + ".obj"),
            sourcePath, "/link", "/NOENTRY", "/NODEFAULTLIB", "/OUT:" + Path.Combine(_tempDir, dllName),
            "/IMPLIB:" + Path.Combine(_tempDir, libraryName), "/DEF:" + definitionPath })
            psi.ArgumentList.Add(argument);
        RunNativeCompiler(psi);
        NativeRuntimePath = Path.Combine(_tempDir, dllName);
        _objFiles.Add(Path.Combine(_tempDir, libraryName));
        return this;
    }

    private static void RunNativeCompiler(ProcessStartInfo psi)
    {
        using var proc = Process.Start(psi);
        var stderrTask = proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        proc.WaitForExit();
        var stderr = stderrTask.Result;
        var stdout = stdoutTask.Result;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"{psi.FileName} failed with exit code {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
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
    private readonly bool _netCore;

    internal LinkResult(string exePath, bool netCore = CompilationBuilder.DefaultNetCore)
    {
        _exePath = exePath;
        _netCore = netCore;
    }

    /// <summary>
    /// Run the linked executable and assert that it exits with the expected code.
    /// </summary>
    public void RunAndCheck(int exitCode, string workingDirectory = null,
        string standardInput = null, string expectedOutput = null)
    {
        if (!_netCore && !OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(".NET Framework execution requires Windows.");

        if (_netCore)
            File.WriteAllText(Path.ChangeExtension(_exePath, ".runtimeconfig.json"), """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """);

        var psi = new ProcessStartInfo(_netCore ? "dotnet" : _exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
        };
        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;
        if (_netCore)
            psi.ArgumentList.Add(_exePath);

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        if (standardInput != null)
        {
            proc.StandardInput.Write(standardInput);
            proc.StandardInput.Close();
        }
        proc.WaitForExit();
        var stderr = stderrTask.Result;
        var stdout = stdoutTask.Result;

        // Unix process exit status only preserves the low eight bits.
        int expectedExitCode = OperatingSystem.IsWindows() ? exitCode : exitCode & 0xff;
        Assert.True(proc.ExitCode == expectedExitCode,
            $"Expected exit code {expectedExitCode} but got {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        if (expectedOutput != null)
            Assert.Equal(expectedOutput, stdout);
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
