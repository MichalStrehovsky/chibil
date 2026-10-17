using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

internal sealed record NativeImport(string Symbol, string Library, string EntryPoint, string Source);
internal sealed record GlobalReference(SourceToken Token, string Name, string Symbol, BlobHandle Signature, bool Method);
internal sealed record ImportedMethod(GlobalReference Reference, NativeImport Import, MethodImportAttributes Convention);

internal sealed partial class MetadataMerger
{
    private readonly Dictionary<SourceToken, ImportedMethod> _imports = new();
    private readonly Dictionary<NativeImport, string> _importSignatures = new();

    public ImportedMethod GetImport(SourceToken token) => _imports.GetValueOrDefault(Resolve(token));

    public IEnumerable<GlobalReference> GetUnresolvedGlobals()
    {
        for (int i = 0; i < _inputs.Count; i++)
        {
            var reader = _inputs[i];
            foreach (var handle in reader.MemberReferences)
            {
                SourceToken token = new(i, handle);
                var reference = reader.GetMemberReference(handle);
                var parent = Resolve(new(i, reference.Parent));
                if (parent != Module && parent != ModuleType)
                    continue;
                var resolved = Resolve(token);
                if (_imports.ContainsKey(resolved) ||
                    (resolved.Handle.Kind != HandleKind.MemberReference && !IsForwardReference(resolved)))
                    continue;
                if (resolved.Handle.Kind == HandleKind.MemberReference &&
                    _referenceParents[resolved].Handle.Kind == HandleKind.MethodDefinition &&
                    !IsForwardReference(_referenceParents[resolved]))
                    continue;
                string name = reader.GetString(reference.Name);
                yield return new(token, name, DecoratedName(token) ?? name, reference.Signature,
                    reference.GetKind() == MemberReferenceKind.Method);
            }
            foreach (var handle in reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(1)).GetMethods())
            {
                SourceToken token = new(i, handle);
                if (_imports.ContainsKey(Resolve(token)) || !IsForwardReference(Resolve(token)))
                    continue;
                var method = reader.GetMethodDefinition(handle);
                string name = reader.GetString(method.Name);
                yield return new(token, name, DecoratedName(token) ?? name, method.Signature, true);
            }
        }
    }

    private bool IsForwardReference(SourceToken token)
    {
        if (token.Handle.Kind != HandleKind.MethodDefinition)
            return false;
        var source = _methodSource[token];
        var method = Reader(source).GetMethodDefinition((MethodDefinitionHandle)source.Handle);
        return (method.ImplAttributes & MethodImplAttributes.ForwardRef) != 0 &&
            (method.ImplAttributes & (MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall)) == 0 &&
            (method.Attributes & MethodAttributes.PinvokeImpl) == 0;
    }

    private string DecoratedName(SourceToken token)
    {
        foreach (var handle in Reader(token).GetCustomAttributes(token.Handle))
        {
            var attribute = new SourceToken(token.Input, handle);
            if (AttributeName(attribute) != "System.Runtime.CompilerServices.DecoratedNameAttribute")
                continue;
            var blob = Reader(token).GetBlobReader(Reader(token).GetCustomAttribute(handle).Value);
            if (blob.ReadUInt16() != 1)
                throw Failure(token, "invalid DecoratedName attribute");
            string name = blob.ReadSerializedString();
            if (string.IsNullOrEmpty(name) || blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
                throw Failure(token, "invalid DecoratedName attribute");
            return name;
        }
        return null;
    }

    private string TypeReferenceName(int input, EntityHandle handle)
    {
        var token = Resolve(new(input, handle));
        var reader = Reader(token);
        if (token.Handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)token.Handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        if (token.Handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)token.Handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        return "";
    }

    private MethodImportAttributes? NativeConvention(GlobalReference reference)
    {
        if (!reference.Method)
            return null;
        var signature = Reader(reference.Token).GetBlobReader(reference.Signature);
        var header = signature.ReadSignatureHeader();
        if (header.IsInstance || header.IsGeneric || header.CallingConvention != SignatureCallingConvention.Default)
            return null;
        signature.ReadCompressedInteger();
        MethodImportAttributes? convention = null;
        SignatureTypeCode code = signature.ReadSignatureTypeCode();
        while (code is SignatureTypeCode.OptionalModifier or SignatureTypeCode.RequiredModifier)
        {
            string name = TypeReferenceName(reference.Token.Input, signature.ReadTypeHandle());
            MethodImportAttributes? current = name switch
            {
                "System.Runtime.CompilerServices.CallConvCdecl" => MethodImportAttributes.CallingConventionCDecl,
                "System.Runtime.CompilerServices.CallConvStdcall" => MethodImportAttributes.CallingConventionStdCall,
                "System.Runtime.CompilerServices.CallConvThiscall" => MethodImportAttributes.CallingConventionThisCall,
                "System.Runtime.CompilerServices.CallConvFastcall" => MethodImportAttributes.CallingConventionFastCall,
                _ => null,
            };
            if (current != null && convention != null && convention != current)
                throw Failure(reference.Token, "conflicting native calling-convention modifiers");
            convention ??= current;
            code = signature.ReadSignatureTypeCode();
        }
        return convention;
    }

    public IEnumerable<string> GetLibrarySymbols(GlobalReference reference)
    {
        yield return reference.Symbol;
        if (!reference.Method)
        {
            if (!reference.Symbol.StartsWith("__imp_", StringComparison.Ordinal))
                yield return "__imp_" + reference.Symbol;
            yield break;
        }
        if (NativeConvention(reference) == null)
            yield break;
        // MSVC decorates a C managed entrypoint even though its native export is bare.
        string prefix = "?" + reference.Name + "@@$$J0Y";
        if (reference.Symbol.StartsWith(prefix, StringComparison.Ordinal) &&
            !reference.Name.Contains('@') && !reference.Name.Contains('?'))
            yield return reference.Name;
    }

    public void BindImport(GlobalReference reference, NativeImport import)
    {
        MethodImportAttributes convention = NativeConvention(reference)
            ?? throw Failure(reference.Token, $"import '{import.Symbol}' requires a static nongeneric native-convention method");
        if (reference.Token.Handle.Kind == HandleKind.MethodDefinition)
        {
            var method = Reader(reference.Token).GetMethodDefinition((MethodDefinitionHandle)reference.Token.Handle);
            if ((method.Attributes & MethodAttributes.Static) == 0 ||
                (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.PrivateScope)
                throw Failure(reference.Token, "native import cannot bind a private-scope or instance method");
        }
        string signature = SignatureKey(reference.Token.Input, reference.Signature);
        if (_importSignatures.TryGetValue(import, out string previous) && previous != signature)
            throw Failure(reference.Token, $"incompatible signatures for native import '{import.Symbol}'");
        _importSignatures[import] = signature;
        _imports.TryAdd(Resolve(reference.Token), new(reference, import, convention));
    }
}
