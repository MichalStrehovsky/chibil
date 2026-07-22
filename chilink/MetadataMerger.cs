using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;

namespace Chilink;

public static class MetadataMerger
{
    public static MetadataMergeResult Merge(MetadataMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Merger(request).Merge();
    }

    private sealed class Merger
    {
        private readonly MetadataMergeRequest _request;
        private readonly MetadataBuilder _output = new();
        private readonly List<InputState> _inputs = new();
        private readonly Dictionary<TypeKey, TypePlan> _typesByKey = new();
        private readonly Dictionary<TypeDefinitionHandle, TypePlan> _typesByOutput = new();
        private readonly List<TypePlan> _types = new();
        private readonly List<ReferencePlan<AssemblyReferenceHandle>> _assemblyRefs = new();
        private readonly List<ReferencePlan<ModuleReferenceHandle>> _moduleRefs = new();
        private readonly List<SourceRow> _typeRefs = new();
        private readonly List<SourceRow> _typeSpecs = new();
        private readonly List<MemberPlan> _fields = new();
        private readonly List<MemberPlan> _methods = new();
        private readonly List<SourceRow> _parameters = new();
        private readonly List<SourceRow> _memberRefs = new();
        private readonly List<SourceRow> _standaloneSignatures = new();
        private readonly List<SourceRow> _methodSpecs = new();
        private readonly List<SourceRow> _genericParameters = new();
        private readonly List<SourceRow> _genericConstraints = new();
        private readonly List<InterfacePlan> _interfaces = new();
        private readonly List<ConstantPlan> _constants = new();
        private readonly List<MethodImplementationPlan> _methodImplementations = new();
        private readonly List<CustomAttributePlan> _customAttributes = new();
        private readonly Dictionary<SourceRow, byte[]> _rewrittenMemberRefSignatures = new();
        private readonly Dictionary<SourceRow, EntityHandle> _memberRefParents = new();
        private readonly Dictionary<MemberReferenceHandle, EntityHandle> _memberRefParentsByOutput = new();
        private readonly HashSet<string> _inputModuleNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inputAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly TypePlan _moduleType;

        public Merger(MetadataMergeRequest request)
        {
            _request = request;

            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (int inputIndex = 0; inputIndex < request.Inputs.Count; inputIndex++)
            {
                MetadataMergeInput input = request.Inputs[inputIndex];
                if (!identities.Add(input.Identity))
                    throw new ArgumentException(
                        $"Duplicate metadata input identity '{input.Identity}'.",
                        nameof(request));

                ValidateTables(input);
                var state = new InputState(
                    input,
                    new LinkTokenMap(input.Identity, input.Reader, _output),
                    inputIndex);
                _inputs.Add(state);

                ModuleDefinition module = input.Reader.GetModuleDefinition();
                _inputModuleNames.Add(input.Reader.GetString(module.Name));
                if (input.Reader.IsAssembly)
                {
                    AssemblyDefinition assembly = input.Reader.GetAssemblyDefinition();
                    _inputAssemblyNames.Add(input.Reader.GetString(assembly.Name));
                }
            }

            _inputAssemblyNames.Add(request.AssemblyName);

            foreach (InputState input in _inputs)
                input.FreezeSelection();

            _moduleType = new TypePlan(
                key: new TypeKey(string.Empty, "<Module>", default),
                output: MetadataTokens.TypeDefinitionHandle(1),
                canonical: default);
            _types.Add(_moduleType);
            _typesByOutput.Add(_moduleType.Output, _moduleType);
        }

        public MetadataMergeResult Merge()
        {
            PlanModuleAndAssembly();
            PlanTypeDefinitions();
            PlanReferences();
            PlanTypeSpecifications();
            PlanMembers();
            PlanMemberReferences();
            PlanRemainingRows();

            EmitModuleAndAssembly();
            EmitReferences();
            EmitTypeReferences();
            EmitTypeSpecifications();
            EmitTypeDefinitions();
            EmitFields();
            EmitMethodsAndParameters();
            EmitMemberReferences();
            EmitInterfaces();
            EmitConstants();
            EmitLayoutsAndFieldRvas();
            EmitMethodImplementations();
            EmitStandaloneSignatures();
            EmitMethodSpecifications();
            EmitNestedTypes();
            EmitGenerics();
            EmitCustomAttributes();

            MethodDefinitionHandle entryPoint = default;
            if (_request.EntryPoint is MetadataSourceEntity source)
            {
                InputState input = GetInput(source.InputIdentity);
                EntityHandle mapped = input.Map.Map(source.Handle);
                if (mapped.Kind != HandleKind.MethodDefinition)
                    throw new InvalidOperationException(
                        $"Entry point '{source}' mapped to {mapped.Kind}, not MethodDef.");
                entryPoint = (MethodDefinitionHandle)mapped;
            }

            return new MetadataMergeResult(
                _output,
                _inputs.Select(static input => input.Map).ToArray(),
                entryPoint);
        }

        private void PlanModuleAndAssembly()
        {
            EntityHandle outputModule = EntityHandle.ModuleDefinition;
            EntityHandle outputAssembly = EntityHandle.AssemblyDefinition;
            foreach (InputState input in _inputs)
            {
                input.Map.Set(
                    EntityHandle.ModuleDefinition,
                    outputModule,
                    isDuplicate: true);
                if (input.Reader.IsAssembly)
                {
                    input.Map.Set(
                        EntityHandle.AssemblyDefinition,
                        outputAssembly,
                        isDuplicate: true);
                }

                int typeDefCount = input.Reader.GetTableRowCount(TableIndex.TypeDef);
                if (typeDefCount > 0)
                {
                    input.Map.Set(
                        MetadataTokens.TypeDefinitionHandle(1),
                        _moduleType.Output,
                        isDuplicate: true);
                    _moduleType.Sources.Add(
                        new SourceRow(input, MetadataTokens.TypeDefinitionHandle(1)));
                }
            }

        }

        private void PlanTypeDefinitions()
        {
            int nextRow = 2;
            var planning = new HashSet<MetadataSourceEntity>();

            TypePlan PlanTypeDefinition(
                InputState input,
                TypeDefinitionHandle handle)
            {
                if (input.Map.TryGetMapping(handle, out LinkTokenMap.Mapping mapping))
                    return _typesByOutput[(TypeDefinitionHandle)mapping.Destination];
                if (!input.RetainType(handle))
                    return null;

                var sourceEntity = new MetadataSourceEntity(input.Input.Identity, handle);
                if (!planning.Add(sourceEntity))
                {
                    throw new BadImageFormatException(
                        $"Cyclic nested type relationship in input '{input.Input.Identity}'.");
                }

                TypeDefinition incoming = input.Reader.GetTypeDefinition(handle);
                TypeDefinitionHandle enclosing = incoming.GetDeclaringType();
                TypeDefinitionHandle outputEnclosing = enclosing.IsNil
                    ? default
                    : PlanTypeDefinition(input, enclosing)?.Output
                        ?? throw new BadImageFormatException(
                            $"Retained nested type 0x{MetadataTokens.GetToken(handle):X8} " +
                            $"has no retained enclosing type in '{input.Input.Identity}'.");
                var key = new TypeKey(
                    input.Reader.GetString(incoming.Namespace),
                    input.Reader.GetString(incoming.Name),
                    outputEnclosing);
                bool duplicate = _typesByKey.TryGetValue(key, out TypePlan plan);
                if (!duplicate)
                {
                    plan = new TypePlan(
                        key,
                        MetadataTokens.TypeDefinitionHandle(nextRow++),
                        new SourceRow(input, handle));
                    _typesByKey.Add(key, plan);
                    _typesByOutput.Add(plan.Output, plan);
                    _types.Add(plan);
                }

                plan.Sources.Add(new SourceRow(input, handle));
                if (duplicate)
                {
                    bool incomingSuppressed = HasSuppressMergeCheck(input, handle);
                    bool canonicalSuppressed = HasSuppressMergeCheck(
                        plan.Canonical.Input,
                        plan.Canonical.Handle);
                    if (incomingSuppressed != canonicalSuppressed)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate type '{plan.Key}' has inconsistent " +
                            "SuppressMergeCheckAttribute usage.");
                    }

                    TypeDefinition canonical =
                        plan.Canonical.Input.Reader.GetTypeDefinition(
                            (TypeDefinitionHandle)plan.Canonical.Handle);
                    if (!IsUnmanagedType(input, handle) &&
                        (incoming.Attributes & TypeAttributes.VisibilityMask) !=
                        (canonical.Attributes & TypeAttributes.VisibilityMask))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate managed type '{plan.Key}' has inconsistent visibility.");
                    }
                }

                TypeLayout layout = incoming.GetLayout();
                if (!duplicate)
                {
                    if (!layout.IsDefault)
                        plan.Layout = layout;
                }
                else if (!layout.IsDefault &&
                    (plan.Layout is not TypeLayout existing ||
                     existing.PackingSize != layout.PackingSize ||
                     existing.Size != layout.Size))
                {
                    throw new InvalidOperationException(
                        $"Conflicting ClassLayout rows for merged type '{plan.Key}'.");
                }

