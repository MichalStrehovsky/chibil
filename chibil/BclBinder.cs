using System.Reflection.Metadata;

namespace Chibil;

public abstract class BclBinder
{
    protected readonly MsilObjectEmitter Emit;
    private readonly Dictionary<(string Name, Version Version, byte[] PublicKeyToken), AssemblyReferenceHandle> _lazyAssemblyRefs = new();
    private readonly Dictionary<(AssemblyReferenceHandle Assembly, string Namespace, string Name), TypeReferenceHandle> _lazyTypeRefs = new();
    private readonly Dictionary<(EntityHandle Parent, string Name, byte[] Signature), MemberReferenceHandle> _lazyMemberRefs = new();

    protected BclBinder(MsilObjectEmitter emit)
        => Emit = emit;

    public TypeReferenceHandle GetCallConvCdeclRef() => GetLazyTypeRef(GetSystemRef(), "System.Runtime.CompilerServices", "CallConvCdecl");
    public TypeReferenceHandle GetCallConvStdcallRef() => GetLazyTypeRef(GetSystemRef(), "System.Runtime.CompilerServices", "CallConvStdcall");
    public TypeReferenceHandle GetIsSignUnspecifiedByteRef() => GetLazyTypeRef(GetVisualCRef(), "System.Runtime.CompilerServices", "IsSignUnspecifiedByte");
    public TypeReferenceHandle GetIsConstRef() => GetLazyTypeRef(GetSystemRef(), "System.Runtime.CompilerServices", "IsConst");
    public TypeReferenceHandle GetIsVolatileRef() => GetLazyTypeRef(GetSystemRef(), "System.Runtime.CompilerServices", "IsVolatile");
    public TypeReferenceHandle GetIsLongRef() => GetLazyTypeRef(GetVisualCRef(), "System.Runtime.CompilerServices", "IsLong");
    public TypeReferenceHandle GetValueTypeRef() => GetLazyTypeRef(GetSystemRef(), "System", "ValueType");
    private TypeReferenceHandle GetInterlockedRef() => GetLazyTypeRef(GetThreadingRef(), "System.Threading", "Interlocked");

    private static readonly byte[] Instance_String_RetVoid =
        [(byte)SignatureAttributes.Instance, 1, (byte)SignatureTypeCode.Void, (byte)SignatureTypeCode.String];

    private static readonly byte[] Instance_RetVoid =
        [(byte)SignatureAttributes.Instance, 0, (byte)SignatureTypeCode.Void];

    public MemberReferenceHandle GetDecoratedNameCtorRef()
        => GetLazyMemberRef(GetLazyTypeRef(GetVisualCRef(), "System.Runtime.CompilerServices", "DecoratedNameAttribute"), ".ctor", Instance_String_RetVoid);

    public MemberReferenceHandle GetNativeCppClassCtorRef()
        => GetLazyMemberRef(GetLazyTypeRef(GetVisualCRef(), "System.Runtime.CompilerServices", "NativeCppClassAttribute"), ".ctor", Instance_RetVoid);

    public MemberReferenceHandle GetFixedAddressValueTypeCtorRef()
        => GetLazyMemberRef(GetLazyTypeRef(GetSystemRef(), "System.Runtime.CompilerServices", "FixedAddressValueTypeAttribute"), ".ctor", Instance_RetVoid);

    private static readonly byte[] Static_RefInt32_Int32_Int32_RetInt32 =
        [(byte)SignatureAttributes.None, 3, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.ByReference, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32];

    private static readonly byte[] Static_RefInt32_Int32_RetInt32 =
        [(byte)SignatureAttributes.None, 2, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.ByReference, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32];

    public MemberReferenceHandle GetCompareExchangeInt32Ref()
        => GetLazyMemberRef(GetInterlockedRef(), "CompareExchange", Static_RefInt32_Int32_Int32_RetInt32);

    public MemberReferenceHandle GetExchangeInt32Ref()
        => GetLazyMemberRef(GetInterlockedRef(), "Exchange", Static_RefInt32_Int32_RetInt32);

    protected abstract AssemblyReferenceHandle GetVisualCRef();
    protected abstract AssemblyReferenceHandle GetSystemRef();
    protected abstract AssemblyReferenceHandle GetThreadingRef();

    protected AssemblyReferenceHandle GetLazyAssemblyRef(string name, Version version, byte[] publicKeyToken)
    {
        var key = (name, version, publicKeyToken);
        if (!_lazyAssemblyRefs.TryGetValue(key, out var handle))
        {
            handle = Emit.AddAssemblyReference(name, version, publicKeyToken);
            _lazyAssemblyRefs.Add(key, handle);
        }
        return handle;
    }

    private TypeReferenceHandle GetLazyTypeRef(AssemblyReferenceHandle assembly, string @namespace, string name)
    {
        var key = (assembly, @namespace, name);
        if (!_lazyTypeRefs.TryGetValue(key, out var handle))
        {
            handle = Emit.AddTypeReference(assembly, @namespace, name);
            _lazyTypeRefs[key] = handle;
        }
        return handle;
    }

    private MemberReferenceHandle GetLazyMemberRef(EntityHandle parent, string memberName, byte[] signature)
    {
        var key = (parent, memberName, signature);
        if (!_lazyMemberRefs.TryGetValue(key, out var handle))
        {
            handle = Emit.AddMemberReference(parent, memberName, signature);
            _lazyMemberRefs[key] = handle;
        }
        return handle;
    }
}

public sealed class NetFrameworkBclBinder : BclBinder
{
    private static readonly byte[] MscorlibPkt = [0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89];

    public NetFrameworkBclBinder(MsilObjectEmitter emit) : base(emit) { }

    protected override AssemblyReferenceHandle GetVisualCRef() => GetSystemRef();
    protected override AssemblyReferenceHandle GetSystemRef() => GetLazyAssemblyRef("mscorlib", new Version(4, 0, 0, 0), MscorlibPkt);
    protected override AssemblyReferenceHandle GetThreadingRef() => GetSystemRef();
}

public sealed class NetCoreBclBinder : BclBinder
{
    private static readonly byte[] PublicKeyToken = [0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A];

    public NetCoreBclBinder(MsilObjectEmitter emit) : base(emit) { }

    protected override AssemblyReferenceHandle GetVisualCRef() => GetLazyAssemblyRef("System.Runtime.CompilerServices.VisualC", new Version(10, 0, 0, 0), PublicKeyToken);
    protected override AssemblyReferenceHandle GetSystemRef() => GetLazyAssemblyRef("System.Runtime", new Version(10, 0, 0, 0), PublicKeyToken);
    protected override AssemblyReferenceHandle GetThreadingRef() => GetLazyAssemblyRef("System.Threading", new Version(10, 0, 0, 0), PublicKeyToken);
}
