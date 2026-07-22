using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Chilink;
using Xunit;

namespace Chibil.Tests;

public sealed class MetadataMergerTests
{
    [Fact]
    public void DuplicateTypeWithDifferentOrdinaryMethodsIsRejected()
    {
        using TestMetadata first = CreateTypeWithMethod("C", "First");
        using TestMetadata second = CreateTypeWithMethod("C", "Second");

        Assert.Throws<InvalidOperationException>(() => Merge(first, second));
    }

    [Fact]
    public void MatchingDuplicateMethodAndParameterMapToSingleRows()
    {
        using TestMetadata first = CreateTypeWithMethod("C", "M", parameterAttributes: ParameterAttributes.In);
        using TestMetadata second = CreateTypeWithMethod("C", "M", parameterAttributes: ParameterAttributes.In);

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.MethodDef));
        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.Param));
    }

    [Fact]
    public void DuplicateMethodParameterFlagMismatchIsRejected()
    {
        using TestMetadata first = CreateTypeWithMethod("C", "M", parameterAttributes: ParameterAttributes.In);
        using TestMetadata second = CreateTypeWithMethod("C", "M", parameterAttributes: ParameterAttributes.Out);

        Assert.Throws<InvalidOperationException>(() => Merge(first, second));
    }

    [Fact]
    public void PrivateScopeFieldsRemainAdditive()
    {
        using TestMetadata first = CreateTypeWithField(
            "C",
            "value",
            FieldAttributes.PrivateScope | FieldAttributes.Static);
        using TestMetadata second = CreateTypeWithField(
            "C",
            "value",
            FieldAttributes.PrivateScope | FieldAttributes.Static);

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(2, result.Metadata.GetRowCount(TableIndex.Field));
    }

    [Fact]
    public void ConstantOnDuplicateFieldIsNotCopiedTwice()
    {
        const FieldAttributes attributes =
            FieldAttributes.Public |
            FieldAttributes.Static |
            FieldAttributes.Literal |
            FieldAttributes.HasDefault;
        using TestMetadata first = CreateTypeWithField("C", "Value", attributes, constant: 42);
        using TestMetadata second = CreateTypeWithField("C", "Value", attributes, constant: 42);

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.Field));
        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.Constant));
    }

    [Fact]
    public void DuplicateInterfaceImplementationMapsToSingleRow()
    {
        using TestMetadata first = CreateTypeWithInterface("C", "I");
        using TestMetadata second = CreateTypeWithInterface("C", "I");

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.InterfaceImpl));
    }

    [Fact]
    public void ExactCustomAttributeOnDuplicateParentMapsToSingleRow()
    {
        using TestMetadata first = CreateTypeWithCustomAttribute("C");
        using TestMetadata second = CreateTypeWithCustomAttribute("C");

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.CustomAttribute));
    }

    [Fact]
    public void DifferentGenericConstraintSetsAreRejected()
    {
        using TestMetadata first = CreateGenericType("C", "ILeft");
        using TestMetadata second = CreateGenericType("C", "IRight");

        Assert.Throws<InvalidOperationException>(() => Merge(first, second));
    }

    [Fact]
    public void SelectingNonGlobalMethodRetainsCompleteOwningType()
    {
        using TestMetadata input = CreateTypeWithMethods("C", "First", "Second");
        var mergeInput = new MetadataMergeInput(
            "0",
            input.Reader,
            retainedEntities: [input.Handles["First"]]);

        MetadataMergeResult result = MetadataMerger.Merge(
            new MetadataMergeRequest([mergeInput], "out.exe", "out"));

        Assert.Equal(2, result.Metadata.GetRowCount(TableIndex.MethodDef));
    }

    [Fact]
    public void DiscardedTypeDefinitionIsNotEmitted()
    {
        using TestMetadata input = CreateEmptyType("C");
        var mergeInput = new MetadataMergeInput(
            "0",
            input.Reader,
            discardedEntities: [input.Handles["Type"]]);

        MetadataMergeResult result = MetadataMerger.Merge(
            new MetadataMergeRequest([mergeInput], "out.exe", "out"));

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.TypeDef));
    }

    [Fact]
    public void EquivalentReferenceAndSignatureRowsAreCanonicalized()
    {
        using TestMetadata first = CreateReferencesAndSignatures();
        using TestMetadata second = CreateReferencesAndSignatures();

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.TypeRef));
        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.TypeSpec));
        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.MemberRef));
        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.StandAloneSig));
    }

    [Fact]
    public void NestedTypeIdentityIsStructural()
    {
        using TestMetadata topLevel = CreateEmptyType("Outer+Inner");
        using TestMetadata nested = CreateNestedType("Outer", "Inner");

        MetadataMergeResult result = Merge(topLevel, nested);

        Assert.Equal(4, result.Metadata.GetRowCount(TableIndex.TypeDef));
    }

    [Fact]
    public void PrivateScopeMethodIsNotDuplicateMatchCandidate()
    {
        using TestMetadata first = CreateTypeWithMethod(
            "C",
            "M",
            methodAttributes: MethodAttributes.PrivateScope | MethodAttributes.Static);
        using TestMetadata second = CreateTypeWithMethod(
            "C",
            "M",
            methodAttributes: MethodAttributes.Public | MethodAttributes.Static);

        Assert.Throws<InvalidOperationException>(() => Merge(first, second));
    }

    [Fact]
    public void MemberReferenceResolvesThroughBaseType()
    {
        using TestMetadata input = CreateInheritanceMemberReference(vararg: false);

        MetadataMergeResult result = Merge(input);

        EntityHandle mapped = result.TokenMaps[0].Map(input.Handles["MemberRef"]);
        Assert.Equal(HandleKind.MethodDefinition, mapped.Kind);
        Assert.Equal(0, result.Metadata.GetRowCount(TableIndex.MemberRef));
    }

    [Fact]
    public void VarargMemberReferenceUsesMatchedMethodAsParent()
    {
        using TestMetadata input = CreateInheritanceMemberReference(vararg: true);
        MetadataMergeResult result = Merge(input);
        using TestMetadata output = TestMetadata.Create(
            result.Metadata,
            new Dictionary<string, EntityHandle>());

        MemberReferenceHandle memberHandle = output.Reader.MemberReferences.Single();
        MemberReference member = output.Reader.GetMemberReference(memberHandle);

        Assert.Equal(HandleKind.MethodDefinition, member.Parent.Kind);
    }

    [Fact]
    public void FakeSuppressMergeCheckAttributeDoesNotSuppressVerification()
    {
        using TestMetadata first = CreateTypeWithSuppressionAttribute("C", "First", "Fake");
        using TestMetadata second = CreateTypeWithSuppressionAttribute("C", "Second", "Fake");

        Assert.Throws<InvalidOperationException>(() => Merge(first, second));
    }

    [Fact]
    public void MissingIncomingClassLayoutDoesNotConflict()
    {
        using TestMetadata first = CreateTypeWithOptionalLayout("C", hasLayout: true);
        using TestMetadata second = CreateTypeWithOptionalLayout("C", hasLayout: false);

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.ClassLayout));
    }

    [Fact]
    public void ModuleRefMatchingIsCaseSensitiveAndUsesOnlyInputModules()
    {
        using TestMetadata first = CreateTypeWithModuleReference(
            "Case.obj",
            "case.obj");
        using TestMetadata second = CreateTypeWithModuleReference(
            "input.obj",
            "out.exe");

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(2, result.Metadata.GetRowCount(TableIndex.ModuleRef));
    }

    [Fact]
    public void AdditiveAttributeOnDuplicateParentKeepsDuplicateProvenance()
    {
        using TestMetadata first = CreateEmptyType("C");
        using TestMetadata second = CreateTypeWithAdditiveAttribute("C");

        MetadataMergeResult result = Merge(first, second);

        Assert.True(result.TokenMaps[1].TryGetMapping(
            second.Handles["Attribute"],
            out LinkTokenMap.Mapping mapping));
        Assert.True(mapping.IsDuplicate);
    }

    [Fact]
    public void UnresolvedInheritedMemberReferenceUsesBaseTypeRefParent()
    {
        using TestMetadata input = CreateUnresolvedBaseMemberReference();
        MetadataMergeResult result = Merge(input);
        using TestMetadata output = TestMetadata.Create(
            result.Metadata,
            new Dictionary<string, EntityHandle>());

        MemberReference member = output.Reader.GetMemberReference(
            output.Reader.MemberReferences.Single());

        Assert.Equal(HandleKind.TypeReference, member.Parent.Kind);
    }

    [Fact]
    public void CustomAttributeOnVarargMemberReferenceIsDropped()
    {
        using TestMetadata input = CreateInheritanceMemberReference(
            vararg: true,
            addMemberAttribute: true);

        MetadataMergeResult result = Merge(input);

        Assert.Equal(0, result.Metadata.GetRowCount(TableIndex.CustomAttribute));
    }

    [Fact]
    public void CustomAttributeWithDiscardedConstructorIsSkipped()
    {
        using TestMetadata input = CreateTypeWithCustomAttribute("C");
        var mergeInput = new MetadataMergeInput(
            "0",
            input.Reader,
            retainedEntities: [input.Handles["Type"]],
            discardedEntities: [input.Handles["Constructor"]]);

        MetadataMergeResult result = MetadataMerger.Merge(
            new MetadataMergeRequest([mergeInput], "out.exe", "out"));

        Assert.Equal(0, result.Metadata.GetRowCount(TableIndex.CustomAttribute));
    }

    [Fact]
    public void EmptyModuleRefNameIsNotMappedToModuleDefinition()
    {
        using TestMetadata input = CreateTypeWithModuleReference("", "");

        MetadataMergeResult result = Merge(input);

        Assert.Equal(1, result.Metadata.GetRowCount(TableIndex.ModuleRef));
    }

    [Fact]
    public void BoundVarargMemberReferenceRetainsCallSiteSignature()
    {
        using TestMetadata input = CreateInheritanceMemberReference(vararg: true);
        var source = new MetadataSourceEntity("0", input.Handles["MemberRef"]);
        var target = new MetadataSourceEntity("0", input.Handles["Method"]);
        MetadataMergeResult result = MetadataMerger.Merge(
            new MetadataMergeRequest(
                [new MetadataMergeInput("0", input.Reader)],
                "out.exe",
                "out")
            {
                ReferenceBindings =
                    new Dictionary<MetadataSourceEntity, MetadataSourceEntity>
                    {
                        [source] = target,
                    },
            });
        using TestMetadata output = TestMetadata.Create(
            result.Metadata,
            new Dictionary<string, EntityHandle>());

        EntityHandle mapped = result.TokenMaps[0].Map(input.Handles["MemberRef"]);
        MemberReference member = output.Reader.GetMemberReference(
            (MemberReferenceHandle)mapped);

        Assert.Equal(HandleKind.MemberReference, mapped.Kind);
        Assert.Equal(HandleKind.MethodDefinition, member.Parent.Kind);
    }

    [Fact]
    public void ForwardRefReplacementUsesConcreteMethodBodyOffset()
    {
        using TestMetadata declaration = CreateTypeWithMethodImplementation(
            "C",
            "M",
            MethodImplAttributes.IL | MethodImplAttributes.ForwardRef);
        using TestMetadata definition = CreateTypeWithMethodImplementation(
            "C",
            "M",
            MethodImplAttributes.IL);
        var definitionMethod = new MetadataSourceEntity(
            "1",
            definition.Handles["M"]);
        MetadataMergeResult result = MetadataMerger.Merge(
            new MetadataMergeRequest(
                [
                    new MetadataMergeInput("0", declaration.Reader),
                    new MetadataMergeInput("1", definition.Reader),
                ],
                "out.exe",
                "out")
            {
                MethodBodyOffsets = new Dictionary<MetadataSourceEntity, int>
                {
                    [definitionMethod] = 123,
                },
            });
        using TestMetadata output = TestMetadata.Create(
            result.Metadata,
            new Dictionary<string, EntityHandle>());

        MethodDefinition method = output.Reader.GetMethodDefinition(
            output.Reader.MethodDefinitions.Single());

        Assert.Equal(123, method.RelativeVirtualAddress);
        Assert.Equal(MethodImplAttributes.IL, method.ImplAttributes);
    }

    [Fact]
    public void UnmanagedTypeMarkerDoesNotRequireAssemblyProvenance()
    {
        using TestMetadata first = CreateTypeWithNativeMarker(
            "C",
            TypeAttributes.Public | TypeAttributes.Class,
            "Fake");
        using TestMetadata second = CreateTypeWithNativeMarker(
            "C",
            TypeAttributes.NotPublic | TypeAttributes.Class,
            "Fake");

        MetadataMergeResult result = Merge(first, second);

        Assert.Equal(2, result.Metadata.GetRowCount(TableIndex.TypeDef));
    }

    private static MetadataMergeResult Merge(params TestMetadata[] inputs)
    {
        MetadataMergeInput[] mergeInputs = inputs
            .Select((input, index) => new MetadataMergeInput(index.ToString(), input.Reader))
            .ToArray();
        return MetadataMerger.Merge(new MetadataMergeRequest(mergeInputs, "out.exe", "out"));
    }

    private static TestMetadata CreateEmptyType(string typeName)
        => CreateType(typeName, static (_, _, _) => { });

    private static TestMetadata CreateTypeWithMethod(
        string typeName,
        string methodName,
        ParameterAttributes? parameterAttributes = null,
        MethodAttributes methodAttributes =
            MethodAttributes.Public | MethodAttributes.Static)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            BlobHandle signature = metadata.GetOrAddBlob(
                parameterAttributes.HasValue
                    ? new byte[] { 0x00, 0x01, 0x08, 0x08 }
                    : new byte[] { 0x00, 0x00, 0x08 });
            MethodDefinitionHandle method = metadata.AddMethodDefinition(
                methodAttributes,
                MethodImplAttributes.IL | MethodImplAttributes.ForwardRef,
                metadata.GetOrAddString(methodName),
                signature,
                0,
                MetadataTokens.ParameterHandle(1));
            handles[methodName] = method;
            if (parameterAttributes.HasValue)
            {
                handles["Parameter"] = metadata.AddParameter(
                    parameterAttributes.Value,
                    metadata.GetOrAddString("value"),
                    1);
            }
        });

    private static TestMetadata CreateTypeWithMethodImplementation(
        string typeName,
        string methodName,
        MethodImplAttributes implementation)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            handles[methodName] = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                implementation,
                metadata.GetOrAddString(methodName),
                metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x08 }),
                0,
                MetadataTokens.ParameterHandle(1));
        });

    private static TestMetadata CreateTypeWithNativeMarker(
        string typeName,
        TypeAttributes attributes,
        string assemblyName)
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, typeName + ".obj");
        TypeDefinitionHandle type = metadata.AddTypeDefinition(
            attributes,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString(typeName),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        AssemblyReferenceHandle assembly = metadata.AddAssemblyReference(
            metadata.GetOrAddString(assemblyName),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            default);
        TypeReferenceHandle attributeType = metadata.AddTypeReference(
            assembly,
            metadata.GetOrAddString("System.Runtime.CompilerServices"),
            metadata.GetOrAddString("NativeCppClassAttribute"));
        MemberReferenceHandle constructor = metadata.AddMemberReference(
            attributeType,
            metadata.GetOrAddString(".ctor"),
            metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));
        metadata.AddCustomAttribute(
            type,
            constructor,
            metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
        return TestMetadata.Create(
            metadata,
            new Dictionary<string, EntityHandle>
            {
                ["Type"] = type,
            });
    }

    private static TestMetadata CreateNestedType(
        string enclosingName,
        string nestedName)
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, "nested.obj");
        TypeDefinitionHandle enclosing = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString(enclosingName),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        TypeDefinitionHandle nested = metadata.AddTypeDefinition(
            TypeAttributes.NestedPublic | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString(nestedName),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddNestedType(nested, enclosing);
        return TestMetadata.Create(
            metadata,
            new Dictionary<string, EntityHandle>
            {
                ["Outer"] = enclosing,
                ["Inner"] = nested,
            });
    }

    private static TestMetadata CreateInheritanceMemberReference(
        bool vararg,
        bool addMemberAttribute = false)
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, "inheritance.obj");
        TypeDefinitionHandle baseType = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString("Base"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        TypeDefinitionHandle derivedType = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString("Derived"),
            baseType,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(2));
        MethodDefinitionHandle method = metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.ForwardRef,
            metadata.GetOrAddString("M"),
            metadata.GetOrAddBlob(
                vararg
                    ? new byte[] { 0x05, 0x01, 0x08, 0x08 }
                    : new byte[] { 0x00, 0x01, 0x08, 0x08 }),
            0,
            MetadataTokens.ParameterHandle(1));
        metadata.AddParameter(
            ParameterAttributes.None,
            metadata.GetOrAddString("value"),
            1);
        MemberReferenceHandle member = metadata.AddMemberReference(
            derivedType,
            metadata.GetOrAddString("M"),
            metadata.GetOrAddBlob(
                vararg
                    ? new byte[] { 0x05, 0x02, 0x08, 0x08, 0x41, 0x08 }
                    : new byte[] { 0x00, 0x01, 0x08, 0x08 }));
        if (addMemberAttribute)
        {
            AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
            TypeReferenceHandle attributeType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("Test"),
                metadata.GetOrAddString("MarkerAttribute"));
            MemberReferenceHandle constructor = metadata.AddMemberReference(
                attributeType,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));
            metadata.AddCustomAttribute(
                member,
                constructor,
                metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
        }
        return TestMetadata.Create(
            metadata,
            new Dictionary<string, EntityHandle>
            {
                ["Base"] = baseType,
                ["Derived"] = derivedType,
                ["Method"] = method,
                ["MemberRef"] = member,
            });
    }

    private static TestMetadata CreateUnresolvedBaseMemberReference()
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, "unresolved-base.obj");
        AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
        TypeReferenceHandle baseType = metadata.AddTypeReference(
            assembly,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString("ExternalBase"));
        TypeDefinitionHandle derivedType = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString("Derived"),
            baseType,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        MemberReferenceHandle member = metadata.AddMemberReference(
            derivedType,
            metadata.GetOrAddString("M"),
            metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x08 }));
        return TestMetadata.Create(
            metadata,
            new Dictionary<string, EntityHandle>
            {
                ["Base"] = baseType,
                ["Derived"] = derivedType,
                ["MemberRef"] = member,
            });
    }

    private static TestMetadata CreateTypeWithSuppressionAttribute(
        string typeName,
        string methodName,
        string assemblyName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = metadata.AddAssemblyReference(
                metadata.GetOrAddString(assemblyName),
                new Version(1, 0, 0, 0),
                default,
                default,
                default,
                default);
            TypeReferenceHandle attributeType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("System.Runtime.CompilerServices"),
                metadata.GetOrAddString("SuppressMergeCheckAttribute"));
            MemberReferenceHandle constructor = metadata.AddMemberReference(
                attributeType,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));
            metadata.AddCustomAttribute(
                type,
                constructor,
                metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
            BlobHandle signature = metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x08 });
            handles[methodName] = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL | MethodImplAttributes.ForwardRef,
                metadata.GetOrAddString(methodName),
                signature,
                0,
                MetadataTokens.ParameterHandle(1));
        });

    private static TestMetadata CreateTypeWithOptionalLayout(
        string typeName,
        bool hasLayout)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            if (hasLayout)
            {
                metadata.AddTypeLayout(type, 0, 4);
            }
        });

    private static TestMetadata CreateTypeWithModuleReference(
        string moduleName,
        string referenceName)
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, moduleName);
        ModuleReferenceHandle moduleRef = metadata.AddModuleReference(
            metadata.GetOrAddString(referenceName));
        return TestMetadata.Create(
            metadata,
            new Dictionary<string, EntityHandle>
            {
                ["ModuleRef"] = moduleRef,
            });
    }

    private static TestMetadata CreateTypeWithAdditiveAttribute(string typeName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = metadata.AddAssemblyReference(
                metadata.GetOrAddString("mscorlib"),
                new Version(4, 0, 0, 0),
                default,
                default,
                default,
                default);
            TypeReferenceHandle attributeType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("System.Runtime.CompilerServices"),
                metadata.GetOrAddString("NativeCppClassAttribute"));
            MemberReferenceHandle constructor = metadata.AddMemberReference(
                attributeType,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));
            CustomAttributeHandle attribute = metadata.AddCustomAttribute(
                type,
                constructor,
                metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
            handles["Attribute"] = attribute;
        });

    private static TestMetadata CreateTypeWithMethods(
        string typeName,
        string firstName,
        string secondName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            BlobHandle signature = metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x08 });
            handles[firstName] = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL | MethodImplAttributes.ForwardRef,
                metadata.GetOrAddString(firstName),
                signature,
                0,
                MetadataTokens.ParameterHandle(1));
            handles[secondName] = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL | MethodImplAttributes.ForwardRef,
                metadata.GetOrAddString(secondName),
                signature,
                0,
                MetadataTokens.ParameterHandle(1));
        });

    private static TestMetadata CreateTypeWithField(
        string typeName,
        string fieldName,
        FieldAttributes attributes,
        int? constant = null)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            FieldDefinitionHandle field = metadata.AddFieldDefinition(
                attributes,
                metadata.GetOrAddString(fieldName),
                metadata.GetOrAddBlob(new byte[] { 0x06, 0x08 }));
            handles[fieldName] = field;
            if (constant.HasValue)
            {
                metadata.AddConstant(field, constant.Value);
            }
        });

    private static TestMetadata CreateTypeWithInterface(string typeName, string interfaceName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
            TypeReferenceHandle interfaceType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("Test"),
                metadata.GetOrAddString(interfaceName));
            handles["Interface"] = interfaceType;
            handles["InterfaceImpl"] = metadata.AddInterfaceImplementation(type, interfaceType);
        });

    private static TestMetadata CreateTypeWithCustomAttribute(string typeName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
            TypeReferenceHandle attributeType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("Test"),
                metadata.GetOrAddString("MarkerAttribute"));
            MemberReferenceHandle constructor = metadata.AddMemberReference(
                attributeType,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));
            handles["AttributeType"] = attributeType;
            handles["Constructor"] = constructor;
            handles["Attribute"] = metadata.AddCustomAttribute(
                type,
                constructor,
                metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
        });

    private static TestMetadata CreateGenericType(string typeName, string constraintName)
        => CreateType(typeName, (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
            TypeReferenceHandle constraintType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("Test"),
                metadata.GetOrAddString(constraintName));
            GenericParameterHandle parameter = metadata.AddGenericParameter(
                type,
                GenericParameterAttributes.None,
                metadata.GetOrAddString("T"),
                0);
            handles["GenericParameter"] = parameter;
            handles["Constraint"] = metadata.AddGenericParameterConstraint(
                parameter,
                constraintType);
        });

    private static TestMetadata CreateReferencesAndSignatures()
        => CreateType("C", (metadata, type, handles) =>
        {
            AssemblyReferenceHandle assembly = AddExternalAssembly(metadata);
            TypeReferenceHandle externalType = metadata.AddTypeReference(
                assembly,
                metadata.GetOrAddString("Test"),
                metadata.GetOrAddString("External"));
            var typeSpecSignature = new BlobBuilder();
            new SignatureTypeEncoder(typeSpecSignature).Type(externalType, isValueType: false);
            handles["TypeRef"] = externalType;
            handles["TypeSpec"] = metadata.AddTypeSpecification(
                metadata.GetOrAddBlob(typeSpecSignature));
            handles["MemberRef"] = metadata.AddMemberReference(
                externalType,
                metadata.GetOrAddString("M"),
                metadata.GetOrAddBlob(new byte[] { 0x00, 0x00, 0x08 }));
            handles["StandaloneSig"] = metadata.AddStandaloneSignature(
                metadata.GetOrAddBlob(new byte[] { 0x07, 0x01, 0x08 }));
        });

    private static AssemblyReferenceHandle AddExternalAssembly(MetadataBuilder metadata)
        => metadata.AddAssemblyReference(
            metadata.GetOrAddString("External"),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            default);

    private static TestMetadata CreateType(
        string typeName,
        Action<MetadataBuilder, TypeDefinitionHandle, Dictionary<string, EntityHandle>> populate)
    {
        var metadata = new MetadataBuilder();
        AddModuleAndModuleType(metadata, typeName + ".obj");
        TypeDefinitionHandle type = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Test"),
            metadata.GetOrAddString(typeName),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var handles = new Dictionary<string, EntityHandle>
        {
            ["Type"] = type,
        };
        populate(metadata, type, handles);
        return TestMetadata.Create(metadata, handles);
    }

    private static void AddModuleAndModuleType(
        MetadataBuilder metadata,
        string moduleName)
    {
        metadata.AddModule(
            0,
            metadata.GetOrAddString(moduleName),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
    }

    private sealed class TestMetadata : IDisposable
    {
        private readonly MetadataReaderProvider _provider;

        private TestMetadata(
            MetadataReaderProvider provider,
            IReadOnlyDictionary<string, EntityHandle> handles)
        {
            _provider = provider;
            Reader = provider.GetMetadataReader();
            Handles = handles;
        }

        public MetadataReader Reader { get; }

        public IReadOnlyDictionary<string, EntityHandle> Handles { get; }

        public static TestMetadata Create(
            MetadataBuilder metadata,
            IReadOnlyDictionary<string, EntityHandle> handles)
        {
            var image = new BlobBuilder();
            new MetadataRootBuilder(metadata).Serialize(image, 0, 0);
            MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(
                ImmutableArray.Create(image.ToArray()));
            return new TestMetadata(provider, handles);
        }

        public void Dispose() => _provider.Dispose();
    }
}
