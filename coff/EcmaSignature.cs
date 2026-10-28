using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Coff;

/// <summary>Lossless ECMA-335 signature traversal, with a callback for embedded type tokens.</summary>
public static class EcmaSignature
{
    public static BlobBuilder Rewrite(BlobReader reader, Func<EntityHandle, EntityHandle> map,
        bool typeSpecification = false, bool fixedArgumentsOnly = false)
    {
        var walker = new Walker(reader, map);
        var result = new BlobBuilder();
        if (typeSpecification)
            walker.Type(result, 0);
        else
            walker.Signature(result, 0, fixedArgumentsOnly);
        if (walker.Reader.RemainingBytes != 0)
            throw new BadImageFormatException("Trailing bytes in metadata signature.");
        return result;
    }

    /// <summary>One method-signature element with mapped bytes and its unmapped shape.</summary>
    public sealed record Element(byte[] Bytes, byte Code, EntityHandle Type, EntityHandle[] Modifiers);

    /// <summary>A non-generic method signature split around the vararg sentinel.</summary>
    public sealed record MethodParts(byte Header, Element Return, Element[] Parameters, int FixedCount);

    public static MethodParts SplitMethod(BlobReader reader, Func<EntityHandle, EntityHandle> map)
    {
        var walker = new Walker(reader, map);
        SignatureHeader header = walker.Reader.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || header.IsGeneric || header.IsInstance)
            throw new BadImageFormatException("Expected a static non-generic method signature.");
        int count = walker.Reader.ReadCompressedInteger();
        if (count < 0 || count > walker.Reader.RemainingBytes)
            throw new BadImageFormatException("Invalid metadata signature count.");
        Element Next()
        {
            BlobReader peek = walker.Reader;
            var modifiers = new System.Collections.Generic.List<EntityHandle>();
            byte code;
            while ((code = peek.ReadByte()) is (byte)SignatureTypeCode.RequiredModifier or (byte)SignatureTypeCode.OptionalModifier)
                modifiers.Add(peek.ReadTypeHandle());
            EntityHandle type = code is (byte)SignatureTypeKind.ValueType or (byte)SignatureTypeKind.Class
                ? peek.ReadTypeHandle() : default;
            var bytes = new BlobBuilder();
            walker.Type(bytes, 1);
            return new Element(bytes.ToArray(), code, type, modifiers.ToArray());
        }
        Element result = Next();
        var parameters = new Element[count];
        int fixedCount = count;
        for (int i = 0; i < count; i++)
        {
            BlobReader peek = walker.Reader;
            if (peek.ReadByte() == (byte)SignatureTypeCode.Sentinel)
            {
                if (fixedCount != count || header.CallingConvention != SignatureCallingConvention.VarArgs)
                    throw new BadImageFormatException("Invalid vararg sentinel.");
                fixedCount = i;
                walker.Reader.ReadByte();
            }
            parameters[i] = Next();
        }
        if (walker.Reader.RemainingBytes != 0)
            throw new BadImageFormatException("Trailing bytes in metadata signature.");
        return new MethodParts(header.RawValue, result, parameters, fixedCount);
    }

    private struct Walker
    {
        private const int MaxNestingDepth = 128;
        private const byte NativeVarArgsCallingConvention = 0x0B;

        public BlobReader Reader;
        private readonly Func<EntityHandle, EntityHandle> _map;

        public Walker(BlobReader reader, Func<EntityHandle, EntityHandle> map)
        {
            Reader = reader;
            _map = map ?? throw new ArgumentNullException(nameof(map));
        }

        private static void Depth(int depth)
        {
            if (depth > MaxNestingDepth)
                throw new BadImageFormatException("Metadata signature nesting is too deep.");
        }

        private int Count()
        {
            int count = Reader.ReadCompressedInteger();
            if (count < 0 || count > Reader.RemainingBytes)
                throw new BadImageFormatException("Invalid metadata signature count.");
            return count;
        }

        private void Token(BlobBuilder output)
        {
            EntityHandle input = Reader.ReadTypeHandle();
            if (input.IsNil)
                throw new BadImageFormatException("Nil type token in metadata signature.");
            EntityHandle mapped = _map(input);
            if (mapped.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification))
                throw new BadImageFormatException("Signature type token mapped to a non-type.");
            if (mapped.IsNil)
                throw new BadImageFormatException("Signature type token has no mapping.");
            output.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(mapped));
        }

        public void Signature(BlobBuilder output, int depth, bool fixedArgumentsOnly = false)
        {
            Depth(depth);
            SignatureHeader header = Reader.ReadSignatureHeader();
            output.WriteByte(header.RawValue);
            SignatureKind kind = header.Kind;
            if ((header.Attributes & ~(SignatureAttributes.Generic | SignatureAttributes.Instance | SignatureAttributes.ExplicitThis)) != 0)
                throw new BadImageFormatException("Invalid signature header.");
            if (kind == SignatureKind.Field)
            {
                Type(output, depth + 1);
                return;
            }
            if (kind is SignatureKind.LocalVariables or SignatureKind.MethodSpecification)
            {
                int count = Count();
                output.WriteCompressedInteger(count);
                for (int i = 0; i < count; i++)
                    Type(output, depth + 1);
                return;
            }
            bool nativeVarArgs = (header.RawValue & SignatureHeader.CallingConventionOrKindMask) == NativeVarArgsCallingConvention;
            if (kind is not (SignatureKind.Method or SignatureKind.Property) && !nativeVarArgs)
                throw new BadImageFormatException("Unsupported signature calling convention.");
            if (header.IsGeneric)
                output.WriteCompressedInteger(Reader.ReadCompressedInteger());
            int parameterCount = Count();
            var parameters = new BlobBuilder();
            Type(parameters, depth + 1);
            int fixedCount = parameterCount;
            bool optional = false;
            for (int i = 0; i < parameterCount; i++)
            {
                byte next = Reader.ReadByte();
                Reader.Offset--;
                if (next == (byte)SignatureTypeCode.Sentinel)
                {
                    if (optional || (header.CallingConvention != SignatureCallingConvention.VarArgs && !nativeVarArgs))
                        throw new BadImageFormatException("Invalid vararg sentinel.");
                    Reader.ReadByte();
                    optional = true;
                    fixedCount = i;
                    if (!fixedArgumentsOnly)
                        parameters.WriteByte((byte)SignatureTypeCode.Sentinel);
                }
                Type(fixedArgumentsOnly && optional ? new BlobBuilder() : parameters, depth + 1);
            }
            output.WriteCompressedInteger(fixedArgumentsOnly ? fixedCount : parameterCount);
            parameters.WriteContentTo(output);
        }

        public void Type(BlobBuilder output, int depth)
        {
            Depth(depth);
            // ReadSignatureTypeCode would collapse the class/value-type distinction.
            var code = (SignatureTypeCode)Reader.ReadByte();
            output.WriteByte((byte)code);
            switch (code)
            {
                case >= SignatureTypeCode.Void and <= SignatureTypeCode.String:
                case SignatureTypeCode.TypedReference:
                case SignatureTypeCode.IntPtr:
                case SignatureTypeCode.UIntPtr:
                case SignatureTypeCode.Object:
                    return;
                case (SignatureTypeCode)SignatureTypeKind.ValueType:
                case (SignatureTypeCode)SignatureTypeKind.Class:
                    Token(output);
                    return;
                case SignatureTypeCode.Pointer:
                case SignatureTypeCode.ByReference:
                case SignatureTypeCode.SZArray:
                case SignatureTypeCode.Pinned:
                    Type(output, depth + 1);
                    return;
                case SignatureTypeCode.GenericTypeParameter:
                case SignatureTypeCode.GenericMethodParameter:
                    output.WriteCompressedInteger(Reader.ReadCompressedInteger());
                    return;
                case SignatureTypeCode.RequiredModifier:
                case SignatureTypeCode.OptionalModifier:
                    Token(output);
                    Type(output, depth + 1);
                    return;
                case SignatureTypeCode.Array:
                    Type(output, depth + 1);
                    int rank = Reader.ReadCompressedInteger();
                    if (rank <= 0)
                        throw new BadImageFormatException("Invalid array rank.");
                    output.WriteCompressedInteger(rank);
                    int sizes = Count();
                    if (sizes > rank)
                        throw new BadImageFormatException("Too many array sizes.");
                    output.WriteCompressedInteger(sizes);
                    for (int i = 0; i < sizes; i++)
                        output.WriteCompressedInteger(Reader.ReadCompressedInteger());
                    int bounds = Count();
                    if (bounds > rank)
                        throw new BadImageFormatException("Too many array lower bounds.");
                    output.WriteCompressedInteger(bounds);
                    for (int i = 0; i < bounds; i++)
                        output.WriteCompressedSignedInteger(Reader.ReadCompressedSignedInteger());
                    return;
                case SignatureTypeCode.GenericTypeInstance:
                    var tag = (SignatureTypeKind)Reader.ReadByte();
                    if (tag is not (SignatureTypeKind.ValueType or SignatureTypeKind.Class))
                        throw new BadImageFormatException("Invalid generic type signature.");
                    output.WriteByte((byte)tag);
                    Token(output);
                    int arguments = Count();
                    output.WriteCompressedInteger(arguments);
                    for (int i = 0; i < arguments; i++)
                        Type(output, depth + 1);
                    return;
                case SignatureTypeCode.FunctionPointer:
                    Signature(output, depth + 1);
                    return;
                default:
                    throw new BadImageFormatException($"Unsupported signature element 0x{(byte)code:X2}.");
            }
        }
    }
}
