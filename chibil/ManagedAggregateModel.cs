using System.Reflection;

namespace Chibil;

public enum ManagedAggregateRepresentationKind
{
    TypeDefinition,
    ForwardDeclaredTypeReference,
    AddressOnly,
}

public enum ManagedAggregateMemberAccessKind
{
    OffsetAddress,
    MetadataField,
}

public readonly record struct ManagedAggregateField(
    string Name,
    CType Type,
    FieldAttributes Attributes,
    int? Offset,
    IReadOnlyList<Member> Members = null);

public abstract class ManagedAggregateModel
{
    public ManagedAggregateRepresentationKind GetRepresentationKind(CType ty)
    {
        CType canonical = ty.Canonicalize();

        if (canonical.Kind == TypeKind.Array)
            return canonical.ArrayLen >= 0
                ? ManagedAggregateRepresentationKind.TypeDefinition
                : ManagedAggregateRepresentationKind.AddressOnly;

        if (canonical.Kind is not (TypeKind.Struct or TypeKind.Union))
            throw new InvalidOperationException("Internal error: expected aggregate type");

        if (canonical.Members == null)
            return ManagedAggregateRepresentationKind.ForwardDeclaredTypeReference;

        return IsAddressOnlyStructOrUnion(canonical)
            ? ManagedAggregateRepresentationKind.AddressOnly
            : ManagedAggregateRepresentationKind.TypeDefinition;
    }

    protected virtual bool IsAddressOnlyStructOrUnion(CType ty) => false;

    public abstract TypeAttributes GetTypeAttributes(CType ty);

    public virtual ushort GetPackingSize(CType ty) => 0;

    public abstract IEnumerable<ManagedAggregateField> GetFields(CType ty);

    public abstract ManagedAggregateMemberAccessKind GetMemberAccessKind(CType owner, Member member);
}

public sealed class MsvcManagedAggregateModel : ManagedAggregateModel
{
    private readonly TypeSystem _types;

    public MsvcManagedAggregateModel(TypeSystem types)
    {
        _types = types;
    }

    protected override bool IsAddressOnlyStructOrUnion(CType ty) => ty.IsNestedMember;

    public override TypeAttributes GetTypeAttributes(CType ty)
    {
        CType canonical = ty.Canonicalize();
        TypeAttributes layoutAttr = canonical.Kind == TypeKind.Union
            ? TypeAttributes.ExplicitLayout
            : TypeAttributes.SequentialLayout;

        return layoutAttr | TypeAttributes.Sealed | TypeAttributes.AnsiClass;
    }

    public override ManagedAggregateMemberAccessKind GetMemberAccessKind(CType owner, Member member) =>
        ManagedAggregateMemberAccessKind.OffsetAddress;

    public override IEnumerable<ManagedAggregateField> GetFields(CType ty)
    {
        CType canonical = ty.Canonicalize();
        if (_types.PointerSize == 4 || canonical.Kind == TypeKind.Array)
            yield break;

        CType fieldType = canonical.Align >= 8 ? _types.TyLongLong : _types.TyInt;
        yield return new ManagedAggregateField(
            "<alignment member>",
            fieldType,
            FieldAttributes.Private,
            canonical.Kind == TypeKind.Union ? 0 : null);
    }
}

public sealed class FieldBackedManagedAggregateModel : ManagedAggregateModel
{
    private readonly TypeSystem _types;

    public FieldBackedManagedAggregateModel(TypeSystem types)
    {
        _types = types;
    }

    public override TypeAttributes GetTypeAttributes(CType ty)
    {
        CType canonical = ty.Canonicalize();
        TypeAttributes layoutAttr = canonical.Kind == TypeKind.Union
            ? TypeAttributes.ExplicitLayout
            : TypeAttributes.SequentialLayout;
        TypeAttributes visibility = canonical.IsNestedMember
            ? TypeAttributes.NestedAssembly
            : TypeAttributes.NotPublic;

        return layoutAttr | visibility | TypeAttributes.Sealed | TypeAttributes.AnsiClass;
    }

    public override ushort GetPackingSize(CType ty)
    {
        CType canonical = ty.Canonicalize();
        return canonical.Kind == TypeKind.Struct && canonical.IsPacked ? (ushort)1 : (ushort)0;
    }

    public override ManagedAggregateMemberAccessKind GetMemberAccessKind(CType owner, Member member) =>
        IsFlexibleArrayMember(owner, member)
            ? ManagedAggregateMemberAccessKind.OffsetAddress
            : ManagedAggregateMemberAccessKind.MetadataField;

    // A flexible array member (e.g. `int values[]`) is encoded as an incomplete
    // array. Emitting it as a metadata field would give it pointer size/alignment,
    // changing the managed layout and producing a wrong base address via ldflda.
    // Skip it from field emission and address it by offset instead.
    private static bool IsFlexibleArrayMember(CType owner, Member member)
    {
        CType canonical = owner.Canonicalize();
        return canonical.IsFlexible &&
            member.Next == null &&
            member.Ty.Canonicalize().Kind == TypeKind.Array;
    }

    public override IEnumerable<ManagedAggregateField> GetFields(CType ty)
    {
        CType canonical = ty.Canonicalize();
        if (canonical.Kind == TypeKind.Array)
            yield break;

        if (canonical.Kind is not (TypeKind.Struct or TypeKind.Union) || canonical.Members == null)
            yield break;

        var bitfieldUnits = new HashSet<string>(StringComparer.Ordinal);

        for (Member mem = canonical.Members; mem != null; mem = mem.Next)
        {
            if (IsFlexibleArrayMember(canonical, mem))
                continue;

            if (mem.IsBitfield)
            {
                if (mem.BitWidth == 0)
                    continue;

                string unitKey = $"{mem.Offset}:{mem.Ty.Size}:{mem.Ty.Kind}:{mem.Ty.IsUnsigned}";
                if (!bitfieldUnits.Add(unitKey))
                    continue;

                var members = new List<Member>();
                for (Member bit = mem; bit != null; bit = bit.Next)
                {
                    if (bit.IsBitfield && bit.BitWidth != 0 &&
                        bit.Offset == mem.Offset &&
                        bit.Ty.Size == mem.Ty.Size &&
                        bit.Ty.Kind == mem.Ty.Kind &&
                        bit.Ty.IsUnsigned == mem.Ty.IsUnsigned)
                    {
                        members.Add(bit);
                    }
                }

                yield return new ManagedAggregateField(
                    mem.Name != null ? $"<bitfield storage for {Util.GetTokenText(mem.Name)}>" : $"<bitfield storage {unitKey}>",
                    GetBlittableFieldStorageType(mem.Ty),
                    FieldAttributes.Assembly,
                    canonical.Kind == TypeKind.Union ? mem.Offset : null,
                    Members: members);
                continue;
            }

            yield return new ManagedAggregateField(
                mem.Name != null ? Util.GetTokenText(mem.Name) : $"<anonymous member {mem.Idx}>",
                GetBlittableFieldStorageType(mem.Ty),
                FieldAttributes.Assembly,
                canonical.Kind == TypeKind.Union ? mem.Offset : null,
                [mem]);
        }
    }

    private CType GetBlittableFieldStorageType(CType ty)
    {
        CType canonical = ty.Canonicalize();
        return canonical.Kind switch
        {
            TypeKind.Bool => _types.TyUchar,
            TypeKind.Ptr when ty.Base.Kind is TypeKind.Func => _types.PointerTo(_types.TyVoid), // desktop CLR doesn't consider blittable
            _ => canonical,
        };
    }
}
