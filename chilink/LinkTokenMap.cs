using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

public sealed class LinkTokenMap
{
    public readonly record struct Mapping(EntityHandle Destination, bool IsDuplicate);

    private readonly MetadataReader _reader;
    private readonly MetadataBuilder _builder;
    private readonly Dictionary<EntityHandle, Mapping> _entities = new();
    private readonly Dictionary<UserStringHandle, UserStringHandle> _userStrings = new();

    internal LinkTokenMap(string inputIdentity, MetadataReader reader, MetadataBuilder builder)
    {
        InputIdentity = inputIdentity;
        _reader = reader;
        _builder = builder;
    }

    public string InputIdentity { get; }

    internal void Set(
        EntityHandle source,
        EntityHandle destination,
        bool isDuplicate = false)
    {
        if (source.IsNil)
            throw new ArgumentException("A nil source handle cannot be mapped.", nameof(source));
        if (destination.IsNil)
            throw new ArgumentException("A nil destination handle cannot be mapped.", nameof(destination));
        var mapping = new Mapping(destination, isDuplicate);
        if (_entities.TryGetValue(source, out Mapping existing) && existing != mapping)
            throw new InvalidOperationException(
                $"Source token 0x{MetadataTokens.GetToken(source):X8} in '{InputIdentity}' " +
                $"was planned twice ({existing.Destination.Kind} and {destination.Kind}).");

        _entities[source] = mapping;
    }

    internal bool TryMap(EntityHandle source, out EntityHandle destination) =>
        TryGetMapping(source, out Mapping mapping, out destination);

    private bool TryGetMapping(
        EntityHandle source,
        out Mapping mapping,
        out EntityHandle destination)
    {
        if (_entities.TryGetValue(source, out mapping))
        {
            destination = mapping.Destination;
            return true;
        }

        destination = default;
        return false;
    }

    public bool TryGetMapping(EntityHandle source, out Mapping mapping) =>
        _entities.TryGetValue(source, out mapping);

    public bool IsDuplicate(EntityHandle source) =>
        TryGetMapping(source, out Mapping mapping)
            ? mapping.IsDuplicate
            : throw new InvalidOperationException(
                $"Metadata token 0x{MetadataTokens.GetToken(source):X8} ({source.Kind}) " +
                $"from input '{InputIdentity}' was not retained or planned.");

    public EntityHandle Map(EntityHandle source)
    {
        if (source.IsNil)
            return default;
        if (_entities.TryGetValue(source, out Mapping mapping))
            return mapping.Destination;

        throw new InvalidOperationException(
            $"Metadata token 0x{MetadataTokens.GetToken(source):X8} ({source.Kind}) " +
            $"from input '{InputIdentity}' was not retained or planned.");
    }

    public EntityHandle MapToken(int metadataToken) =>
        Map(MetadataTokens.EntityHandle(metadataToken));

    public UserStringHandle MapUserString(UserStringHandle source)
    {
        if (source.IsNil)
            return default;
        if (_userStrings.TryGetValue(source, out UserStringHandle destination))
            return destination;

        destination = _builder.GetOrAddUserString(_reader.GetUserString(source));
        _userStrings.Add(source, destination);
        return destination;
    }

    public Handle MapTokenOrUserString(int token) =>
        (token >> 24) == 0x70
            ? MapUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff))
            : Map(MetadataTokens.EntityHandle(token));

    internal static void AssertHandle(EntityHandle expected, EntityHandle actual)
    {
        if (expected != actual)
            throw new InvalidOperationException(
                $"Metadata RID planning mismatch: expected {expected.Kind} " +
                $"row {MetadataTokens.GetRowNumber(expected)}, got {actual.Kind} " +
                $"row {MetadataTokens.GetRowNumber(actual)}.");
    }
}
