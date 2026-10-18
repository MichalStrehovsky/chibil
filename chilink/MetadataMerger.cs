using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using Coff;

namespace Chilink;

internal readonly record struct SourceToken(int Input, EntityHandle Handle);

internal sealed partial class MetadataMerger
{
    private readonly IReadOnlyList<MetadataReader> _inputs;
    private readonly Dictionary<SourceToken, SourceToken> _canonical = new();
    private readonly Dictionary<SourceToken, List<SourceToken>> _sources = new();
    private readonly Dictionary<(HandleKind Kind, string Key), SourceToken> _identities = new();
    private readonly Dictionary<(SourceToken Parent, string Namespace, string Name), SourceToken> _types = new();
    private readonly Dictionary<(SourceToken Owner, string Name, string Signature), SourceToken> _members = new();
    private readonly Dictionary<SourceToken, SourceToken> _owners = new();
    private readonly Dictionary<SourceToken, SourceToken> _methodSource = new();
    private readonly Dictionary<SourceToken, SourceToken> _referenceParents = new();
    private readonly Dictionary<(SourceToken Type, SourceToken Declaration), SourceToken> _implementationBodies = new();
    private readonly Dictionary<SourceToken, int> _signatureTypes = new();
    private readonly Dictionary<SourceToken, string> _errors = new();
    private readonly HashSet<SourceToken> _resolving = new();
    private readonly Dictionary<SourceToken, EntityHandle> _outputHandles = new();
    private readonly Dictionary<(int Input, UserStringHandle Handle), UserStringHandle> _userStrings = new();
    private readonly List<SourceToken> _typeOrder = new();
    private readonly HashSet<string> _moduleNames = new(StringComparer.Ordinal);
    private MetadataBuilder _output;
    private SourceToken Module => new(0, EntityHandle.ModuleDefinition);
    private SourceToken ModuleType => new(0, MetadataTokens.TypeDefinitionHandle(1));
    private SourceToken Assembly => new(0, EntityHandle.AssemblyDefinition);

    public MetadataMerger(IReadOnlyList<MetadataReader> inputs)
    {
        _inputs = inputs;
        if (inputs.Count == 0)
            throw new LinkException("No input metadata.");
        for (int i = 0; i < inputs.Count; i++)
        {
            var reader = inputs[i];
            _moduleNames.Add(reader.GetString(reader.GetModuleDefinition().Name));
            Alias(new(i, EntityHandle.ModuleDefinition), Module);
            if (reader.IsAssembly)
                Alias(new(i, EntityHandle.AssemblyDefinition), Assembly);
        }
        _canonical.TryAdd(Assembly, Assembly);
        _sources.TryAdd(Assembly, new());
        for (int i = 0; i < inputs.Count; i++)
            foreach (var handle in inputs[i].TypeDefinitions)
                DefineType(new(i, handle));
        for (int i = 0; i < inputs.Count; i++)
        {
            foreach (var handle in Rows(i, TableIndex.ModuleRef))
                Resolve(handle);
            foreach (var handle in inputs[i].AssemblyReferences)
                Resolve(new(i, handle));
        }
        for (int i = 0; i < inputs.Count; i++)
            foreach (var handle in inputs[i].TypeReferences)
                Resolve(new(i, handle));
        for (int i = 0; i < inputs.Count; i++)
            foreach (var token in Rows(i, TableIndex.TypeSpec))
                Resolve(token);
        for (int i = 0; i < inputs.Count; i++)
            foreach (var handle in inputs[i].TypeDefinitions)
            {
                SourceToken owner = new(i, handle);
                var type = inputs[i].GetTypeDefinition(handle);
                foreach (var field in type.GetFields())
                    DefineMember(new(i, field), owner);
                foreach (var method in type.GetMethods())
                    DefineMember(new(i, method), owner);
                foreach (var implementation in type.GetInterfaceImplementations())
                    _owners.Add(new(i, implementation), owner);
            }
        for (int i = 0; i < inputs.Count; i++)
        {
            foreach (var handle in inputs[i].MethodDefinitions)
            {
                SourceToken method = new(i, handle);
                foreach (var parameter in inputs[i].GetMethodDefinition(handle).GetParameters())
                {
                    SourceToken token = new(i, parameter);
                    _owners.Add(token, method);
                    var p = inputs[i].GetParameter(parameter);
                    var canonical = Intern(token, $"{Key(Resolve(method))}:{p.SequenceNumber}");
                    if (canonical != token && Reader(canonical).GetParameter((ParameterHandle)canonical.Handle).Attributes != p.Attributes)
                        Error(method, "inconsistent parameter attributes");
                }
            }
            foreach (var token in Rows(i, TableIndex.GenericParam))
            {
                var parameter = inputs[i].GetGenericParameter((GenericParameterHandle)token.Handle);
                var canonical = Intern(token, $"{Key(Resolve(new(i, parameter.Parent)))}:{parameter.Index}");
                if (canonical != token)
                {
                    var previous = Reader(canonical).GetGenericParameter((GenericParameterHandle)canonical.Handle);
                    if (previous.Attributes != parameter.Attributes ||
                        Reader(canonical).GetString(previous.Name) != inputs[i].GetString(parameter.Name))
                        Error(new(i, parameter.Parent), "inconsistent generic parameters");
                }
            }
        }
        VerifyDuplicateTypes();
        for (int i = 0; i < inputs.Count; i++)
            foreach (var table in new[] { TableIndex.MemberRef, TableIndex.StandAloneSig, TableIndex.MethodSpec,
                TableIndex.InterfaceImpl, TableIndex.MethodImpl, TableIndex.GenericParamConstraint, TableIndex.Constant })
                foreach (var token in Rows(i, table))
                    Resolve(token);
    }

