// Phase A — Classification. Decides per-type and per-method disposition based
// on attribute marker analysis, drops the input <Module>, flattens
// CompilerGlobalScope types, and identifies ForwardRef extern methods.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    // Recognized control attribute full names.
    private const string CompilerGlobalScopeAttrFullName = "System.Runtime.CompilerServices.CompilerGlobalScopeAttribute";
    private const string DecoratedNameAttrFullName = "System.Runtime.CompilerServices.DecoratedNameAttribute";

    private static readonly string[] AcceptedCoreLibs = { "mscorlib" };

    private void ClassifyTypesAndMethods()
    {
        // Validate core-lib references (loudly reject System.Runtime-based assemblies).
        ValidateCoreLibReferences();

        int typeDefCount = _reader.GetTableRowCount(TableIndex.TypeDef);
        int methodDefCount = _reader.GetTableRowCount(TableIndex.MethodDef);
        int caCount = _reader.GetTableRowCount(TableIndex.CustomAttribute);

        _typeInfo = new TypeInfo[typeDefCount + 1];
        _methodInfo = new MethodInfo[methodDefCount + 1];
        _customAttrSkip = new bool[caCount + 1];

        // ─── Classify types ──────────────────────────────────────────────────
        // <Module> in the input always sits at TypeDef row 1.
        for (int row = 1; row <= typeDefCount; row++)
        {
            var handle = MetadataTokens.TypeDefinitionHandle(row);
            var td = _reader.GetTypeDefinition(handle);
            string name = _reader.GetString(td.Name);

            if (row == 1)
            {
                // Drop input <Module>; a fresh output <Module> is emitted as row 1.
                _typeInfo[row].Disposition = TypeDisposition.Drop;
                continue;
            }

            bool hasGlobalScope = HasCustomAttribute(td.GetCustomAttributes(), CompilerGlobalScopeAttrFullName);
            if (hasGlobalScope)
            {
                ValidateFlattenable(handle, td);
                _typeInfo[row].Disposition = TypeDisposition.Flatten;
                TokenMap.SetTypeDefUnmappedReason(
                    handle,
                    $"asm2obj cannot convert metadata references to [CompilerGlobalScope] type '{GetTypeDefFullName(handle)}' " +
                    $"because the type is flattened into <Module>. Avoid typeof({name}), signatures, or other metadata tokens that name the flattened type.");
            }
            else
            {
                _typeInfo[row].Disposition = TypeDisposition.Copy;
            }
        }

        // ─── Classify methods ────────────────────────────────────────────────
        for (int row = 1; row <= methodDefCount; row++)
        {
            var mh = MetadataTokens.MethodDefinitionHandle(row);
            var md = _reader.GetMethodDefinition(mh);
            int ownerInputRow = MetadataTokens.GetRowNumber(md.GetDeclaringType());

            // Owner type disposition determines whether the method survives at all.
            var ownerDisp = _typeInfo[ownerInputRow].Disposition;
            if (ownerDisp == TypeDisposition.Drop)
            {
                _methodInfo[row].Disposition = MethodDisposition.Drop;
                continue;
            }

            // ForwardRef detection: ImplFlags & ForwardRef AND RVA == 0 AND no body.
            bool isForwardRef = (md.ImplAttributes & MethodImplAttributes.ForwardRef) != 0;
            bool hasNoBody = md.RelativeVirtualAddress == 0;
            if (isForwardRef && !hasNoBody)
                throw new NotSupportedException(
                    $"Method '{_reader.GetString(md.Name)}' is marked ForwardRef but has an IL body. " +
                    "v1 rejects ambiguous ForwardRef methods.");
            if (isForwardRef && hasNoBody)
            {
                _methodInfo[row].Disposition = MethodDisposition.ForwardRefMemberRef;
            }
            else if (hasNoBody)
            {
                // Allow abstract / PInvokeImpl / InternalCall / Runtime methods to flow
                // through as MethodDef rows with no body. They are uncommon in the C
                // runtime use case but technically valid.
                _methodInfo[row].Disposition = MethodDisposition.Regular;
            }
            else
            {
                _methodInfo[row].Disposition = MethodDisposition.Regular;
            }

            // [DecoratedNameAttribute("...")] — extract the string and mark the
            // CA row for skipping in the CustomAttribute population phase.
            _methodInfo[row].DecoratedName = ExtractDecoratedName(md.GetCustomAttributes());

            // Method-body EH region rejection (only relevant for regular methods with body)
            if (_methodInfo[row].Disposition == MethodDisposition.Regular && !hasNoBody)
                RejectIfHasExceptionRegions(md, row);
        }

        // ─── Validate: no duplicate .cctor across flattened classes ──────────
        ValidateNoCctorCollisions();
        ValidateMethodDefMemberRefParents();

        // HasFieldRVA fields are accepted; their data is emitted in Phase D.

        // ─── Tables we don't copy (silently drop) ────────────────────────────
        // These tables exist on real Roslyn output but don't affect linker
        // behavior in the chibil/MSVC `/clr` scenario. We drop them without
        // copying, which is a safe v1 simplification.
        // - DeclSecurity: CAS security attributes (legacy, ignored at link time)
        // - ManifestResource: embedded resources (no place for them in a COFF .obj)
        // - File: multi-file assembly entries (we're producing a single .obj)
        // - ExportedType: type forwarders (only meaningful in the Assembly table)
        // - ImplMap: P/Invoke metadata (not needed for managed-only methods)
        // - FieldMarshal: P/Invoke marshalling info
    }

    private void ValidateMethodDefMemberRefParents()
    {
        for (int row = 1; row <= _reader.GetTableRowCount(TableIndex.MemberRef); row++)
        {
            var memberRef = _reader.GetMemberReference(MetadataTokens.MemberReferenceHandle(row));
            if (memberRef.Parent.Kind != HandleKind.MethodDefinition)
                continue;

            var parent = (MethodDefinitionHandle)memberRef.Parent;
            int parentRow = MetadataTokens.GetRowNumber(parent);
            if (_methodInfo[parentRow].Disposition == MethodDisposition.Regular)
                continue;

            string methodName = _reader.GetString(_reader.GetMethodDefinition(parent).Name);
            throw new NotSupportedException(
                $"MemberRef row {row} is parented by method '{methodName}', which is not emitted as a MethodDef. " +
                "Vararg call sites parented by ForwardRef or dropped methods are not supported.");
        }
    }

    /// <summary>
    /// Set <c>UnmanagedExport</c> for each defined method whose return-type
    /// slot carries a <c>modopt(CallConvCdecl)</c> or
    /// <c>modopt(CallConvStdcall)</c> — either already in the input
    /// signature blob or scheduled to be injected by
    /// <see cref="MethodSignatureInjections"/>. The presence of an
    /// explicit native calling-convention modopt is precisely what tells
    /// native callers (chibil-compiled C, MSVC <c>/clr</c>, ...) "this
    /// method is reachable via the bare-name COFF symbol with that
    /// calling convention", which is what triggers NEP-thunk emission
    /// in <see cref="EmitNepThunks"/>. <c>__clrcall</c> methods (no
    /// callconv modopt) are managed-only and reachable through the
    /// metadata token alone, so they need no NEP machinery.
    ///
    /// Must run after <see cref="ScanSignatureAttributes"/> so the
    /// injection plan is populated.
    /// </summary>
    private void ComputeUnmanagedExportFlags()
    {
        int methodDefCount = _reader.GetTableRowCount(TableIndex.MethodDef);
        for (int row = 1; row <= methodDefCount; row++)
        {
            if (_methodInfo[row].Disposition != MethodDisposition.Regular) continue;

            var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
            _methodInfo[row].UnmanagedExport =
                ReturnTypeHasNativeCallConvModopt(md) ||
                InjectionPlanHasNativeCallConvOnReturn(row);
        }
    }

    private bool ReturnTypeHasNativeCallConvModopt(MethodDefinition md)
    {
        var r = _reader.GetBlobReader(md.Signature);
        var hdr = r.ReadSignatureHeader();
        if (hdr.IsGeneric) r.ReadCompressedInteger(); // generic arity
        r.ReadCompressedInteger();                     // param count

        // Walk the return-type's leading modopt chain. Stop at the first
        // non-modifier byte (the return type itself). We only care about
        // the OUTER modopt slot — modopts inside a Pointer's pointee or
        // FNPTR sub-signature don't determine the method's own ABI.
        while (r.RemainingBytes > 0)
        {
            int save = r.Offset;
            var tc = (SignatureTypeCode)r.ReadByte();
            if (tc != SignatureTypeCode.OptionalModifier && tc != SignatureTypeCode.RequiredModifier)
            {
                r.Offset = save;
                return false;
            }
            EntityHandle modH = r.ReadTypeHandle();
            if (IsNativeCallConvTypeRef(modH)) return true;
        }
        return false;
    }

    private bool InjectionPlanHasNativeCallConvOnReturn(int methodRow)
    {
        var plan = _methodInjections[methodRow];
        if (plan == null) return false;
        if (plan.PerParam.Length == 0) return false;
        var list = plan.PerParam[0]; // index 0 = return type
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Slot == 0 && list[i].Kind.IsCallConv()) return true;
        return false;
    }

    private bool IsNativeCallConvTypeRef(EntityHandle modH)
    {
        if (modH.Kind != HandleKind.TypeReference) return false;
        var tr = _reader.GetTypeReference((TypeReferenceHandle)modH);
        if (_reader.GetString(tr.Namespace) != "System.Runtime.CompilerServices") return false;
        string name = _reader.GetString(tr.Name);
        return name == "CallConvCdecl" || name == "CallConvStdcall";
    }

    private void ValidateFlattenable(TypeDefinitionHandle handle, TypeDefinition td)
    {
        string name = _reader.GetString(td.Name);

        // No nested types.
        if (td.GetNestedTypes().Length > 0)
            throw new NotSupportedException(
                $"[CompilerGlobalScope] type '{name}' has nested types — not supported in v1.");

        // No generics.
        if (td.GetGenericParameters().Count > 0)
            throw new NotSupportedException(
                $"[CompilerGlobalScope] type '{name}' is generic — not supported.");

        // No interfaces.
        if (td.GetInterfaceImplementations().Count > 0)
            throw new NotSupportedException(
                $"[CompilerGlobalScope] type '{name}' implements interfaces — <Module> cannot.");

        // Base type must be nil or System.Object — anything else would
        // produce invalid metadata when the members are flattened into
        // <Module>.
        if (!td.BaseType.IsNil)
        {
            string baseFullName = td.BaseType.Kind switch
            {
                HandleKind.TypeReference =>
                    GetTypeRefFullName((TypeReferenceHandle)td.BaseType),
                HandleKind.TypeDefinition =>
                    GetTypeDefFullName((TypeDefinitionHandle)td.BaseType),
                _ => null,
            };
            if (baseFullName != "System.Object")
                throw new NotSupportedException(
                    $"[CompilerGlobalScope] type '{name}' has base type '{baseFullName ?? "<unknown>"}' — must be System.Object.");
        }

        // No instance members.
        foreach (var mh in td.GetMethods())
        {
            var m = _reader.GetMethodDefinition(mh);
            if ((m.Attributes & MethodAttributes.Static) == 0)
                throw new NotSupportedException(
                    $"[CompilerGlobalScope] type '{name}' has instance method '{_reader.GetString(m.Name)}'.");
        }
        foreach (var fh in td.GetFields())
        {
            var f = _reader.GetFieldDefinition(fh);
            if ((f.Attributes & FieldAttributes.Static) == 0)
                throw new NotSupportedException(
                    $"[CompilerGlobalScope] type '{name}' has instance field '{_reader.GetString(f.Name)}'.");
        }
    }

    private void ValidateNoCctorCollisions()
    {
        bool seen = false;
        for (int row = 1; row < _typeInfo.Length; row++)
        {
            if (_typeInfo[row].Disposition != TypeDisposition.Flatten) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row));
            foreach (var mh in td.GetMethods())
            {
                var m = _reader.GetMethodDefinition(mh);
                string mname = _reader.GetString(m.Name);
                if (mname == ".cctor")
                {
                    if (seen)
                        throw new NotSupportedException(
                            "Multiple .cctor methods on [CompilerGlobalScope] classes cannot be flattened into a single <Module>.");
                    seen = true;
                }
            }
        }
    }

    private void RejectIfHasExceptionRegions(MethodDefinition md, int methodRow)
    {
        // We can't open the body without the PEReader; defer to Phase D where
        // the PEReader is available. Mark for later check by reading RVA — but
        // for clarity, do the actual EH check in Phase D since it needs the body.
        // (Left intentionally as a no-op here.)
    }

    private void ValidateCoreLibReferences()
    {
        for (int row = 1; row <= _reader.GetTableRowCount(TableIndex.AssemblyRef); row++)
        {
            var ar = _reader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(row));
            string name = _reader.GetString(ar.Name);
            // The first AssemblyRef is treated as the core-lib by convention.
            // We loudly reject System.Runtime / System.Private.CoreLib.
            if (name == "System.Runtime" || name == "System.Private.CoreLib" || name == "netstandard")
            {
                throw new NotSupportedException(
                    $"Input references '{name}'. v1 requires inputs compiled against 'mscorlib'. " +
                    "Build the C# CRT code against a Framework mscorlib.dll reference assembly.");
            }
        }
    }

    private bool HasCustomAttribute(CustomAttributeHandleCollection caCollection, string fullName)
    {
        foreach (var caHandle in caCollection)
        {
            if (GetCustomAttributeTypeFullName(caHandle) == fullName)
                return true;
        }
        return false;
    }

    private string ExtractDecoratedName(CustomAttributeHandleCollection caCollection)
    {
        foreach (var caHandle in caCollection)
        {
            if (GetCustomAttributeTypeFullName(caHandle) != DecoratedNameAttrFullName)
                continue;

            // Skip this CA row when copying (its value is consumed for the symbol name).
            _customAttrSkip[MetadataTokens.GetRowNumber(caHandle)] = true;

            var ca = _reader.GetCustomAttribute(caHandle);
            var blobReader = _reader.GetBlobReader(ca.Value);
            ushort prolog = blobReader.ReadUInt16();
            if (prolog != 0x0001) continue;
            return blobReader.ReadSerializedString();
        }
        return null;
    }

    /// <summary>
    /// Returns the "Namespace.Name" of the type that owns the CA constructor.
    /// Handles both MemberRef-based and MethodDef-based constructor references.
    /// </summary>
    private string GetCustomAttributeTypeFullName(CustomAttributeHandle caHandle)
    {
        var ca = _reader.GetCustomAttribute(caHandle);
        EntityHandle ctor = ca.Constructor;
        return ctor.Kind switch
        {
            HandleKind.MemberReference => GetMemberRefParentFullName(_reader.GetMemberReference((MemberReferenceHandle)ctor).Parent),
            HandleKind.MethodDefinition => GetTypeDefFullName(_reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()),
            _ => null,
        };
    }

    private string GetMemberRefParentFullName(EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                {
                    var tr = _reader.GetTypeReference((TypeReferenceHandle)parent);
                    string ns = _reader.GetString(tr.Namespace);
                    string nm = _reader.GetString(tr.Name);
                    return ns.Length == 0 ? nm : ns + "." + nm;
                }
            case HandleKind.TypeDefinition:
                return GetTypeDefFullName((TypeDefinitionHandle)parent);
            default:
                return null;
        }
    }

    private string GetTypeRefFullName(TypeReferenceHandle h)
    {
        var tr = _reader.GetTypeReference(h);
        string ns = _reader.GetString(tr.Namespace);
        string nm = _reader.GetString(tr.Name);
        return ns.Length == 0 ? nm : ns + "." + nm;
    }

    private string GetTypeDefFullName(TypeDefinitionHandle h)
    {
        var td = _reader.GetTypeDefinition(h);
        string ns = _reader.GetString(td.Namespace);
        string nm = _reader.GetString(td.Name);
        return ns.Length == 0 ? nm : ns + "." + nm;
    }
}
