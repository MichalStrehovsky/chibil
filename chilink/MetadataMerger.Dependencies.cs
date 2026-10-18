using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed partial class MetadataMerger
{
    public IEnumerable<SourceToken> GetRoots()
    {
        yield return Module;
        yield return ModuleType;
        for (int input = 0; input < _inputs.Count; input++)
        {
            var reader = _inputs[input];
            if (reader.IsAssembly)
                yield return new(input, EntityHandle.AssemblyDefinition);
            if (reader.GetTableRowCount(TableIndex.ManifestResource) != 0 ||
                reader.GetTableRowCount(TableIndex.File) != 0 ||
                reader.GetTableRowCount(TableIndex.ExportedType) != 0)
                throw Failure(new(input, EntityHandle.ModuleDefinition), "resources and multi-file manifests are not supported");
            foreach (var handle in reader.AssemblyReferences)
                yield return new(input, handle);
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
                    yield return new(input, handle);
            }
            foreach (var handle in reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1)).GetMethods())
            {
                var method = reader.GetMethodDefinition(handle);
                if (reader.GetString(method.Name) == ".cctor")
                    yield return new(input, handle);
            }
            foreach (var handle in reader.TypeReferences)
                if (reader.GetCustomAttributes(handle).Count != 0)
                {
                    if (AssemblyAttributeParent(new(input, handle)))
                    {
                        foreach (var attribute in reader.GetCustomAttributes(handle))
                            if (!SkipAttribute(new(input, attribute)))
                                yield return new(input, attribute);
                    }
                    else
                        yield return new(input, handle);
                }
        }
    }

    public IEnumerable<SourceToken> GetDependencies(SourceToken token)
    {
        token = Resolve(token);
        if (_errors.TryGetValue(token, out string error))
            throw Failure(token, error);
        var dependencies = new HashSet<SourceToken>();
        foreach (var source in _sources[token].ToArray())
        {
            var reader = Reader(source);
            void Add(EntityHandle handle)
            {
                if (!handle.IsNil)
                    dependencies.Add(new(source.Input, handle));
            }
            void Signature(BlobHandle signature, bool type = false)
            {
                EcmaSignature.Rewrite(reader.GetBlobReader(signature), handle =>
                {
                    Add(handle);
                    return handle;
                }, type);
            }
            switch (source.Handle.Kind)
            {
                case HandleKind.ModuleDefinition:
                    break;
                case HandleKind.AssemblyDefinition:
                    if (reader.IsAssembly && reader.GetAssemblyDefinition().GetDeclarativeSecurityAttributes().Count != 0)
                        throw Failure(source, "assembly security metadata is not supported");
                    break;
                case HandleKind.TypeDefinition:
                    var definition = reader.GetTypeDefinition((TypeDefinitionHandle)source.Handle);
                    Add(definition.BaseType);
                    Add(definition.GetDeclaringType());
                    if (definition.GetDeclarativeSecurityAttributes().Count != 0 ||
                        (definition.Attributes & TypeAttributes.HasSecurity) != 0)
                        throw Failure(source, "type security metadata is not supported");
                    if (token != ModuleType)
                    {
                        if (definition.GetProperties().Count != 0 || definition.GetEvents().Count != 0)
                            throw Failure(source, "properties and events are not supported");
                        foreach (var field in definition.GetFields()) Add(field);
                        foreach (var method in definition.GetMethods()) Add(method);
                        foreach (var implementation in definition.GetInterfaceImplementations()) Add(implementation);
                        foreach (var implementation in definition.GetMethodImplementations()) Add(implementation);
                        foreach (var parameter in definition.GetGenericParameters()) Add(parameter);
                        foreach (var nested in definition.GetNestedTypes())
                        {
                            var visibility = reader.GetTypeDefinition(nested).Attributes & TypeAttributes.VisibilityMask;
                            if (visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)
                                Add(nested);
                        }
                    }
                    break;
                case HandleKind.MethodDefinition:
                    var methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)source.Handle);
                    Add(methodDefinition.GetDeclaringType());
                    Signature(methodDefinition.Signature);
                    foreach (var parameter in methodDefinition.GetParameters()) Add(parameter);
                    foreach (var parameter in methodDefinition.GetGenericParameters()) Add(parameter);
                    if ((methodDefinition.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
                        !methodDefinition.GetImport().Module.IsNil)
                        throw Failure(source, "P/Invoke methods are not supported");
                    if (methodDefinition.GetDeclarativeSecurityAttributes().Count != 0 ||
                        (methodDefinition.Attributes & (MethodAttributes.HasSecurity | MethodAttributes.RequireSecObject)) != 0)
                        throw Failure(source, "method security metadata is not supported");
                    break;
                case HandleKind.FieldDefinition:
                    var fieldDefinition = reader.GetFieldDefinition((FieldDefinitionHandle)source.Handle);
                    Add(fieldDefinition.GetDeclaringType());
                    Signature(fieldDefinition.Signature);
                    Add(fieldDefinition.GetDefaultValue());
                    if (!fieldDefinition.GetMarshallingDescriptor().IsNil ||
                        (fieldDefinition.Attributes & (FieldAttributes.PinvokeImpl | FieldAttributes.HasFieldMarshal)) != 0)
                        throw Failure(source, "field marshalling and P/Invoke are not supported");
                    break;
                case HandleKind.Parameter:
                    var parameterDefinition = reader.GetParameter((ParameterHandle)source.Handle);
                    dependencies.Add(_owners[source]);
                    Add(parameterDefinition.GetDefaultValue());
                    if (!parameterDefinition.GetMarshallingDescriptor().IsNil ||
                        (parameterDefinition.Attributes & ParameterAttributes.HasFieldMarshal) != 0)
                        throw Failure(source, "parameter marshalling is not supported");
                    break;
                case HandleKind.TypeReference:
                    dependencies.Add(_referenceParents[source]);
                    break;
                case HandleKind.MemberReference:
                    var reference = reader.GetMemberReference((MemberReferenceHandle)source.Handle);
                    SourceToken parent = _referenceParents[source];
                    if (parent == ModuleType || parent.Handle.Kind == HandleKind.ModuleDefinition)
                        throw Failure(source, $"unresolved managed member '{reader.GetString(reference.Name)}'");
                    dependencies.Add(parent);
                    Signature(reference.Signature);
                    break;
                case HandleKind.TypeSpecification:
                    Signature(reader.GetTypeSpecification((TypeSpecificationHandle)source.Handle).Signature, type: true);
                    break;
                case HandleKind.StandaloneSignature:
                    Signature(reader.GetStandaloneSignature((StandaloneSignatureHandle)source.Handle).Signature);
                    break;
                case HandleKind.MethodSpecification:
                    var methodSpec = reader.GetMethodSpecification((MethodSpecificationHandle)source.Handle);
                    Add(methodSpec.Method);
                    Signature(methodSpec.Signature);
                    break;
                case HandleKind.InterfaceImplementation:
                    dependencies.Add(_owners[source]);
                    Add(reader.GetInterfaceImplementation((InterfaceImplementationHandle)source.Handle).Interface);
                    break;
                case HandleKind.MethodImplementation:
                    var methodImpl = reader.GetMethodImplementation((MethodImplementationHandle)source.Handle);
                    Add(methodImpl.Type);
                    Add(methodImpl.MethodBody);
                    Add(methodImpl.MethodDeclaration);
                    break;
                case HandleKind.GenericParameter:
                    var genericParameter = reader.GetGenericParameter((GenericParameterHandle)source.Handle);
                    Add(genericParameter.Parent);
                    foreach (var constraint in genericParameter.GetConstraints()) Add(constraint);
                    var first = Reader(token).GetGenericParameter((GenericParameterHandle)token.Handle);
                    var firstConstraints = first.GetConstraints().Select(h =>
                        Resolve(new(token.Input, Reader(token).GetGenericParameterConstraint(h).Type))).ToHashSet();
                    var constraints = genericParameter.GetConstraints().Select(h =>
                        Resolve(new(source.Input, reader.GetGenericParameterConstraint(h).Type))).ToHashSet();
                    if (!firstConstraints.SetEquals(constraints))
                        throw Failure(source, "inconsistent generic parameter constraints");
                    break;
                case HandleKind.GenericParameterConstraint:
                    var genericConstraint = reader.GetGenericParameterConstraint((GenericParameterConstraintHandle)source.Handle);
                    Add(genericConstraint.Parameter);
                    Add(genericConstraint.Type);
                    break;
                case HandleKind.Constant:
                    Add(reader.GetConstant((ConstantHandle)source.Handle).Parent);
                    break;
                case HandleKind.CustomAttribute:
                    if (!SkipAttribute(source))
                    {
                        var attribute = reader.GetCustomAttribute((CustomAttributeHandle)source.Handle);
                        Add(attribute.Constructor);
                        if (AssemblyAttributeParent(new(source.Input, attribute.Parent)))
                            dependencies.Add(Assembly);
                        else
                            Add(attribute.Parent);
                    }
                    break;
                case HandleKind.AssemblyReference:
                    break;
                case HandleKind.ModuleReference:
                    throw Failure(source, "external module references require unsupported multi-file or native linking");
                default:
                    throw Failure(source, $"unsupported live metadata {source.Handle.Kind}");
            }
            if (source.Handle.Kind is not (HandleKind.Constant or HandleKind.MethodImplementation or HandleKind.CustomAttribute))
                foreach (var attribute in reader.GetCustomAttributes(source.Handle))
                    if (!SkipAttribute(new(source.Input, attribute)))
                        Add(attribute);
        }
        return dependencies;
    }

    private bool AssemblyAttributeParent(SourceToken token)
    {
        if (token.Handle.Kind != HandleKind.TypeReference)
            return false;
        var reader = Reader(token);
        var reference = reader.GetTypeReference((TypeReferenceHandle)token.Handle);
        return reader.GetString(reference.Namespace) == "System.Runtime.CompilerServices" &&
            reader.GetString(reference.Name) is "AssemblyAttributesGoHere" or "AssemblyAttributesGoHereM" or
                "AssemblyAttributesGoHereS" or "AssemblyAttributesGoHereSM";
    }

    private bool SkipAttribute(SourceToken token)
    {
        if (AttributeName(token) == "System.Runtime.CompilerServices.SuppressMergeCheckAttribute")
            return true;
        var attribute = Reader(token).GetCustomAttribute((CustomAttributeHandle)token.Handle);
        if (attribute.Parent.Kind is HandleKind.TypeReference or HandleKind.MemberReference)
        {
            var parent = Resolve(new(token.Input, attribute.Parent));
            if (parent.Handle.Kind != attribute.Parent.Kind)
                return true;
            if (attribute.Parent.Kind == HandleKind.MemberReference &&
                _referenceParents[parent].Handle.Kind == HandleKind.MethodDefinition)
                return true;
        }
        return false;
    }
}