    private MetadataReader Reader(SourceToken token) => _inputs[token.Input];
    private static string Key(SourceToken token) => $"{token.Input}:{MetadataTokens.GetToken(token.Handle):X8}";
    private static string TextKey(string text) => $"{text.Length}:{text}";
    private LinkException Failure(SourceToken token, string message) =>
        new($"'{_inputs[token.Input].GetString(_inputs[token.Input].GetModuleDefinition().Name)}', token {MetadataTokens.GetToken(token.Handle):X8}: {message}");
    private void Error(SourceToken token, string message) => _errors.TryAdd(Resolve(token), message);

    private IEnumerable<SourceToken> Rows(int input, TableIndex table)
    {
        for (int row = 1; row <= _inputs[input].GetTableRowCount(table); row++)
            yield return new(input, MetadataTokens.EntityHandle(table, row));
    }

    private SourceToken Alias(SourceToken token, SourceToken canonical)
    {
        _canonical[token] = canonical;
        if (token.Handle.Kind == canonical.Handle.Kind)
        {
            if (!_sources.TryGetValue(canonical, out var list))
                _sources.Add(canonical, list = new());
            if (!list.Contains(token))
                list.Add(token);
        }
        return canonical;
    }

    private SourceToken Intern(SourceToken token, string key)
    {
        if (!_identities.TryGetValue((token.Handle.Kind, key), out SourceToken canonical))
            _identities.Add((token.Handle.Kind, key), canonical = token);
        return Alias(token, canonical);
    }

    private SourceToken DefineType(SourceToken token)
    {
        if (_canonical.TryGetValue(token, out var canonical))
            return canonical;
        if (!_resolving.Add(token))
            throw Failure(token, "cyclic nested type ownership");
        var reader = Reader(token);
        var type = reader.GetTypeDefinition((TypeDefinitionHandle)token.Handle);
        string name = reader.GetString(type.Name), ns = reader.GetString(type.Namespace);
        if (MetadataTokens.GetRowNumber(token.Handle) == 1)
        {
            if (name != "<Module>" || ns.Length != 0 || !type.GetDeclaringType().IsNil)
                throw Failure(token, "first TypeDef must be <Module>");
            canonical = Alias(token, ModuleType);
            if (!_typeOrder.Contains(canonical))
                _typeOrder.Add(canonical);
        }
        else
        {
            var enclosing = type.GetDeclaringType();
            SourceToken parent = enclosing.IsNil ? default : DefineType(new(token.Input, enclosing));
            if (!_types.TryGetValue((parent, ns, name), out canonical))
            {
                _types.Add((parent, ns, name), canonical = token);
                _typeOrder.Add(token);
            }
            Alias(token, canonical);
        }
        _resolving.Remove(token);
        return canonical;
    }

