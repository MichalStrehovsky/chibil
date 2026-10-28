using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal enum VarargThunkKind
{
    /// <summary>Direct call to a chibil emulated variadic definition.</summary>
    Emulated,
    /// <summary>calli through a managed (__clrcall) variadic function pointer.</summary>
    ManagedCalli,
    WindowsNative,
    WindowsNativeCalli,
    LinuxNative,
    LinuxNativeCalli,
}

/// <summary>
/// A synthesized method with the exact signature of a CLR vararg call site.
/// The call site is rewritten to <c>call</c> the thunk.
/// </summary>
internal sealed class VarargThunk
{
    public VarargThunkKind Kind;
    public SourceToken Site;
    public BlobHandle Signature;
    public SourceToken Target;
    public ImportedMethod Import;
    public string Name;
    public int Index;
    public EcmaSignature.MethodParts Parts;
    // Per optional argument: buffer offset, value size, and stored slot size.
    public int[] Offsets, Sizes;
    public int BufferSize;
    public int CodeOffset;
    public int CodeSize;
    public MethodDefinitionHandle Method;
    public MethodDefinitionHandle PInvoke;
    public StandaloneSignatureHandle CalliSignature;
    public StandaloneSignatureHandle Locals;

    public bool IsCalli => Kind is VarargThunkKind.ManagedCalli or VarargThunkKind.WindowsNativeCalli or VarargThunkKind.LinuxNativeCalli;
    public bool UsesBuffer => Kind is VarargThunkKind.Emulated or VarargThunkKind.ManagedCalli;
    public bool IsLinuxNative => Kind is VarargThunkKind.LinuxNative or VarargThunkKind.LinuxNativeCalli;
    public bool IsWindowsNative => Kind is VarargThunkKind.WindowsNative or VarargThunkKind.WindowsNativeCalli;
    public int ParameterCount => Parts.Parameters.Length + (IsCalli ? 1 : 0);
}

internal sealed partial class Linker
{
    private const string VarargStubSymbol = "__chilink_get_vararg_stub";
    private readonly Dictionary<SourceToken, VarargThunk> _varargThunks = new();
    private readonly List<VarargThunk> _varargThunkList = new();
    private readonly Dictionary<(InputSection, uint), VarargThunk> _varargSites = new();
    private SourceToken _varargStubResolver;

    private void ResetVarargThunks()
    {
        _varargThunks.Clear();
        _varargThunkList.Clear();
        _varargSites.Clear();
        _varargStubResolver = default;
    }

