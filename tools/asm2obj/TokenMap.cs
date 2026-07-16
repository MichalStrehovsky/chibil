using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

/// <summary>
/// Maps EntityHandles from an input MetadataReader into EntityHandles of the
/// output MetadataBuilder. Populated during Phase B (row prediction) and
/// consulted everywhere else (signature rewriting, IL token slot substitution,
/// custom-attribute reparenting, sort-required-table bucketing).
///
/// UserString tokens (#US heap, used by ldstr) are not EntityHandles and are
/// handled separately via <see cref="MapUserString"/>, which deduplicates
/// strings in the output #US heap on demand.
/// </summary>
public sealed class TokenMap
{
    // Per-table dense arrays indexed by input row number (1-based; slot 0 unused).
    // A zero value means "not mapped" (entity was dropped).
    private readonly int[] _typeRef;
    private readonly int[] _typeDef;
    private readonly int[] _typeDefReference;
    private readonly int[] _field;
    private readonly int[] _fieldReference;
    private readonly int[] _methodDef;
    private readonly int[] _methodDefReference;
    private readonly int[] _param;
    private readonly int[] _memberRef;
    private readonly int[] _typeSpec;
    private readonly int[] _methodSpec;
    private readonly int[] _standaloneSig;
    private readonly int[] _assemblyRef;
    private readonly int[] _moduleRef;
    private readonly int[] _genericParam;
    private readonly int[] _genericParamConstraint;
    private readonly int[] _property;
    private readonly int[] _event;
    private readonly string[] _typeDefUnmappedReasons;

    private readonly MetadataReader _reader;
    private readonly MetadataBuilder _builder;

    private readonly Dictionary<UserStringHandle, UserStringHandle> _userStringCache = new();

    public TokenMap(MetadataReader reader, MetadataBuilder builder)
    {
        _reader = reader;
        _builder = builder;

        _typeRef = new int[reader.GetTableRowCount(TableIndex.TypeRef) + 1];
        _typeDef = new int[reader.GetTableRowCount(TableIndex.TypeDef) + 1];
        _typeDefReference = new int[_typeDef.Length];
        _field = new int[reader.GetTableRowCount(TableIndex.Field) + 1];
        _fieldReference = new int[_field.Length];
        _methodDef = new int[reader.GetTableRowCount(TableIndex.MethodDef) + 1];
        _methodDefReference = new int[_methodDef.Length];
        _param = new int[reader.GetTableRowCount(TableIndex.Param) + 1];
        _memberRef = new int[reader.GetTableRowCount(TableIndex.MemberRef) + 1];
        _typeSpec = new int[reader.GetTableRowCount(TableIndex.TypeSpec) + 1];
        _methodSpec = new int[reader.GetTableRowCount(TableIndex.MethodSpec) + 1];
        _standaloneSig = new int[reader.GetTableRowCount(TableIndex.StandAloneSig) + 1];
        _assemblyRef = new int[reader.GetTableRowCount(TableIndex.AssemblyRef) + 1];
        _moduleRef = new int[reader.GetTableRowCount(TableIndex.ModuleRef) + 1];
        _genericParam = new int[reader.GetTableRowCount(TableIndex.GenericParam) + 1];
        _genericParamConstraint = new int[reader.GetTableRowCount(TableIndex.GenericParamConstraint) + 1];
        _property = new int[reader.GetTableRowCount(TableIndex.Property) + 1];
        _event = new int[reader.GetTableRowCount(TableIndex.Event) + 1];
        _typeDefUnmappedReasons = new string[_typeDef.Length];
    }

    // ─── Predictions (Phase B) ────────────────────────────────────────────────
    // Set the predicted output row for an input handle. Called during Phase B
    // in deterministic input-table order while a running output-row counter is
    // incremented. The actual Add* call later returns the same row.

