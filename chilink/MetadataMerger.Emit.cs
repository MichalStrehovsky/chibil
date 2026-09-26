using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed partial class MetadataMerger
{
    public Handle Map(int input, Handle handle)
    {
        if (_output == null)
            throw new LinkException("Metadata has not been built.");
        if (handle.IsNil)
            return default;
        if (handle.Kind == HandleKind.UserString)
        {
            var userString = (UserStringHandle)handle;
            if (!_userStrings.TryGetValue((input, userString), out var mapped))
                _userStrings.Add((input, userString), mapped = _output.GetOrAddUserString(_inputs[input].GetUserString(userString)));
            return mapped;
        }
        return Output(Resolve(new(input, (EntityHandle)handle)));
    }

    private EntityHandle Output(SourceToken token)
    {
        if (token.Handle.IsNil)
            return default;
        token = Resolve(token);
        if (!_outputHandles.TryGetValue(token, out var mapped))
            throw Failure(token, "required metadata token was not retained");
        return mapped;
    }

    private EntityHandle Output(int input, EntityHandle handle) => Output(new(input, handle));
    private StringHandle String(SourceToken token, StringHandle handle) => _output.GetOrAddString(Reader(token).GetString(handle));
    private BlobHandle Blob(SourceToken token, BlobHandle handle) =>
        handle.IsNil ? default : _output.GetOrAddBlob(Reader(token).GetBlobBytes(handle));
    private BlobHandle Signature(SourceToken token, BlobHandle handle, bool type = false) =>
        _output.GetOrAddBlob(EcmaSignature.Rewrite(Reader(token).GetBlobReader(handle), h => Output(token.Input, h), type));

    private void Predict(IEnumerable<SourceToken> tokens, TableIndex table, int row = 0)
    {
        foreach (var token in tokens)
            _outputHandles.Add(token, MetadataTokens.EntityHandle(table, ++row));
    }

    private void Check(SourceToken token, EntityHandle handle)
    {
        if (Output(token) != handle)
            throw Failure(token, "metadata row prediction mismatch");
    }

    public void Build(MetadataBuilder output, IReadOnlySet<SourceToken> live,
        Func<SourceToken, int> bodyOffset, Func<SourceToken, int> fieldOffset, DataInitialization initialization)
    {
        if (_output != null)
            throw new LinkException("Metadata merger can only be built once.");
        _output = output;
        var retained = live.Select(Resolve).ToHashSet();
        retained.Add(ModuleType);
        _outputHandles.Add(Module, EntityHandle.ModuleDefinition);
        _outputHandles.Add(Assembly, EntityHandle.AssemblyDefinition);
        var types = _typeOrder.Where(retained.Contains).ToArray();
        SourceToken[] RowsOf(HandleKind kind) => retained.Where(t => t.Handle.Kind == kind)
            .OrderBy(t => t.Input).ThenBy(t => MetadataTokens.GetRowNumber(t.Handle)).ToArray();
        SourceToken[] Members(SourceToken owner, bool methods) => _sources[owner]
            .SelectMany(t => methods
                ? Reader(t).GetTypeDefinition((TypeDefinitionHandle)t.Handle).GetMethods().Select(h => new SourceToken(t.Input, h))
                : Reader(t).GetTypeDefinition((TypeDefinitionHandle)t.Handle).GetFields().Select(h => new SourceToken(t.Input, h)))
            .Select(Resolve).Where(retained.Contains).Distinct().ToArray();
        var fieldsByType = types.ToDictionary(t => t, t => Members(t, false));
        var methodsByType = types.ToDictionary(t => t, t => Members(t, true));
        var fields = types.SelectMany(t => fieldsByType[t]).ToArray();
        var methods = types.SelectMany(t => methodsByType[t]).ToArray();
        var parametersByMethod = methods.ToDictionary(t => t, t => _sources[t]
            .SelectMany(s => Reader(s).GetMethodDefinition((MethodDefinitionHandle)s.Handle).GetParameters()
                .Select(h => new SourceToken(s.Input, h)))
            .Select(Resolve).Where(retained.Contains).Distinct()
            .OrderBy(p => Reader(p).GetParameter((ParameterHandle)p.Handle).SequenceNumber).ToArray());
        var parameters = methods.SelectMany(t => parametersByMethod[t]).ToArray();
        int syntheticMethods = initialization.HasInitializer ? 1 : 0;
        Predict(types, TableIndex.TypeDef);
        Predict(fields, TableIndex.Field, initialization.Copies.Count);
        Predict(methods, TableIndex.MethodDef, syntheticMethods);
        Predict(parameters, TableIndex.Param);
        var simpleTables = new[]
        {
            (HandleKind.AssemblyReference, TableIndex.AssemblyRef),
            (HandleKind.ModuleReference, TableIndex.ModuleRef),
            (HandleKind.TypeReference, TableIndex.TypeRef),
            (HandleKind.TypeSpecification, TableIndex.TypeSpec),
            (HandleKind.MemberReference, TableIndex.MemberRef),
            (HandleKind.StandaloneSignature, TableIndex.StandAloneSig),
            (HandleKind.MethodSpecification, TableIndex.MethodSpec)
        };
        foreach (var (kind, table) in simpleTables)
            Predict(RowsOf(kind), table);

        var genericParameters = RowsOf(HandleKind.GenericParameter)
            .OrderBy(t => CodedIndex.TypeOrMethodDef(Output(t.Input,
                Reader(t).GetGenericParameter((GenericParameterHandle)t.Handle).Parent)))
            .ThenBy(t => Reader(t).GetGenericParameter((GenericParameterHandle)t.Handle).Index).ToArray();
        Predict(genericParameters, TableIndex.GenericParam);
        var constraints = RowsOf(HandleKind.GenericParameterConstraint)
            .OrderBy(t => MetadataTokens.GetRowNumber(Output(t.Input,
                Reader(t).GetGenericParameterConstraint((GenericParameterConstraintHandle)t.Handle).Parameter))).ToArray();
        Predict(constraints, TableIndex.GenericParamConstraint);
        var interfaces = RowsOf(HandleKind.InterfaceImplementation)
            .OrderBy(t => MetadataTokens.GetRowNumber(Output(_owners[t]))).ToArray();
        Predict(interfaces, TableIndex.InterfaceImpl);
        var methodImplementations = RowsOf(HandleKind.MethodImplementation)
            .OrderBy(t => MetadataTokens.GetRowNumber(Output(t.Input,
                Reader(t).GetMethodImplementation((MethodImplementationHandle)t.Handle).Type))).ToArray();
        Predict(methodImplementations, TableIndex.MethodImpl);
        var constants = RowsOf(HandleKind.Constant)
            .OrderBy(t => CodedIndex.HasConstant(Output(t.Input,
                Reader(t).GetConstant((ConstantHandle)t.Handle).Parent))).ToArray();
        Predict(constants, TableIndex.Constant);

        foreach (var token in retained)
        {
            if (_errors.TryGetValue(token, out string error))
                throw Failure(token, error);
            foreach (var dependency in GetDependencies(token))
            {
                var canonical = Resolve(dependency);
                if (canonical != Module && canonical != Assembly && !retained.Contains(canonical))
                    throw Failure(token, $"incomplete metadata closure: {Key(canonical)} is required");
            }
        }

        foreach (var token in RowsOf(HandleKind.AssemblyReference))
        {
            var reference = Reader(token).GetAssemblyReference((AssemblyReferenceHandle)token.Handle);
            Check(token, output.AddAssemblyReference(String(token, reference.Name), reference.Version,
                String(token, reference.Culture), Blob(token, reference.PublicKeyOrToken), reference.Flags, Blob(token, reference.HashValue)));
        }
        foreach (var token in RowsOf(HandleKind.ModuleReference))
            Check(token, output.AddModuleReference(String(token, Reader(token).GetModuleReference((ModuleReferenceHandle)token.Handle).Name)));
        foreach (var token in RowsOf(HandleKind.TypeReference))
        {
            var reference = Reader(token).GetTypeReference((TypeReferenceHandle)token.Handle);
            var scope = Output(_referenceParents[token]);
            if (scope.Kind is not (HandleKind.ModuleDefinition or HandleKind.ModuleReference or HandleKind.AssemblyReference or HandleKind.TypeReference))
                throw Failure(token, "invalid resolved TypeRef scope");
            Check(token, output.AddTypeReference(scope, String(token, reference.Namespace), String(token, reference.Name)));
        }
        foreach (var token in RowsOf(HandleKind.TypeSpecification))
            Check(token, output.AddTypeSpecification(Signature(token,
                Reader(token).GetTypeSpecification((TypeSpecificationHandle)token.Handle).Signature, type: true)));

        int fieldRow = 1, methodRow = 1;
        foreach (var token in types)
        {
            var type = Reader(token).GetTypeDefinition((TypeDefinitionHandle)token.Handle);
            Check(token, output.AddTypeDefinition(type.Attributes, String(token, type.Namespace), String(token, type.Name),
                Output(token.Input, type.BaseType), MetadataTokens.FieldDefinitionHandle(fieldRow), MetadataTokens.MethodDefinitionHandle(methodRow)));
            fieldRow += fieldsByType[token].Length;
            methodRow += methodsByType[token].Length;
            if (token == ModuleType)
            {
                fieldRow += initialization.Copies.Count;
                methodRow += syntheticMethods;
            }
        }
        if (initialization.HasInitializer)
        {
            var names = fieldsByType[ModuleType].Select(t =>
                Reader(t).GetString(Reader(t).GetFieldDefinition((FieldDefinitionHandle)t.Handle).Name))
                .ToHashSet(StringComparer.Ordinal);
            int suffix = 0;
            foreach (var copy in initialization.Copies)
            {
                string name;
                do { name = "<chilink data>" + suffix++; } while (!names.Add(name));
                var field = Reader(copy.Field).GetFieldDefinition((FieldDefinitionHandle)copy.Field.Handle);
                copy.DataField = output.AddFieldDefinition(
                    FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.HasFieldRVA,
                    output.GetOrAddString(name), Signature(copy.Field, field.Signature));
                output.AddFieldRelativeVirtualAddress(copy.DataField, copy.Offset);
            }
            initialization.Method = output.AddMethodDefinition(
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL, output.GetOrAddString(".cctor"),
                output.GetOrAddBlob(new byte[] { 0, 0, 1 }), initialization.CodeOffset, MetadataTokens.ParameterHandle(1));
        }
        foreach (var token in fields)
        {
            var field = Reader(token).GetFieldDefinition((FieldDefinitionHandle)token.Handle);
            var attributes = initialization.Converted.Contains(token)
                ? field.Attributes & ~FieldAttributes.HasFieldRVA : field.Attributes;
            Check(token, output.AddFieldDefinition(attributes, String(token, field.Name), Signature(token, field.Signature)));
        }
        int parameterRow = 1;
        foreach (var token in methods)
        {
            var method = Reader(token).GetMethodDefinition((MethodDefinitionHandle)token.Handle);
            var implementationSource = _methodSource[token];
            var implementation = Reader(implementationSource).GetMethodDefinition((MethodDefinitionHandle)implementationSource.Handle);
            int offset = bodyOffset(token);
            bool noBody = (method.Attributes & MethodAttributes.Abstract) != 0 ||
                (implementation.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime ||
                (implementation.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
            if (offset < 0 && !noBody)
                throw Failure(token, $"missing body for method '{Reader(token).GetString(method.Name)}'");
            if (offset >= 0 && noBody)
                throw Failure(token, "abstract/runtime method has an unexpected body");
            if ((implementation.ImplAttributes & MethodImplAttributes.Unmanaged) != 0 ||
                ((implementation.ImplAttributes & MethodImplAttributes.CodeTypeMask) is not (MethodImplAttributes.IL or MethodImplAttributes.Runtime)))
                throw Failure(token, "native method implementation is not supported");
            Check(token, output.AddMethodDefinition(method.Attributes & ~MethodAttributes.UnmanagedExport,
                implementation.ImplAttributes, String(token, method.Name), Signature(token, method.Signature),
                offset, MetadataTokens.ParameterHandle(parameterRow)));
            parameterRow += parametersByMethod[token].Length;
        }
        foreach (var token in parameters)
        {
            var parameter = Reader(token).GetParameter((ParameterHandle)token.Handle);
            Check(token, output.AddParameter(parameter.Attributes, String(token, parameter.Name), parameter.SequenceNumber));
        }
        foreach (var token in RowsOf(HandleKind.MemberReference))
        {
            var reference = Reader(token).GetMemberReference((MemberReferenceHandle)token.Handle);
            var parent = Output(_referenceParents[token]);
            if (parent.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification or
                HandleKind.ModuleReference or HandleKind.MethodDefinition))
                throw Failure(token, "unsupported resolved MemberRef parent");
            Check(token, output.AddMemberReference(parent, String(token, reference.Name), Signature(token, reference.Signature)));
        }
        foreach (var token in RowsOf(HandleKind.StandaloneSignature))
            Check(token, output.AddStandaloneSignature(Signature(token,
                Reader(token).GetStandaloneSignature((StandaloneSignatureHandle)token.Handle).Signature)));
        foreach (var token in RowsOf(HandleKind.MethodSpecification))
        {
            var specification = Reader(token).GetMethodSpecification((MethodSpecificationHandle)token.Handle);
            Check(token, output.AddMethodSpecification(Output(token.Input, specification.Method), Signature(token, specification.Signature)));
        }
        foreach (var token in genericParameters)
        {
            var parameter = Reader(token).GetGenericParameter((GenericParameterHandle)token.Handle);
            Check(token, output.AddGenericParameter(Output(token.Input, parameter.Parent),
                parameter.Attributes, String(token, parameter.Name), parameter.Index));
        }
        foreach (var token in constraints)
        {
            var constraint = Reader(token).GetGenericParameterConstraint((GenericParameterConstraintHandle)token.Handle);
            Check(token, output.AddGenericParameterConstraint((GenericParameterHandle)Output(token.Input, constraint.Parameter),
                Output(token.Input, constraint.Type)));
        }
        foreach (var token in interfaces)
        {
            var implementation = Reader(token).GetInterfaceImplementation((InterfaceImplementationHandle)token.Handle);
            Check(token, output.AddInterfaceImplementation((TypeDefinitionHandle)Output(_owners[token]),
                Output(token.Input, implementation.Interface)));
        }
        foreach (var token in methodImplementations)
        {
            var implementation = Reader(token).GetMethodImplementation((MethodImplementationHandle)token.Handle);
            Check(token, output.AddMethodImplementation((TypeDefinitionHandle)Output(token.Input, implementation.Type),
                Output(token.Input, implementation.MethodBody), Output(token.Input, implementation.MethodDeclaration)));
        }
        foreach (var token in constants)
        {
            var constant = Reader(token).GetConstant((ConstantHandle)token.Handle);
            var value = Reader(token).GetBlobReader(constant.Value);
            Check(token, output.AddConstant(Output(token.Input, constant.Parent), value.ReadConstant(constant.TypeCode)));
        }
        foreach (var token in types)
        {
            var type = Reader(token).GetTypeDefinition((TypeDefinitionHandle)token.Handle);
            var layout = type.GetLayout();
            if (!layout.IsDefault)
                output.AddTypeLayout((TypeDefinitionHandle)Output(token), checked((ushort)layout.PackingSize), checked((uint)layout.Size));
            if (!type.GetDeclaringType().IsNil)
                output.AddNestedType((TypeDefinitionHandle)Output(token), (TypeDefinitionHandle)Output(token.Input, type.GetDeclaringType()));
        }
        foreach (var token in fields)
        {
            var field = Reader(token).GetFieldDefinition((FieldDefinitionHandle)token.Handle);
            if (field.GetOffset() >= 0)
                output.AddFieldLayout((FieldDefinitionHandle)Output(token), field.GetOffset());
            if ((field.Attributes & FieldAttributes.HasFieldRVA) != 0 && !initialization.Converted.Contains(token))
            {
                int offset = fieldOffset(token);
                if (offset < 0)
                    throw Failure(token, "retained RVA field has no supported data");
                output.AddFieldRelativeVirtualAddress((FieldDefinitionHandle)Output(token), offset);
            }
        }
        EmitAttributes(RowsOf(HandleKind.CustomAttribute));
    }

    private void EmitAttributes(IEnumerable<SourceToken> attributes)
    {
        var rows = new List<(SourceToken Source, EntityHandle Parent, EntityHandle Constructor, BlobHandle Value, string Key)>();
        var duplicates = new Dictionary<string, List<SourceToken>>();
        foreach (var token in attributes)
        {
            if (SkipAttribute(token))
                continue;
            var reader = Reader(token);
            var attribute = reader.GetCustomAttribute((CustomAttributeHandle)token.Handle);
            SourceToken sourceParent = new(token.Input, attribute.Parent);
            EntityHandle parent = AssemblyAttributeParent(sourceParent) ? EntityHandle.AssemblyDefinition : Output(sourceParent);
            EntityHandle constructor = Output(token.Input, attribute.Constructor);
            string key = $"{MetadataTokens.GetToken(parent)}:{MetadataTokens.GetToken(constructor)}:" +
                Convert.ToHexString(reader.GetBlobBytes(attribute.Value));
            if (duplicates.TryGetValue(key, out var previous))
            {
                var previousAttribute = Reader(previous[0]).GetCustomAttribute((CustomAttributeHandle)previous[0].Handle);
                var previousParent = new SourceToken(previous[0].Input, previousAttribute.Parent);
                if (sourceParent != previousParent)
                {
                    previous.Add(token);
                    continue;
                }
            }
            if (Resolve(sourceParent) != sourceParent && attribute.Parent.Kind is not (HandleKind.ModuleDefinition or HandleKind.TypeReference))
            {
                string name = AttributeName(token);
                bool additive = name.StartsWith("Microsoft.VisualC.", StringComparison.Ordinal) ||
                    name.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) ||
                    name == "System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute";
                if (!additive)
                    throw Failure(token, $"inconsistent custom attribute '{name}' on duplicate definition");
            }
            // Multiple instances on one source parent are legal. Only attributes
            // contributed by another occurrence of the parent are duplicate candidates.
            string rowKey = key;
            if (duplicates.ContainsKey(rowKey))
                rowKey += ":" + Key(token);
            duplicates.Add(rowKey, new() { token });
            rows.Add((token, parent, constructor, Blob(token, attribute.Value), rowKey));
        }
        rows.Sort((a, b) =>
        {
            int order = CodedIndex.HasCustomAttribute(a.Parent).CompareTo(CodedIndex.HasCustomAttribute(b.Parent));
            if (order != 0) return order;
            order = a.Source.Input.CompareTo(b.Source.Input);
            return order != 0 ? order : MetadataTokens.GetRowNumber(a.Source.Handle).CompareTo(MetadataTokens.GetRowNumber(b.Source.Handle));
        });
        for (int i = 0; i < rows.Count; i++)
            foreach (var source in duplicates[rows[i].Key])
                _outputHandles.Add(source, MetadataTokens.CustomAttributeHandle(i + 1));
        foreach (var row in rows)
            Check(row.Source, _output.AddCustomAttribute(row.Parent, row.Constructor, row.Value));
    }
}
