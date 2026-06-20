using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chibil;

internal sealed class ManagedAggregateRegistry
{
    private readonly TypeSystem _types;
    private readonly NameMangler _nameMangler;
    private readonly ManagedAggregateModel _model;
    private readonly MsilObjectEmitter _emit;

    private readonly Dictionary<int, TypeDefinitionHandle> _structTypeDefs = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _arrayTypeDefs = new();
    private readonly Dictionary<string, TypeReferenceHandle> _forwardDeclTypeRefs = new();
    private readonly List<AggregateReservation> _pendingTypeDefs = new();
    private readonly Dictionary<string, IReadOnlyList<ReservedAggregateField>> _reservedFields = new();
    private readonly Dictionary<(string owner, int memberIdx), FieldDefinitionHandle> _memberFields = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _nestedTypeParents = new();
    private bool _materializing;

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

    public FieldDefinitionHandle GetFieldToken(CType owner, Member member)
    {
        CType canonical = owner.Canonicalize();
        string ownerKey = GetAggregateKey(canonical);
        ReserveFieldsInTypeDefOrder(canonical);

        return _memberFields[(ownerKey, member.Idx)];
    }

    public TypeDefinitionHandle GetTypeDefinitionHandle(CType ty)
    {
        CType canonical = ty.Canonicalize();
        ManagedAggregateRepresentationKind representation = _model.GetRepresentationKind(canonical);
        if (representation != ManagedAggregateRepresentationKind.TypeDefinition)
            throw new InvalidOperationException(
                $"Internal error: aggregate type '{GetAggregateKey(canonical)}' has {representation} representation, not a TypeDef");

        if (canonical.Kind == TypeKind.Array)
            return GetOrReserveArrayTypeHandle(canonical);

        return GetOrReserveStructTypeHandle(canonical);
    }