    public void SetTypeRef(TypeReferenceHandle input, int outputRow) => _typeRef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetTypeDef(TypeDefinitionHandle input, int outputRow) => _typeDef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetTypeDefReference(TypeDefinitionHandle input, int outputTypeRefRow) => _typeDefReference[MetadataTokens.GetRowNumber(input)] = outputTypeRefRow;
    public void SetTypeDefUnmappedReason(TypeDefinitionHandle input, string reason) => _typeDefUnmappedReasons[MetadataTokens.GetRowNumber(input)] = reason;
    public void SetField(FieldDefinitionHandle input, int outputRow) => _field[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetFieldReference(FieldDefinitionHandle input, int outputMemberRefRow) => _fieldReference[MetadataTokens.GetRowNumber(input)] = outputMemberRefRow;
    public void SetMethodDef(MethodDefinitionHandle input, int outputRow) => _methodDef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetMethodDefReference(MethodDefinitionHandle input, int outputMemberRefRow) => _methodDefReference[MetadataTokens.GetRowNumber(input)] = outputMemberRefRow;
    public void SetParam(ParameterHandle input, int outputRow) => _param[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetMemberRef(MemberReferenceHandle input, int outputRow) => _memberRef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetTypeSpec(TypeSpecificationHandle input, int outputRow) => _typeSpec[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetMethodSpec(MethodSpecificationHandle input, int outputRow) => _methodSpec[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetStandaloneSig(StandaloneSignatureHandle input, int outputRow) => _standaloneSig[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetAssemblyRef(AssemblyReferenceHandle input, int outputRow) => _assemblyRef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetModuleRef(ModuleReferenceHandle input, int outputRow) => _moduleRef[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetGenericParam(GenericParameterHandle input, int outputRow) => _genericParam[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetGenericParamConstraint(GenericParameterConstraintHandle input, int outputRow) => _genericParamConstraint[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetProperty(PropertyDefinitionHandle input, int outputRow) => _property[MetadataTokens.GetRowNumber(input)] = outputRow;
    public void SetEvent(EventDefinitionHandle input, int outputRow) => _event[MetadataTokens.GetRowNumber(input)] = outputRow;

    // ─── Lookups ─────────────────────────────────────────────────────────────

    public TypeReferenceHandle MapTypeRef(TypeReferenceHandle h) => MetadataTokens.TypeReferenceHandle(_typeRef[MetadataTokens.GetRowNumber(h)]);
    public TypeDefinitionHandle MapTypeDef(TypeDefinitionHandle h)
    {
        int inputRow = MetadataTokens.GetRowNumber(h);
        int outputRow = _typeDef[inputRow];
        if (outputRow == 0)
            throw new NotSupportedException(
                _typeDefUnmappedReasons[inputRow] ??
                $"Input TypeDef '{GetTypeDefFullName(h)}' is not emitted by asm2obj and cannot be referenced.");
        return MetadataTokens.TypeDefinitionHandle(outputRow);
    }
    public TypeReferenceHandle MapTypeDefReference(TypeDefinitionHandle h)
    {
        int inputRow = MetadataTokens.GetRowNumber(h);
        int outputRow = _typeDefReference[inputRow];
        if (outputRow == 0)
            throw new NotSupportedException(
                _typeDefUnmappedReasons[inputRow] ??
                $"Input TypeDef '{GetTypeDefFullName(h)}' has no emitted reference.");
        return MetadataTokens.TypeReferenceHandle(outputRow);
    }
    public FieldDefinitionHandle MapField(FieldDefinitionHandle h) => MetadataTokens.FieldDefinitionHandle(_field[MetadataTokens.GetRowNumber(h)]);
    public MemberReferenceHandle MapFieldReference(FieldDefinitionHandle h) => MetadataTokens.MemberReferenceHandle(_fieldReference[MetadataTokens.GetRowNumber(h)]);
    public MethodDefinitionHandle MapMethodDef(MethodDefinitionHandle h) => MetadataTokens.MethodDefinitionHandle(_methodDef[MetadataTokens.GetRowNumber(h)]);
    public MemberReferenceHandle MapMethodDefReference(MethodDefinitionHandle h) => MetadataTokens.MemberReferenceHandle(_methodDefReference[MetadataTokens.GetRowNumber(h)]);
    public MemberReferenceHandle MapMemberRef(MemberReferenceHandle h) => MetadataTokens.MemberReferenceHandle(_memberRef[MetadataTokens.GetRowNumber(h)]);
    public TypeSpecificationHandle MapTypeSpec(TypeSpecificationHandle h) => MetadataTokens.TypeSpecificationHandle(_typeSpec[MetadataTokens.GetRowNumber(h)]);
    public MethodSpecificationHandle MapMethodSpec(MethodSpecificationHandle h) => MetadataTokens.MethodSpecificationHandle(_methodSpec[MetadataTokens.GetRowNumber(h)]);
    public StandaloneSignatureHandle MapStandaloneSig(StandaloneSignatureHandle h) => MetadataTokens.StandaloneSignatureHandle(_standaloneSig[MetadataTokens.GetRowNumber(h)]);
    public AssemblyReferenceHandle MapAssemblyRef(AssemblyReferenceHandle h) => MetadataTokens.AssemblyReferenceHandle(_assemblyRef[MetadataTokens.GetRowNumber(h)]);
    public ModuleReferenceHandle MapModuleRef(ModuleReferenceHandle h) => (ModuleReferenceHandle)MetadataTokens.Handle(TableIndex.ModuleRef, _moduleRef[MetadataTokens.GetRowNumber(h)]);

    /// <summary>Maps an entity used as a metadata definition or owner.</summary>
    public EntityHandle MapEntity(EntityHandle handle)
    {
        if (handle.IsNil) return default;
        switch (handle.Kind)
        {
            case HandleKind.TypeReference: return MapTypeRef((TypeReferenceHandle)handle);
            case HandleKind.TypeDefinition: return MapTypeDef((TypeDefinitionHandle)handle);
            case HandleKind.FieldDefinition: return MapField((FieldDefinitionHandle)handle);
            case HandleKind.MethodDefinition:
                {
                    var method = (MethodDefinitionHandle)handle;
                    int row = _methodDef[MetadataTokens.GetRowNumber(method)];
                    return row != 0
                        ? MetadataTokens.MethodDefinitionHandle(row)
                        : MapMethodDefReference(method);
                }
            case HandleKind.MemberReference: return MapMemberRef((MemberReferenceHandle)handle);
            case HandleKind.TypeSpecification: return MapTypeSpec((TypeSpecificationHandle)handle);
            case HandleKind.MethodSpecification: return MapMethodSpec((MethodSpecificationHandle)handle);
            case HandleKind.StandaloneSignature: return MapStandaloneSig((StandaloneSignatureHandle)handle);
            case HandleKind.AssemblyReference: return MapAssemblyRef((AssemblyReferenceHandle)handle);
            case HandleKind.ModuleReference: return MapModuleRef((ModuleReferenceHandle)handle);
            case HandleKind.ModuleDefinition: return EntityHandle.ModuleDefinition;
            case HandleKind.Parameter:
                {
                    int row = _param[MetadataTokens.GetRowNumber((ParameterHandle)handle)];
                    return MetadataTokens.ParameterHandle(row);
                }
            case HandleKind.GenericParameter:
                {
                    int row = _genericParam[MetadataTokens.GetRowNumber((GenericParameterHandle)handle)];
                    return MetadataTokens.GenericParameterHandle(row);
                }
            case HandleKind.GenericParameterConstraint:
                {
                    int row = _genericParamConstraint[MetadataTokens.GetRowNumber((GenericParameterConstraintHandle)handle)];
                    return MetadataTokens.GenericParameterConstraintHandle(row);
                }
            case HandleKind.PropertyDefinition:
                {
                    int row = _property[MetadataTokens.GetRowNumber((PropertyDefinitionHandle)handle)];
                    return MetadataTokens.PropertyDefinitionHandle(row);
                }
            case HandleKind.EventDefinition:
                {
                    int row = _event[MetadataTokens.GetRowNumber((EventDefinitionHandle)handle)];
                    return MetadataTokens.EventDefinitionHandle(row);
                }
            default:
                throw new NotSupportedException($"TokenMap cannot map handle kind {handle.Kind}");
        }
    }

    /// <summary>Maps an entity used from a signature, IL operand, or other reference context.</summary>
    public EntityHandle MapReference(EntityHandle handle)
    {
        if (handle.IsNil) return default;
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => MapTypeDefReference((TypeDefinitionHandle)handle),
            HandleKind.FieldDefinition => MapFieldReference((FieldDefinitionHandle)handle),
            HandleKind.MethodDefinition => MapMethodDefReference((MethodDefinitionHandle)handle),
            _ => MapEntity(handle),
        };
    }

    // ─── User strings ────────────────────────────────────────────────────────

    /// <summary>
    /// Copies an input #US heap entry to the output #US heap and returns the
    /// corresponding output handle. Results are cached so repeated lookups for
    /// the same input handle return the same output token.
    /// </summary>
    public UserStringHandle MapUserString(UserStringHandle input)
    {
        if (_userStringCache.TryGetValue(input, out var cached))
            return cached;
        string s = _reader.GetUserString(input);
        var output = _builder.GetOrAddUserString(s);
        _userStringCache[input] = output;
        return output;
    }

    // ─── Assertion helpers ───────────────────────────────────────────────────

    public void AssertHandle(int expectedRow, EntityHandle actual)
    {
        int actualRow = MetadataTokens.GetRowNumber(actual);
        if (actualRow != expectedRow)
            throw new InvalidOperationException(
                $"TokenMap prediction mismatch: expected row {expectedRow}, got {actualRow} for handle kind {actual.Kind}.");
    }

    private string GetTypeDefFullName(TypeDefinitionHandle h)
    {
        var td = _reader.GetTypeDefinition(h);
        string ns = _reader.GetString(td.Namespace);
        string name = _reader.GetString(td.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