                input.Map.Set(handle, plan.Output, duplicate);
                planning.Remove(sourceEntity);
                return plan;
            }

            foreach (InputState input in _inputs)
            {
                for (int row = 2;
                     row <= input.Reader.GetTableRowCount(TableIndex.TypeDef);
                     row++)
                {
                    var handle = MetadataTokens.TypeDefinitionHandle(row);
                    PlanTypeDefinition(input, handle);
                }
            }
        }

        private void PlanReferences()
        {
            int assemblyRefRow = 0;
            var assemblyRefsByKey = new Dictionary<string, AssemblyReferenceHandle>(
                StringComparer.Ordinal);
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.AssemblyRef);
                     row++)
                {
                    var source = MetadataTokens.AssemblyReferenceHandle(row);
                    AssemblyReference reference = input.Reader.GetAssemblyReference(source);
                    string name = input.Reader.GetString(reference.Name);
                    if (_inputAssemblyNames.Contains(name))
                    {
                        input.Map.Set(
                            source,
                            EntityHandle.AssemblyDefinition,
                            isDuplicate: true);
                        continue;
                    }

                    string key = GetAssemblyReferenceKey(input.Reader, reference);
                    bool duplicate = assemblyRefsByKey.TryGetValue(
                        key,
                        out AssemblyReferenceHandle output);
                    if (!duplicate)
                    {
                        output = MetadataTokens.AssemblyReferenceHandle(++assemblyRefRow);
                        assemblyRefsByKey.Add(key, output);
                        _assemblyRefs.Add(new ReferencePlan<AssemblyReferenceHandle>(
                            new SourceRow(input, source),
                            output));
                    }
                    input.Map.Set(source, output, duplicate);
                }
            }

            int moduleRefRow = 0;
            var moduleRefsByName = new Dictionary<string, ModuleReferenceHandle>(
                StringComparer.Ordinal);
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.ModuleRef);
                     row++)
                {
                    var source = MetadataTokens.ModuleReferenceHandle(row);
                    if (!input.IsMarked(source))
                        continue;

                    ModuleReference reference = input.Reader.GetModuleReference(source);
                    string name = input.Reader.GetString(reference.Name);
                    if (name.Length != 0 && _inputModuleNames.Contains(name))
                    {
                        input.Map.Set(
                            source,
                            EntityHandle.ModuleDefinition,
                            isDuplicate: true);
                        continue;
                    }

                    bool duplicate = moduleRefsByName.TryGetValue(
                        name,
                        out ModuleReferenceHandle output);
                    if (!duplicate)
                    {
                        output = MetadataTokens.ModuleReferenceHandle(++moduleRefRow);
                        moduleRefsByName.Add(name, output);
                        _moduleRefs.Add(new ReferencePlan<ModuleReferenceHandle>(
                            new SourceRow(input, source),
                            output));
                    }
                    input.Map.Set(source, output, duplicate);
                }
            }

            int typeRefRow = 0;
            var typeRefsByKey =
                new Dictionary<(EntityHandle Scope, string Namespace, string Name), TypeReferenceHandle>();
            var planning = new HashSet<MetadataSourceEntity>();

            void PlanTypeReference(InputState input, TypeReferenceHandle source)
            {
                if (!input.IsMarked(source) || input.Map.TryMap(source, out _))
                    return;

                var sourceEntity = new MetadataSourceEntity(input.Input.Identity, source);
                if (!planning.Add(sourceEntity))
                    throw new BadImageFormatException(
                        $"Cyclic TypeRef resolution scope in input '{input.Input.Identity}'.");

                TypeReference reference = input.Reader.GetTypeReference(source);
                if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                {
                    PlanTypeReference(
                        input,
                        (TypeReferenceHandle)reference.ResolutionScope);
                }

                if (TryResolveTypeReference(input, source, out TypeDefinitionHandle resolved))
                {
                    input.Map.Set(source, resolved, isDuplicate: true);
                    planning.Remove(sourceEntity);
                    return;
                }

                EntityHandle scope = reference.ResolutionScope.IsNil
                    ? default
                    : MapTypeReferenceResolutionScope(
                        input,
                        reference.ResolutionScope);
                var key = (
                    scope,
                    input.Reader.GetString(reference.Namespace),
                    input.Reader.GetString(reference.Name));
                bool duplicate = typeRefsByKey.TryGetValue(
                    key,
                    out TypeReferenceHandle output);
                if (!duplicate)
                {
                    output = MetadataTokens.TypeReferenceHandle(++typeRefRow);
                    typeRefsByKey.Add(key, output);
                    _typeRefs.Add(new SourceRow(input, source));
                }
                input.Map.Set(source, output, duplicate);
                planning.Remove(sourceEntity);
            }

            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.TypeRef);
                     row++)
                {
                    PlanTypeReference(
                        input,
                        MetadataTokens.TypeReferenceHandle(row));
                }
            }
        }

        private void PlanTypeSpecifications()
        {
            int row = 0;
            var specificationsBySignature =
                new Dictionary<string, TypeSpecificationHandle>(StringComparer.Ordinal);
            var planning = new HashSet<MetadataSourceEntity>();

            void PlanTypeSpecification(
                InputState input,
                TypeSpecificationHandle source)
            {
                if (!input.IsMarked(source) || input.Map.TryMap(source, out _))
                    return;

                var sourceEntity = new MetadataSourceEntity(input.Input.Identity, source);
                if (!planning.Add(sourceEntity))
                    throw new BadImageFormatException(
                        $"Cyclic TypeSpec signature in input '{input.Input.Identity}'.");

                TypeSpecification specification =
                    input.Reader.GetTypeSpecification(source);
                SignatureRewriter.MarkTypeSpecification(
                    input.Reader.GetBlobReader(specification.Signature),
                    dependency =>
                    {
                        if (dependency.Kind == HandleKind.TypeSpecification)
                        {
                            PlanTypeSpecification(
                                input,
                                (TypeSpecificationHandle)dependency);
                        }
                        else if (!input.Map.TryMap(dependency, out _))
                        {
                            throw new BadImageFormatException(
                                $"TypeSpec dependency 0x{MetadataTokens.GetToken(dependency):X8} " +
                                $"was not planned in input '{input.Input.Identity}'.");
                        }
                    });

                byte[] rewritten = SignatureRewriter.RewriteTypeSpecification(
                    input.Reader.GetBlobReader(specification.Signature),
                    input.Map).ToArray();
                string key = Convert.ToHexString(rewritten);
                bool duplicate = specificationsBySignature.TryGetValue(
                    key,
                    out TypeSpecificationHandle output);
                if (!duplicate)
                {
                    output = MetadataTokens.TypeSpecificationHandle(++row);
                    specificationsBySignature.Add(key, output);
                    _typeSpecs.Add(new SourceRow(input, source));
                }
                input.Map.Set(source, output, duplicate);
                planning.Remove(sourceEntity);
            }

            foreach (InputState input in _inputs)
            {
                for (int sourceRow = 1;
                     sourceRow <= input.Reader.GetTableRowCount(TableIndex.TypeSpec);
                     sourceRow++)
                {
                    PlanTypeSpecification(
                        input,
                        MetadataTokens.TypeSpecificationHandle(sourceRow));
                }
            }
        }

        private void PlanMembers()
        {
            int fieldRow = 0;
            int methodRow = 0;
            int parameterRow = 0;

            foreach (TypePlan type in _types)
            {
                type.FirstField = MetadataTokens.FieldDefinitionHandle(fieldRow + 1);
                type.FirstMethod = MetadataTokens.MethodDefinitionHandle(methodRow + 1);
                var fieldsByKey = new Dictionary<string, MemberPlan>(StringComparer.Ordinal);
                var methodsByKey = new Dictionary<string, MemberPlan>(StringComparer.Ordinal);
                var pendingFieldBindings = new List<(
                    InputState Input,
                    FieldDefinitionHandle Source,
                    MetadataSourceEntity SourceEntity,
                    MetadataSourceEntity Target)>();

                foreach (SourceRow typeSource in type.Sources)
                {
                    bool duplicateType = typeSource.Input.Map.IsDuplicate(typeSource.Handle);
                    TypeDefinition definition =
                        typeSource.Input.Reader.GetTypeDefinition(
                            (TypeDefinitionHandle)typeSource.Handle);
                    bool typeSuppressed = HasSuppressMergeCheck(
                        typeSource.Input,
                        typeSource.Handle);
                    int checkedFields = 0;
                    int checkedMethods = 0;

                    foreach (FieldDefinitionHandle source in definition.GetFields())
                    {
                        if (!typeSource.Input.RetainField(source))
                            continue;

                        FieldDefinition fieldDefinition =
                            typeSource.Input.Reader.GetFieldDefinition(source);
                        var sourceEntity = new MetadataSourceEntity(
                            typeSource.Input.Input.Identity,
                            source);
                        if (_request.FieldDefinitionBindings.TryGetValue(
                                sourceEntity,
                                out MetadataSourceEntity fieldBinding))
                        {
                            pendingFieldBindings.Add((
                                typeSource.Input,
                                source,
                                sourceEntity,
                                fieldBinding));
                            continue;
                        }
                        BlobBuilder rewrittenSignature = SignatureRewriter.RewriteField(
                            typeSource.Input.Reader.GetBlobReader(fieldDefinition.Signature),
                            typeSource.Input.Map);
                        string fieldKey =
                            typeSource.Input.Reader.GetString(fieldDefinition.Name) + "\0" +
                            Convert.ToHexString(rewrittenSignature.ToArray());
                        bool privateScope =
                            (fieldDefinition.Attributes & FieldAttributes.FieldAccessMask) ==
                            FieldAttributes.PrivateScope;
                        bool suppressed = HasSuppressMergeCheck(
                            typeSource.Input,
                            source) ||
                            (typeSuppressed &&
                             (fieldDefinition.Attributes & FieldAttributes.Static) != 0);

                        if (!privateScope &&
                            duplicateType &&
                            fieldsByKey.TryGetValue(
                                fieldKey,
                                out MemberPlan existingField))
                        {
                            typeSource.Input.Map.Set(
                                source,
                                existingField.Output,
                                isDuplicate: true);
                            if (_request.FieldsWithoutRva.Contains(sourceEntity))
                                existingField.RemoveFieldRva = true;
                            if (type != _moduleType && !suppressed)
                                checkedFields++;
                            continue;
                        }

                        if (duplicateType &&
                            !privateScope &&
                            !suppressed &&
                            type != _moduleType)
                        {
                            throw new InvalidOperationException(
                                $"Duplicate type '{type.Key}' has no matching field " +
                                $"'{typeSource.Input.Reader.GetString(fieldDefinition.Name)}'.");
                        }

                        var output = MetadataTokens.FieldDefinitionHandle(++fieldRow);
                        typeSource.Input.Map.Set(source, output, isDuplicate: false);
                        var plan = new MemberPlan(
                            new SourceRow(typeSource.Input, source),
                            output,
                            type);
                        plan.RemoveFieldRva =
                            _request.FieldsWithoutRva.Contains(sourceEntity);
                        if ((fieldDefinition.Attributes & FieldAttributes.HasFieldRVA) != 0 &&
                            !plan.RemoveFieldRva)
                        {
                            if (!_request.FieldRvaOffsets.TryGetValue(
                                sourceEntity,
                                out int rvaOffset))
                            {
                                throw new InvalidOperationException(
                                    $"No final FieldRVA offset was supplied for '{sourceEntity}'.");
                            }
                            if (rvaOffset < 0)
                                throw new ArgumentOutOfRangeException(
                                    nameof(_request.FieldRvaOffsets),
                                    $"FieldRVA offset for '{sourceEntity}' must be non-negative.");
                            plan.FieldRvaOffset = rvaOffset;
                        }
                        if (!privateScope)
                            fieldsByKey.TryAdd(fieldKey, plan);
                        type.Fields.Add(plan);
                        _fields.Add(plan);
                    }

                    foreach (MethodDefinitionHandle source in definition.GetMethods())
                    {
                        if (!typeSource.Input.RetainMethod(source))
                            continue;

                        MethodDefinition methodDefinition =
                            typeSource.Input.Reader.GetMethodDefinition(source);
                        if (type == _moduleType &&
                            _request.ModuleInitializerBodyOffset >= 0 &&
                            typeSource.Input.Reader.GetString(methodDefinition.Name) == ".cctor")
                        {
                            throw new NotSupportedException(
                                $"Input '{typeSource.Input.Input.Identity}' contains an existing <Module>..cctor; composing module initializers is not supported.");
                        }
                        BlobBuilder rewrittenSignature = SignatureRewriter.RewriteMethod(
                            typeSource.Input.Reader.GetBlobReader(
                                methodDefinition.Signature),
                            typeSource.Input.Map);
                        string methodKey =
                            typeSource.Input.Reader.GetString(methodDefinition.Name) + "\0" +
                            Convert.ToHexString(rewrittenSignature.ToArray());
                        bool privateScope =
                            (methodDefinition.Attributes & MethodAttributes.MemberAccessMask) ==
                            MethodAttributes.PrivateScope;
                        bool suppressed = typeSuppressed ||
                            HasSuppressMergeCheck(typeSource.Input, source);

                        if (!privateScope &&
                            duplicateType &&
                            methodsByKey.TryGetValue(
                                methodKey,
                                out MemberPlan existingMethod))
                        {
                            VerifyDuplicateMethod(
                                typeSource.Input,
                                source,
                                existingMethod,
                                type == _moduleType);
                            if (type != _moduleType && !suppressed)
                                checkedMethods++;
                            continue;
                        }

                        if (duplicateType &&
                            !privateScope &&
                            !suppressed &&
                            type != _moduleType)
                        {
                            throw new InvalidOperationException(
                                $"Duplicate type '{type.Key}' has no matching method " +
                                $"'{typeSource.Input.Reader.GetString(methodDefinition.Name)}'.");
                        }

                        var output = MetadataTokens.MethodDefinitionHandle(++methodRow);
                        typeSource.Input.Map.Set(source, output, isDuplicate: false);
                        var plan = new MemberPlan(
                            new SourceRow(typeSource.Input, source),
                            output,
                            type);
                        var key = new MetadataSourceEntity(
                            typeSource.Input.Input.Identity,
                            source);
                        plan.BodyOffset = _request.MethodBodyOffsets.TryGetValue(
                            key,
                            out int bodyOffset)
                            ? bodyOffset
                            : -1;
                        if (plan.BodyOffset < 0 &&
                            RequiresMethodBodyOffset(methodDefinition))
                        {
                            throw new InvalidOperationException(
                                $"No final method body offset was supplied for '{key}'.");
                        }
                        if (plan.BodyOffset < -1)
                        {
                            throw new ArgumentOutOfRangeException(
                                nameof(_request.MethodBodyOffsets),
                                $"Method body offset for '{key}' must be -1 or non-negative.");
                        }
                        if (!privateScope)
                            methodsByKey.TryAdd(methodKey, plan);
                        type.Methods.Add(plan);
                        _methods.Add(plan);

                        plan.FirstParameter = MetadataTokens.ParameterHandle(parameterRow + 1);
                        foreach (ParameterHandle parameter in
                                 methodDefinition.GetParameters())
                        {
                            var outputParameter =
                                MetadataTokens.ParameterHandle(++parameterRow);
                            typeSource.Input.Map.Set(
                                parameter,
                                outputParameter,
                                isDuplicate: false);
                            Parameter value =
                                typeSource.Input.Reader.GetParameter(parameter);
                            plan.Parameters.Add(
                                value.SequenceNumber,
                                outputParameter);
                            _parameters.Add(
                                new SourceRow(typeSource.Input, parameter));
                        }
                    }

                    if (!duplicateType)
                    {
                        type.CheckedFieldCount = definition.GetFields().Count(field =>
                        {
                            if (!typeSource.Input.RetainField(field))
                                return false;
                            FieldDefinition value =
                                typeSource.Input.Reader.GetFieldDefinition(field);
                            bool privateScope =
                                (value.Attributes & FieldAttributes.FieldAccessMask) ==
                                FieldAttributes.PrivateScope;
                            bool suppressed = HasSuppressMergeCheck(
                                typeSource.Input,
                                field) ||
                                (typeSuppressed &&
                                 (value.Attributes & FieldAttributes.Static) != 0);
                            return !privateScope && !suppressed;
                        });
                        type.CheckedMethodCount = definition.GetMethods().Count(method =>
                        {
                            if (!typeSource.Input.RetainMethod(method))
                                return false;
                            MethodDefinition value =
                                typeSource.Input.Reader.GetMethodDefinition(method);
                            bool privateScope =
                                (value.Attributes & MethodAttributes.MemberAccessMask) ==
                                MethodAttributes.PrivateScope;
                            return !privateScope &&
                                !typeSuppressed &&
                                !HasSuppressMergeCheck(typeSource.Input, method);
                        });
                    }
                    else if (type != _moduleType)
                    {
                        if (checkedFields != type.CheckedFieldCount)
                        {
                            throw new InvalidOperationException(
                                $"Duplicate type '{type.Key}' has a different field count.");
                        }
                        if (checkedMethods != type.CheckedMethodCount)
                        {
                            throw new InvalidOperationException(
                                $"Duplicate type '{type.Key}' has a different method count.");
                        }
                    }
                }

                foreach ((InputState input,
                          FieldDefinitionHandle source,
                          MetadataSourceEntity sourceEntity,
                          MetadataSourceEntity target) in pendingFieldBindings)
                {
                    InputState targetInput = GetInput(target.InputIdentity);
                    if (!targetInput.Map.TryGetMapping(
                            target.Handle,
                            out LinkTokenMap.Mapping targetMapping))
                    {
                        throw new InvalidOperationException(
                            $"Canonical global field '{target}' was not planned for alias '{sourceEntity}'.");
                    }
                    input.Map.Set(
                        source,
                        targetMapping.Destination,
                        isDuplicate: true);
                }

                if (type == _moduleType &&
                    _request.ModuleInitializerBodyOffset >= 0)
                {
                    var output = MetadataTokens.MethodDefinitionHandle(++methodRow);
                    var plan = new MemberPlan(default, output, type)
                    {
                        IsModuleInitializer = true,
                        BodyOffset = _request.ModuleInitializerBodyOffset,
                        FirstParameter = MetadataTokens.ParameterHandle(parameterRow + 1),
                    };
                    type.Methods.Add(plan);
                    _methods.Add(plan);
                }
            }

            void VerifyDuplicateMethod(
                InputState input,
                MethodDefinitionHandle source,
                MemberPlan existing,
                bool global)
            {
                MethodDefinition incoming = input.Reader.GetMethodDefinition(source);
                MethodDefinition canonical =
                    existing.Source.Input.Reader.GetMethodDefinition(
                        (MethodDefinitionHandle)existing.Source.Handle);
                MethodImplAttributes existingImpl =
                    existing.ImplAttributesOverride ?? canonical.ImplAttributes;
                if ((incoming.ImplAttributes & MethodImplAttributes.ForwardRef) == 0)
                {
                    if ((existingImpl & MethodImplAttributes.ForwardRef) != 0)
                    {
                        existing.ImplAttributesOverride = incoming.ImplAttributes;
                        var key = new MetadataSourceEntity(
                            input.Input.Identity,
                            source);
                        existing.BodyOffset = _request.MethodBodyOffsets.TryGetValue(
                            key,
                            out int bodyOffset)
                            ? bodyOffset
                            : -1;
                        if (existing.BodyOffset < 0 &&
                            RequiresMethodBodyOffset(incoming))
                        {
                            throw new InvalidOperationException(
                                $"No final method body offset was supplied for '{key}'.");
                        }
                    }
                    else if (existingImpl != incoming.ImplAttributes)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate method '{input.Reader.GetString(incoming.Name)}' " +
                            "has inconsistent implementation flags.");
                    }
                }

                ParameterHandle[] incomingParameters = incoming.GetParameters().ToArray();
                if (!global &&
                    incomingParameters.Length != existing.Parameters.Count)
                {
                    throw new InvalidOperationException(
                        $"Duplicate method '{input.Reader.GetString(incoming.Name)}' " +
                        "has a different parameter count.");
                }

                foreach (ParameterHandle parameterHandle in incomingParameters)
                {
                    Parameter parameter = input.Reader.GetParameter(parameterHandle);
                    if (!existing.Parameters.TryGetValue(
                        parameter.SequenceNumber,
                        out ParameterHandle output))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate method '{input.Reader.GetString(incoming.Name)}' " +
                            $"has no parameter with sequence {parameter.SequenceNumber}.");
                    }

                    Parameter canonicalParameter =
                        existing.Source.Input.Reader.GetParameter(
                            canonical.GetParameters().Single(candidate =>
                                existing.Source.Input.Reader.GetParameter(candidate)
                                    .SequenceNumber == parameter.SequenceNumber));
                    if (canonicalParameter.Attributes != parameter.Attributes)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate method '{input.Reader.GetString(incoming.Name)}' " +
                            $"has inconsistent flags for parameter sequence " +
                            $"{parameter.SequenceNumber}.");
                    }
                    input.Map.Set(
                        parameterHandle,
                        output,
                        isDuplicate: true);
                }

                input.Map.Set(
                    source,
                    existing.Output,
                    isDuplicate: true);
            }
        }

        private void PlanMemberReferences()
        {
            var definitions = new Dictionary<MemberKey, EntityHandle>();
            foreach (MemberPlan field in _fields)
            {
                FieldDefinition definition =
                    field.Source.Input.Reader.GetFieldDefinition(
                        (FieldDefinitionHandle)field.Source.Handle);
                if ((definition.Attributes & FieldAttributes.FieldAccessMask) ==
                    FieldAttributes.PrivateScope)
                {
                    continue;
                }
                byte[] signature = SignatureRewriter.RewriteField(
                    field.Source.Input.Reader.GetBlobReader(definition.Signature),
                    field.Source.Input.Map).ToArray();
                definitions.TryAdd(
                    new MemberKey(
                        field.Owner.Output,
                        field.Source.Input.Reader.GetString(definition.Name),
                        MemberReferenceKind.Field,
                        Convert.ToHexString(signature)),
                    field.Output);
            }

            foreach (MemberPlan method in _methods)
            {
                if (method.IsModuleInitializer)
                    continue;

                MethodDefinition definition =
                    method.Source.Input.Reader.GetMethodDefinition(
                        (MethodDefinitionHandle)method.Source.Handle);
                if ((definition.Attributes & MethodAttributes.MemberAccessMask) ==
                    MethodAttributes.PrivateScope)
                {
                    continue;
                }
                byte[] signature = SignatureRewriter.RewriteMethod(
                    method.Source.Input.Reader.GetBlobReader(definition.Signature),
                    method.Source.Input.Map).ToArray();
                definitions.TryAdd(
                    new MemberKey(
                        method.Owner.Output,
                        method.Source.Input.Reader.GetString(definition.Name),
                        MemberReferenceKind.Method,
                        Convert.ToHexString(signature)),
                    method.Output);
            }

            int memberRefRow = 0;
            var memberReferences = new Dictionary<MemberKey, MemberReferenceHandle>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.MemberRef);
                     row++)
                {
                    var source = MetadataTokens.MemberReferenceHandle(row);
                    if (!input.IsMarked(source))
                        continue;

                    MemberReference reference = input.Reader.GetMemberReference(source);
                    var sourceEntity = new MetadataSourceEntity(input.Input.Identity, source);
                    EntityHandle boundParent = default;
                    if (_request.ReferenceBindings.TryGetValue(
                        sourceEntity,
                        out MetadataSourceEntity binding))
                    {
                        InputState targetInput = GetInput(binding.InputIdentity);
                        EntityHandle target = targetInput.Map.Map(binding.Handle);
                        bool isVararg = reference.GetKind() == MemberReferenceKind.Method &&
                            SignatureRewriter.TryRewriteVarargMethodFixed(
                                input.Reader.GetBlobReader(reference.Signature),
                                input.Map,
                                out _);
                        if (isVararg && target.Kind == HandleKind.MethodDefinition)
                        {
                            boundParent = target;
                        }
                        else
                        {
                            input.Map.Set(source, target, isDuplicate: true);
                            continue;
                        }
                    }

                    EntityHandle parent = boundParent.IsNil
                        ? MapMemberReferenceParent(input, reference.Parent)
                        : boundParent;
                    byte[] signature = SignatureRewriter.RewriteMemberReference(
                        input.Reader.GetBlobReader(reference.Signature),
                        input.Map).ToArray();
                    string name = input.Reader.GetString(reference.Name);
                    MemberReferenceKind kind = reference.GetKind();

                    if (parent.Kind == HandleKind.TypeDefinition)
                    {
                        EntityHandle unresolvedParent = default;
                        if (kind == MemberReferenceKind.Method &&
                            SignatureRewriter.TryRewriteVarargMethodFixed(
                                input.Reader.GetBlobReader(reference.Signature),
                                input.Map,
                                out BlobBuilder fixedSignature) &&
                            TryFindDefinition(
                                (TypeDefinitionHandle)parent,
                                name,
                                kind,
                                Convert.ToHexString(fixedSignature.ToArray()),
                                out EntityHandle varargDefinition,
                                out unresolvedParent))
                        {
                            parent = varargDefinition;
                        }
                        else if (kind == MemberReferenceKind.Method &&
                                !unresolvedParent.IsNil)
                        {
                            parent = unresolvedParent;
                        }
                        else if (TryFindDefinition(
                            (TypeDefinitionHandle)parent,
                            name,
                            kind,
                            Convert.ToHexString(signature),
                            out EntityHandle definition,
                            out unresolvedParent))
                        {
                            input.Map.Set(source, definition, isDuplicate: true);
                            continue;
                        }
                        else if (!unresolvedParent.IsNil)
                        {
                            parent = unresolvedParent;
                        }
                    }

                    var key = new MemberKey(
                        parent,
                        name,
                        kind,
                        Convert.ToHexString(signature));
                    if (memberReferences.TryGetValue(
                        key,
                        out MemberReferenceHandle existing))
                    {
                        input.Map.Set(source, existing, isDuplicate: true);
                    }
                    else
                    {
                        var output = MetadataTokens.MemberReferenceHandle(++memberRefRow);
                        input.Map.Set(source, output, isDuplicate: false);
                        memberReferences.Add(key, output);
                        var sourceInfo = new SourceRow(input, source);
                        _memberRefs.Add(sourceInfo);
                        _rewrittenMemberRefSignatures.Add(sourceInfo, signature);
                        _memberRefParents.Add(sourceInfo, parent);
                        _memberRefParentsByOutput.Add(output, parent);
                    }
                }
            }

            bool TryFindDefinition(
                TypeDefinitionHandle type,
                string name,
                MemberReferenceKind kind,
                string signature,
                out EntityHandle definition,
                out EntityHandle unresolvedParent)
            {
                var visited = new HashSet<TypeDefinitionHandle>();
                unresolvedParent = default;
                while (visited.Add(type))
                {
                    if (definitions.TryGetValue(
                        new MemberKey(type, name, kind, signature),
                        out definition))
                    {
                        return true;
                    }

                    if (!_typesByOutput.TryGetValue(type, out TypePlan plan) ||
                        plan == _moduleType)
                    {
                        break;
                    }
                    TypeDefinition source =
                        plan.Canonical.Input.Reader.GetTypeDefinition(
                            (TypeDefinitionHandle)plan.Canonical.Handle);
                    if (source.BaseType.IsNil)
                        break;
                    EntityHandle baseType =
                        plan.Canonical.Input.Map.Map(source.BaseType);
                    if (baseType.Kind != HandleKind.TypeDefinition)
                    {
                        if (baseType.Kind == HandleKind.TypeReference)
                            unresolvedParent = baseType;
                        break;
                    }
                    type = (TypeDefinitionHandle)baseType;
                }

                definition = default;
                return false;
            }
        }

        private void PlanRemainingRows()
        {
            PlanStandaloneSignatures();
            PlanMethodSpecifications();
            PlanGenerics();
            PlanInterfaces();
            PlanConstants();
            PlanMethodImplementations();
            PlanCustomAttributes();
        }

        private void PlanStandaloneSignatures()
        {
            int outputRow = 0;
            var signatures =
                new Dictionary<string, StandaloneSignatureHandle>(StringComparer.Ordinal);
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.StandAloneSig);
                     row++)
                {
                    var source = MetadataTokens.StandaloneSignatureHandle(row);
                    if (!input.IsMarked(source))
                        continue;

                    StandaloneSignature signature =
                        input.Reader.GetStandaloneSignature(source);
                    byte[] rewritten = SignatureRewriter.RewriteStandalone(
                        input.Reader.GetBlobReader(signature.Signature),
                        input.Map).ToArray();
                    string key = Convert.ToHexString(rewritten);
                    bool duplicate = signatures.TryGetValue(
                        key,
                        out StandaloneSignatureHandle output);
                    if (!duplicate)
                    {
                        output = MetadataTokens.StandaloneSignatureHandle(++outputRow);
                        signatures.Add(key, output);
                        _standaloneSignatures.Add(new SourceRow(input, source));
                    }
                    input.Map.Set(source, output, duplicate);
                }
            }
        }

        private void PlanGenerics()
        {
            var newParameters = new List<SourceRow>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.GenericParam);
                     row++)
                {
                    var source = MetadataTokens.GenericParameterHandle(row);
                    GenericParameter parameter = input.Reader.GetGenericParameter(source);
                    if (!input.IsMarked(source) ||
                        !input.Map.TryGetMapping(
                            parameter.Parent,
                            out LinkTokenMap.Mapping owner) ||
                        owner.IsDuplicate)
                    {
                        continue;
                    }
                    newParameters.Add(new SourceRow(input, source));
                }
            }

            newParameters.Sort(static (left, right) =>
            {
                GenericParameter leftParameter =
                    left.Input.Reader.GetGenericParameter(
                        (GenericParameterHandle)left.Handle);
                GenericParameter rightParameter =
                    right.Input.Reader.GetGenericParameter(
                        (GenericParameterHandle)right.Handle);
                int owner = CodedIndex.TypeOrMethodDef(
                    left.Input.Map.Map(leftParameter.Parent)).CompareTo(
                    CodedIndex.TypeOrMethodDef(
                        right.Input.Map.Map(rightParameter.Parent)));
                if (owner != 0)
                    return owner;
                int index = leftParameter.Index.CompareTo(rightParameter.Index);
                return index != 0 ? index : CompareSource(left, right);
            });

            var parametersByKey =
                new Dictionary<(EntityHandle Owner, int Index),
                    (SourceRow Source, GenericParameterHandle Output)>();
            int parameterRow = 0;
            foreach (SourceRow source in newParameters)
            {
                GenericParameter parameter =
                    source.Input.Reader.GetGenericParameter(
                        (GenericParameterHandle)source.Handle);
                var key = (
                    source.Input.Map.Map(parameter.Parent),
                    parameter.Index);
                if (parametersByKey.ContainsKey(key))
                {
                    throw new BadImageFormatException(
                        $"Input '{source.Input.Input.Identity}' has duplicate generic " +
                        $"parameter index {parameter.Index} for one owner.");
                }

                var output = MetadataTokens.GenericParameterHandle(++parameterRow);
                source.Input.Map.Set(source.Handle, output, isDuplicate: false);
                parametersByKey.Add(key, (source, output));
                _genericParameters.Add(source);
            }

            var newConstraints = new List<SourceRow>();
            foreach (SourceRow parameterSource in newParameters)
            {
                GenericParameter parameter =
                    parameterSource.Input.Reader.GetGenericParameter(
                        (GenericParameterHandle)parameterSource.Handle);
                foreach (GenericParameterConstraintHandle constraint in
                         parameter.GetConstraints())
                {
                    if (parameterSource.Input.IsMarked(constraint))
                    {
                        newConstraints.Add(
                            new SourceRow(parameterSource.Input, constraint));
                    }
                }
            }
            newConstraints.Sort(static (left, right) =>
            {
                GenericParameterConstraint leftConstraint =
                    left.Input.Reader.GetGenericParameterConstraint(
                        (GenericParameterConstraintHandle)left.Handle);
                GenericParameterConstraint rightConstraint =
                    right.Input.Reader.GetGenericParameterConstraint(
                        (GenericParameterConstraintHandle)right.Handle);
                int owner = MetadataTokens.GetRowNumber(
                    left.Input.Map.Map(leftConstraint.Parameter)).CompareTo(
                    MetadataTokens.GetRowNumber(
                        right.Input.Map.Map(rightConstraint.Parameter)));
                return owner != 0 ? owner : CompareSource(left, right);
            });

            var constraintsByKey =
                new Dictionary<(GenericParameterHandle Parameter, EntityHandle Type),
                    GenericParameterConstraintHandle>();
            int constraintRow = 0;
            foreach (SourceRow source in newConstraints)
            {
                GenericParameterConstraint constraint =
                    source.Input.Reader.GetGenericParameterConstraint(
                        (GenericParameterConstraintHandle)source.Handle);
                var key = (
                    (GenericParameterHandle)source.Input.Map.Map(
                        constraint.Parameter),
                    source.Input.Map.Map(constraint.Type));
                var output =
                    MetadataTokens.GenericParameterConstraintHandle(++constraintRow);
                constraintsByKey.Add(key, output);
                _genericConstraints.Add(source);
            }

            foreach (InputState input in _inputs)
            {
                VerifyDuplicateGenericOwners(
                    input,
                    TableIndex.TypeDef,
                    static row => MetadataTokens.TypeDefinitionHandle(row));
                VerifyDuplicateGenericOwners(
                    input,
                    TableIndex.MethodDef,
                    static row => MetadataTokens.MethodDefinitionHandle(row));
            }

            void VerifyDuplicateGenericOwners(
                InputState input,
                TableIndex table,
                Func<int, EntityHandle> handleFactory)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(table);
                     row++)
                {
                    EntityHandle ownerSource = handleFactory(row);
                    if (!input.IsMarked(ownerSource) ||
                        !input.Map.TryGetMapping(
                            ownerSource,
                            out LinkTokenMap.Mapping ownerMapping) ||
                        !ownerMapping.IsDuplicate)
                    {
                        continue;
                    }

                    GenericParameterHandle[] incoming = ownerSource.Kind switch
                    {
                        HandleKind.TypeDefinition => input.Reader
                            .GetTypeDefinition((TypeDefinitionHandle)ownerSource)
                            .GetGenericParameters()
                            .Where(handle => input.IsMarked(handle))
                            .ToArray(),
                        HandleKind.MethodDefinition => input.Reader
                            .GetMethodDefinition((MethodDefinitionHandle)ownerSource)
                            .GetGenericParameters()
                            .Where(handle => input.IsMarked(handle))
                            .ToArray(),
                        _ => throw new InvalidOperationException(),
                    };
                    var existing = parametersByKey
                        .Where(pair => pair.Key.Owner == ownerMapping.Destination)
                        .ToDictionary(pair => pair.Key.Index, pair => pair.Value);
                    if (incoming.Length != existing.Count)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate generic owner 0x{MetadataTokens.GetToken(ownerSource):X8} " +
                            $"in '{input.Input.Identity}' has a different generic parameter count.");
                    }

                    foreach (GenericParameterHandle incomingHandle in incoming)
                    {
                        GenericParameter incomingParameter =
                            input.Reader.GetGenericParameter(incomingHandle);
                        if (!existing.TryGetValue(
                            incomingParameter.Index,
                            out var canonical))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate generic owner in '{input.Input.Identity}' has no " +
                                $"generic parameter at index {incomingParameter.Index}.");
                        }

                        GenericParameter canonicalParameter =
                            canonical.Source.Input.Reader.GetGenericParameter(
                                (GenericParameterHandle)canonical.Source.Handle);
                        if (incomingParameter.Attributes !=
                                canonicalParameter.Attributes ||
                            input.Reader.GetString(incomingParameter.Name) !=
                                canonical.Source.Input.Reader.GetString(
                                    canonicalParameter.Name))
                        {
                            throw new InvalidOperationException(
                                $"Generic parameter {incomingParameter.Index} on duplicate " +
                                $"owner in '{input.Input.Identity}' is inconsistent.");
                        }

                        GenericParameterConstraintHandle[] incomingConstraints =
                            incomingParameter.GetConstraints()
                                .Where(handle => input.IsMarked(handle))
                                .ToArray();
                        EntityHandle[] incomingTypes = incomingConstraints
                            .Select(constraint => input.Map.Map(
                                input.Reader.GetGenericParameterConstraint(constraint).Type))
                            .ToArray();
                        EntityHandle[] canonicalTypes = canonicalParameter.GetConstraints()
                            .Where(handle =>
                                canonical.Source.Input.IsMarked(handle))
                            .Select(constraint => canonical.Source.Input.Map.Map(
                                canonical.Source.Input.Reader
                                    .GetGenericParameterConstraint(constraint).Type))
                            .ToArray();
                        if (incomingTypes.Length != canonicalTypes.Length ||
                            !incomingTypes.ToHashSet().SetEquals(canonicalTypes))
                        {
                            throw new InvalidOperationException(
                                $"Generic parameter {incomingParameter.Index} on duplicate " +
                                $"owner in '{input.Input.Identity}' has inconsistent constraints.");
                        }

                        input.Map.Set(
                            incomingHandle,
                            canonical.Output,
                            isDuplicate: true);
                        foreach (GenericParameterConstraintHandle constraintHandle in
                                 incomingConstraints)
                        {
                            GenericParameterConstraint constraint =
                                input.Reader.GetGenericParameterConstraint(constraintHandle);
                            var constraintKey = (
                                canonical.Output,
                                input.Map.Map(constraint.Type));
                            _ = constraintsByKey[constraintKey];
                        }
                    }
                }
            }

        }

        private void PlanMethodSpecifications()
        {
            int outputRow = 0;
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.MethodSpec);
                     row++)
                {
                    var source = MetadataTokens.MethodSpecificationHandle(row);
                    if (!input.IsMarked(source))
                        continue;

                    MethodSpecification specification =
                        input.Reader.GetMethodSpecification(source);
                    if (!input.Map.TryMap(specification.Method, out _))
                        continue;
                    input.Map.Set(
                        source,
                        MetadataTokens.MethodSpecificationHandle(++outputRow),
                        isDuplicate: false);
                    _methodSpecs.Add(new SourceRow(input, source));
                }
            }
        }

        private void PlanInterfaces()
        {
            var entries = new List<InterfacePlan>();
            var entryKeys =
                new HashSet<(TypeDefinitionHandle Type, EntityHandle Interface)>();
            var duplicates =
                new List<(SourceRow Source,
                    (TypeDefinitionHandle Type, EntityHandle Interface) Key)>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.InterfaceImpl);
                     row++)
                {
                    var sourceHandle = MetadataTokens.InterfaceImplementationHandle(row);
                    if (!input.IsMarked(sourceHandle))
                        continue;

                    InterfaceImplementation source =
                        input.Reader.GetInterfaceImplementation(sourceHandle);
                    TypeDefinitionHandle sourceOwner =
                        GetInterfaceOwner(input, sourceHandle);
                    if (!input.Map.TryGetMapping(
                        sourceOwner,
                        out LinkTokenMap.Mapping owner))
                    {
                        continue;
                    }

                    var key = (
                        (TypeDefinitionHandle)owner.Destination,
                        input.Map.Map(source.Interface));
                    var sourceRow = new SourceRow(input, sourceHandle);
                    if (owner.IsDuplicate)
                    {
                        if (!entryKeys.Contains(key))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate type has no matching InterfaceImpl " +
                                $"0x{MetadataTokens.GetToken(sourceHandle):X8} in " +
                                $"'{input.Input.Identity}'.");
                        }
                        duplicates.Add((sourceRow, key));
                    }
                    else
                    {
                        entryKeys.Add(key);
                        entries.Add(new InterfacePlan(
                            sourceRow,
                            key.Item1,
                            key.Item2));
                    }
                }
            }

            entries.Sort(static (left, right) =>
            {
                int type = MetadataTokens.GetRowNumber(left.Type).CompareTo(
                    MetadataTokens.GetRowNumber(right.Type));
                return type != 0
                    ? type
                    : CompareSource(left.Source, right.Source);
            });
            var outputs =
                new Dictionary<(TypeDefinitionHandle Type, EntityHandle Interface),
                    InterfaceImplementationHandle>();
            for (int i = 0; i < entries.Count; i++)
            {
                InterfacePlan plan = entries[i];
                var output = MetadataTokens.InterfaceImplementationHandle(i + 1);
                plan.Source.Input.Map.Set(
                    plan.Source.Handle,
                    output,
                    isDuplicate: false);
                outputs.Add((plan.Type, plan.Interface), output);
                _interfaces.Add(plan);
            }
            foreach (var duplicate in duplicates)
            {
                duplicate.Source.Input.Map.Set(
                    duplicate.Source.Handle,
                    outputs[duplicate.Key],
                    isDuplicate: true);
            }
        }

        private void PlanConstants()
        {
            var entries = new List<ConstantPlan>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.Constant);
                     row++)
                {
                    var sourceHandle = MetadataTokens.ConstantHandle(row);
                    Constant source = input.Reader.GetConstant(sourceHandle);
                    if (!input.Map.TryGetMapping(
                        source.Parent,
                        out LinkTokenMap.Mapping parentMapping) ||
                        parentMapping.IsDuplicate)
                    {
                        continue;
                    }
                    object value = input.Reader.GetBlobReader(source.Value)
                        .ReadConstant(source.TypeCode);
                    entries.Add(new ConstantPlan(
                        new SourceRow(input, sourceHandle),
                        parentMapping.Destination,
                        value));
                }
            }
            entries.Sort(static (left, right) =>
            {
                int parent = CodedIndex.HasConstant(left.Parent).CompareTo(
                    CodedIndex.HasConstant(right.Parent));
                return parent != 0
                    ? parent
                    : CompareSource(left.Source, right.Source);
            });
            for (int i = 0; i < entries.Count; i++)
            {
                ConstantPlan plan = entries[i];
                _constants.Add(plan);
            }
        }

        private void PlanMethodImplementations()
        {
            var entries = new List<MethodImplementationPlan>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.MethodImpl);
                     row++)
                {
                    var sourceHandle = MetadataTokens.MethodImplementationHandle(row);
                    if (!input.IsMarked(sourceHandle))
                        continue;

                    MethodImplementation source =
                        input.Reader.GetMethodImplementation(sourceHandle);
                    TypeDefinitionHandle sourceOwner =
                        GetMethodImplementationOwner(input, sourceHandle);
                    if (!input.Map.TryGetMapping(
                            sourceOwner,
                            out LinkTokenMap.Mapping owner) ||
                        owner.IsDuplicate ||
                        !input.Map.TryMap(source.MethodBody, out EntityHandle body) ||
                        !input.Map.TryMap(
                            source.MethodDeclaration,
                            out EntityHandle declaration))
                    {
                        continue;
                    }
                    entries.Add(new MethodImplementationPlan(
                        new SourceRow(input, sourceHandle),
                    (TypeDefinitionHandle)owner.Destination,
                        body,
                        declaration));
                }
            }
            entries.Sort(static (left, right) =>
            {
                int type = MetadataTokens.GetRowNumber(left.Type).CompareTo(
                    MetadataTokens.GetRowNumber(right.Type));
                return type != 0
                    ? type
                    : CompareSource(left.Source, right.Source);
            });
            for (int i = 0; i < entries.Count; i++)
            {
                MethodImplementationPlan plan = entries[i];
                _methodImplementations.Add(plan);
            }
        }

        private void PlanCustomAttributes()
        {
            var entries = new List<CustomAttributePlan>();
            var attributesByKey =
                new Dictionary<CustomAttributeKey, CustomAttributePlan>();
            foreach (InputState input in _inputs)
            {
                for (int row = 1;
                     row <= input.Reader.GetTableRowCount(TableIndex.CustomAttribute);
                     row++)
                {
                    var sourceHandle = MetadataTokens.CustomAttributeHandle(row);
                    if (!input.IsMarked(sourceHandle))
                        continue;

                    CustomAttribute source =
                        input.Reader.GetCustomAttribute(sourceHandle);
                    if (!input.IsMarked(source.Constructor))
                        continue;
                    if (!input.Map.TryGetMapping(
                        source.Parent,
                        out LinkTokenMap.Mapping parent))
                    {
                        throw new BadImageFormatException(
                            $"Marked CustomAttribute 0x{MetadataTokens.GetToken(sourceHandle):X8} " +
                            $"has no mapped parent in '{input.Input.Identity}'.");
                    }
                    if (!input.Map.TryMap(
                        source.Constructor,
                        out EntityHandle constructor))
                    {
                        throw new BadImageFormatException(
                            $"Marked CustomAttribute 0x{MetadataTokens.GetToken(sourceHandle):X8} " +
                            $"has no mapped constructor in '{input.Input.Identity}'.");
                    }
                    if (source.Parent.Kind != parent.Destination.Kind ||
                        IsSuppressMergeCheckConstructor(input, source.Constructor))
                    {
                        continue;
                    }
                    if (source.Parent.Kind == HandleKind.MemberReference &&
                        parent.Destination.Kind == HandleKind.MemberReference &&
                        _memberRefParentsByOutput.TryGetValue(
                            (MemberReferenceHandle)parent.Destination,
                            out EntityHandle memberParent) &&
                        memberParent.Kind == HandleKind.MethodDefinition)
                    {
                        continue;
                    }

                    string value = source.Value.IsNil
                        ? string.Empty
                        : Convert.ToHexString(input.Reader.GetBlobBytes(source.Value));
                    var key = new CustomAttributeKey(
                        parent.Destination,
                        constructor,
                        value);
                    var sourceRow = new SourceRow(input, sourceHandle);
                    if (parent.IsDuplicate &&
                        attributesByKey.TryGetValue(
                            key,
                            out CustomAttributePlan existing))
                    {
                        existing.DuplicateSources.Add(sourceRow);
                        continue;
                    }

                    if (parent.IsDuplicate &&
                        !IsAdditiveCustomAttribute(
                            input,
                            source.Parent,
                            source.Constructor))
                    {
                        throw new InvalidOperationException(
                            $"CustomAttribute 0x{MetadataTokens.GetToken(sourceHandle):X8} " +
                            $"on duplicate parent has no exact match in " +
                            $"'{input.Input.Identity}'.");
                    }

                    var plan = new CustomAttributePlan(
                        sourceRow,
                        parent.Destination,
                        constructor,
                        parent.IsDuplicate);
                    entries.Add(plan);
                    attributesByKey.TryAdd(key, plan);
                }
            }
            entries.Sort(static (left, right) =>
            {
                int parent = CodedIndex.HasCustomAttribute(left.Parent).CompareTo(
                    CodedIndex.HasCustomAttribute(right.Parent));
                return parent != 0
                    ? parent
                    : CompareSource(left.Source, right.Source);
            });
            for (int i = 0; i < entries.Count; i++)
            {
                CustomAttributePlan plan = entries[i];
                plan.Output = MetadataTokens.CustomAttributeHandle(i + 1);
                plan.Source.Input.Map.Set(
                    plan.Source.Handle,
                    plan.Output,
                    isDuplicate: plan.IsDuplicate);
                foreach (SourceRow duplicate in plan.DuplicateSources)
                {
                    duplicate.Input.Map.Set(
                        duplicate.Handle,
                        plan.Output,
                        isDuplicate: true);
                }
                _customAttributes.Add(plan);
            }
        }

        private void EmitModuleAndAssembly()
        {
            Guid mvid = _request.ModuleVersionId ?? CreateDeterministicMvid();
            ModuleDefinitionHandle module = _output.AddModule(
                0,
                _output.GetOrAddString(_request.ModuleName),
                _output.GetOrAddGuid(mvid),
                default,
                default);
            LinkTokenMap.AssertHandle(EntityHandle.ModuleDefinition, module);

            AssemblyDefinitionHandle assembly = _output.AddAssembly(
                _output.GetOrAddString(_request.AssemblyName),
                _request.AssemblyVersion ?? new Version(0, 0, 0, 0),
                GetString(_request.AssemblyCulture),
                GetBlob(_request.AssemblyPublicKey),
                _request.AssemblyFlags,
                _request.AssemblyHashAlgorithm);
            LinkTokenMap.AssertHandle(EntityHandle.AssemblyDefinition, assembly);
        }

        private void EmitReferences()
        {
            foreach (ReferencePlan<AssemblyReferenceHandle> plan in _assemblyRefs)
            {
                AssemblyReference source =
                    plan.Source.Input.Reader.GetAssemblyReference(
                        (AssemblyReferenceHandle)plan.Source.Handle);
                AssemblyReferenceHandle output = _output.AddAssemblyReference(
                    GetString(plan.Source.Input.Reader.GetString(source.Name)),
                    source.Version,
                    GetString(plan.Source.Input.Reader.GetString(source.Culture)),
                    CopyBlob(plan.Source.Input.Reader, source.PublicKeyOrToken),
                    source.Flags,
                    CopyBlob(plan.Source.Input.Reader, source.HashValue));
                LinkTokenMap.AssertHandle(plan.Output, output);
            }

            foreach (ReferencePlan<ModuleReferenceHandle> plan in _moduleRefs)
            {
                ModuleReference source =
                    plan.Source.Input.Reader.GetModuleReference(
                        (ModuleReferenceHandle)plan.Source.Handle);
                ModuleReferenceHandle output = _output.AddModuleReference(
                    GetString(plan.Source.Input.Reader.GetString(source.Name)));
                LinkTokenMap.AssertHandle(plan.Output, output);
            }
        }

        private void EmitTypeReferences()
        {
            foreach (SourceRow sourceInfo in _typeRefs)
            {
                var source = (TypeReferenceHandle)sourceInfo.Handle;
                TypeReference reference =
                    sourceInfo.Input.Reader.GetTypeReference(source);
                TypeReferenceHandle output = _output.AddTypeReference(
                    reference.ResolutionScope.IsNil
                        ? default
                        : MapTypeReferenceResolutionScope(
                            sourceInfo.Input,
                            reference.ResolutionScope),
                    GetString(sourceInfo.Input.Reader.GetString(reference.Namespace)),
                    GetString(sourceInfo.Input.Reader.GetString(reference.Name)));
                LinkTokenMap.AssertHandle(sourceInfo.Input.Map.Map(source), output);
            }
        }

        private void EmitTypeSpecifications()
        {
            foreach (SourceRow sourceInfo in _typeSpecs)
            {
                var source = (TypeSpecificationHandle)sourceInfo.Handle;
                TypeSpecification specification =
                    sourceInfo.Input.Reader.GetTypeSpecification(source);
                BlobBuilder signature = SignatureRewriter.RewriteTypeSpecification(
                    sourceInfo.Input.Reader.GetBlobReader(specification.Signature),
                    sourceInfo.Input.Map);
                TypeSpecificationHandle output =
                    _output.AddTypeSpecification(_output.GetOrAddBlob(signature));
                LinkTokenMap.AssertHandle(sourceInfo.Input.Map.Map(source), output);
            }
        }

        private void EmitTypeDefinitions()
        {
            TypeDefinitionHandle module = _output.AddTypeDefinition(
                TypeAttributes.Class,
                default,
                _output.GetOrAddString("<Module>"),
                default,
                _moduleType.FirstField,
                _moduleType.FirstMethod);
            LinkTokenMap.AssertHandle(_moduleType.Output, module);

            foreach (TypePlan plan in _types.Skip(1))
            {
                SourceRow sourceInfo = plan.Canonical;
                TypeDefinition source =
                    sourceInfo.Input.Reader.GetTypeDefinition(
                        (TypeDefinitionHandle)sourceInfo.Handle);
                TypeDefinitionHandle output = _output.AddTypeDefinition(
                    source.Attributes,
                    GetString(sourceInfo.Input.Reader.GetString(source.Namespace)),
                    GetString(sourceInfo.Input.Reader.GetString(source.Name)),
                    source.BaseType.IsNil
                        ? default
                        : sourceInfo.Input.Map.Map(source.BaseType),
                    plan.FirstField,
                    plan.FirstMethod);
                LinkTokenMap.AssertHandle(plan.Output, output);
            }
        }

        private void EmitFields()
        {
            foreach (MemberPlan plan in _fields)
            {
                var sourceHandle = (FieldDefinitionHandle)plan.Source.Handle;
                FieldDefinition source =
                    plan.Source.Input.Reader.GetFieldDefinition(sourceHandle);
                BlobBuilder signature = SignatureRewriter.RewriteField(
                    plan.Source.Input.Reader.GetBlobReader(source.Signature),
                    plan.Source.Input.Map);
                FieldDefinitionHandle output = _output.AddFieldDefinition(
                    plan.RemoveFieldRva
                        ? source.Attributes & ~FieldAttributes.HasFieldRVA
                        : source.Attributes,
                    GetString(plan.Source.Input.Reader.GetString(source.Name)),
                    _output.GetOrAddBlob(signature));
                LinkTokenMap.AssertHandle(plan.Output, output);
            }
        }

        private void EmitMethodsAndParameters()
        {
            foreach (MemberPlan plan in _methods)
            {
                if (plan.IsModuleInitializer)
                {
                    var initializerSignature = new BlobBuilder();
                    new BlobEncoder(initializerSignature)
                        .MethodSignature()
                        .Parameters(
                            0,
                            out ReturnTypeEncoder returnType,
                            out ParametersEncoder _);
                    returnType.Void();

                    MethodDefinitionHandle initializer = _output.AddMethodDefinition(
                        MethodAttributes.Private |
                            MethodAttributes.Static |
                            MethodAttributes.HideBySig |
                            MethodAttributes.SpecialName |
                            MethodAttributes.RTSpecialName,
                        MethodImplAttributes.IL | MethodImplAttributes.Managed,
                        _output.GetOrAddString(".cctor"),
                        _output.GetOrAddBlob(initializerSignature),
                        plan.BodyOffset,
                        plan.FirstParameter);
                    LinkTokenMap.AssertHandle(plan.Output, initializer);
                    continue;
                }

                var sourceHandle = (MethodDefinitionHandle)plan.Source.Handle;
                MethodDefinition source =
                    plan.Source.Input.Reader.GetMethodDefinition(sourceHandle);
                BlobBuilder signature = SignatureRewriter.RewriteMethod(
                    plan.Source.Input.Reader.GetBlobReader(source.Signature),
                    plan.Source.Input.Map);

                MethodDefinitionHandle output = _output.AddMethodDefinition(
                    source.Attributes,
                    plan.ImplAttributesOverride ?? source.ImplAttributes,
                    GetString(plan.Source.Input.Reader.GetString(source.Name)),
                    _output.GetOrAddBlob(signature),
                    plan.BodyOffset,
                    plan.FirstParameter);
                LinkTokenMap.AssertHandle(plan.Output, output);

                foreach (ParameterHandle parameterHandle in source.GetParameters())
                {
                    Parameter parameter =
                        plan.Source.Input.Reader.GetParameter(parameterHandle);
                    ParameterHandle outputParameter = _output.AddParameter(
                        parameter.Attributes,
                        GetString(plan.Source.Input.Reader.GetString(parameter.Name)),
                        parameter.SequenceNumber);
                    LinkTokenMap.AssertHandle(
                        plan.Source.Input.Map.Map(parameterHandle),
                        outputParameter);
                }
            }
        }

        private void EmitMemberReferences()
        {
            foreach (SourceRow sourceInfo in _memberRefs)
            {
                var sourceHandle = (MemberReferenceHandle)sourceInfo.Handle;
                MemberReference source =
                    sourceInfo.Input.Reader.GetMemberReference(sourceHandle);
                MemberReferenceHandle output = _output.AddMemberReference(
                    _memberRefParents[sourceInfo],
                    GetString(sourceInfo.Input.Reader.GetString(source.Name)),
                    _output.GetOrAddBlob(_rewrittenMemberRefSignatures[sourceInfo]));
                LinkTokenMap.AssertHandle(
                    sourceInfo.Input.Map.Map(sourceHandle),
                    output);
            }
        }

        private void EmitInterfaces()
        {
            foreach (InterfacePlan plan in _interfaces)
            {
                InterfaceImplementationHandle output =
                    _output.AddInterfaceImplementation(
                        plan.Type,
                        plan.Interface);
                LinkTokenMap.AssertHandle(
                    plan.Source.Input.Map.Map(plan.Source.Handle),
                    output);
            }
        }

        private void EmitConstants()
        {
            foreach (ConstantPlan plan in _constants)
            {
                ConstantHandle output = _output.AddConstant(
                    plan.Parent,
                    plan.Value);
                LinkTokenMap.AssertHandle(
                    MetadataTokens.ConstantHandle(
                        _constants.IndexOf(plan) + 1),
                    output);
            }
        }

        private void EmitLayoutsAndFieldRvas()
        {
            foreach (TypePlan plan in _types.Skip(1))
            {
                if (plan.Layout is TypeLayout value)
                    _output.AddTypeLayout(
                        plan.Output,
                        (ushort)value.PackingSize,
                        (uint)value.Size);
            }

            foreach (MemberPlan plan in _fields.OrderBy(
                         static field => MetadataTokens.GetRowNumber(field.Output)))
            {
                var sourceHandle = (FieldDefinitionHandle)plan.Source.Handle;
                FieldDefinition source =
                    plan.Source.Input.Reader.GetFieldDefinition(sourceHandle);
                int offset = source.GetOffset();
                if (offset >= 0)
                    _output.AddFieldLayout((FieldDefinitionHandle)plan.Output, offset);

                if ((source.Attributes & FieldAttributes.HasFieldRVA) == 0)
                    continue;
                if (plan.RemoveFieldRva)
                    continue;

                _output.AddFieldRelativeVirtualAddress(
                    (FieldDefinitionHandle)plan.Output,
                    plan.FieldRvaOffset.Value);
            }
        }

        private void EmitMethodImplementations()
        {
            foreach (MethodImplementationPlan plan in _methodImplementations)
            {
                MethodImplementationHandle output =
                    _output.AddMethodImplementation(
                        plan.Type,
                        plan.Body,
                        plan.Declaration);
                LinkTokenMap.AssertHandle(
                    MetadataTokens.MethodImplementationHandle(
                        _methodImplementations.IndexOf(plan) + 1),
                    output);
            }
        }

        private void EmitStandaloneSignatures()
        {
            foreach (SourceRow sourceInfo in _standaloneSignatures)
            {
                var sourceHandle = (StandaloneSignatureHandle)sourceInfo.Handle;
                StandaloneSignature source =
                    sourceInfo.Input.Reader.GetStandaloneSignature(sourceHandle);
                BlobBuilder signature = SignatureRewriter.RewriteStandalone(
                    sourceInfo.Input.Reader.GetBlobReader(source.Signature),
                    sourceInfo.Input.Map);
                StandaloneSignatureHandle output =
                    _output.AddStandaloneSignature(_output.GetOrAddBlob(signature));
                LinkTokenMap.AssertHandle(
                    sourceInfo.Input.Map.Map(sourceHandle),
                    output);
            }
        }

        private void EmitMethodSpecifications()
        {
            foreach (SourceRow sourceInfo in _methodSpecs)
            {
                var sourceHandle = (MethodSpecificationHandle)sourceInfo.Handle;
                MethodSpecification source =
                    sourceInfo.Input.Reader.GetMethodSpecification(sourceHandle);
                BlobBuilder signature = SignatureRewriter.RewriteMethodSpecification(
                    sourceInfo.Input.Reader.GetBlobReader(source.Signature),
                    sourceInfo.Input.Map);
                MethodSpecificationHandle output = _output.AddMethodSpecification(
                    sourceInfo.Input.Map.Map(source.Method),
                    _output.GetOrAddBlob(signature));
                LinkTokenMap.AssertHandle(
                    sourceInfo.Input.Map.Map(sourceHandle),
                    output);
            }
        }

        private void EmitNestedTypes()
        {
            foreach (TypePlan plan in _types.Skip(1).OrderBy(
                         static type => MetadataTokens.GetRowNumber(type.Output)))
            {
                SourceRow sourceInfo = plan.Canonical;
                TypeDefinitionHandle enclosing =
                    sourceInfo.Input.Reader.GetTypeDefinition(
                        (TypeDefinitionHandle)sourceInfo.Handle).GetDeclaringType();
                if (enclosing.IsNil)
                    continue;
                _output.AddNestedType(
                    plan.Output,
                    (TypeDefinitionHandle)sourceInfo.Input.Map.Map(enclosing));
            }
        }

        private void EmitGenerics()
        {
            foreach (SourceRow sourceInfo in _genericParameters)
            {
                var sourceHandle = (GenericParameterHandle)sourceInfo.Handle;
                GenericParameter source =
                    sourceInfo.Input.Reader.GetGenericParameter(sourceHandle);
                GenericParameterHandle output = _output.AddGenericParameter(
                    sourceInfo.Input.Map.Map(source.Parent),
                    source.Attributes,
                    GetString(sourceInfo.Input.Reader.GetString(source.Name)),
                    source.Index);
                LinkTokenMap.AssertHandle(
                    sourceInfo.Input.Map.Map(sourceHandle),
                    output);
            }

            foreach (SourceRow sourceInfo in _genericConstraints)
            {
                var sourceHandle =
                    (GenericParameterConstraintHandle)sourceInfo.Handle;
                GenericParameterConstraint source =
                    sourceInfo.Input.Reader.GetGenericParameterConstraint(sourceHandle);
                GenericParameterConstraintHandle output =
                    _output.AddGenericParameterConstraint(
                        (GenericParameterHandle)sourceInfo.Input.Map.Map(source.Parameter),
                        sourceInfo.Input.Map.Map(source.Type));
                LinkTokenMap.AssertHandle(
                    MetadataTokens.GenericParameterConstraintHandle(
                        _genericConstraints.IndexOf(sourceInfo) + 1),
                    output);
            }
        }

        private void EmitCustomAttributes()
        {
            foreach (CustomAttributePlan plan in _customAttributes)
            {
                CustomAttribute source =
                    plan.Source.Input.Reader.GetCustomAttribute(
                        (CustomAttributeHandle)plan.Source.Handle);
                BlobHandle value = source.Value.IsNil
                    ? default
                    : _output.GetOrAddBlob(
                        plan.Source.Input.Reader.GetBlobBytes(source.Value));
                CustomAttributeHandle output = _output.AddCustomAttribute(
                    plan.Parent,
                    plan.Constructor,
                    value);
                LinkTokenMap.AssertHandle(
                    plan.Source.Input.Map.Map(plan.Source.Handle),
                    output);
            }
        }

        private static bool HasSuppressMergeCheck(
            InputState input,
            EntityHandle parent)
        {
            for (int row = 1;
                 row <= input.Reader.GetTableRowCount(TableIndex.CustomAttribute);
                 row++)
            {
                CustomAttribute attribute = input.Reader.GetCustomAttribute(
                    MetadataTokens.CustomAttributeHandle(row));
                if (attribute.Parent == parent &&
                    IsSuppressMergeCheckConstructor(
                        input,
                        attribute.Constructor))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUnmanagedType(
            InputState input,
            TypeDefinitionHandle type)
        {
            for (int row = 1;
                 row <= input.Reader.GetTableRowCount(TableIndex.CustomAttribute);
                 row++)
            {
                CustomAttribute attribute = input.Reader.GetCustomAttribute(
                    MetadataTokens.CustomAttributeHandle(row));
                if (attribute.Parent != type ||
                    !TryGetAttributeTypeIdentity(
                        input,
                        attribute.Constructor,
                        out string ns,
                        out string name,
                        out _))
                {
                    continue;
                }

                if (ns == "System.Runtime.CompilerServices" &&
                    name == "NativeCppClassAttribute")
                {
                    return true;
                }
                if (ns == "Microsoft.VisualC" &&
                    name == "MiscellaneousBitsAttribute" &&
                    !attribute.Value.IsNil)
                {
                    byte[] value = input.Reader.GetBlobBytes(attribute.Value);
                    if (value.Length > 2 && (value[2] & 0x40) != 0)
                        return true;
                }
            }
            return false;
        }

        private static bool IsSuppressMergeCheckConstructor(
            InputState input,
            EntityHandle constructor) =>
            TryGetAttributeTypeIdentity(
                input,
                constructor,
                out string ns,
                out string name,
                out string assembly) &&
            ns == "System.Runtime.CompilerServices" &&
            name == "SuppressMergeCheckAttribute" &&
            IsAssembly(assembly, "mscorlib");

        private static bool IsAdditiveCustomAttribute(
            InputState input,
            EntityHandle parent,
            EntityHandle constructor)
        {
            if (parent.Kind is HandleKind.ModuleDefinition or HandleKind.TypeReference)
                return true;

            if (!TryGetAttributeTypeIdentity(
                input,
                constructor,
                out string ns,
                out string name,
                out string assembly))
            {
                return false;
            }

            if (ns == "System.Runtime.CompilerServices" &&
                IsAssembly(assembly, "mscorlib"))
            {
                return true;
            }
            if (ns == "Microsoft.VisualC" &&
                IsAssembly(assembly, "Microsoft.VisualC"))
            {
                return true;
            }

            return parent.Kind == HandleKind.MethodDefinition &&
                ns == "System.Runtime.ExceptionServices" &&
                name == "HandleProcessCorruptedStateExceptionsAttribute" &&
                IsAssembly(assembly, "mscorlib");
        }

        private static bool TryGetAttributeTypeIdentity(
            InputState input,
            EntityHandle constructor,
            out string ns,
            out string name,
            out string assembly)
        {
            EntityHandle type = constructor.Kind switch
            {
                HandleKind.MethodDefinition => input.Reader
                    .GetMethodDefinition((MethodDefinitionHandle)constructor)
                    .GetDeclaringType(),
                HandleKind.MemberReference => input.Reader
                    .GetMemberReference((MemberReferenceHandle)constructor).Parent,
                _ => default,
            };

            if (type.Kind == HandleKind.TypeReference)
            {
                TypeReference reference =
                    input.Reader.GetTypeReference((TypeReferenceHandle)type);
                ns = input.Reader.GetString(reference.Namespace);
                name = input.Reader.GetString(reference.Name);
                if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
                {
                    AssemblyReference referenceAssembly =
                        input.Reader.GetAssemblyReference(
                            (AssemblyReferenceHandle)reference.ResolutionScope);
                    assembly = input.Reader.GetString(referenceAssembly.Name);
                    return true;
                }
            }

            ns = null;
            name = null;
            assembly = null;
            return false;
        }

        private static bool IsAssembly(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        private bool TryResolveTypeReference(
            InputState input,
            TypeReferenceHandle source,
            out TypeDefinitionHandle resolved)
        {
            var visiting = new HashSet<TypeReferenceHandle>();
            if (TryGetLocalTypeReferenceKey(input, source, visiting, out TypeKey key) &&
                _typesByKey.TryGetValue(key, out TypePlan plan))
            {
                resolved = plan.Output;
                return true;
            }
            resolved = default;
            return false;
        }

        private EntityHandle MapMemberReferenceParent(
            InputState input,
            EntityHandle sourceParent)
        {
            EntityHandle parent = input.Map.Map(sourceParent);
            return parent.Kind == HandleKind.ModuleDefinition
                ? _moduleType.Output
                : parent;
        }

        private EntityHandle MapTypeReferenceResolutionScope(
            InputState input,
            EntityHandle sourceScope)
        {
            EntityHandle scope = input.Map.Map(sourceScope);
            if (scope.Kind is HandleKind.ModuleDefinition or
                HandleKind.ModuleReference or
                HandleKind.AssemblyReference or
                HandleKind.TypeReference)
            {
                return scope;
            }

            throw new NotSupportedException(
                $"TypeRef resolution scope 0x{MetadataTokens.GetToken(sourceScope):X8} " +
                $"in input '{input.Input.Identity}' mapped to {scope.Kind}. " +
                "An unresolved nested or assembly-scoped TypeRef cannot use a " +
                "definition token as its ResolutionScope.");
        }

        private bool TryGetLocalTypeReferenceKey(
            InputState input,
            TypeReferenceHandle source,
            HashSet<TypeReferenceHandle> visiting,
            out TypeKey key)
        {
            if (!visiting.Add(source))
                throw new BadImageFormatException(
                    $"Cyclic TypeRef resolution scope in input '{input.Input.Identity}'.");

            TypeReference reference = input.Reader.GetTypeReference(source);
            string name = input.Reader.GetString(reference.Name);
            string ns = input.Reader.GetString(reference.Namespace);
            EntityHandle scope = reference.ResolutionScope;
            bool local;
            if (scope.IsNil || scope.Kind == HandleKind.ModuleDefinition)
            {
                local = true;
                key = new TypeKey(ns, name, default);
            }
            else if (scope.Kind == HandleKind.ModuleReference)
            {
                string moduleName = input.Reader.GetString(
                    input.Reader.GetModuleReference(
                        (ModuleReferenceHandle)scope).Name);
                local = _inputModuleNames.Contains(moduleName);
                key = new TypeKey(ns, name, default);
            }
            else if (scope.Kind == HandleKind.AssemblyReference)
            {
                string assemblyName = input.Reader.GetString(
                    input.Reader.GetAssemblyReference(
                        (AssemblyReferenceHandle)scope).Name);
                local = _inputAssemblyNames.Contains(assemblyName);
                key = new TypeKey(ns, name, default);
            }
            else if (scope.Kind == HandleKind.TypeReference)
            {
                TypePlan enclosing = null;
                local = TryGetLocalTypeReferenceKey(
                    input,
                    (TypeReferenceHandle)scope,
                    visiting,
                    out TypeKey enclosingKey) &&
                    _typesByKey.TryGetValue(enclosingKey, out enclosing);
                key = local
                    ? new TypeKey(ns, name, enclosing.Output)
                    : default;
            }
            else
            {
                local = false;
                key = default;
            }

            visiting.Remove(source);
            return local;
        }

        private InputState GetInput(string identity) =>
            _inputs.FirstOrDefault(
                input => string.Equals(
                    input.Input.Identity,
                    identity,
                    StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No metadata input has identity '{identity}'.");

        private StringHandle GetString(string value) =>
            string.IsNullOrEmpty(value) ? default : _output.GetOrAddString(value);

        private BlobHandle GetBlob(byte[] value) =>
            value is null || value.Length == 0
                ? default
                : _output.GetOrAddBlob(value);

        private BlobHandle CopyBlob(MetadataReader reader, BlobHandle source) =>
            source.IsNil ? default : _output.GetOrAddBlob(reader.GetBlobBytes(source));

        private Guid CreateDeterministicMvid()
        {
            var text = new StringBuilder()
                .Append(_request.ModuleName)
                .Append('\0')
                .Append(_request.AssemblyName);
            foreach (InputState input in _inputs)
            {
                text.Append('\0').Append(input.Input.Identity);
                GuidHandle mvid = input.Reader.GetModuleDefinition().Mvid;
                if (!mvid.IsNil)
                    text.Append('\0').Append(input.Reader.GetGuid(mvid));
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
            byte[] guid = hash[..16];
            guid[7] = (byte)((guid[7] & 0x0f) | 0x50);
            guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
            return new Guid(guid);
        }

        private static string GetAssemblyReferenceKey(
            MetadataReader reader,
            AssemblyReference reference) =>
            string.Join(
                "\0",
                reader.GetString(reference.Name),
                reference.Version,
                reader.GetString(reference.Culture),
                (int)reference.Flags,
                reference.PublicKeyOrToken.IsNil
                    ? string.Empty
                    : Convert.ToHexString(
                        reader.GetBlobBytes(reference.PublicKeyOrToken)),
                reference.HashValue.IsNil
                    ? string.Empty
                    : Convert.ToHexString(reader.GetBlobBytes(reference.HashValue)));

        private static TypeDefinitionHandle GetInterfaceOwner(
            InputState input,
            InterfaceImplementationHandle target)
        {
            foreach (TypeDefinitionHandle typeHandle in input.Reader.TypeDefinitions)
            {
                foreach (InterfaceImplementationHandle handle in
                         input.Reader.GetTypeDefinition(typeHandle)
                             .GetInterfaceImplementations())
                {
                    if (handle == target)
                        return typeHandle;
                }
            }
            throw new BadImageFormatException(
                $"InterfaceImpl token has no owner in '{input.Input.Identity}'.");
        }

        private static TypeDefinitionHandle GetMethodImplementationOwner(
            InputState input,
            MethodImplementationHandle target)
        {
            foreach (TypeDefinitionHandle typeHandle in input.Reader.TypeDefinitions)
            {
                foreach (MethodImplementationHandle handle in
                         input.Reader.GetTypeDefinition(typeHandle)
                             .GetMethodImplementations())
                {
                    if (handle == target)
                        return typeHandle;
                }
            }
            throw new BadImageFormatException(
                $"MethodImpl token has no owner in '{input.Input.Identity}'.");
        }

        private static MethodDefinitionHandle GetParameterOwner(
            InputState input,
            ParameterHandle target)
        {
            foreach (MethodDefinitionHandle methodHandle in input.Reader.MethodDefinitions)
            {
                foreach (ParameterHandle handle in
                         input.Reader.GetMethodDefinition(methodHandle).GetParameters())
                {
                    if (handle == target)
                        return methodHandle;
                }
            }
            throw new BadImageFormatException(
                $"Param token has no owner in '{input.Input.Identity}'.");
        }

        private static int CompareSource(SourceRow left, SourceRow right)
        {
            int input = left.Input.Index.CompareTo(right.Input.Index);
            return input != 0
                ? input
                : MetadataTokens.GetToken(left.Handle).CompareTo(
                    MetadataTokens.GetToken(right.Handle));
        }

        private static bool RequiresMethodBodyOffset(MethodDefinition method)
        {
            if ((method.Attributes & MethodAttributes.Abstract) != 0 ||
                (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                return false;
            }

            MethodImplAttributes implementation = method.ImplAttributes;
            if ((implementation & MethodImplAttributes.ForwardRef) != 0 ||
                (implementation & MethodImplAttributes.InternalCall) != 0 ||
                (implementation & MethodImplAttributes.CodeTypeMask) ==
                    MethodImplAttributes.Runtime)
            {
                return false;
            }

            return (implementation & MethodImplAttributes.CodeTypeMask) ==
                MethodImplAttributes.IL;
        }

        private static void ValidateTables(MetadataMergeInput input)
        {
            var supported = new HashSet<TableIndex>
            {
                TableIndex.Module,
                TableIndex.TypeRef,
                TableIndex.TypeDef,
                TableIndex.FieldPtr,
                TableIndex.Field,
                TableIndex.MethodPtr,
                TableIndex.MethodDef,
                TableIndex.ParamPtr,
                TableIndex.Param,
                TableIndex.InterfaceImpl,
                TableIndex.MemberRef,
                TableIndex.Constant,
                TableIndex.CustomAttribute,
                TableIndex.ClassLayout,
                TableIndex.FieldLayout,
                TableIndex.StandAloneSig,
                TableIndex.MethodImpl,
                TableIndex.ModuleRef,
                TableIndex.TypeSpec,
                TableIndex.FieldRva,
                TableIndex.Assembly,
                TableIndex.AssemblyRef,
                TableIndex.NestedClass,
                TableIndex.GenericParam,
                TableIndex.MethodSpec,
                TableIndex.GenericParamConstraint,
                TableIndex.EventPtr,
                TableIndex.PropertyPtr,
            };

            foreach (TableIndex table in Enum.GetValues<TableIndex>())
            {
                int count = input.Reader.GetTableRowCount(table);
                if (count == 0 || supported.Contains(table))
                    continue;

                string detail = table switch
                {
                    TableIndex.DeclSecurity =>
                        "declarative security cannot be safely composed",
                    TableIndex.ManifestResource =>
                        "managed resources require section and manifest merging",
                    TableIndex.ImplMap =>
                        "P/Invoke ImplMap rows require native import policy",
                    TableIndex.FieldMarshal =>
                        "marshalling descriptors are not supported",
                    TableIndex.File or TableIndex.ExportedType =>
                        "multi-file assembly manifests are not supported",
                    TableIndex.Property or TableIndex.Event or
                    TableIndex.PropertyMap or TableIndex.EventMap or
                    TableIndex.MethodSemantics =>
                        "property/event metadata is outside the current asm2obj subset",
                    _ => "the table cannot currently be merged without losing semantics",
                };
                throw new NotSupportedException(
                    $"Input '{input.Identity}' contains {count} {table} row(s); {detail}.");
            }
        }

        private sealed class InputState
        {
            private readonly HashSet<EntityHandle> _roots;
            private readonly HashSet<EntityHandle> _discarded;
            private readonly bool _retainAll;
            private readonly HashSet<EntityHandle> _marked = new();
            private readonly Dictionary<TypeKey, TypeDefinitionHandle> _typesByKey =
                new();

            public InputState(
                MetadataMergeInput input,
                LinkTokenMap map,
                int index)
            {
                Input = input;
                Reader = input.Reader;
                Map = map;
                Index = index;
                _retainAll = input.RetainedEntities is null;
                _roots = _retainAll
                    ? null
                    : new HashSet<EntityHandle>(input.RetainedEntities);
                _discarded = input.DiscardedEntities is null
                    ? new HashSet<EntityHandle>()
                    : new HashSet<EntityHandle>(input.DiscardedEntities);

                foreach (TypeDefinitionHandle type in Reader.TypeDefinitions)
                    _typesByKey.TryAdd(GetTypeKey(type), type);
            }

            public MetadataMergeInput Input { get; }
            public MetadataReader Reader { get; }
            public LinkTokenMap Map { get; }
            public int Index { get; }

            public void FreezeSelection()
            {
                Mark(EntityHandle.ModuleDefinition);

                if (_retainAll)
                {
                    foreach (TypeDefinitionHandle type in Reader.TypeDefinitions)
                    {
                        if (MetadataTokens.GetRowNumber(type) != 1)
                            Mark(type);
                    }

                    TypeDefinition module = Reader.GetTypeDefinition(
                        MetadataTokens.TypeDefinitionHandle(1));
                    foreach (MethodDefinitionHandle method in module.GetMethods())
                        Mark(method);
                    foreach (FieldDefinitionHandle field in module.GetFields())
                        Mark(field);

                    MarkTable(
                        TableIndex.TypeRef,
                        static row => MetadataTokens.TypeReferenceHandle(row));
                    MarkTable(
                        TableIndex.TypeSpec,
                        static row => MetadataTokens.TypeSpecificationHandle(row));
                    MarkTable(
                        TableIndex.MemberRef,
                        static row => MetadataTokens.MemberReferenceHandle(row));
                    MarkTable(
                        TableIndex.StandAloneSig,
                        static row => MetadataTokens.StandaloneSignatureHandle(row));
                    MarkTable(
                        TableIndex.MethodSpec,
                        static row => MetadataTokens.MethodSpecificationHandle(row));
                    MarkTable(
                        TableIndex.ModuleRef,
                        static row => MetadataTokens.ModuleReferenceHandle(row));
                    MarkTable(
                        TableIndex.AssemblyRef,
                        static row => MetadataTokens.AssemblyReferenceHandle(row));

                    if (Reader.IsAssembly)
                        Mark(EntityHandle.AssemblyDefinition);

                    for (int row = 1;
                         row <= Reader.GetTableRowCount(TableIndex.CustomAttribute);
                         row++)
                    {
                        var attribute = MetadataTokens.CustomAttributeHandle(row);
                        CustomAttribute value = Reader.GetCustomAttribute(attribute);
                        if (!IsExcluded(value.Parent))
                            Mark(attribute);
                    }
                }
                else
                {
                    foreach (EntityHandle root in _roots)
                        Mark(root);
                }
            }

            public bool IsMarked(EntityHandle handle) =>
                !handle.IsNil && _marked.Contains(handle);

            public bool RetainType(TypeDefinitionHandle type) => IsMarked(type);

            public bool RetainField(FieldDefinitionHandle field) => IsMarked(field);

            public bool RetainMethod(MethodDefinitionHandle method) => IsMarked(method);

            private void MarkTable(
                TableIndex table,
                Func<int, EntityHandle> handleFactory)
            {
                for (int row = 1; row <= Reader.GetTableRowCount(table); row++)
                    Mark(handleFactory(row));
            }

            private void Mark(EntityHandle handle)
            {
                if (handle.IsNil || _discarded.Contains(handle))
                    return;

                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    TypeDefinitionHandle owner =
                        Reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                            .GetDeclaringType();
                    if (_discarded.Contains(owner))
                        return;
                }
                else if (handle.Kind == HandleKind.FieldDefinition)
                {
                    TypeDefinitionHandle owner =
                        Reader.GetFieldDefinition((FieldDefinitionHandle)handle)
                            .GetDeclaringType();
                    if (_discarded.Contains(owner))
                        return;
                }

                if (!_marked.Add(handle))
                    return;

                switch (handle.Kind)
                {
                    case HandleKind.ModuleDefinition:
                        MarkCustomAttributes(handle);
                        break;

                    case HandleKind.AssemblyDefinition:
                        MarkCustomAttributes(handle);
                        break;

                    case HandleKind.TypeDefinition:
                        MarkTypeDefinition((TypeDefinitionHandle)handle);
                        break;

                    case HandleKind.MethodDefinition:
                        MarkMethod((MethodDefinitionHandle)handle);
                        break;

                    case HandleKind.FieldDefinition:
                        MarkField((FieldDefinitionHandle)handle);
                        break;

                    case HandleKind.Parameter:
                        MarkCustomAttributes(handle);
                        break;

                    case HandleKind.TypeReference:
                        MarkTypeReference((TypeReferenceHandle)handle);
                        break;

                    case HandleKind.TypeSpecification:
                    {
                        TypeSpecification specification =
                            Reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                        SignatureRewriter.MarkTypeSpecification(
                            Reader.GetBlobReader(specification.Signature),
                            Mark);
                        MarkCustomAttributes(handle);
                        break;
                    }

                    case HandleKind.MemberReference:
                        MarkMemberReference((MemberReferenceHandle)handle);
                        break;

                    case HandleKind.StandaloneSignature:
                    {
                        StandaloneSignature signature =
                            Reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                        SignatureRewriter.MarkStandalone(
                            Reader.GetBlobReader(signature.Signature),
                            Mark);
                        MarkCustomAttributes(handle);
                        break;
                    }

                    case HandleKind.MethodSpecification:
                    {
                        MethodSpecification specification =
                            Reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                        Mark(specification.Method);
                        SignatureRewriter.MarkMethodSpecification(
                            Reader.GetBlobReader(specification.Signature),
                            Mark);
                        break;
                    }

                    case HandleKind.ModuleReference:
                    case HandleKind.AssemblyReference:
                        MarkCustomAttributes(handle);
                        break;

                    case HandleKind.InterfaceImplementation:
                    {
                        InterfaceImplementation implementation =
                            Reader.GetInterfaceImplementation(
                                (InterfaceImplementationHandle)handle);
                        Mark(implementation.Interface);
                        MarkCustomAttributes(handle);
                        break;
                    }

                    case HandleKind.GenericParameter:
                    {
                        GenericParameter parameter =
                            Reader.GetGenericParameter((GenericParameterHandle)handle);
                        foreach (GenericParameterConstraintHandle constraint in
                                 parameter.GetConstraints())
                        {
                            if (_discarded.Contains(constraint))
                                continue;
                            _marked.Add(constraint);
                            Mark(Reader.GetGenericParameterConstraint(constraint).Type);
                        }
                        break;
                    }

                    case HandleKind.GenericParameterConstraint:
                    {
                        GenericParameterConstraint constraint =
                            Reader.GetGenericParameterConstraint(
                                (GenericParameterConstraintHandle)handle);
                        Mark(constraint.Type);
                        break;
                    }

                    case HandleKind.CustomAttribute:
                    {
                        CustomAttribute attribute =
                            Reader.GetCustomAttribute((CustomAttributeHandle)handle);
                        if (IsExcluded(attribute.Parent))
                        {
                            _marked.Remove(handle);
                            break;
                        }
                        Mark(attribute.Constructor);
                        break;
                    }

                    case HandleKind.MethodImplementation:
                    {
                        MethodImplementation implementation =
                            Reader.GetMethodImplementation(
                                (MethodImplementationHandle)handle);
                        Mark(implementation.MethodBody);
                        Mark(implementation.MethodDeclaration);
                        break;
                    }

                    default:
                        throw new ArgumentException(
                            $"Metadata token 0x{MetadataTokens.GetToken(handle):X8} " +
                            $"({handle.Kind}) is not a supported selection root.");
                }
            }

            private void MarkTypeDefinition(TypeDefinitionHandle handle)
            {
                TypeDefinition type = Reader.GetTypeDefinition(handle);
                Mark(type.BaseType);

                foreach (InterfaceImplementationHandle implementation in
                         type.GetInterfaceImplementations())
                {
                    Mark(implementation);
                }
                foreach (MethodDefinitionHandle method in type.GetMethods())
                    Mark(method);
                foreach (MethodImplementationHandle implementation in
                         type.GetMethodImplementations())
                {
                    Mark(implementation);
                }
                foreach (FieldDefinitionHandle field in type.GetFields())
                    Mark(field);
                foreach (GenericParameterHandle parameter in type.GetGenericParameters())
                    Mark(parameter);

                MarkCustomAttributes(handle);
                Mark(type.GetDeclaringType());
            }

            private void MarkMethod(MethodDefinitionHandle handle)
            {
                MethodDefinition method = Reader.GetMethodDefinition(handle);
                TypeDefinitionHandle owner = method.GetDeclaringType();
                if (MetadataTokens.GetRowNumber(owner) == 1)
                {
                    _marked.Add(owner);
                }
                else
                {
                    Mark(owner);
                }

                foreach (ParameterHandle parameter in method.GetParameters())
                    Mark(parameter);
                foreach (GenericParameterHandle parameter in method.GetGenericParameters())
                    Mark(parameter);
                SignatureRewriter.MarkMethod(
                    Reader.GetBlobReader(method.Signature),
                    Mark);
                MarkCustomAttributes(handle);
            }

            private void MarkField(FieldDefinitionHandle handle)
            {
                FieldDefinition field = Reader.GetFieldDefinition(handle);
                TypeDefinitionHandle owner = field.GetDeclaringType();
                if (MetadataTokens.GetRowNumber(owner) == 1)
                {
                    _marked.Add(owner);
                }
                else
                {
                    Mark(owner);
                }

                SignatureRewriter.MarkField(
                    Reader.GetBlobReader(field.Signature),
                    Mark);
                MarkCustomAttributes(handle);
            }

            private void MarkTypeReference(TypeReferenceHandle handle)
            {
                TypeReference reference = Reader.GetTypeReference(handle);
                Mark(reference.ResolutionScope);
                if (TryResolveLocalTypeReference(handle, out TypeDefinitionHandle type))
                    Mark(type);
                MarkCustomAttributes(handle);
            }

            private void MarkMemberReference(MemberReferenceHandle handle)
            {
                MemberReference reference = Reader.GetMemberReference(handle);
                if (reference.Parent.Kind == HandleKind.TypeDefinition &&
                    MetadataTokens.GetRowNumber(reference.Parent) == 1)
                {
                    _marked.Add(reference.Parent);
                }
                else
                {
                    Mark(reference.Parent);
                }

                SignatureRewriter.MarkMemberReference(
                    Reader.GetBlobReader(reference.Signature),
                    Mark);

                if (TryResolveMemberReference(reference, out EntityHandle definition))
                    Mark(definition);
                MarkCustomAttributes(handle);
            }

            private bool TryResolveMemberReference(
                MemberReference reference,
                out EntityHandle definition)
            {
                TypeDefinitionHandle owner = reference.Parent.Kind switch
                {
                    HandleKind.TypeDefinition =>
                        (TypeDefinitionHandle)reference.Parent,
                    HandleKind.TypeReference when TryResolveLocalTypeReference(
                        (TypeReferenceHandle)reference.Parent,
                        out TypeDefinitionHandle type) => type,
                    _ => default,
                };
                if (owner.IsNil)
                {
                    definition = default;
                    return false;
                }

                string name = Reader.GetString(reference.Name);
                byte[] signature = Reader.GetBlobBytes(reference.Signature);
                MemberReferenceKind kind = reference.GetKind();
                if (kind == MemberReferenceKind.Method &&
                    SignatureRewriter.TryRewriteVarargMethodFixed(
                        Reader.GetBlobReader(reference.Signature),
                        static _ => { },
                        out BlobBuilder fixedSignature))
                {
                    signature = fixedSignature.ToArray();
                }

                var visited = new HashSet<TypeDefinitionHandle>();
                while (visited.Add(owner))
                {
                    TypeDefinition typeDefinition = Reader.GetTypeDefinition(owner);
                    if (kind == MemberReferenceKind.Method)
                    {
                        foreach (MethodDefinitionHandle methodHandle in
                                 typeDefinition.GetMethods())
                        {
                            MethodDefinition method =
                                Reader.GetMethodDefinition(methodHandle);
                            if ((method.Attributes &
                                    MethodAttributes.MemberAccessMask) !=
                                    MethodAttributes.PrivateScope &&
                                Reader.GetString(method.Name) == name &&
                                Reader.GetBlobBytes(method.Signature).AsSpan()
                                    .SequenceEqual(signature))
                            {
                                definition = methodHandle;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        foreach (FieldDefinitionHandle fieldHandle in
                                 typeDefinition.GetFields())
                        {
                            FieldDefinition field =
                                Reader.GetFieldDefinition(fieldHandle);
                            if ((field.Attributes & FieldAttributes.FieldAccessMask) !=
                                    FieldAttributes.PrivateScope &&
                                Reader.GetString(field.Name) == name &&
                                Reader.GetBlobBytes(field.Signature).AsSpan()
                                    .SequenceEqual(signature))
                            {
                                definition = fieldHandle;
                                return true;
                            }
                        }
                    }

                    EntityHandle baseType = typeDefinition.BaseType;
                    owner = baseType.Kind switch
                    {
                        HandleKind.TypeDefinition =>
                            (TypeDefinitionHandle)baseType,
                        HandleKind.TypeReference when TryResolveLocalTypeReference(
                            (TypeReferenceHandle)baseType,
                            out TypeDefinitionHandle resolved) => resolved,
                        _ => default,
                    };
                    if (owner.IsNil)
                        break;
                }

                definition = default;
                return false;
            }

            private void MarkCustomAttributes(EntityHandle parent)
            {
                for (int row = 1;
                     row <= Reader.GetTableRowCount(TableIndex.CustomAttribute);
                     row++)
                {
                    var handle = MetadataTokens.CustomAttributeHandle(row);
                    if (Reader.GetCustomAttribute(handle).Parent == parent)
                        Mark(handle);
                }
            }

            private bool TryResolveLocalTypeReference(
                TypeReferenceHandle handle,
                out TypeDefinitionHandle type)
            {
                var visiting = new HashSet<TypeReferenceHandle>();
                if (TryGetTypeReferenceKey(handle, visiting, out TypeKey key) &&
                    _typesByKey.TryGetValue(key, out type))
                {
                    return true;
                }

                type = default;
                return false;
            }

            private bool TryGetTypeReferenceKey(
                TypeReferenceHandle handle,
                HashSet<TypeReferenceHandle> visiting,
                out TypeKey key)
            {
                if (!visiting.Add(handle))
                    throw new BadImageFormatException(
                        $"Cyclic TypeRef resolution scope in input '{Input.Identity}'.");

                TypeReference reference = Reader.GetTypeReference(handle);
                string name = Reader.GetString(reference.Name);
                string ns = Reader.GetString(reference.Namespace);
                EntityHandle scope = reference.ResolutionScope;
                bool local;
                if (scope.IsNil || scope.Kind == HandleKind.ModuleDefinition)
                {
                    local = true;
                    key = new TypeKey(ns, name, default);
                }
                else if (scope.Kind == HandleKind.TypeReference)
                {
                    TypeDefinitionHandle enclosing = default;
                    local = TryGetTypeReferenceKey(
                        (TypeReferenceHandle)scope,
                        visiting,
                        out TypeKey enclosingKey) &&
                        _typesByKey.TryGetValue(
                            enclosingKey,
                            out enclosing);
                    key = local
                        ? new TypeKey(ns, name, enclosing)
                        : default;
                }
                else
                {
                    local = false;
                    key = default;
                }

                visiting.Remove(handle);
                return local;
            }

            private TypeKey GetTypeKey(TypeDefinitionHandle handle)
            {
                TypeDefinition definition = Reader.GetTypeDefinition(handle);
                return new TypeKey(
                    Reader.GetString(definition.Namespace),
                    Reader.GetString(definition.Name),
                    definition.GetDeclaringType());
            }

            private bool IsExcluded(EntityHandle handle)
            {
                if (handle.IsNil)
                    return false;
                if (_discarded.Contains(handle))
                    return true;

                return handle.Kind switch
                {
                    HandleKind.MethodDefinition => _discarded.Contains(
                        Reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                            .GetDeclaringType()),
                    HandleKind.FieldDefinition => _discarded.Contains(
                        Reader.GetFieldDefinition((FieldDefinitionHandle)handle)
                            .GetDeclaringType()),
                    HandleKind.Parameter => IsExcluded(
                        GetParameterOwner(this, (ParameterHandle)handle)),
                    HandleKind.GenericParameter => IsExcluded(
                        Reader.GetGenericParameter((GenericParameterHandle)handle).Parent),
                    HandleKind.GenericParameterConstraint => IsExcluded(
                        Reader.GetGenericParameterConstraint(
                            (GenericParameterConstraintHandle)handle).Parameter),
                    HandleKind.InterfaceImplementation => _discarded.Contains(
                        GetInterfaceOwner(this, (InterfaceImplementationHandle)handle)),
                    HandleKind.MethodImplementation => _discarded.Contains(
                        GetMethodImplementationOwner(
                            this,
                            (MethodImplementationHandle)handle)),
                    _ => false,
                };
            }
        }

        private sealed class TypePlan
        {
            public TypePlan(
                TypeKey key,
                TypeDefinitionHandle output,
                SourceRow canonical)
            {
                Key = key;
                Output = output;
                Canonical = canonical;
            }

            public TypeKey Key { get; }
            public TypeDefinitionHandle Output { get; }
            public SourceRow Canonical { get; }
            public List<SourceRow> Sources { get; } = new();
            public List<MemberPlan> Fields { get; } = new();
            public List<MemberPlan> Methods { get; } = new();
            public FieldDefinitionHandle FirstField { get; set; }
            public MethodDefinitionHandle FirstMethod { get; set; }
            public TypeLayout? Layout { get; set; }
            public int CheckedFieldCount { get; set; }
            public int CheckedMethodCount { get; set; }
        }

        private sealed class MemberPlan
        {
            public MemberPlan(
                SourceRow source,
                EntityHandle output,
                TypePlan owner)
            {
                Source = source;
                Output = output;
                Owner = owner;
            }

            public SourceRow Source { get; }
            public EntityHandle Output { get; }
            public TypePlan Owner { get; }
            public ParameterHandle FirstParameter { get; set; }
            public Dictionary<int, ParameterHandle> Parameters { get; } = new();
            public int BodyOffset { get; set; } = -1;
            public int? FieldRvaOffset { get; set; }
            public bool RemoveFieldRva { get; set; }
            public bool IsModuleInitializer { get; set; }
            public MethodImplAttributes? ImplAttributesOverride { get; set; }
        }

        private readonly record struct SourceRow(InputState Input, EntityHandle Handle);

        private readonly record struct ReferencePlan<THandle>(
            SourceRow Source,
            THandle Output)
            where THandle : struct;

        private readonly record struct MemberKey(
            EntityHandle Parent,
            string Name,
            MemberReferenceKind Kind,
            string Signature);

        private readonly record struct TypeKey(
            string Namespace,
            string Name,
            TypeDefinitionHandle Enclosing)
        {
            public override string ToString() =>
                Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";
        }

        private readonly record struct InterfacePlan(
            SourceRow Source,
            TypeDefinitionHandle Type,
            EntityHandle Interface);

        private readonly record struct ConstantPlan(
            SourceRow Source,
            EntityHandle Parent,
            object Value);

        private readonly record struct MethodImplementationPlan(
            SourceRow Source,
            TypeDefinitionHandle Type,
            EntityHandle Body,
            EntityHandle Declaration);

        private readonly record struct CustomAttributeKey(
            EntityHandle Parent,
            EntityHandle Constructor,
            string Value);

        private sealed class CustomAttributePlan
        {
            public CustomAttributePlan(
                SourceRow source,
                EntityHandle parent,
                EntityHandle constructor,
                bool isDuplicate)
            {
                Source = source;
                Parent = parent;
                Constructor = constructor;
                IsDuplicate = isDuplicate;
            }

            public SourceRow Source { get; }
            public EntityHandle Parent { get; }
            public EntityHandle Constructor { get; }
            public bool IsDuplicate { get; }
            public List<SourceRow> DuplicateSources { get; } = new();
            public CustomAttributeHandle Output { get; set; }
        }
    }
}
