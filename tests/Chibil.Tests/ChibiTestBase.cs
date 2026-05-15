using Chibil;

namespace Chibil.Tests;

/// <summary>
/// Base class for chibil end-to-end tests.
/// Provides fluent entry points for compiling C source strings
/// and manages temp file cleanup via <see cref="IDisposable"/>.
/// </summary>
public abstract class ChibiTestBase : IDisposable
{
    private CompilationBuilder _builder;

    /// <summary>
    /// Compile a C source string, starting a new fluent compilation chain.
    /// Chain additional <see cref="CompilationBuilder.Compile"/> calls for
    /// multi-TU tests, then call <see cref="CompilationBuilder.Link"/> and
    /// <see cref="LinkResult.RunAndCheck"/>.
    /// </summary>
    protected CompilationBuilder Compile(string source, string[] extraArgs = null)
    {
        _builder?.Dispose();
        _builder = new CompilationBuilder();
        return _builder.Compile(source, extraArgs);
    }

    /// <summary>
    /// Attempt to compile a C source string, expecting the compilation to fail.
    /// Returns a <see cref="CompilationError"/> for asserting on the error message.
    /// Throws if the compilation unexpectedly succeeds.
    /// </summary>
    protected CompilationError CompileExpectingError(string source, string[] extraArgs = null)
    {
        using var builder = new CompilationBuilder();
        try
        {
            builder.Compile(source, extraArgs);
            throw new Xunit.Sdk.XunitException(
                "Expected compilation to fail, but it succeeded.");
        }
        catch (ChibiException ex)
        {
            return new CompilationError(ex.Message);
        }
    }

    public void Dispose()
    {
        _builder?.Dispose();
        _builder = null;
    }
}
