using System.Reflection.Metadata;

namespace Chibil;

public class BclBinder
{
    private readonly MsilObjectEmitter _emit;
    private readonly Dictionary<(string Namespace, string Name), TypeReferenceHandle> _lazyTypeRefs = new();
    private readonly Dictionary<(EntityHandle Parent, string Name, byte[] Signature), MemberReferenceHandle> _lazyMemberRefs = new();

    private AssemblyReferenceHandle _mscorlibRef;

    private static readonly byte[] MscorlibPkt = { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 };

    public BclBinder(MsilObjectEmitter emit)
        => _emit = emit;

    public TypeReferenceHandle GetCallConvCdeclRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "CallConvCdecl");
    public TypeReferenceHandle GetCallConvStdcallRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "CallConvStdcall");
    public TypeReferenceHandle GetIsSignUnspecifiedByteRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsSignUnspecifiedByte");
    public TypeReferenceHandle GetIsConstRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsConst");
    public TypeReferenceHandle GetIsVolatileRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsVolatile");
    public TypeReferenceHandle GetIsLongRef() => GetLazyTypeRef("System.Runtime.CompilerServices", "IsLong");
    public TypeReferenceHandle GetValueTypeRef() => GetLazyTypeRef("System", "ValueType");

    private static readonly byte[] Instance_String_RetVoid =
        [(byte)SignatureAttributes.Instance, 1, (byte)SignatureTypeCode.Void, (byte)SignatureTypeCode.String];

    private static readonly byte[] Instance_RetVoid =
        [(byte)SignatureAttributes.Instance, 0, (byte)SignatureTypeCode.Void];

    public MemberReferenceHandle GetDecoratedNameCtorRef()
        => GetLazyMemberRef(GetLazyTypeRef("System.Runtime.CompilerServices", "DecoratedNameAttribute"), ".ctor", Instance_String_RetVoid);

    public MemberReferenceHandle GetNativeCppClassCtorRef()
        => GetLazyMemberRef(GetLazyTypeRef("System.Runtime.CompilerServices", "NativeCppClassAttribute"), ".ctor", Instance_RetVoid);

    private static readonly byte[] Static_PtrInt32_Int32_Int32_RetInt32 =
        [(byte)SignatureAttributes.None, 3, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Pointer, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32];

    private static readonly byte[] Static_PtrInt32_Int32_RetInt32 =
        [(byte)SignatureAttributes.None, 2, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Pointer, (byte)SignatureTypeCode.Int32, (byte)SignatureTypeCode.Int32];

    public MemberReferenceHandle GetCompareExchangeInt32Ref()
        => GetLazyMemberRef(GetLazyTypeRef("System.Threading", "Interlocked"), "CompareExchange", Static_PtrInt32_Int32_Int32_RetInt32);

    public MemberReferenceHandle GetExchangeInt32Ref()
        => GetLazyMemberRef(GetLazyTypeRef("System.Threading", "Interlocked"), "Exchange", Static_PtrInt32_Int32_RetInt32);

    private AssemblyReferenceHandle GetMscorlibRef()
    {
        if (_mscorlibRef.IsNil)
            _mscorlibRef = _emit.AddAssemblyReference("mscorlib", new Version(4, 0, 0, 0), MscorlibPkt);
        return _mscorlibRef;
    }

    private TypeReferenceHandle GetLazyTypeRef(string @namespace, string name)
    {
        var key = (@namespace, name);
        if (!_lazyTypeRefs.TryGetValue(key, out var handle))
        {
            handle = _emit.AddTypeReference(GetMscorlibRef(), @namespace, name);
            _lazyTypeRefs[key] = handle;
        }
        return handle;
    }

    private MemberReferenceHandle GetLazyMemberRef(EntityHandle parent, string memberName, byte[] signature)
    {
        var key = (parent, memberName, signature);
        if (!_lazyMemberRefs.TryGetValue(key, out var handle))
        {
            handle = _emit.AddMemberReference(parent, memberName, signature);
            _lazyMemberRefs[key] = handle;
        }
        return handle;
    }
}