    /// <summary>Records a vararg call/calli site and marks what its thunk needs.</summary>
    private bool TryMarkVarargSite(InputSection section, CoffRelocation relocation, byte[] data, InputSymbol symbol)
    {
        if (relocation.Offset == 0)
            return false;
        byte opcode = data[relocation.Offset - 1];
        var site = new SourceToken(section.Input.Index, (EntityHandle)symbol.Token);
        var reader = section.Input.Metadata;
        BlobHandle signature;
        if (opcode == (byte)ILOpCode.Call && site.Handle.Kind == HandleKind.MemberReference)
            signature = reader.GetMemberReference((MemberReferenceHandle)site.Handle).Signature;
        else if (opcode == (byte)ILOpCode.Calli && site.Handle.Kind == HandleKind.StandaloneSignature)
            signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)site.Handle).Signature;
        else
            return false;
        var blob = reader.GetBlobReader(signature);
        if (blob.Length == 0 || (blob.ReadByte() & 0x0F) != (byte)SignatureCallingConvention.VarArgs)
            return false;
        if (!_varargThunks.TryGetValue(site, out var thunk))
        {
            thunk = ClassifyVarargSite(section, site, signature, opcode == (byte)ILOpCode.Calli);
            _varargThunks.Add(site, thunk);
            if (thunk != null)
            {
                thunk.Index = _varargThunkList.Count;
                _varargThunkList.Add(thunk);
            }
        }
        if (thunk == null)
            return false;
        _varargSites[(section, relocation.Offset)] = thunk;
        return true;
    }

    private VarargThunk ClassifyVarargSite(InputSection section, SourceToken site, BlobHandle signature, bool calli)
    {
        var reader = section.Input.Metadata;
        var parts = EcmaSignature.SplitMethod(reader.GetBlobReader(signature), h => h);
        var thunk = new VarargThunk { Site = site, Signature = signature, Parts = parts };
        if (calli)
        {
            bool native = parts.Return.Modifiers.Any(m => IsCallConvModifier(reader, m));
            thunk.Kind = !native ? VarargThunkKind.ManagedCalli :
                _options.TargetLinux ? VarargThunkKind.LinuxNativeCalli : VarargThunkKind.WindowsNativeCalli;
            thunk.Name = "calli";
            Mark(site);
        }
        else
        {
            thunk.Name = reader.GetString(reader.GetMemberReference((MemberReferenceHandle)site.Handle).Name);
            if (_merger.IsEmulatedVararg(site))
            {
                thunk.Kind = VarargThunkKind.Emulated;
                thunk.Target = _merger.Resolve(site);
                Mark(thunk.Target);
            }
            else if (_merger.GetImport(site) is { } import)
            {
                thunk.Kind = _options.TargetLinux ? VarargThunkKind.LinuxNative : VarargThunkKind.WindowsNative;
                thunk.Import = import;
            }
            else
                return null;
            // The thunk signature embeds every type of the call site, including the optional ones.
            EcmaSignature.Rewrite(reader.GetBlobReader(signature), h =>
            {
                Mark(new SourceToken(site.Input, h));
                return h;
            });
        }
        LayoutOptionalArguments(section, thunk);
        return thunk;
    }

    private static bool IsCallConvModifier(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference)
            return false;
        var type = reader.GetTypeReference((TypeReferenceHandle)handle);
        return reader.StringComparer.Equals(type.Namespace, "System.Runtime.CompilerServices") &&
            reader.GetString(type.Name).StartsWith("CallConv", StringComparison.Ordinal);
    }

    private void LayoutOptionalArguments(InputSection section, VarargThunk thunk)
    {
        var optional = thunk.Parts.Parameters[thunk.Parts.FixedCount..];
        thunk.Offsets = new int[optional.Length];
        thunk.Sizes = new int[optional.Length];
        int offset = 0;
        for (int i = 0; i < optional.Length; i++)
        {
            var element = optional[i];
            int size = element.Code switch
            {
                (byte)SignatureTypeCode.Boolean or (byte)SignatureTypeCode.SByte or (byte)SignatureTypeCode.Byte => 1,
                (byte)SignatureTypeCode.Char or (byte)SignatureTypeCode.Int16 or (byte)SignatureTypeCode.UInt16 => 2,
                (byte)SignatureTypeCode.Int32 or (byte)SignatureTypeCode.UInt32 or (byte)SignatureTypeCode.Single => 4,
                (byte)SignatureTypeCode.Int64 or (byte)SignatureTypeCode.UInt64 or (byte)SignatureTypeCode.Double or
                (byte)SignatureTypeCode.IntPtr or (byte)SignatureTypeCode.UIntPtr or (byte)SignatureTypeCode.Pointer or
                (byte)SignatureTypeCode.FunctionPointer => 8,
                (byte)SignatureTypeKind.ValueType => ValueTypeSize(section, thunk, element),
                _ => throw section.Input.Error(
                    $"variadic call to '{thunk.Name}' passes an unsupported optional argument type (0x{element.Code:X2})"),
            };
            thunk.Sizes[i] = size;
            if (!thunk.UsesBuffer)
                continue;
            int slot = element.Code == (byte)SignatureTypeKind.ValueType && !_options.TargetLinux &&
                size is not (1 or 2 or 4 or 8) ? PointerSize : size;
            thunk.Offsets[i] = offset;
            offset = checked(offset + Align(slot, PointerSize));
        }
        thunk.BufferSize = Math.Max(PointerSize, offset);
    }

    private int ValueTypeSize(InputSection section, VarargThunk thunk, EcmaSignature.Element element)
    {
        var (size, packing) = _merger.GetValueTypeLayout(new SourceToken(thunk.Site.Input, element.Type));
        if (size <= 0)
            throw section.Input.Error($"variadic call to '{thunk.Name}' passes a structure without an explicit size");
        if (_options.TargetLinux && packing > PointerSize)
            throw section.Input.Error($"variadic call to '{thunk.Name}' passes an over-aligned structure, which is not supported");
        return size;
    }

    private bool MarkVarargStubResolver()
    {
        if (!_varargStubResolver.Handle.IsNil)
            return false;
        var thunk = _varargThunkList.FirstOrDefault(t => t.IsLinuxNative);
        if (thunk == null)
            return false;
        if (!HasSymbol(VarargStubSymbol))
        {
            if (SelectLibrarySymbol(VarargStubSymbol, null))
                return true;
            string callee = thunk.Kind == VarargThunkKind.LinuxNative ? $"'{thunk.Import.Import.Symbol}'" : "through a native pointer";
            throw new LinkException($"variadic native call {callee} requires {VarargStubSymbol} for Linux targets " +
                "(link the chibil CRT)");
        }
        _varargStubResolver = FindSymbol(VarargStubSymbol);
        bool valid = false;
        if (_varargStubResolver.Handle.Kind == HandleKind.MethodDefinition)
        {
            var reader = _inputs[_varargStubResolver.Input].Metadata;
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)_varargStubResolver.Handle);
            valid = (method.Attributes & MethodAttributes.Static) != 0 &&
                reader.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(new byte[] {
                    0, 1, (byte)SignatureTypeCode.IntPtr, (byte)SignatureTypeCode.IntPtr });
        }
        if (!valid)
            throw new LinkException($"{VarargStubSymbol} must be a static managed method returning native int and accepting native int");
        Mark(_varargStubResolver);
        return true;
    }

    /// <summary>Encodes a thunk body; before metadata is built, tokens are placeholders of the final width.</summary>
    private byte[] EncodeVarargThunk(VarargThunk thunk, bool final)
    {
        int Token(Func<EntityHandle> handle) => final ? MetadataTokens.GetToken(handle()) : 0x06000001;
        int Mapped(SourceToken token) => Token(() => (EntityHandle)_merger.Map(token.Input, token.Handle));
        var code = new BlobBuilder();
        var il = new InstructionEncoder(code);
        void Emit(ILOpCode opcode, int token)
        {
            il.OpCode(opcode);
            il.CodeBuilder.WriteInt32(token);
        }
        var parts = thunk.Parts;
        int fixedCount = parts.FixedCount;
        if (thunk.UsesBuffer)
        {
            il.LoadConstantI4(thunk.BufferSize);
            il.OpCode(ILOpCode.Conv_u);
            il.OpCode(ILOpCode.Localloc);
            il.StoreLocal(0);
            for (int i = 0; i < thunk.Sizes.Length; i++)
            {
                var element = parts.Parameters[fixedCount + i];
                int argument = fixedCount + i;
                il.LoadLocal(0);
                if (thunk.Offsets[i] != 0)
                {
                    il.LoadConstantI4(thunk.Offsets[i]);
                    il.OpCode(ILOpCode.Add);
                }
                if (element.Code == (byte)SignatureTypeKind.ValueType)
                {
                    if (!_options.TargetLinux && thunk.Sizes[i] is not (1 or 2 or 4 or 8))
                    {
                        // Windows x64 passes such structures by reference to a caller-owned copy.
                        il.LoadArgumentAddress(argument);
                        il.OpCode(ILOpCode.Conv_u);
                        il.OpCode(ILOpCode.Stind_i);
                    }
                    else
                    {
                        il.LoadArgument(argument);
                        Emit(ILOpCode.Stobj, Mapped(new SourceToken(thunk.Site.Input, element.Type)));
                    }
                    continue;
                }
                il.LoadArgument(argument);
                il.OpCode(element.Code switch
                {
                    (byte)SignatureTypeCode.Single => ILOpCode.Stind_r4,
                    (byte)SignatureTypeCode.Double => ILOpCode.Stind_r8,
                    (byte)SignatureTypeCode.Int64 or (byte)SignatureTypeCode.UInt64 => ILOpCode.Stind_i8,
                    _ => thunk.Sizes[i] switch
                    {
                        1 => ILOpCode.Stind_i1,
                        2 => ILOpCode.Stind_i2,
                        4 => ILOpCode.Stind_i4,
                        _ => ILOpCode.Stind_i,
                    },
                });
            }
            for (int i = 0; i < fixedCount; i++)
                il.LoadArgument(i);
            il.LoadLocal(0);
            if (thunk.Kind == VarargThunkKind.Emulated)
                Emit(ILOpCode.Call, Mapped(thunk.Target));
            else
            {
                il.LoadArgument(parts.Parameters.Length);
                Emit(ILOpCode.Calli, Token(() => thunk.CalliSignature));
            }
        }
        else if (thunk.IsWindowsNative)
        {
            for (int i = 0; i < fixedCount; i++)
                il.LoadArgument(i);
            for (int i = 0; i < thunk.Sizes.Length; i++)
            {
                var element = parts.Parameters[fixedCount + i];
                int argument = fixedCount + i;
                switch (WindowsLowering(element, thunk.Sizes[i]))
                {
                    case SignatureTypeCode.Int64:
                        il.LoadArgumentAddress(argument);
                        il.OpCode(thunk.Sizes[i] switch
                        {
                            1 => ILOpCode.Ldind_u1,
                            2 => ILOpCode.Ldind_u2,
                            4 => ILOpCode.Ldind_u4,
                            _ => ILOpCode.Ldind_i8,
                        });
                        il.OpCode(ILOpCode.Conv_u8);
                        break;
                    case SignatureTypeCode.IntPtr:
                        il.LoadArgumentAddress(argument);
                        il.OpCode(ILOpCode.Conv_u);
                        break;
                    default:
                        il.LoadArgument(argument);
                        break;
                }
            }
            if (thunk.Kind == VarargThunkKind.WindowsNative)
                Emit(ILOpCode.Call, Token(() => thunk.PInvoke));
            else
            {
                il.LoadArgument(parts.Parameters.Length);
                Emit(ILOpCode.Calli, Token(() => thunk.CalliSignature));
            }
        }
        else
        {
            for (int i = 0; i < parts.Parameters.Length; i++)
                il.LoadArgument(i);
            if (thunk.Kind == VarargThunkKind.LinuxNative)
            {
                Emit(ILOpCode.Ldstr, final ? MetadataTokens.GetToken(_merger.GetUserString(thunk.Import.Import.Library)) : 0x70000001);
                Emit(ILOpCode.Ldstr, final ? MetadataTokens.GetToken(_merger.GetUserString(thunk.Import.Import.EntryPoint)) : 0x70000001);
                Emit(ILOpCode.Call, Mapped(_nativeResolver));
            }
            else
                il.LoadArgument(parts.Parameters.Length);
            Emit(ILOpCode.Call, Mapped(_varargStubResolver));
            Emit(ILOpCode.Calli, Token(() => thunk.CalliSignature));
        }
        il.OpCode(ILOpCode.Ret);

        var body = new BlobBuilder();
        body.WriteUInt16(0x3013); // fat header, 3 dwords, InitLocals
        body.WriteUInt16((ushort)(thunk.ParameterCount + 4));
        body.WriteInt32(code.Count);
        body.WriteInt32(thunk.UsesBuffer ? Token(() => thunk.Locals) : 0);
        body.LinkSuffix(code);
        return body.ToArray();
    }

    /// <summary>Windows x64 variadic arguments: floating point in integer registers, aggregates by value or reference.</summary>
    internal static SignatureTypeCode? WindowsLowering(EcmaSignature.Element element, int size) => element.Code switch
    {
        (byte)SignatureTypeCode.Single or (byte)SignatureTypeCode.Double => SignatureTypeCode.Int64,
        (byte)SignatureTypeKind.ValueType => size is 1 or 2 or 4 or 8 ? SignatureTypeCode.Int64 : SignatureTypeCode.IntPtr,
        _ => null,
    };

    private int LayoutVarargThunks(int offset)
    {
        foreach (var thunk in _varargThunkList)
        {
            offset = Align(offset, 4);
            thunk.CodeOffset = offset;
            thunk.CodeSize = EncodeVarargThunk(thunk, final: false).Length;
            offset = checked(offset + thunk.CodeSize);
        }
        return offset;
    }

    private void EmitVarargThunks(BlobBuilder il)
    {
        foreach (var thunk in _varargThunkList)
        {
            if (il.Count > thunk.CodeOffset)
                throw new LinkException("vararg thunk layout mismatch");
            il.WriteBytes(0, thunk.CodeOffset - il.Count);
            byte[] body = EncodeVarargThunk(thunk, final: true);
            if (body.Length != thunk.CodeSize)
                throw new LinkException("vararg thunk size mismatch");
            il.WriteBytes(body);
        }
    }
}

