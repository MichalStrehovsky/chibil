using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Asm2Obj;

/// <summary>
/// Produces MSVC-compatible decorated names from ECMA method signatures.
/// Mirrors the algorithm in <c>chibil/CodeGen.cs::MangleFunctionName</c> so
/// symbols emitted by asm2obj and chibil for matching C-style signatures agree
/// at link time.
///
/// Format: <c>?name@@$$J0Y[A|G|M]&lt;ret&gt;&lt;params&gt;@Z</c>.
/// Calling-convention letter:
///   A — cdecl  (return type carries modopt(CallConvCdecl))
///   G — stdcall(return type carries modopt(CallConvStdcall))
///   M — clrcall(return type carries modopt(CallConvClrcall) OR no callconv modopt — default)
///
/// Only C-shaped signatures are auto-manglable. Managed reference types
/// (String, Object, SZArray of reference types, generic class instances)
/// cannot be auto-mangled and require <c>[DecoratedNameAttribute]</c>.
/// </summary>
public sealed class MsvcNameMangler
{
    private readonly MetadataReader _reader;
    private readonly Machine _machine;
    private readonly bool _is32;
    private readonly string _e;

    // Per-function backref tables, reset on each Mangle* call.
    private List<string> _nameBackRefs;
    private Dictionary<string, int> _argBackRefs;

    public MsvcNameMangler(MetadataReader reader, Machine machine)
    {
        _reader = reader;
        _machine = machine;
        _is32 = machine == Machine.I386;
        _e = _is32 ? "" : "E";
    }

    /// <summary>
    /// Mangle a MethodDefinition into its MSVC decorated name.
    /// </summary>
    public string MangleMethod(MethodDefinitionHandle handle)
    {
        var def = _reader.GetMethodDefinition(handle);
        string name = _reader.GetString(def.Name);
        var sigReader = _reader.GetBlobReader(def.Signature);
        return MangleMethodCore(name, sigReader);
    }

    /// <summary>
    /// Mangle a MemberReference (must be a method member ref) into its MSVC
    /// decorated name. Used for ForwardRef extern declarations.
    /// </summary>
    public string MangleMemberRef(MemberReferenceHandle handle)
    {
        var mr = _reader.GetMemberReference(handle);
        if (mr.GetKind() != MemberReferenceKind.Method)
            throw new InvalidOperationException("MangleMemberRef requires a method MemberReference.");
        string name = _reader.GetString(mr.Name);
        var sigReader = _reader.GetBlobReader(mr.Signature);
        return MangleMethodCore(name, sigReader);
    }

    private string MangleMethodCore(string methodName, BlobReader sigReader)
    {
        _nameBackRefs = new List<string> { methodName };
        _argBackRefs = new Dictionary<string, int>();

        // Read SignatureHeader; we ignore the in-band calling convention because
        // managed methods always have Default (0x00) and the real callconv is
        // expressed via modopts on the return type.
        SignatureHeader header = sigReader.ReadSignatureHeader();
        if (header.IsGeneric)
            sigReader.ReadCompressedInteger(); // discard generic arity
        int paramCount = sigReader.ReadCompressedInteger();

        // Decode return-type modopts first to determine the callconv letter.
        // We collect modopts but defer emitting the return-type mangling until
        // after we've written the cc letter.
        var retMods = ReadModOptChain(ref sigReader);
        char ccLetter = retMods.CallConv switch
        {
            CallConvKind.Cdecl => 'A',
            CallConvKind.Stdcall => 'G',
            CallConvKind.Clrcall => 'M',
            _ => 'M', // default: clrcall
        };

        var sb = new StringBuilder();
        sb.Append('?').Append(methodName).Append("@@$$J0Y").Append(ccLetter);

        // Now mangle the return type (which has its modopts already consumed).
        // The sigReader is positioned at the return-type's type code, but we
        // haven't read it yet (ReadModOptChain stops at the first non-modopt code).
        MangleTypeFromBlob(sb, ref sigReader, retMods, isReturn: true, registerArgBackref: false);

        if (paramCount == 0)
        {
            sb.Append("XZ");
        }
        else
        {
            for (int i = 0; i < paramCount; i++)
            {
                MangleArg(sb, ref sigReader);
            }
            sb.Append("@Z");
        }

        return sb.ToString();
    }