    private void DefineMember(SourceToken token, SourceToken owner)
    {
        var reader = Reader(token);
        string name;
        BlobHandle signature;
        bool privateScope;
        if (token.Handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)token.Handle);
            name = reader.GetString(method.Name);
            signature = method.Signature;
            privateScope = (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.PrivateScope;
        }
        else
        {
            var field = reader.GetFieldDefinition((FieldDefinitionHandle)token.Handle);
            name = reader.GetString(field.Name);
            signature = field.Signature;
            privateScope = (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.PrivateScope;
        }
        _owners.Add(token, owner);
        var key = (Resolve(owner), name, SignatureKey(token.Input, signature));
        SourceToken canonical = token;
        if (!privateScope)
        {
            if (!_members.TryGetValue(key, out canonical))
                _members.Add(key, canonical = token);
        }
        Alias(token, canonical);
        if (token.Handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)token.Handle);
            if (!_methodSource.TryGetValue(canonical, out var previous))
                _methodSource.Add(canonical, token);
            else
            {
                var first = Reader(previous).GetMethodDefinition((MethodDefinitionHandle)previous.Handle);
                if ((first.ImplAttributes & MethodImplAttributes.ForwardRef) != 0 &&
                    (method.ImplAttributes & MethodImplAttributes.ForwardRef) == 0)
                    _methodSource[canonical] = token;
                else if ((method.ImplAttributes & MethodImplAttributes.ForwardRef) == 0 &&
                    first.ImplAttributes != method.ImplAttributes)
                    Error(token, $"inconsistent implementation flags for method '{name}'");
            }
        }
    }

    private string SignatureKey(int input, BlobHandle signature, bool type = false, bool fixedOnly = false)
    {
        return Convert.ToHexString(EcmaSignature.Rewrite(_inputs[input].GetBlobReader(signature), handle =>
        {
            var canonical = Resolve(new(input, handle));
            // Comparison identities are independent of retained/output row numbers.
            if (!_signatureTypes.TryGetValue(canonical, out int id))
                _signatureTypes.Add(canonical, id = _signatureTypes.Count + 1);
            return MetadataTokens.TypeDefinitionHandle(id);
        }, type, fixedOnly).ToArray());
    }

    public SourceToken Resolve(SourceToken token)
    {
        if (token.Handle.IsNil)
            return token;
        if ((uint)token.Input >= (uint)_inputs.Count)
            throw new LinkException("Metadata token has an invalid input index.");
        if (_canonical.TryGetValue(token, out var canonical))
            return canonical;
        if (!_resolving.Add(token))
            throw Failure(token, "cyclic metadata reference");
        try
        {
            var reader = Reader(token);
            switch (token.Handle.Kind)
            {
                case HandleKind.ModuleReference:
                    string module = reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)token.Handle).Name);
                    return _moduleNames.Contains(module) && module.Length != 0
                        ? Alias(token, Module) : Intern(token, module);
                case HandleKind.AssemblyReference:
                    var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)token.Handle);
                    string name = reader.GetString(assembly.Name);
                    byte[] key = reader.GetBlobBytes(assembly.PublicKeyOrToken);
                    if ((assembly.Flags & AssemblyFlags.PublicKey) != 0 && key.Length != 0)
                        key = SHA1.HashData(key).TakeLast(8).Reverse().ToArray();
                    Version version = assembly.Version;
                    string versionKey = name is "mscorlib" or "Microsoft.VisualC"
                        ? $"{version.Major}.{version.Minor}" : version.ToString();
                    return Intern(token, $"{TextKey(name)}:{versionKey}:{TextKey(reader.GetString(assembly.Culture))}:{Convert.ToHexString(key)}");
                case HandleKind.TypeReference:
                    var reference = reader.GetTypeReference((TypeReferenceHandle)token.Handle);
                    var scope = reference.ResolutionScope.IsNil ? Module : Resolve(new(token.Input, reference.ResolutionScope));
                    string ns = reader.GetString(reference.Namespace), typeName = reader.GetString(reference.Name);
                    if (scope == Module && typeName == "<Module>" && ns.Length == 0)
                        return Alias(token, ModuleType);
                    if (scope.Handle.Kind == HandleKind.ModuleDefinition || scope.Handle.Kind == HandleKind.TypeDefinition)
                    {
                        var parent = scope.Handle.Kind == HandleKind.TypeDefinition ? scope : default;
                        if (_types.TryGetValue((parent, ns, typeName), out canonical))
                            return Alias(token, canonical);
                    }
                    _referenceParents[token] = scope;
                    canonical = Intern(token, $"{Key(scope)}:{TextKey(ns)}:{TextKey(typeName)}");
                    if (scope.Handle.Kind == HandleKind.TypeDefinition)
                        _errors.TryAdd(canonical, $"nested type '{typeName}' has no definition in its resolved owner");
                    return canonical;
                case HandleKind.TypeSpecification:
                    return Intern(token, SignatureKey(token.Input,
                        reader.GetTypeSpecification((TypeSpecificationHandle)token.Handle).Signature, type: true));
                case HandleKind.StandaloneSignature:
                    return Intern(token, SignatureKey(token.Input,
                        reader.GetStandaloneSignature((StandaloneSignatureHandle)token.Handle).Signature));
                case HandleKind.MemberReference:
                    return ResolveMemberReference(token);
                case HandleKind.MethodSpecification:
                    var spec = reader.GetMethodSpecification((MethodSpecificationHandle)token.Handle);
                    return Intern(token, $"{Key(Resolve(new(token.Input, spec.Method)))}:{SignatureKey(token.Input, spec.Signature)}");
                case HandleKind.InterfaceImplementation:
                    var implementation = reader.GetInterfaceImplementation((InterfaceImplementationHandle)token.Handle);
                    return Intern(token, $"{Key(Resolve(_owners[token]))}:{Key(Resolve(new(token.Input, implementation.Interface)))}");
                case HandleKind.MethodImplementation:
                    var methodImpl = reader.GetMethodImplementation((MethodImplementationHandle)token.Handle);
                    var owner = Resolve(new(token.Input, methodImpl.Type));
                    var body = Resolve(new(token.Input, methodImpl.MethodBody));
                    var declaration = Resolve(new(token.Input, methodImpl.MethodDeclaration));
                    if (_implementationBodies.TryGetValue((owner, declaration), out var previousBody) && previousBody != body)
                        Error(owner, "inconsistent explicit method implementations on duplicate type");
                    else
                        _implementationBodies[(owner, declaration)] = body;
                    return Intern(token, $"{Key(owner)}:{Key(body)}:{Key(declaration)}");
                case HandleKind.GenericParameterConstraint:
                    var constraint = reader.GetGenericParameterConstraint((GenericParameterConstraintHandle)token.Handle);
                    return Intern(token, $"{Key(Resolve(new(token.Input, constraint.Parameter)))}:{Key(Resolve(new(token.Input, constraint.Type)))}");
                case HandleKind.Constant:
                    var constant = reader.GetConstant((ConstantHandle)token.Handle);
                    return Intern(token, Key(Resolve(new(token.Input, constant.Parent))));
                case HandleKind.CustomAttribute:
                    return Alias(token, token);
                default:
                    throw Failure(token, $"unsupported or invalid metadata handle {token.Handle.Kind}");
            }
        }
        finally { _resolving.Remove(token); }
    }

    private SourceToken ResolveMemberReference(SourceToken token)
    {
        var reader = Reader(token);
        var reference = reader.GetMemberReference((MemberReferenceHandle)token.Handle);
        SourceToken parent = Resolve(new(token.Input, reference.Parent));
        string name = reader.GetString(reference.Name);
        byte header = reader.GetBlobReader(reference.Signature).ReadByte();
        bool vararg = (header & 15) is 5 or 11;
        string signature = SignatureKey(token.Input, reference.Signature, fixedOnly: vararg);
        var visited = new HashSet<SourceToken>();
        while (parent.Handle.Kind == HandleKind.TypeDefinition)
        {
            if (!visited.Add(parent))
                throw Failure(token, "cyclic type inheritance");
            if (_members.TryGetValue((parent, name, signature), out var member))
            {
                if (!vararg)
                    return Alias(token, member);
                parent = member;
                break;
            }
            var type = Reader(parent).GetTypeDefinition((TypeDefinitionHandle)parent.Handle);
            if (type.BaseType.IsNil)
                break;
            SourceToken baseType = Resolve(new(parent.Input, type.BaseType));
            if (baseType.Handle.Kind == HandleKind.TypeSpecification)
                break;
            parent = baseType;
        }
        _referenceParents[token] = parent;
        return Intern(token, $"{Key(parent)}:{TextKey(name)}:{SignatureKey(token.Input, reference.Signature)}");
    }

    private string AttributeName(SourceToken attribute)
    {
        var reader = Reader(attribute);
        var ca = reader.GetCustomAttribute((CustomAttributeHandle)attribute.Handle);
        EntityHandle owner = ca.Constructor.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType(),
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent,
            _ => default
        };
        return owner.Kind switch
        {
            HandleKind.TypeDefinition => TypeName(reader.GetTypeDefinition((TypeDefinitionHandle)owner)),
            HandleKind.TypeReference => TypeReferenceName(reader.GetTypeReference((TypeReferenceHandle)owner)),
            _ => ""
        };
        string TypeName(TypeDefinition type) => reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        string TypeReferenceName(TypeReference type) => reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }

    private bool Suppressed(SourceToken token) => Reader(token).GetCustomAttributes(token.Handle)
        .Any(h => AttributeName(new(token.Input, h)) == "System.Runtime.CompilerServices.SuppressMergeCheckAttribute");

    public bool HasFixedAddress(SourceToken field) => _sources[Resolve(field)].Any(source =>
        Reader(source).GetCustomAttributes(source.Handle).Any(h =>
            AttributeName(new(source.Input, h)) == "System.Runtime.CompilerServices.FixedAddressValueTypeAttribute"));

    private bool UnmanagedType(SourceToken token)
    {
        foreach (var handle in Reader(token).GetCustomAttributes(token.Handle))
        {
            var attribute = new SourceToken(token.Input, handle);
            string name = AttributeName(attribute);
            if (name == "System.Runtime.CompilerServices.NativeCppClassAttribute")
                return true;
            if (name == "Microsoft.VisualC.MiscellaneousBitsAttribute")
            {
                var bytes = Reader(token).GetBlobBytes(Reader(token).GetCustomAttribute(handle).Value);
                if (bytes.Length > 2 && (bytes[2] & 0x40) != 0)
                    return true;
            }
        }
        return false;
    }

    private bool MemberSuppressed(SourceToken token)
    {
        if (Suppressed(token))
            return true;
        if (token.Handle.Kind == HandleKind.MethodDefinition)
            return Suppressed(_owners[token]);
        return (Reader(token).GetFieldDefinition((FieldDefinitionHandle)token.Handle).Attributes & FieldAttributes.Static) != 0
            && Suppressed(_owners[token]);
    }

    private void VerifyDuplicateTypes()
    {
        void VerifyGenerics(SourceToken first, SourceToken second)
        {
            IEnumerable<int> Indices(SourceToken token) => token.Handle.Kind == HandleKind.TypeDefinition
                ? Reader(token).GetTypeDefinition((TypeDefinitionHandle)token.Handle).GetGenericParameters()
                    .Select(h => Reader(token).GetGenericParameter(h).Index)
                : Reader(token).GetMethodDefinition((MethodDefinitionHandle)token.Handle).GetGenericParameters()
                    .Select(h => Reader(token).GetGenericParameter(h).Index);
            if (!Indices(first).ToHashSet().SetEquals(Indices(second)))
                Error(second, "inconsistent generic parameter counts");
        }
        foreach (var canonical in _typeOrder.Where(t => t != ModuleType))
        {
            var first = Reader(canonical).GetTypeDefinition((TypeDefinitionHandle)canonical.Handle);
            var firstLayout = first.GetLayout();
            foreach (var token in _sources[canonical].Skip(1))
            {
                var type = Reader(token).GetTypeDefinition((TypeDefinitionHandle)token.Handle);
                VerifyGenerics(canonical, token);
                if (Suppressed(token) != Suppressed(canonical))
                    Error(token, "inconsistent SuppressMergeCheckAttribute on duplicate type");
                if (!UnmanagedType(token) && !UnmanagedType(canonical) &&
                    (type.Attributes & TypeAttributes.VisibilityMask) != (first.Attributes & TypeAttributes.VisibilityMask))
                    Error(token, "inconsistent visibility on duplicate type");
                var layout = type.GetLayout();
                if (layout.IsDefault != firstLayout.IsDefault || layout.Size != firstLayout.Size ||
                    layout.PackingSize != firstLayout.PackingSize)
                    Error(token, "inconsistent layout on duplicate type");
                var originalMembers = first.GetMethods().Select(h => new SourceToken(canonical.Input, h))
                    .Concat(first.GetFields().Select(h => new SourceToken(canonical.Input, h)))
                    .Where(t => !MemberSuppressed(t) && !PrivateScope(t)).Select(Resolve).ToHashSet();
                var duplicateMembers = type.GetMethods().Select(h => new SourceToken(token.Input, h))
                    .Concat(type.GetFields().Select(h => new SourceToken(token.Input, h)))
                    .Where(t => !MemberSuppressed(t) && !PrivateScope(t)).Select(Resolve).ToHashSet();
                if (!originalMembers.SetEquals(duplicateMembers))
                    Error(token, "inconsistent members on duplicate type");
                var firstInterfaces = first.GetInterfaceImplementations()
                    .Select(h => Resolve(new(canonical.Input, Reader(canonical).GetInterfaceImplementation(h).Interface))).ToHashSet();
                var interfaces = type.GetInterfaceImplementations()
                    .Select(h => Resolve(new(token.Input, Reader(token).GetInterfaceImplementation(h).Interface))).ToHashSet();
                if (!firstInterfaces.SetEquals(interfaces))
                    Error(token, "inconsistent interfaces on duplicate type");
            }
        }
        foreach (var pair in _sources.Where(p => p.Key.Handle.Kind is HandleKind.MethodDefinition or HandleKind.FieldDefinition))
        {
            var canonical = pair.Key;
            foreach (var token in pair.Value.Skip(1))
            {
                if (token.Handle.Kind == HandleKind.FieldDefinition)
                {
                    var first = Reader(canonical).GetFieldDefinition((FieldDefinitionHandle)canonical.Handle);
                    var field = Reader(token).GetFieldDefinition((FieldDefinitionHandle)token.Handle);
                    if (first.Attributes != field.Attributes || first.GetOffset() != field.GetOffset())
                        Error(token, "inconsistent attributes or layout on duplicate field");
                }
                else if (Resolve(_owners[token]) != ModuleType)
                {
                    var first = Reader(canonical).GetMethodDefinition((MethodDefinitionHandle)canonical.Handle);
                    var method = Reader(token).GetMethodDefinition((MethodDefinitionHandle)token.Handle);
                    var a = first.GetParameters().Select(h => Reader(canonical).GetParameter(h).SequenceNumber).ToHashSet();
                    var b = method.GetParameters().Select(h => Reader(token).GetParameter(h).SequenceNumber).ToHashSet();
                    if (!a.SetEquals(b))
                        Error(token, "inconsistent parameter counts on duplicate method");
                }
                if (token.Handle.Kind == HandleKind.MethodDefinition)
                    VerifyGenerics(canonical, token);
            }
        }
    }

    private bool PrivateScope(SourceToken token) => token.Handle.Kind == HandleKind.MethodDefinition
        ? (Reader(token).GetMethodDefinition((MethodDefinitionHandle)token.Handle).Attributes & MethodAttributes.MemberAccessMask) == 0
        : (Reader(token).GetFieldDefinition((FieldDefinitionHandle)token.Handle).Attributes & FieldAttributes.FieldAccessMask) == 0;

    public void ValidateReferences()
    {
        for (int input = 0; input < _inputs.Count; input++)
            foreach (var handle in _inputs[input].MemberReferences)
            {
                SourceToken token = new(input, handle);
                var reference = _inputs[input].GetMemberReference(handle);
                SourceToken parent = Resolve(new(input, reference.Parent));
                var resolved = Resolve(token);
                if (parent != ModuleType && parent != Module)
                    continue;
                var target = resolved.Handle.Kind == HandleKind.MemberReference ? _referenceParents[resolved] : resolved;
                bool unresolved = resolved.Handle.Kind == HandleKind.MemberReference &&
                    target.Handle.Kind != HandleKind.MethodDefinition;
                if (target.Handle.Kind == HandleKind.MethodDefinition)
                {
                    var source = _methodSource[target];
                    var method = Reader(source).GetMethodDefinition((MethodDefinitionHandle)source.Handle);
                    unresolved |= (method.ImplAttributes & MethodImplAttributes.ForwardRef) != 0 &&
                        (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Runtime &&
                        (method.ImplAttributes & MethodImplAttributes.InternalCall) == 0 &&
                        (method.Attributes & MethodAttributes.PinvokeImpl) == 0;
                }
                if (unresolved)
                    throw Failure(token, $"unresolved managed global '{_inputs[input].GetString(reference.Name)}'");
            }
    }
}
