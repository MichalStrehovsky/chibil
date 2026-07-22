using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Chilink;

public static class SignatureRewriter
{
    public static BlobBuilder RewriteMethod(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteMethod();

    public static BlobBuilder RewriteField(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteField();

    public static BlobBuilder RewriteMemberReference(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteMemberReference();

    public static BlobBuilder RewriteStandalone(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteStandalone();

    public static BlobBuilder RewriteTypeSpecification(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteTypeSpecification();

    public static BlobBuilder RewriteMethodSpecification(BlobReader reader, LinkTokenMap map) =>
        new Worker(reader, map).RewriteMethodSpecification();

    internal static void MarkMethod(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteMethod();

    internal static void MarkField(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteField();

    internal static void MarkMemberReference(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteMemberReference();

    internal static void MarkStandalone(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteStandalone();

    internal static void MarkTypeSpecification(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteTypeSpecification();

    internal static void MarkMethodSpecification(BlobReader reader, Action<EntityHandle> mark) =>
        new Worker(reader, mark).RewriteMethodSpecification();

    internal static bool TryRewriteVarargMethodFixed(
        BlobReader reader,
        LinkTokenMap map,
        out BlobBuilder signature)
    {
        BlobReader probe = reader;
        SignatureHeader header = probe.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method ||
            header.CallingConvention != SignatureCallingConvention.VarArgs)
        {
            signature = null;
            return false;
        }

        int fixedParameterCount = new Worker(reader, map).CountFixedParameters();
        signature = new Worker(reader, map).RewriteFixedMethod(fixedParameterCount);
        return true;
    }

    internal static bool TryRewriteVarargMethodFixed(
        BlobReader reader,
        Action<EntityHandle> mark,
        out BlobBuilder signature)
    {
        BlobReader probe = reader;
        SignatureHeader header = probe.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method ||
            header.CallingConvention != SignatureCallingConvention.VarArgs)
        {
            signature = null;
            return false;
        }

        int fixedParameterCount = new Worker(reader, mark).CountFixedParameters();
        signature = new Worker(reader, mark).RewriteFixedMethod(fixedParameterCount);
        return true;
    }

    private struct Worker
    {
        private BlobReader _reader;
        private readonly LinkTokenMap _map;
        private readonly Action<EntityHandle> _mark;

        public Worker(BlobReader reader, LinkTokenMap map)
        {
            _reader = reader;
            _map = map;
            _mark = null;
        }

        public Worker(BlobReader reader, Action<EntityHandle> mark)
        {
            _reader = reader;
            _map = null;
            _mark = mark ?? throw new ArgumentNullException(nameof(mark));
        }

        public BlobBuilder RewriteMethod()
        {
            var output = new BlobBuilder();
            RewriteMethod(output, _reader.ReadSignatureHeader());
            EnsureComplete();
            return output;
        }

        public BlobBuilder RewriteField()
        {
            var output = new BlobBuilder();
            RewriteField(output, _reader.ReadSignatureHeader());
            EnsureComplete();
            return output;
        }

        public BlobBuilder RewriteMemberReference()
        {
            var output = new BlobBuilder();
            SignatureHeader header = _reader.ReadSignatureHeader();
            if (header.Kind == SignatureKind.Method)
                RewriteMethod(output, header);
            else if (header.Kind == SignatureKind.Field)
                RewriteField(output, header);
            else
                throw new BadImageFormatException(
                    $"Unexpected MemberRef signature kind {header.Kind}.");
            EnsureComplete();
            return output;
        }

        public BlobBuilder RewriteStandalone()
        {
            var output = new BlobBuilder();
            SignatureHeader header = _reader.ReadSignatureHeader();
            if (header.Kind == SignatureKind.Method)
            {
                RewriteMethod(output, header);
            }
            else if (header.Kind == SignatureKind.LocalVariables)
            {
                RewriteLocals(output);
            }
            else
            {
                throw new BadImageFormatException(
                    $"Unexpected StandAloneSig signature kind {header.Kind}.");
            }
            EnsureComplete();
            return output;
        }

        public BlobBuilder RewriteTypeSpecification()
        {
            var output = new BlobBuilder();
            RewriteType(new SignatureTypeEncoder(output));
            EnsureComplete();
            return output;
        }

        public BlobBuilder RewriteMethodSpecification()
        {
            var output = new BlobBuilder();
            SignatureHeader header = _reader.ReadSignatureHeader();
            if (header.Kind != SignatureKind.MethodSpecification)
                throw new BadImageFormatException("Expected a MethodSpec signature.");

            int count = _reader.ReadCompressedInteger();
            GenericTypeArgumentsEncoder arguments =
                new BlobEncoder(output).MethodSpecificationSignature(count);
            for (int i = 0; i < count; i++)
                RewriteType(arguments.AddArgument());
            EnsureComplete();
            return output;
        }

        public int CountFixedParameters()
        {
            SignatureHeader header = _reader.ReadSignatureHeader();
            if (header.Kind != SignatureKind.Method ||
                header.CallingConvention != SignatureCallingConvention.VarArgs)
            {
                throw new BadImageFormatException("Expected a vararg method signature.");
            }

            if (header.IsGeneric)
                _reader.ReadCompressedInteger();
            int count = _reader.ReadCompressedInteger();
            var scratch = new BlobBuilder();
            RewriteReturnType(new ReturnTypeEncoder(scratch));
            var parameters = new ParametersEncoder(scratch, hasVarArgs: true);
            for (int i = 0; i < count; i++)
            {
                if (PeekTypeCode() == SignatureTypeCode.Sentinel)
                    return i;
                RewriteParameter(parameters.AddParameter());
            }
            return count;
        }

        public BlobBuilder RewriteFixedMethod(int fixedParameterCount)
        {
            SignatureHeader header = _reader.ReadSignatureHeader();
            if (header.Kind != SignatureKind.Method ||
                header.CallingConvention != SignatureCallingConvention.VarArgs)
            {
                throw new BadImageFormatException("Expected a vararg method signature.");
            }

            int genericArity = header.IsGeneric ? _reader.ReadCompressedInteger() : 0;
            int count = _reader.ReadCompressedInteger();
            if ((uint)fixedParameterCount > (uint)count)
                throw new BadImageFormatException("Invalid fixed vararg parameter count.");

            var output = new BlobBuilder();
            output.WriteByte(header.RawValue);
            if (header.IsGeneric)
                output.WriteCompressedInteger(genericArity);
            output.WriteCompressedInteger(fixedParameterCount);
            RewriteReturnType(new ReturnTypeEncoder(output));
            var parameters = new ParametersEncoder(output, hasVarArgs: true);
            for (int i = 0; i < fixedParameterCount; i++)
                RewriteParameter(parameters.AddParameter());
            return output;
        }

        private void RewriteMethod(BlobBuilder output, SignatureHeader header)
        {
            int genericArity = header.IsGeneric ? _reader.ReadCompressedInteger() : 0;
            int count = _reader.ReadCompressedInteger();
            output.WriteByte(header.RawValue);
            if (header.IsGeneric)
                output.WriteCompressedInteger(genericArity);
            output.WriteCompressedInteger(count);
            RewriteMethodParameters(
                count,
                new ReturnTypeEncoder(output),
                new ParametersEncoder(
                    output,
                    header.CallingConvention == SignatureCallingConvention.VarArgs));
        }

        private void RewriteMethod(MethodSignatureEncoder signature)
        {
            int count = _reader.ReadCompressedInteger();
            signature.Parameters(
                count,
                out ReturnTypeEncoder returnType,
                out ParametersEncoder parameters);
            RewriteMethodParameters(count, returnType, parameters);
        }

        private void RewriteMethodParameters(
            int count,
            ReturnTypeEncoder returnType,
            ParametersEncoder parameters)
        {
            RewriteReturnType(returnType);

            for (int i = 0; i < count; i++)
            {
                SignatureTypeCode code = PeekTypeCode();
                if (code == SignatureTypeCode.Sentinel)
                {
                    _reader.ReadSignatureTypeCode();
                    parameters = parameters.StartVarArgs();
                }
                RewriteParameter(parameters.AddParameter());
            }
        }

        private void RewriteReturnType(ReturnTypeEncoder encoder)
        {
            bool byRef = false;
            while (true)
            {
                SignatureTypeCode code = _reader.ReadSignatureTypeCode();
                if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                {
                    RewriteModifier(code, encoder.CustomModifiers());
                    continue;
                }
                if (code == SignatureTypeCode.ByReference)
                {
                    byRef = true;
                    continue;
                }
                if (code == SignatureTypeCode.Void)
                    encoder.Void();
                else if (code == SignatureTypeCode.TypedReference)
                    encoder.TypedReference();
                else
                    RewriteType(code, encoder.Type(byRef));
                return;
            }
        }

        private void RewriteParameter(ParameterTypeEncoder encoder)
        {
            bool byRef = false;
            while (true)
            {
                SignatureTypeCode code = _reader.ReadSignatureTypeCode();
                if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                {
                    RewriteModifier(code, encoder.CustomModifiers());
                    continue;
                }
                if (code == SignatureTypeCode.ByReference)
                {
                    byRef = true;
                    continue;
                }
                if (code == SignatureTypeCode.TypedReference)
                    encoder.TypedReference();
                else
                    RewriteType(code, encoder.Type(byRef));
                return;
            }
        }

        private void RewriteField(BlobBuilder output, SignatureHeader header)
        {
            if (header.Kind != SignatureKind.Field)
                throw new BadImageFormatException("Expected a field signature.");

            FieldTypeEncoder encoder = new BlobEncoder(output).Field();
            bool byRef = false;
            while (true)
            {
                SignatureTypeCode code = _reader.ReadSignatureTypeCode();
                if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                {
                    RewriteModifier(code, encoder.CustomModifiers());
                    continue;
                }
                if (code == SignatureTypeCode.ByReference)
                {
                    byRef = true;
                    continue;
                }
                if (code == SignatureTypeCode.TypedReference)
                    encoder.TypedReference();
                else
                    RewriteType(code, encoder.Type(byRef));
                return;
            }
        }

        private void RewriteLocals(BlobBuilder output)
        {
            int count = _reader.ReadCompressedInteger();
            LocalVariablesEncoder locals = new BlobEncoder(output).LocalVariableSignature(count);
            for (int i = 0; i < count; i++)
            {
                LocalVariableTypeEncoder local = locals.AddVariable();
                bool byRef = false;
                bool pinned = false;
                while (true)
                {
                    SignatureTypeCode code = _reader.ReadSignatureTypeCode();
                    if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
                    {
                        RewriteModifier(code, local.CustomModifiers());
                        continue;
                    }
                    if (code == SignatureTypeCode.ByReference)
                    {
                        byRef = true;
                        continue;
                    }
                    if (code == SignatureTypeCode.Pinned)
                    {
                        pinned = true;
                        continue;
                    }
                    if (code == SignatureTypeCode.TypedReference)
                        local.TypedReference();
                    else
                        RewriteType(code, local.Type(byRef, pinned));
                    break;
                }
            }
        }

        private void RewriteType(SignatureTypeEncoder encoder) =>
            RewriteType(_reader.ReadSignatureTypeCode(), encoder);

        private void RewriteType(SignatureTypeCode code, SignatureTypeEncoder encoder)
        {
        again:
            switch (code)
            {
                case SignatureTypeCode.Void:
                    encoder.Builder.WriteByte((byte)SignatureTypeCode.Void);
                    break;
                case SignatureTypeCode.Boolean: encoder.Boolean(); break;
                case SignatureTypeCode.Char: encoder.Char(); break;
                case SignatureTypeCode.SByte: encoder.SByte(); break;
                case SignatureTypeCode.Byte: encoder.Byte(); break;
                case SignatureTypeCode.Int16: encoder.Int16(); break;
                case SignatureTypeCode.UInt16: encoder.UInt16(); break;
                case SignatureTypeCode.Int32: encoder.Int32(); break;
                case SignatureTypeCode.UInt32: encoder.UInt32(); break;
                case SignatureTypeCode.Int64: encoder.Int64(); break;
                case SignatureTypeCode.UInt64: encoder.UInt64(); break;
                case SignatureTypeCode.Single: encoder.Single(); break;
                case SignatureTypeCode.Double: encoder.Double(); break;
                case SignatureTypeCode.String: encoder.String(); break;
                case SignatureTypeCode.IntPtr: encoder.IntPtr(); break;
                case SignatureTypeCode.UIntPtr: encoder.UIntPtr(); break;
                case SignatureTypeCode.Object: encoder.Object(); break;
                case SignatureTypeCode.TypedReference: encoder.TypedReference(); break;

                case SignatureTypeCode.TypeHandle:
                {
                    _reader.Offset--;
                    byte classOrValueType = _reader.ReadByte();
                    if (classOrValueType is not 0x11 and not 0x12)
                        throw new BadImageFormatException("Invalid class/value-type signature.");
                    encoder.Type(
                        Map(_reader.ReadTypeHandle()),
                        isValueType: classOrValueType == 0x11);
                    break;
                }

                case SignatureTypeCode.Pointer:
                {
                    SignatureTypeEncoder target = encoder.Pointer();
                    RewriteType(target);
                    break;
                }

                case SignatureTypeCode.SZArray:
                    RewriteType(encoder.SZArray());
                    break;

                case SignatureTypeCode.Array:
                {
                    encoder.Array(out SignatureTypeEncoder element, out ArrayShapeEncoder shape);
                    RewriteType(element);
                    int rank = _reader.ReadCompressedInteger();
                    int sizeCount = _reader.ReadCompressedInteger();
                    var sizes = ImmutableArray.CreateBuilder<int>(sizeCount);
                    for (int i = 0; i < sizeCount; i++)
                        sizes.Add(_reader.ReadCompressedInteger());
                    int lowerBoundCount = _reader.ReadCompressedInteger();
                    var lowerBounds = ImmutableArray.CreateBuilder<int>(lowerBoundCount);
                    for (int i = 0; i < lowerBoundCount; i++)
                        lowerBounds.Add(_reader.ReadCompressedSignedInteger());
                    shape.Shape(rank, sizes.MoveToImmutable(), lowerBounds.MoveToImmutable());
                    break;
                }

                case SignatureTypeCode.GenericTypeParameter:
                    encoder.GenericTypeParameter(_reader.ReadCompressedInteger());
                    break;

                case SignatureTypeCode.GenericMethodParameter:
                    encoder.GenericMethodTypeParameter(_reader.ReadCompressedInteger());
                    break;

                case SignatureTypeCode.RequiredModifier:
                case SignatureTypeCode.OptionalModifier:
                    RewriteModifier(code, encoder.CustomModifiers());
                    code = _reader.ReadSignatureTypeCode();
                    goto again;

                case SignatureTypeCode.GenericTypeInstance:
                {
                    int classOrValueType = _reader.ReadCompressedInteger();
                    if (classOrValueType is not 0x11 and not 0x12)
                        throw new BadImageFormatException("Invalid generic type instantiation.");
                    EntityHandle genericType = Map(_reader.ReadTypeHandle());
                    int count = _reader.ReadCompressedInteger();
                    GenericTypeArgumentsEncoder arguments = encoder.GenericInstantiation(
                        genericType,
                        count,
                        isValueType: classOrValueType == 0x11);
                    for (int i = 0; i < count; i++)
                        RewriteType(arguments.AddArgument());
                    break;
                }

                case SignatureTypeCode.FunctionPointer:
                {
                    SignatureHeader header = _reader.ReadSignatureHeader();
                    int arity = header.IsGeneric ? _reader.ReadCompressedInteger() : 0;
                    FunctionPointerAttributes attributes = 0;
                    if (header.IsInstance)
                        attributes |= FunctionPointerAttributes.HasThis;
                    if (header.HasExplicitThis)
                        attributes |= FunctionPointerAttributes.HasExplicitThis;
                    MethodSignatureEncoder signature = encoder.FunctionPointer(
                        header.CallingConvention,
                        attributes,
                        arity);
                    RewriteMethod(signature);
                    break;
                }

                default:
                    throw new BadImageFormatException(
                        $"Unexpected signature type code 0x{(byte)code:X2}.");
            }
        }

        private void RewriteModifier(
            SignatureTypeCode code,
            CustomModifiersEncoder modifiers) =>
            modifiers.AddModifier(
                Map(_reader.ReadTypeHandle()),
                code == SignatureTypeCode.OptionalModifier);

        private EntityHandle Map(EntityHandle source)
        {
            if (_mark is null)
                return _map.Map(source);

            _mark(source);
            return source;
        }

        private SignatureTypeCode PeekTypeCode()
        {
            int offset = _reader.Offset;
            SignatureTypeCode code = _reader.ReadSignatureTypeCode();
            _reader.Offset = offset;
            return code;
        }

        private void EnsureComplete()
        {
            if (_reader.RemainingBytes != 0)
                throw new BadImageFormatException(
                    $"Signature rewrite left {_reader.RemainingBytes} unread byte(s).");
        }
    }
}