internal sealed partial class MetadataMerger
{
    public (int Size, int Packing) GetValueTypeLayout(SourceToken type)
    {
        var canonical = Resolve(type);
        if (canonical.Handle.Kind != HandleKind.TypeDefinition)
            return (0, 0);
        var layout = Reader(canonical).GetTypeDefinition((TypeDefinitionHandle)canonical.Handle).GetLayout();
        return (layout.Size, layout.PackingSize);
    }

    private static byte[] StripModifiers(byte[] element)
    {
        int start = 0;
        while (element[start] is (byte)SignatureTypeCode.RequiredModifier or (byte)SignatureTypeCode.OptionalModifier)
        {
            byte first = element[start + 1];
            start += 1 + ((first & 0x80) == 0 ? 1 : (first & 0xC0) == 0x80 ? 2 : 4);
        }
        return element[start..];
    }

    private static BlobBuilder MethodSignature(byte header, byte[] result, IEnumerable<byte[]> parameters)
    {
        var parameterList = parameters.ToArray();
        var blob = new BlobBuilder();
        blob.WriteByte(header);
        blob.WriteCompressedInteger(parameterList.Length);
        blob.WriteBytes(result);
        foreach (var parameter in parameterList)
            blob.WriteBytes(parameter);
        return blob;
    }

