using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed class SymbolResolver
{
    private readonly IReadOnlyList<CoffInput> _inputs;
    private readonly Dictionary<string, CoffInputSymbol> _externalDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<CoffInputSection, CoffInputSection> _canonicalSections = new();
    private readonly HashSet<CoffInputSection> _selectedSections = new();

    public SymbolResolver(IReadOnlyList<CoffInput> inputs)
    {
        _inputs = inputs;
        SelectComdats();
        BuildExternalDefinitions();
    }

    public IReadOnlySet<CoffInputSection> SelectedSections => _selectedSections;

    public CoffInputSection GetCanonicalSection(CoffInputSection section)
        => _canonicalSections.TryGetValue(section, out CoffInputSection canonical) ? canonical : section;

    public CoffInputSymbol ResolveEntryPoint(string name)
    {
        if (!_externalDefinitions.TryGetValue(name, out CoffInputSymbol symbol))
        {
            symbol = ResolveManagedEntryByName(name) ?? ResolveNativeEntryAlias(name);
            if (symbol == null)
            {
                throw new ChilinkException($"unresolved entry point symbol '{name}'");
            }
        }

        return symbol;
    }

    private CoffInputSymbol ResolveManagedEntryByName(string name)
    {
        var candidates = new List<CoffInputSymbol>();
        foreach (CoffInput input in _inputs)
        {
            foreach ((EntityHandle token, CoffInputSymbol tokenSymbol) in input.DefinedClrTokens)
            {
                if (token.Kind != HandleKind.MethodDefinition ||
                    tokenSymbol.ClrTokenTarget is not CoffSymbolHandle targetHandle)
                {
                    continue;
                }

                MethodDefinition method = input.Metadata.GetMethodDefinition(
                    (MethodDefinitionHandle)token);
                if (!input.Metadata.GetString(method.Name).Equals(name, StringComparison.Ordinal))
                {
                    continue;
                }

                CoffInputSymbol target = input.SymbolsByHandle[targetHandle];
                CoffInputSection section = input.Sections.Single(
                    candidate => candidate.Handle == target.Section);
                if (_selectedSections.Contains(GetCanonicalSection(section)))
                {
                    candidates.Add(target);
                }
            }
        }

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new ChilinkException($"entry point name '{name}' is ambiguous"),
        };
    }

    private CoffInputSymbol ResolveNativeEntryAlias(string name)
    {
        foreach (CoffInputSymbol alias in _inputs.SelectMany(input => input.Symbols))
        {
            if (!alias.IsDefined ||
                !alias.IsExternal ||
                !alias.Name.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            CoffInputSection aliasSection = alias.Input.Sections.Single(
                section => section.Handle == alias.Section);
            if (!aliasSection.IsNativeTransitionSection)
            {
                continue;
            }

            foreach (CoffInputRelocation aliasRelocation in aliasSection.Relocations)
            {
                CoffInputSymbol slot = alias.Input.SymbolsByHandle[aliasRelocation.Symbol];
                if (!slot.IsDefined)
                {
                    continue;
                }

                CoffInputSection slotSection = slot.Input.Sections.Single(
                    section => section.Handle == slot.Section);
                foreach (CoffInputRelocation slotRelocation in slotSection.Relocations)
                {
                    CoffInputSymbol tokenSymbol =
                        slot.Input.SymbolsByHandle[slotRelocation.Symbol];
                    if (!tokenSymbol.IsClrToken)
                    {
                        continue;
                    }

                    if (tokenSymbol.ClrToken.Kind == HandleKind.MemberReference)
                    {
                        string decoratedName = GetDecoratedName(
                            slot.Input.Metadata,
                            (MemberReferenceHandle)tokenSymbol.ClrToken);
                        if (decoratedName != null &&
                            _externalDefinitions.TryGetValue(
                                decoratedName,
                                out CoffInputSymbol memberDefinition))
                        {
                            return memberDefinition;
                        }
                    }

                    if (tokenSymbol.ClrToken.Kind == HandleKind.MethodDefinition &&
                        slot.Input.DefinedClrTokens.TryGetValue(
                            tokenSymbol.ClrToken,
                            out CoffInputSymbol definitionToken) &&
                        definitionToken.ClrTokenTarget is CoffSymbolHandle targetHandle)
                    {
                        return slot.Input.SymbolsByHandle[targetHandle];
                    }
                }
            }
        }

        return null;
    }

    public CoffInputSymbol ResolveRelocationTarget(CoffInput source, CoffInputRelocation relocation)
    {
        if (!source.SymbolsByHandle.TryGetValue(relocation.Symbol, out CoffInputSymbol symbol))
        {
            throw new ChilinkException(
                $"input '{source.Path}' contains a relocation that references invalid symbol index {relocation.Symbol.Index}");
        }

        if (symbol.IsDefined)
        {
            return symbol;
        }

        if (symbol.IsClrToken)
        {
            return ResolveClrTokenReference(source, symbol);
        }

        if (symbol.IsExternal && _externalDefinitions.TryGetValue(symbol.Name, out CoffInputSymbol definition))
        {
            return definition;
        }

        throw new ChilinkException($"unresolved external symbol '{symbol.Name}' referenced by '{source.Path}'");
    }

    public bool TryResolveMemberReference(
        CoffInput source,
        MemberReferenceHandle member,
        out CoffInputSymbol definition)
    {
        string decoratedName = GetDecoratedName(source.Metadata, member);
        if (decoratedName != null &&
            _externalDefinitions.TryGetValue(decoratedName, out definition))
        {
            return true;
        }

        definition = null;
        return false;
    }

    private void SelectComdats()
    {
        var groups = new Dictionary<string, List<CoffInputSection>>(StringComparer.Ordinal);

        foreach (CoffInputSection section in _inputs.SelectMany(input => input.Sections))
        {
            if (section.IsDebug || section.IsNativeTransitionSection)
            {
                continue;
            }

            if (!section.IsComdat)
            {
                _selectedSections.Add(section);
                continue;
            }

            if (section.SectionDefinition == null)
            {
                throw new ChilinkException(
                    $"COMDAT section '{section.Name}' in '{section.Input.Path}' has no section-definition symbol");
            }

            if (section.SectionDefinition.Selection == CoffComdatSelection.Associative)
            {
                continue;
            }

            string key = GetComdatKey(section);
            if (!groups.TryGetValue(key, out List<CoffInputSection> contributions))
            {
                contributions = new List<CoffInputSection>();
                groups.Add(key, contributions);
            }
            contributions.Add(section);
        }

        foreach ((string key, List<CoffInputSection> contributions) in groups)
        {
            CoffInputSection winner = SelectComdatWinner(key, contributions);
            _selectedSections.Add(winner);
            foreach (CoffInputSection contribution in contributions)
            {
                _canonicalSections[contribution] = winner;
            }
        }

        foreach (CoffInputSection section in _inputs.SelectMany(input => input.Sections))
        {
            if (!section.IsComdat ||
                section.SectionDefinition?.Selection != CoffComdatSelection.Associative ||
                section.IsDebug ||
                section.IsNativeTransitionSection)
            {
                continue;
            }

            CoffSectionHandle associated = section.SectionDefinition.AssociatedSection;
            CoffInputSection parent = section.Input.Sections.SingleOrDefault(
                candidate => candidate.Handle == associated);
            if (parent == null)
            {
                throw new ChilinkException(
                    $"associative COMDAT '{section.Name}' in '{section.Input.Path}' references missing section {associated.SectionNumber}");
            }

            CoffInputSection canonicalParent = GetCanonicalSection(parent);
            if (_selectedSections.Contains(canonicalParent) && ReferenceEquals(parent, canonicalParent))
            {
                _selectedSections.Add(section);
            }
        }
    }

    private static string GetComdatKey(CoffInputSection section)
    {
        CoffInputSymbol key = section.Symbols.FirstOrDefault(
            symbol => !symbol.IsClrToken && symbol.SectionDefinition == null && symbol.Name != section.Name);
        if (key == null)
        {
            throw new ChilinkException(
                $"cannot determine COMDAT key for section '{section.Name}' in '{section.Input.Path}'");
        }

        return key.Name;
    }

    private static CoffInputSection SelectComdatWinner(string key, List<CoffInputSection> contributions)
    {
        CoffComdatSelection selection = contributions[0].SectionDefinition.Selection;
        if (contributions.Any(section => section.SectionDefinition.Selection != selection))
        {
            throw new ChilinkException($"COMDAT '{key}' has conflicting selection kinds");
        }

        return selection switch
        {
            CoffComdatSelection.NoDuplicates when contributions.Count > 1
                => throw new ChilinkException($"multiply defined COMDAT symbol '{key}'"),
            CoffComdatSelection.NoDuplicates or CoffComdatSelection.Any
                => contributions[0],
            CoffComdatSelection.SameSize
                => SelectSameSize(key, contributions),
            CoffComdatSelection.ExactMatch
                => SelectExactMatch(key, contributions),
            CoffComdatSelection.Largest
                => contributions.OrderByDescending(section => section.Content.Length).First(),
            _ => throw new ChilinkException($"unsupported COMDAT selection '{selection}' for '{key}'"),
        };
    }

    private static CoffInputSection SelectSameSize(string key, List<CoffInputSection> contributions)
    {
        int size = contributions[0].Content.Length;
        if (contributions.Any(section => section.Content.Length != size))
        {
            throw new ChilinkException($"COMDAT '{key}' has different sizes with SAME_SIZE selection");
        }
        return contributions[0];
    }

    private static CoffInputSection SelectExactMatch(string key, List<CoffInputSection> contributions)
    {
        CoffInputSection first = contributions[0];
        foreach (CoffInputSection section in contributions.Skip(1))
        {
            if (!first.Content.AsSpan().SequenceEqual(section.Content.AsSpan()) ||
                !RelocationsMatch(first, section))
            {
                throw new ChilinkException($"COMDAT '{key}' differs with EXACT_MATCH selection");
            }
        }
        return first;
    }

    private static bool RelocationsMatch(CoffInputSection left, CoffInputSection right)
    {
        if (left.Relocations.Count != right.Relocations.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Relocations.Count; i++)
        {
            CoffInputRelocation l = left.Relocations[i];
            CoffInputRelocation r = right.Relocations[i];
            if (l.Offset != r.Offset || l.Type != r.Type)
            {
                return false;
            }

            string leftName = left.Input.SymbolsByHandle[l.Symbol].Name;
            string rightName = right.Input.SymbolsByHandle[r.Symbol].Name;
            if (!leftName.Equals(rightName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void BuildExternalDefinitions()
    {
        foreach (CoffInputSymbol symbol in _inputs.SelectMany(input => input.Symbols))
        {
            if (!symbol.IsDefined || !symbol.IsExternal || symbol.IsClrToken)
            {
                continue;
            }

            CoffInputSection section = symbol.Input.Sections.Single(candidate => candidate.Handle == symbol.Section);
            CoffInputSection canonical = GetCanonicalSection(section);
            if (!_selectedSections.Contains(canonical) || !ReferenceEquals(section, canonical))
            {
                continue;
            }

            if (!_externalDefinitions.TryAdd(symbol.Name, symbol))
            {
                throw new ChilinkException($"multiply defined external symbol '{symbol.Name}'");
            }
        }
    }

    private CoffInputSymbol ResolveClrTokenReference(CoffInput source, CoffInputSymbol tokenSymbol)
    {
        if (tokenSymbol.Name.Length != 8 ||
            !int.TryParse(tokenSymbol.Name, System.Globalization.NumberStyles.HexNumber, null, out int tokenValue))
        {
            throw new ChilinkException(
                $"input '{source.Path}' contains malformed CLR token symbol '{tokenSymbol.Name}'");
        }

        Handle handle = MetadataTokens.Handle(tokenValue);
        if (handle.Kind == HandleKind.UserString)
        {
            return null;
        }
        if (handle.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference member = source.Metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (TryResolveMemberReference(
            source,
            (MemberReferenceHandle)handle,
            out CoffInputSymbol definition))
        {
            return definition;
        }

        string decoratedName = GetDecoratedName(source.Metadata, (MemberReferenceHandle)handle);
        string memberName = source.Metadata.GetString(member.Name);
        var candidates = _externalDefinitions.Values.Where(
            candidate => candidate.ClrToken.Kind == HandleKind.MethodDefinition &&
                         candidate.Input.Metadata.GetString(
                             candidate.Input.Metadata.GetMethodDefinition(
                                 (MethodDefinitionHandle)candidate.ClrToken).Name) == memberName).ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        if (decoratedName == null)
        {
            return null;
        }

        throw new ChilinkException(
            $"unresolved external symbol '{decoratedName}' referenced by '{source.Path}'");
    }

    private static string GetDecoratedName(MetadataReader reader, MemberReferenceHandle member)
    {
        foreach (CustomAttributeHandle attributeHandle in reader.GetCustomAttributes(member))
        {
            CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
            if (!IsDecoratedNameConstructor(reader, attribute.Constructor))
            {
                continue;
            }

            BlobReader value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                throw new ChilinkException("malformed DecoratedNameAttribute");
            }
            return value.ReadSerializedString();
        }

        return null;
    }

    private static bool IsDecoratedNameConstructor(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle owner = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };

        return owner.Kind switch
        {
            HandleKind.TypeReference => IsDecoratedNameType(reader, reader.GetTypeReference((TypeReferenceHandle)owner)),
            HandleKind.TypeDefinition => IsDecoratedNameType(reader, reader.GetTypeDefinition((TypeDefinitionHandle)owner)),
            _ => false,
        };
    }

    private static bool IsDecoratedNameType(MetadataReader reader, TypeReference type)
        => reader.GetString(type.Namespace) == "System.Runtime.CompilerServices" &&
           reader.GetString(type.Name) == "DecoratedNameAttribute";

    private static bool IsDecoratedNameType(MetadataReader reader, TypeDefinition type)
        => reader.GetString(type.Namespace) == "System.Runtime.CompilerServices" &&
           reader.GetString(type.Name) == "DecoratedNameAttribute";
}