    public EntityHandle GetSignatureTypeHandle(CType ty)
    {
        CType canonical = ty.Canonicalize();

        switch (_model.GetRepresentationKind(canonical))
        {
            case ManagedAggregateRepresentationKind.TypeDefinition:
                return GetTypeDefinitionHandle(canonical);

            case ManagedAggregateRepresentationKind.ForwardDeclaredTypeReference:
            {
                string name = _types.GetStructName(canonical);
                if (!_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
                {
                    typeRef = _emit.AddTypeReference(default, string.Empty, name);
                    _forwardDeclTypeRefs[name] = typeRef;
                }
                return typeRef;
            }

            default:
                throw new UnreachableException();
        }
    }

    public void MaterializeAll()
    {
        for (int i = 0; i < _pendingTypeDefs.Count; i++)
            ReserveFieldsInTypeDefOrder(_pendingTypeDefs[i].Type);

        _materializing = true;
        try
        {
            var nestedTypes = new List<(TypeDefinitionHandle Child, TypeDefinitionHandle Parent)>();

            for (int i = 0; i < _pendingTypeDefs.Count; i++)
            {
                AggregateReservation reservation = _pendingTypeDefs[i];
                IReadOnlyList<ReservedAggregateField> fields = _reservedFields[reservation.Key];

                _emit.AddAggregateTypeDefinition(
                    reservation.Handle,
                    _model.GetTypeAttributes(reservation.Type),
                    reservation.Key,
                    GetFieldListHandle(i, fields),
                    _model.GetPackingSize(reservation.Type),
                    (uint)reservation.Type.Size);

                if (_nestedTypeParents.TryGetValue(reservation.Key, out TypeDefinitionHandle parent))
                    nestedTypes.Add((reservation.Handle, parent));

                foreach (ReservedAggregateField field in fields)
                {
                    var fieldSig = new BlobBuilder();
                    fieldSig.WriteByte(0x06); // FIELD
                    _emit.EncodeType(fieldSig, field.Field.Type);

                    _emit.AddAggregateFieldDefinition(
                        field.Handle,
                        field.Field.Attributes,
                        field.Field.Name,
                        fieldSig,
                        field.Field.Offset);
                }
            }
            foreach (var (child, parent) in nestedTypes)
                _emit.AddNestedType(child, parent);
        }
        finally
        {
            _materializing = false;
        }
    }

    private FieldDefinitionHandle? GetFieldListHandle(int reservationIndex, IReadOnlyList<ReservedAggregateField> fields)
    {
        if (fields.Count > 0)
            return fields[0].Handle;

        for (int i = reservationIndex + 1; i < _pendingTypeDefs.Count; i++)
        {
            IReadOnlyList<ReservedAggregateField> nextFields = _reservedFields[_pendingTypeDefs[i].Key];
            if (nextFields.Count > 0)
                return nextFields[0].Handle;
        }

        return null;
    }

    private TypeDefinitionHandle GetOrReserveStructTypeHandle(CType ty)
    {
        CType canonical = ty.Canonicalize();
        Debug.Assert(canonical.Kind is TypeKind.Struct or TypeKind.Union);

        int id = _types.GetTypeId(canonical);
        if (_structTypeDefs.TryGetValue(id, out TypeDefinitionHandle handle))
            return handle;

        // An enclosing class must precede the classes it encloses in the TypeDef table.
        if (canonical.EnclosingAggregate is CType enclosing &&
            _model.GetRepresentationKind(enclosing.Canonicalize()) == ManagedAggregateRepresentationKind.TypeDefinition)
        {
            GetTypeDefinitionHandle(enclosing);
        }

        handle = ReserveAggregateTypeDefinition();
        string key = GetAggregateKey(canonical);
        _structTypeDefs[id] = handle;
        _pendingTypeDefs.Add(new AggregateReservation(key, handle, canonical));
        return handle;
    }

    private TypeDefinitionHandle GetOrReserveArrayTypeHandle(CType ty)
    {
        CType canonical = ty.Canonicalize();
        Debug.Assert(canonical.Kind == TypeKind.Array && canonical.ArrayLen >= 0);

        string name = _nameMangler.MangleArrayTypeName(canonical);
        if (_arrayTypeDefs.TryGetValue(name, out TypeDefinitionHandle handle))
            return handle;

        handle = ReserveAggregateTypeDefinition();
        string key = GetAggregateKey(canonical);
        _arrayTypeDefs[name] = handle;
        _pendingTypeDefs.Add(new AggregateReservation(key, handle, canonical));

        ReserveTypeDefinitionsFromType(canonical.Base);
        return handle;
    }

    private TypeDefinitionHandle ReserveAggregateTypeDefinition()
    {
        if (_materializing)
            throw new InvalidOperationException("Internal error: aggregate TypeDefs cannot be reserved during materialization");
        return _emit.ReserveTypeDefinition();
    }

    private void ReserveFieldsInTypeDefOrder(CType ty)
    {
        CType canonical = ty.Canonicalize();
        if (_model.GetRepresentationKind(canonical) != ManagedAggregateRepresentationKind.TypeDefinition)
            return;

        TypeDefinitionHandle typeHandle = GetTypeDefinitionHandle(canonical);
        string targetKey = GetAggregateKey(canonical);

        for (int i = 0; i < _pendingTypeDefs.Count; i++)
        {
            AggregateReservation reservation = _pendingTypeDefs[i];
            ReserveFieldsCore(reservation);
            if (reservation.Key.Equals(targetKey))
                return;
        }

        throw new InvalidOperationException(
            $"Internal error: aggregate TypeDef '{_types.GetStructName(canonical)}' was not pending after reserving {typeHandle}");
    }

    private IReadOnlyList<ReservedAggregateField> ReserveFieldsCore(AggregateReservation reservation)
    {
        if (_reservedFields.TryGetValue(reservation.Key, out var reserved))
            return reserved;

        var fields = new List<ReservedAggregateField>();

        foreach (ManagedAggregateField field in _model.GetFields(reservation.Type))
        {
            FieldDefinitionHandle fieldHandle = _emit.ReserveFieldDefinition();
            fields.Add(new ReservedAggregateField(field, fieldHandle));

            if (field.Members != null)
            {
                foreach (Member member in field.Members)
                    _memberFields[(reservation.Key, member.Idx)] = fieldHandle;
            }
        }

        _reservedFields[reservation.Key] = fields;

        TypeDefinitionHandle ownerHandle = reservation.Handle;
        foreach (ReservedAggregateField field in fields)
            ReserveFieldType(field.Field.Type, ownerHandle);

        return fields;
    }

    private void ReserveFieldType(CType ty, TypeDefinitionHandle ownerHandle)
    {
        if (ty == null)
            return;

        CType canonical = ty.Canonicalize();
        switch (canonical.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
            {
                if (_model.GetRepresentationKind(canonical) == ManagedAggregateRepresentationKind.TypeDefinition)
                {
                    TypeDefinitionHandle handle = GetTypeDefinitionHandle(canonical);
                    if (canonical.IsNestedMember)
                        _nestedTypeParents[GetAggregateKey(canonical)] = ownerHandle;
                    ReserveFieldsInTypeDefOrder(canonical);
                }
                return;
            }

            case TypeKind.Array:
                if (canonical.ArrayLen >= 0)
                {
                    GetOrReserveArrayTypeHandle(canonical);
                    ReserveFieldType(canonical.Base, ownerHandle);
                }
                return;

            case TypeKind.Ptr:
                ReserveFieldType(canonical.Base, ownerHandle);
                return;

            case TypeKind.Func:
                ReserveFieldType(canonical.ReturnTy, ownerHandle);
                for (CType p = canonical.Params; p != null; p = p.Next)
                    ReserveFieldType(p, ownerHandle);
                return;
        }
    }

    private string GetAggregateKey(CType ty)
    {
        CType canonical = ty.Canonicalize();

        if (canonical.Kind == TypeKind.Array)
            return _nameMangler.MangleArrayTypeName(canonical);

        if (canonical.Kind is TypeKind.Struct or TypeKind.Union)
            return _types.GetStructName(canonical);

        throw new InvalidOperationException("Internal error: expected aggregate type");
    }

    private void ReserveTypeDefinitionsFromType(CType ty)
    {
        if (ty == null) return;
        CType canonical = ty.Canonicalize();

        switch (canonical.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                if (_model.GetRepresentationKind(canonical) == ManagedAggregateRepresentationKind.TypeDefinition)
                    GetTypeDefinitionHandle(canonical);
                break;
            case TypeKind.Array:
                if (canonical.ArrayLen >= 0)
                    GetOrReserveArrayTypeHandle(canonical);
                break;
            case TypeKind.Ptr:
                ReserveTypeDefinitionsFromType(canonical.Base);
                break;
            case TypeKind.Func:
                ReserveTypeDefinitionsFromType(canonical.ReturnTy);
                for (CType p = canonical.Params; p != null; p = p.Next)
                    ReserveTypeDefinitionsFromType(p);
                break;
        }
    }

    private readonly record struct AggregateReservation(
        string Key,
        TypeDefinitionHandle Handle,
        CType Type);

    private readonly record struct ReservedAggregateField(
        ManagedAggregateField Field,
        FieldDefinitionHandle Handle);
}