    // ─── Per-arg mangling with backref lookup ────────────────────────────────

    private void MangleArg(StringBuilder sb, ref BlobReader sigReader)
    {
        // Capture the slice for this arg by mangling twice:
        //  1) canonical pass: no name or arg backrefs, used as backref key
        //  2) live pass: name backrefs allowed, arg backref recorded
        //
        // To avoid re-reading the blob, we snapshot the BlobReader offset, do
        // the canonical pass, restore, then do the live pass.

        int startOffset = sigReader.Offset;

        var savedName = _nameBackRefs;
        var savedArg = _argBackRefs;
        _nameBackRefs = null;
        _argBackRefs = new Dictionary<string, int>();

        var tmp = new StringBuilder();
        var canonReader = sigReader; // struct copy
        var mods = ReadModOptChain(ref canonReader);
        MangleTypeFromBlob(tmp, ref canonReader, mods, isReturn: false, registerArgBackref: false);
        string canonical = tmp.ToString();

        _nameBackRefs = savedName;
        _argBackRefs = savedArg;

        // Check arg-backref table
        if (_argBackRefs.TryGetValue(canonical, out int slot))
        {
            // Skip the arg in the real reader to keep it advancing
            sigReader.Offset = canonReader.Offset;
            sb.Append((char)('0' + slot));
            return;
        }

        // Live pass
        var liveReader = sigReader; // struct copy positioned at startOffset already
        var liveMods = ReadModOptChain(ref liveReader);
        MangleTypeFromBlob(sb, ref liveReader, liveMods, isReturn: false, registerArgBackref: false);
        sigReader.Offset = liveReader.Offset;

        if (canonical.Length > 1 && _argBackRefs.Count < 10)
            _argBackRefs[canonical] = _argBackRefs.Count;
    }

    // ─── Core type mangling ──────────────────────────────────────────────────

    private void MangleTypeFromBlob(StringBuilder sb, ref BlobReader sigReader, ModOptInfo mods,
        bool isReturn, bool registerArgBackref)
    {
        SignatureTypeCode tc = sigReader.ReadSignatureTypeCode();
        MangleTypeCore(sb, ref sigReader, tc, mods, isReturn);
    }