    private void MapVarargThunk(VarargThunk thunk) =>
        thunk.Parts = EcmaSignature.SplitMethod(Reader(thunk.Site).GetBlobReader(thunk.Signature), h => Output(thunk.Site.Input, h));

    private IEnumerable<byte[]> LoweredOptionals(VarargThunk thunk) =>
        thunk.Parts.Parameters[thunk.Parts.FixedCount..].Select((p, i) => Linker.WindowsLowering(p, thunk.Sizes[i]) switch
        {
            SignatureTypeCode code => new[] { (byte)code },
            null => p.Bytes,
        });

    /// <summary>Adds thunk and lowered P/Invoke MethodDefs; the caller reserves their rows.</summary>
    private void AddVarargThunkMethods(MetadataBuilder output, IReadOnlyList<VarargThunk> thunks,
        Dictionary<string, ModuleReferenceHandle> nativeModules)
    {
        foreach (var thunk in thunks)
        {
            MapVarargThunk(thunk);
            var parts = thunk.Parts;
            var parameters = parts.Parameters.Select(p => p.Bytes);
            if (thunk.IsCalli)
                parameters = parameters.Append(new[] { (byte)SignatureTypeCode.IntPtr });
            thunk.Method = output.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL, output.GetOrAddString($"__chilink_vararg@{thunk.Name}@{thunk.Index}"),
                output.GetOrAddBlob(MethodSignature(0, StripModifiers(parts.Return.Bytes), parameters)),
                thunk.CodeOffset, MetadataTokens.ParameterHandle(1));
        }
        foreach (var thunk in thunks.Where(t => t.Kind == VarargThunkKind.WindowsNative))
        {
            var parts = thunk.Parts;
            var import = thunk.Import;
            thunk.PInvoke = output.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
                MethodImplAttributes.IL | MethodImplAttributes.PreserveSig,
                output.GetOrAddString($"__chilink_vararg_import@{import.Import.EntryPoint}@{thunk.Index}"),
                output.GetOrAddBlob(MethodSignature(0, parts.Return.Bytes,
                    parts.Parameters[..parts.FixedCount].Select(p => p.Bytes).Concat(LoweredOptionals(thunk)))),
                -1, MetadataTokens.ParameterHandle(1));
            if (!nativeModules.TryGetValue(import.Import.Library, out var module))
                nativeModules.Add(import.Import.Library, module = output.AddModuleReference(output.GetOrAddString(import.Import.Library)));
            output.AddMethodImport(thunk.PInvoke, import.Convention | MethodImportAttributes.ExactSpelling,
                output.GetOrAddString(import.Import.EntryPoint), module);
        }
    }

    public static int VarargThunkMethodCount(IReadOnlyList<VarargThunk> thunks) =>
        thunks.Count + thunks.Count(t => t.Kind == VarargThunkKind.WindowsNative);

    private void AddVarargThunkSignatures(MetadataBuilder output, IReadOnlyList<VarargThunk> thunks)
    {
        StandaloneSignatureHandle locals = default;
        foreach (var thunk in thunks)
        {
            var parts = thunk.Parts;
            var fixedParameters = parts.Parameters[..parts.FixedCount].Select(p => p.Bytes);
            if (thunk.UsesBuffer)
            {
                if (locals.IsNil)
                    locals = output.AddStandaloneSignature(output.GetOrAddBlob(new byte[] { 0x07, 0x01, (byte)SignatureTypeCode.IntPtr }));
                thunk.Locals = locals;
            }
            BlobBuilder signature = thunk.Kind switch
            {
                VarargThunkKind.ManagedCalli => MethodSignature(0, parts.Return.Bytes,
                    fixedParameters.Append(new[] { (byte)SignatureTypeCode.Pointer, (byte)SignatureTypeCode.Void })),
                VarargThunkKind.WindowsNativeCalli => MethodSignature((byte)SignatureCallingConvention.CDecl,
                    StripModifiers(parts.Return.Bytes), fixedParameters.Concat(LoweredOptionals(thunk))),
                VarargThunkKind.LinuxNative or VarargThunkKind.LinuxNativeCalli => MethodSignature(
                    (byte)SignatureCallingConvention.CDecl, StripModifiers(parts.Return.Bytes), parts.Parameters.Select(p => p.Bytes)),
                _ => null,
            };
            if (signature != null)
                thunk.CalliSignature = output.AddStandaloneSignature(output.GetOrAddBlob(signature));
        }
    }
}
