using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

public readonly record struct MetadataSourceEntity(string InputIdentity, EntityHandle Handle)
{
    public override string ToString() =>
        $"{InputIdentity}:0x{MetadataTokens.GetToken(Handle):X8}";
}

public sealed class MetadataMergeInput
{
    public MetadataMergeInput(
        string identity,
        MetadataReader reader,
        IReadOnlyCollection<EntityHandle> retainedEntities = null,
        IReadOnlyCollection<EntityHandle> discardedEntities = null)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("An input identity is required.", nameof(identity));

        Identity = identity;
        Reader = reader;
        RetainedEntities = retainedEntities;
        DiscardedEntities = discardedEntities;
    }

    public string Identity { get; }
    public MetadataReader Reader { get; }

    // Null retains the complete input. A non-null collection is treated as a
    // root set; owners and metadata required by retained definitions are kept.
    public IReadOnlyCollection<EntityHandle> RetainedEntities { get; }
    public IReadOnlyCollection<EntityHandle> DiscardedEntities { get; }
}

public sealed class MetadataMergeRequest
{
    public MetadataMergeRequest(
        IReadOnlyList<MetadataReader> inputs,
        string moduleName,
        string assemblyName)
        : this(
            inputs?.Select(
                static (reader, index) =>
                    new MetadataMergeInput(index.ToString(), reader))
                .ToArray(),
            moduleName,
            assemblyName)
    {
    }

    public MetadataMergeRequest(
        IReadOnlyList<MetadataMergeInput> inputs,
        string moduleName,
        string assemblyName)
    {
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        if (inputs.Count == 0)
            throw new ArgumentException("At least one metadata input is required.", nameof(inputs));
        if (string.IsNullOrWhiteSpace(moduleName))
            throw new ArgumentException("An output module name is required.", nameof(moduleName));
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("An output assembly name is required.", nameof(assemblyName));

        ModuleName = moduleName;
        AssemblyName = assemblyName;
    }

    public IReadOnlyList<MetadataMergeInput> Inputs { get; }
    public string ModuleName { get; }
    public string AssemblyName { get; }
    public Guid? ModuleVersionId { get; init; }
    public Version AssemblyVersion { get; init; } = new(0, 0, 0, 0);
    public string AssemblyCulture { get; init; } = string.Empty;
    public byte[] AssemblyPublicKey { get; init; }
    public AssemblyFlags AssemblyFlags { get; init; }
    public AssemblyHashAlgorithm AssemblyHashAlgorithm { get; init; }
    public IReadOnlyDictionary<MetadataSourceEntity, int> MethodBodyOffsets { get; init; } =
        new ReadOnlyDictionary<MetadataSourceEntity, int>(
            new Dictionary<MetadataSourceEntity, int>());
    public IReadOnlyDictionary<MetadataSourceEntity, int> FieldRvaOffsets { get; init; } =
        new ReadOnlyDictionary<MetadataSourceEntity, int>(
            new Dictionary<MetadataSourceEntity, int>());
    public IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> ReferenceBindings { get; init; } =
        new ReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity>(
            new Dictionary<MetadataSourceEntity, MetadataSourceEntity>());
    public MetadataSourceEntity? EntryPoint { get; init; }
}

public sealed class MetadataMergeResult
{
    internal MetadataMergeResult(
        MetadataBuilder metadata,
        IReadOnlyList<LinkTokenMap> tokenMaps,
        MethodDefinitionHandle entryPoint)
    {
        Metadata = metadata;
        MetadataRoot = new MetadataRootBuilder(metadata);
        TokenMaps = tokenMaps;
        EntryPoint = entryPoint;
        MapsByIdentity = new ReadOnlyDictionary<string, LinkTokenMap>(
            tokenMaps.ToDictionary(static map => map.InputIdentity, StringComparer.Ordinal));
    }

    public MetadataBuilder Metadata { get; }
    public MetadataRootBuilder MetadataRoot { get; }
    public IReadOnlyList<LinkTokenMap> TokenMaps { get; }
    public IReadOnlyDictionary<string, LinkTokenMap> MapsByIdentity { get; }
    public MethodDefinitionHandle EntryPoint { get; }
}
