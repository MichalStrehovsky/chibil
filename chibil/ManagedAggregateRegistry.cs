using System.Diagnostics;
using System.Reflection.Metadata;

namespace Chibil;

internal sealed class ManagedAggregateRegistry
{
    private readonly TypeSystem _types;
    private readonly NameMangler _nameMangler;
    private readonly ManagedAggregateModel _model;
    private readonly MsilObjectEmitter _emit;

    private readonly Dictionary<int, AggregateEntry> _structs = new();
    private readonly Dictionary<string, AggregateEntry> _arrays = new();
    private readonly List<AggregateEntry> _referenced = new();

    public ManagedAggregateRegistry(
        TypeSystem types,
        NameMangler nameMangler,
        ManagedAggregateModel model,
        MsilObjectEmitter emit)
    {
        _types = types;
        _nameMangler = nameMangler;
        _model = model;
        _emit = emit;
    }

    public EntityHandle GetFieldToken(CType owner, Member member)
    {
        CType canonical = owner.Canonicalize();
        if (_model.GetRepresentationKind(canonical) != ManagedAggregateRepresentationKind.TypeDefinition)
            throw new InvalidOperationException("Internal error: aggregate field token requested for address-only type");

        AggregateEntry entry = GetOrCreateEntry(canonical);
        TypeReferenceHandle ownerRef = GetOrCreateTypeReference(entry);
        ManagedAggregateField field = FindField(canonical, member);

        if (entry.FieldRefs.TryGetValue(field.Name, out MemberReferenceHandle fieldRef))
            return fieldRef;

        BlobBuilder signature = _emit.CreateFieldSignature(field.Type);
        fieldRef = _emit.AddMemberReference(ownerRef, field.Name, signature.ToArray());
        entry.FieldRefs.Add(field.Name, fieldRef);
        _emit.EnsureMemberRefTokenSymbol(fieldRef, isFunction: false);
        return fieldRef;
    }

    public EntityHandle GetTypeHandle(CType ty)
    {
        CType canonical = ty.Canonicalize();
        if (_model.GetRepresentationKind(canonical) != ManagedAggregateRepresentationKind.TypeDefinition)
            throw new InvalidOperationException(
                $"Internal error: aggregate type '{GetAggregateKey(canonical)}' is address-only");

        return GetOrCreateTypeReference(GetOrCreateEntry(canonical));
    }

    public void MaterializeAll()
    {
        for (int i = 0; i < _referenced.Count; i++)
        {
            AggregateEntry entry = _referenced[i];
            if (HasDefinition(entry.Type))
                EmitDefinition(entry);
        }
    }

    private AggregateEntry GetOrCreateEntry(CType ty)
    {
        CType canonical = ty.Canonicalize();

        if (canonical.Kind == TypeKind.Array)
        {
            Debug.Assert(canonical.ArrayLen >= 0);
            string name = _nameMangler.MangleArrayTypeName(canonical);
            if (!_arrays.TryGetValue(name, out AggregateEntry entry))
            {
                entry = new AggregateEntry(name, canonical);
                _arrays.Add(name, entry);
            }
            return entry;
        }

        Debug.Assert(canonical.Kind is TypeKind.Struct or TypeKind.Union);
        int id = _types.GetTypeId(canonical);
        if (!_structs.TryGetValue(id, out AggregateEntry structEntry))
        {
            structEntry = new AggregateEntry(GetAggregateKey(canonical), canonical);
            _structs.Add(id, structEntry);
        }
        return structEntry;
    }

    private TypeReferenceHandle GetOrCreateTypeReference(AggregateEntry entry)
    {
        if (!entry.TypeRef.IsNil)
            return entry.TypeRef;

        EntityHandle scope = default;
        CType canonical = entry.Type.Canonicalize();
        if (canonical.EnclosingAggregate is CType enclosing &&
            _model.GetRepresentationKind(enclosing.Canonicalize()) == ManagedAggregateRepresentationKind.TypeDefinition)
        {
            scope = GetOrCreateTypeReference(GetOrCreateEntry(enclosing));
        }
        entry.TypeRef = _emit.AddTypeReference(scope, string.Empty, entry.Key);
        _referenced.Add(entry);
        return entry.TypeRef;
    }

    private void EmitDefinition(AggregateEntry entry)
    {
        if (!entry.TypeDef.IsNil)
            return;

        CType canonical = entry.Type.Canonicalize();
        TypeDefinitionHandle parent = default;
        if (canonical.EnclosingAggregate is CType enclosing &&
            _model.GetRepresentationKind(enclosing.Canonicalize()) == ManagedAggregateRepresentationKind.TypeDefinition)
        {
            AggregateEntry parentEntry = GetOrCreateEntry(enclosing);
            GetOrCreateTypeReference(parentEntry);
            if (HasDefinition(parentEntry.Type))
            {
                EmitDefinition(parentEntry);
                parent = parentEntry.TypeDef;
            }
        }

        entry.TypeDef = _emit.AddAggregateTypeDefinition(
            _model.GetTypeAttributes(canonical),
            entry.Key,
            _model.GetPackingSize(canonical),
            (uint)canonical.Size);

        foreach (ManagedAggregateField field in _model.GetFields(canonical))
        {
            BlobBuilder signature = _emit.CreateFieldSignature(field.Type);
            _emit.AddAggregateFieldDefinition(
                field.Attributes,
                field.Name,
                signature,
                field.Offset);
        }

        if (!parent.IsNil)
            _emit.AddNestedType(entry.TypeDef, parent);
    }

    private ManagedAggregateField FindField(CType owner, Member member)
    {
        foreach (ManagedAggregateField field in _model.GetFields(owner))
        {
            if (field.Members != null && field.Members.Any(candidate => candidate.Idx == member.Idx))
                return field;
        }

        throw new InvalidOperationException(
            $"Internal error: no metadata field found for member {member.Idx} of '{GetAggregateKey(owner)}'");
    }

    private static bool HasDefinition(CType ty)
    {
        CType canonical = ty.Canonicalize();
        return canonical.Kind == TypeKind.Array
            ? canonical.ArrayLen >= 0
            : canonical.Members != null;
    }

    private string GetAggregateKey(CType ty)
    {
        CType canonical = ty.Canonicalize();
        return canonical.Kind == TypeKind.Array
            ? _nameMangler.MangleArrayTypeName(canonical)
            : _types.GetStructName(canonical);
    }

    private sealed class AggregateEntry
    {
        public AggregateEntry(string key, CType type)
        {
            Key = key;
            Type = type;
        }

        public string Key { get; }
        public CType Type { get; }
        public TypeReferenceHandle TypeRef;
        public TypeDefinitionHandle TypeDef;
        public Dictionary<string, MemberReferenceHandle> FieldRefs { get; } = new();
    }
}
