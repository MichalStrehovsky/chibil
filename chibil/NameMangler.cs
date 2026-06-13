namespace Chibil;

public abstract class NameMangler
{
    protected readonly string _tuHash;

    public NameMangler(string tuHash)
        => _tuHash = tuHash;

    public abstract string MangleFunctionBaseName(Obj fn);

    public abstract string MangleFunctionName(Obj fn);

    public abstract string MangleUnmanagedEntryPointName(Obj fn);

    public abstract string MangleArrayTypeName(CType ty);

    public abstract string MangleStaticLocalName(Obj var);

    public abstract string MangleStaticGlobalName(string name);

    public abstract string GenerateAnonymousGlobalName();
}