    private void MangleTypeCore(StringBuilder sb, ref BlobReader sigReader, SignatureTypeCode tc,
        ModOptInfo mods, bool isReturn)
    {
        switch (tc)
        {
            case SignatureTypeCode.Void:
                sb.Append('X');
                return;
            case SignatureTypeCode.Boolean:
                sb.Append("_N");
                return;
            case SignatureTypeCode.Char:
                sb.Append('G'); // wchar_t / uint16
                return;
            case SignatureTypeCode.SByte:
                sb.Append(mods.IsSignUnspecifiedByte ? 'D' : 'C');
                return;
            case SignatureTypeCode.Byte:
                sb.Append('E');
                return;
            case SignatureTypeCode.Int16:
                sb.Append('F');
                return;
            case SignatureTypeCode.UInt16:
                sb.Append('G');
                return;
            case SignatureTypeCode.Int32:
                sb.Append(mods.IsLong ? 'J' : 'H');
                return;
            case SignatureTypeCode.UInt32:
                sb.Append(mods.IsLong ? 'K' : 'I');
                return;
            case SignatureTypeCode.Int64:
                sb.Append("_J");
                return;
            case SignatureTypeCode.UInt64:
                sb.Append("_K");
                return;
            case SignatureTypeCode.Single:
                sb.Append('M');
                return;
            case SignatureTypeCode.Double:
                sb.Append('N');
                return;
            case SignatureTypeCode.IntPtr:
                if (_is32) sb.Append('H');
                else sb.Append("_J");
                return;
            case SignatureTypeCode.UIntPtr:
                sb.Append(_is32 ? "I" : "_K");
                return;
            case SignatureTypeCode.Pointer:
                ManglePointer(sb, ref sigReader);
                return;
            case SignatureTypeCode.TypeHandle:
                {
                    // Step back to read the raw class/valuetype tag.
                    sigReader.Offset = sigReader.Offset - 1;
                    byte cv = sigReader.ReadByte();
                    bool isValueType = cv == 0x11;
                    EntityHandle typeHandle = sigReader.ReadTypeHandle();
                    if (!isValueType)
                        throw new NotSupportedException(
                            "Mangling of managed reference types (class) is not supported. " +
                            "Use [DecoratedNameAttribute] on methods that take/return reference types.");
                    if (isReturn) sb.Append("?A");
                    sb.Append('U');
                    MangleTagName(sb, GetTypeName(typeHandle));
                }
                return;
            case SignatureTypeCode.FunctionPointer:
                MangleFunctionPointer(sb, ref sigReader);
                return;
            case SignatureTypeCode.String:
            case SignatureTypeCode.Object:
            case SignatureTypeCode.SZArray:
            case SignatureTypeCode.Array:
            case SignatureTypeCode.GenericTypeInstance:
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
            case SignatureTypeCode.ByReference:
                throw new NotSupportedException(
                    $"Auto-mangling cannot encode signature type {tc}. " +
                    "Use [DecoratedNameAttribute] on methods involving managed reference, array, generic, or byref types.");
            default:
                throw new NotSupportedException($"Unsupported signature type code 0x{(byte)tc:X2}");
        }
    }

    private void ManglePointer(StringBuilder sb, ref BlobReader sigReader)
    {
        // Read pointee modopts and type code. The pointee qualifier (A/B/C/D)
        // comes from the pointee's IsConst/IsVolatile modopts. The pointer
        // self qualifier is always P (chibil also always emits P for ECMA-
        // sourced pointers since the ECMA signature has no notion of
        // "pointer-to-pointer is itself const").
        var pteeMods = ReadModOptChain(ref sigReader);
        char pteeQual =
            (pteeMods.IsConst && pteeMods.IsVolatile) ? 'D' :
            pteeMods.IsConst ? 'B' :
            pteeMods.IsVolatile ? 'C' : 'A';

        sb.Append('P').Append(_e).Append(pteeQual);

        SignatureTypeCode tc = sigReader.ReadSignatureTypeCode();
        MangleTypeCore(sb, ref sigReader, tc, pteeMods, isReturn: false);
    }

