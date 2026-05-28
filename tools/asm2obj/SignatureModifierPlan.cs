// Per-method plan for injecting modopt/modreq bytes into rewritten
// ECMA signatures, derived from Asm2Obj.* parameter / return attributes
// during Phase A and consumed during Phase C signature rewriting.

using System.Collections.Generic;

namespace Asm2Obj;

/// <summary>
/// Identifies a specific recognized signature-modifier attribute. Each
/// kind maps to (a) a target TypeRef in System.Runtime.CompilerServices,
/// (b) whether the emitted modifier is modopt or modreq, and (c) which
/// signature slot it lives in. The mapping is centralized in
/// <see cref="SignatureModifierEmitter"/> / <see cref="MetadataCopier"/>.
/// </summary>
internal enum ModifierKind
{
    IsConst,
    IsVolatile,
    IsLong,
    IsSignUnspecifiedByte,
    CallConvCdecl,
    CallConvStdcall,
}

internal static class ModifierKindHelpers
{
    /// <summary>
    /// The asm2obj attribute namespace.
    /// </summary>
    public const string AnnotationsNamespace = "Asm2Obj";

    /// <summary>
    /// Returns true iff the modifier should be emitted as modreq (otherwise modopt).
    /// IsVolatile is the only modreq today, matching MSVC/chibil convention.
    /// </summary>
    public static bool IsRequired(this ModifierKind kind) => kind == ModifierKind.IsVolatile;

    /// <summary>
    /// Returns the (namespace, name) of the target TypeRef that ends up
    /// in the output signature for this modifier kind. Always lives in
    /// <c>System.Runtime.CompilerServices</c>, regardless of where the
    /// source <c>Asm2Obj.*</c> attribute came from.
    /// </summary>
    public static (string Namespace, string Name) BclTypeRef(this ModifierKind kind) => kind switch
    {
        ModifierKind.IsConst              => ("System.Runtime.CompilerServices", "IsConst"),
        ModifierKind.IsVolatile           => ("System.Runtime.CompilerServices", "IsVolatile"),
        ModifierKind.IsLong               => ("System.Runtime.CompilerServices", "IsLong"),
        ModifierKind.IsSignUnspecifiedByte=> ("System.Runtime.CompilerServices", "IsSignUnspecifiedByte"),
        ModifierKind.CallConvCdecl        => ("System.Runtime.CompilerServices", "CallConvCdecl"),
        ModifierKind.CallConvStdcall      => ("System.Runtime.CompilerServices", "CallConvStdcall"),
        _ => default,
    };

    /// <summary>
    /// Recognizes a source attribute by namespace+name and returns its kind.
    /// Returns null if the attribute is not one we handle.
    /// </summary>
    public static ModifierKind? FromAttributeName(string ns, string name)
    {
        if (ns != AnnotationsNamespace) return null;
        return name switch
        {
            "IsConstAttribute"               => ModifierKind.IsConst,
            "IsVolatileAttribute"            => ModifierKind.IsVolatile,
            "IsLongAttribute"                => ModifierKind.IsLong,
            "IsSignUnspecifiedByteAttribute" => ModifierKind.IsSignUnspecifiedByte,
            "CallConvCdeclAttribute"         => ModifierKind.CallConvCdecl,
            "CallConvStdcallAttribute"       => ModifierKind.CallConvStdcall,
            _ => null,
        };
    }

    /// <summary>True for the call-convention markers.</summary>
    public static bool IsCallConv(this ModifierKind kind) =>
        kind == ModifierKind.CallConvCdecl ||
        kind == ModifierKind.CallConvStdcall;

    /// <summary>
    /// True for modifiers that only target the leaf integral type
    /// (IsLong / IsSignUnspecifiedByte). asm2obj computes the leaf
    /// level automatically; these attributes take no user-supplied level.
    /// </summary>
    public static bool TargetsLeaf(this ModifierKind kind) =>
        kind == ModifierKind.IsLong ||
        kind == ModifierKind.IsSignUnspecifiedByte;
}

/// <summary>
/// One modifier to inject at a specific signature-slot of a specific
/// parameter / return-type signature.
/// </summary>
internal readonly struct ModifierInjection
{
    /// <summary>
    /// Slot index per ECMA II.23.2.12 grammar: 0 = before the outermost
    /// type code, 1 = the CustomMod* slot after the first PTR (or
    /// SZARRAY), 2 = after the second, … N = before the leaf type at
    /// pointer depth N. For non-pointer leaf parameters slot 0 is the
    /// only slot.
    /// </summary>
    public int Slot { get; }
    public ModifierKind Kind { get; }

    public ModifierInjection(int slot, ModifierKind kind) { Slot = slot; Kind = kind; }
}

/// <summary>
/// Modifier injection plan for one method's parameters (index 0 = return
/// type, indices 1..N = parameters by SequenceNumber). Used by Phase C
/// signature rewriting to interleave injected modifiers at the right
/// signature slots.
/// </summary>
internal sealed class MethodSignatureInjections
{
    // Index 0 = return type (Param.SequenceNumber == 0).
    // Index k (k > 0) = parameter with SequenceNumber == k.
    public List<ModifierInjection>[] PerParam { get; }
    public MethodSignatureInjections(int paramCountIncludingReturn)
    {
        PerParam = new List<ModifierInjection>[paramCountIncludingReturn];
    }

