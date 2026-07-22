using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

public static class Linker
{
    public static void Link(LinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var inputs = new List<CoffInput>(options.InputFiles.Count);
        try
        {
            for (int i = 0; i < options.InputFiles.Count; i++)
            {
                inputs.Add(new CoffInput(options.InputFiles[i], i, options.Machine));
            }

            var symbols = new SymbolResolver(inputs);
            CoffInputSymbol entrySymbol = symbols.ResolveEntryPoint(options.EntryPoint);
            var reachability = new ReachabilityGraph(symbols);
            HashSet<CoffInputSection> liveSections =
                reachability.Compute(entrySymbol, options.OptimizeReferences);
            SectionLayoutPlan layout = SectionLayout.Plan(inputs, liveSections);

            (CoffInput EntryInput, MethodDefinitionHandle EntryMethod) =
                FindEntryMethod(entrySymbol);

            MetadataMergeRequest mergeRequest = CreateMergeRequest(
                options,
                inputs,
                symbols,
                liveSections,
                layout,
                EntryInput,
                EntryMethod);
            MetadataMergeResult metadata = MetadataMerger.Merge(mergeRequest);
            if (metadata.EntryPoint.IsNil)
            {
                throw new ChilinkException(
                    $"entry point symbol '{options.EntryPoint}' did not map to a retained MethodDef");
            }

            SectionStreams streams = layout.Materialize((input, sourceToken) =>
            {
                LinkTokenMap map = metadata.MapsByIdentity[GetInputIdentity(input)];
                Handle mapped = map.MapTokenOrUserString(sourceToken);
                return MetadataTokens.GetToken(mapped);
            });

            ManagedPeEmitter.Emit(
                options,
                metadata.Metadata,
                streams.IlStream,
                streams.MappedFieldData,
                metadata.EntryPoint);
        }
        catch (ChilinkException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is BadImageFormatException or
                InvalidOperationException or
                ArgumentException or
                NotSupportedException or
                OverflowException)
        {
            throw new ChilinkException(ex.Message, ex);
        }
        finally
        {
            foreach (CoffInput input in inputs)
            {
                input.Dispose();
            }
        }
    }

    private static MetadataMergeRequest CreateMergeRequest(
        LinkOptions options,
        IReadOnlyList<CoffInput> inputs,
        SymbolResolver symbols,
        IReadOnlySet<CoffInputSection> liveSections,
        SectionLayoutPlan layout,
        CoffInput entryInput,
        MethodDefinitionHandle entryMethod)
    {
        var mergeInputs = new List<MetadataMergeInput>(inputs.Count);
        foreach (CoffInput input in inputs)
        {
            EntityHandle[] discarded = input.DefinedClrTokens
                .Where(pair =>
                {
                    CoffInputSection section = input.Sections.Single(
                        candidate => candidate.Handle == pair.Value.Section);
                    return !liveSections.Contains(section);
                })
                .Select(pair => pair.Key)
                .ToArray();

            mergeInputs.Add(new MetadataMergeInput(
                GetInputIdentity(input),
                input.Metadata,
                retainedEntities: null,
                discardedEntities: discarded));
        }

        var bodyOffsets = layout.MethodBodyOffsets.ToDictionary(
            pair => new MetadataSourceEntity(
                GetInputIdentity(pair.Key.Input),
                pair.Key.Method),
            pair => pair.Value);
        var fieldOffsets = layout.FieldDataOffsets.ToDictionary(
            pair => new MetadataSourceEntity(
                GetInputIdentity(pair.Key.Input),
                pair.Key.Field),
            pair => pair.Value);
        IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> referenceBindings =
            CreateReferenceBindings(inputs, symbols, liveSections);

        string moduleName = Path.GetFileName(options.OutputFile);
        string assemblyName = Path.GetFileNameWithoutExtension(options.OutputFile);
        return new MetadataMergeRequest(mergeInputs, moduleName, assemblyName)
        {
            MethodBodyOffsets = bodyOffsets,
            FieldRvaOffsets = fieldOffsets,
            ReferenceBindings = referenceBindings,
            EntryPoint = new MetadataSourceEntity(
                GetInputIdentity(entryInput),
                entryMethod),
        };
    }

    private static IReadOnlyDictionary<MetadataSourceEntity, MetadataSourceEntity> CreateReferenceBindings(
        IReadOnlyList<CoffInput> inputs,
        SymbolResolver symbols,
        IReadOnlySet<CoffInputSection> liveSections)
    {
        var bindings = new Dictionary<MetadataSourceEntity, MetadataSourceEntity>();
        foreach (CoffInput input in inputs)
        {
            int memberRefCount = input.Metadata.GetTableRowCount(TableIndex.MemberRef);
            for (int row = 1; row <= memberRefCount; row++)
            {
                var source = MetadataTokens.MemberReferenceHandle(row);
                if (!symbols.TryResolveMemberReference(input, source, out CoffInputSymbol definition))
                {
                    continue;
                }
                CoffInputSection definitionSection = definition.Input.Sections.Single(
                    section => section.Handle == definition.Section);
                if (!liveSections.Contains(definitionSection))
                {
                    continue;
                }

                EntityHandle target = FindDefinitionToken(definition);
                bindings.Add(
                    new MetadataSourceEntity(GetInputIdentity(input), source),
                    new MetadataSourceEntity(GetInputIdentity(definition.Input), target));
            }
        }
        return bindings;
    }

    private static EntityHandle FindDefinitionToken(CoffInputSymbol definition)
    {
        foreach ((EntityHandle token, CoffInputSymbol tokenSymbol) in definition.Input.DefinedClrTokens)
        {
            if (tokenSymbol.ClrTokenTarget == definition.Handle ||
                (tokenSymbol.Section == definition.Section &&
                 tokenSymbol.Value == definition.Value))
            {
                return token;
            }
        }

        throw new ChilinkException(
            $"symbol '{definition.Name}' is not associated with a managed definition token");
    }

    private static (CoffInput Input, MethodDefinitionHandle Method) FindEntryMethod(
        CoffInputSymbol entrySymbol)
    {
        foreach ((EntityHandle token, CoffInputSymbol tokenSymbol) in entrySymbol.Input.DefinedClrTokens)
        {
            if (token.Kind != HandleKind.MethodDefinition)
            {
                continue;
            }

            if (tokenSymbol.ClrTokenTarget == entrySymbol.Handle ||
                (tokenSymbol.Section == entrySymbol.Section &&
                 tokenSymbol.Value == entrySymbol.Value))
            {
                return (entrySymbol.Input, (MethodDefinitionHandle)token);
            }
        }

        throw new ChilinkException(
            $"entry point symbol '{entrySymbol.Name}' is not associated with a MethodDef");
    }

    private static string GetInputIdentity(CoffInput input) => input.Ordinal.ToString();
}