    private void MangleFunctionPointer(StringBuilder sb, ref BlobReader sigReader)
    {
        SignatureHeader fnHeader = sigReader.ReadSignatureHeader();
        if (fnHeader.IsGeneric)
            sigReader.ReadCompressedInteger();
        int count = sigReader.ReadCompressedInteger();

        // Function pointer cc: peek return-type modopts
        var retMods = ReadModOptChain(ref sigReader);
        char ccLetter = retMods.CallConv switch
        {
            CallConvKind.Cdecl => 'A',
            CallConvKind.Stdcall => 'G',
            CallConvKind.Clrcall => 'M',
            _ => 'M',
        };
        // Function-pointer types use `P6<cc>` without the `E` (ptr64) marker
        // — matches chibil's MangleFuncPtr (chibil/CodeGen.cs:743-757) and the
        // MSVC reference (scenarios/NOTES.md `P6A<ret><params>@Z`).
        sb.Append("P6").Append(ccLetter);

        // Return type
        SignatureTypeCode retTc = sigReader.ReadSignatureTypeCode();
        MangleTypeCore(sb, ref sigReader, retTc, retMods, isReturn: false);

        if (count == 0)
        {
            sb.Append("XZ");
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                // Function-pointer params share the outer arg-backref table when
                // present (matches chibil behavior).
                if (_argBackRefs != null)
                    MangleArg(sb, ref sigReader);
                else
                {
                    var pMods = ReadModOptChain(ref sigReader);
                    SignatureTypeCode pTc = sigReader.ReadSignatureTypeCode();
                    MangleTypeCore(sb, ref sigReader, pTc, pMods, isReturn: false);
                }
            }
            sb.Append("@Z");
        }
    }

    private void MangleTagName(StringBuilder sb, string name)
    {
        if (_nameBackRefs != null)
        {
            int idx = _nameBackRefs.IndexOf(name);
            if (idx >= 0)
            {
                sb.Append((char)('0' + idx));
                sb.Append('@');
                return;
            }
            if (_nameBackRefs.Count < 10)
                _nameBackRefs.Add(name);
        }
        sb.Append(name).Append("@@");
    }

    private string GetTypeName(EntityHandle typeHandle)
    {
        switch (typeHandle.Kind)
        {
            case HandleKind.TypeReference:
                {
                    var tr = _reader.GetTypeReference((TypeReferenceHandle)typeHandle);
                    return _reader.GetString(tr.Name);
                }
            case HandleKind.TypeDefinition:
                {
                    var td = _reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                    return _reader.GetString(td.Name);
                }
            default:
                throw new NotSupportedException(
                    $"Cannot derive type tag name for handle kind {typeHandle.Kind}");
        }
    }

    // ─── ModOpt chain reader ─────────────────────────────────────────────────

    private enum CallConvKind { None, Cdecl, Stdcall, Clrcall }

    private struct ModOptInfo
    {
        public CallConvKind CallConv;
        public bool IsConst;
        public bool IsVolatile;
        public bool IsLong;
        public bool IsSignUnspecifiedByte;
    }

    /// <summary>
    /// Reads any leading RequiredModifier/OptionalModifier markers, classifies
    /// each modifier into a known kind, and returns the resulting state. The
    /// BlobReader is left positioned at the first non-modifier byte (i.e. the
    /// actual SignatureTypeCode of the underlying type).
    /// </summary>
    private ModOptInfo ReadModOptChain(ref BlobReader sigReader)
    {
        var info = default(ModOptInfo);
        while (true)
        {
            int startOffset = sigReader.Offset;
            if (sigReader.RemainingBytes == 0) return info;
            byte b = sigReader.ReadByte();
            SignatureTypeCode tc = (SignatureTypeCode)b;
            if (tc != SignatureTypeCode.OptionalModifier && tc != SignatureTypeCode.RequiredModifier)
            {
                sigReader.Offset = startOffset; // rewind
                return info;
            }
            EntityHandle modHandle = sigReader.ReadTypeHandle();
            ClassifyModifier(modHandle, ref info);
        }
    }

    private void ClassifyModifier(EntityHandle modHandle, ref ModOptInfo info)
    {
        if (modHandle.Kind != HandleKind.TypeReference)
            return;
        var tr = _reader.GetTypeReference((TypeReferenceHandle)modHandle);
        string ns = _reader.GetString(tr.Namespace);
        string name = _reader.GetString(tr.Name);
        if (ns != "System.Runtime.CompilerServices") return;
        switch (name)
        {
            case "CallConvCdecl": info.CallConv = CallConvKind.Cdecl; break;
            case "CallConvStdcall": info.CallConv = CallConvKind.Stdcall; break;
            case "CallConvClrcall": info.CallConv = CallConvKind.Clrcall; break;
            case "IsConst": info.IsConst = true; break;
            case "IsVolatile": info.IsVolatile = true; break;
            case "IsLong": info.IsLong = true; break;
            case "IsSignUnspecifiedByte": info.IsSignUnspecifiedByte = true; break;
        }
    }
}