    public void Add(int paramIndex, ModifierInjection inj)
    {
        PerParam[paramIndex] ??= new List<ModifierInjection>();
        PerParam[paramIndex].Add(inj);
    }
}

/// <summary>
/// Callback consulted by <see cref="EcmaSignatureRewriter"/> at every
/// <c>CustomMod*</c> position of a method signature it is rewriting. The
/// rewriter notifies the injector when it enters or exits each parameter
/// (return type included as parameter 0) and each parameterized type
/// (Pointer recursion etc.); the injector is responsible for maintaining
/// whatever cursor state it needs and for emitting any modopt/modreq
/// bytes at the current position.
/// </summary>
public interface ISignatureModifierInjector
{
    /// <summary>
    /// Called before the rewriter starts processing parameter
    /// <paramref name="paramIndex"/> (0 = return type, 1.. = the k-th
    /// parameter by ECMA Param.SequenceNumber).
    /// </summary>
    void BeginParameter(int paramIndex);

    /// <summary>Called after the rewriter finishes the current parameter.</summary>
    void EndParameter();

    /// <summary>
    /// Called when the rewriter is about to recurse into a parameterized
    /// type construct (Pointer's pointee, SZArray's element, etc.). The
    /// injector may use <paramref name="typeCode"/> to decide whether this
    /// step opens a new <c>CustomMod*</c> slot.
    /// </summary>
    void BeginParameterizedType(System.Reflection.Metadata.SignatureTypeCode typeCode);

    /// <summary>Symmetric counterpart to <see cref="BeginParameterizedType"/>.</summary>
    void EndParameterizedType(System.Reflection.Metadata.SignatureTypeCode typeCode);

    /// <summary>
    /// Emit any modifiers at the current cursor position into
    /// <paramref name="modsEnc"/>. Implementations should emit in their
    /// own preferred canonical order; the rewriter calls this exactly
    /// once per signature slot. Depending on the rewrite path, injected
    /// modifiers may be emitted either before or after input modifiers
    /// already present at that slot, so callers must not rely on any
    /// relative ordering between injected and input modifiers.
    /// </summary>
    void EmitInjected(System.Reflection.Metadata.Ecma335.CustomModifiersEncoder modsEnc);
}

/// <summary>
/// Adapter that exposes a <see cref="MethodSignatureInjections"/> plan as
/// an <see cref="ISignatureModifierInjector"/> for the rewriter, mapping
/// each <see cref="ModifierKind"/> to its synthesized output TypeRef row
/// and emitting injected modifiers in canonical order
/// <c>modreq(IsVolatile) -&gt; modopt(IsConst) -&gt; modopt(CallConv*)
/// -&gt; modopt(IsLong) / modopt(IsSignUnspecifiedByte)</c>.
///
/// Tracks its own <c>(paramIndex, slot)</c> cursor via the rewriter's
/// Begin/End callbacks: <see cref="BeginParameter"/> resets the slot;
/// <see cref="BeginParameterizedType"/> increments it on
/// <see cref="System.Reflection.Metadata.SignatureTypeCode.Pointer"/>.
/// </summary>
internal sealed class MethodSignatureInjector : ISignatureModifierInjector
{
    private readonly MethodSignatureInjections _plan;
    private readonly IReadOnlyDictionary<ModifierKind, int> _typeRefRows;
    private int _paramIndex;
    private int _slot;

    public MethodSignatureInjector(MethodSignatureInjections plan, IReadOnlyDictionary<ModifierKind, int> typeRefRows)
    {
        _plan = plan;
        _typeRefRows = typeRefRows;
    }

    public void BeginParameter(int paramIndex) { _paramIndex = paramIndex; _slot = 0; }
    public void EndParameter() { /* no-op; next BeginParameter resets state */ }

    public void BeginParameterizedType(System.Reflection.Metadata.SignatureTypeCode typeCode)
    {
        if (typeCode == System.Reflection.Metadata.SignatureTypeCode.Pointer)
            _slot++;
    }

    public void EndParameterizedType(System.Reflection.Metadata.SignatureTypeCode typeCode)
    {
        if (typeCode == System.Reflection.Metadata.SignatureTypeCode.Pointer)
            _slot--;
    }

    public void EmitInjected(System.Reflection.Metadata.Ecma335.CustomModifiersEncoder modsEnc)
    {
        if (_plan == null) return;
        if (_paramIndex >= _plan.PerParam.Length) return;
        var list = _plan.PerParam[_paramIndex];
        if (list == null) return;

        EmitOfKind(list, ModifierKind.IsVolatile, modsEnc);
        EmitOfKind(list, ModifierKind.IsConst, modsEnc);
        EmitOfKind(list, ModifierKind.CallConvCdecl, modsEnc);
        EmitOfKind(list, ModifierKind.CallConvStdcall, modsEnc);
        EmitOfKind(list, ModifierKind.IsLong, modsEnc);
        EmitOfKind(list, ModifierKind.IsSignUnspecifiedByte, modsEnc);
    }

    private void EmitOfKind(List<ModifierInjection> list, ModifierKind kind,
        System.Reflection.Metadata.Ecma335.CustomModifiersEncoder modsEnc)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Slot != _slot || list[i].Kind != kind) continue;
            int row = _typeRefRows[kind];
            modsEnc.AddModifier(
                System.Reflection.Metadata.Ecma335.MetadataTokens.TypeReferenceHandle(row),
                isOptional: !kind.IsRequired());
        }
    }
}
