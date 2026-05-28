// Phase A — scan each MethodDef's parameter / return-type
// CustomAttribute rows for Asm2Obj.* signature-modifier attributes.
// Records a per-method injection plan and marks the source CA rows for
// skipping in Phase C's CustomAttribute pass.
//
// Validation rules per plan.md "Validation rules":
//   - IsLong / IsSignUnspecifiedByte leaf-kind check
//   - CallConv* return-slot restriction
//   - IsConst(N) / IsVolatile(N) level vs pointer depth
//   - reject SZArray / Array / ByRef / GenericInst / FNPTR / generic-param
//   - identical (kind, slot) duplicates → silently deduplicate

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    private void ScanSignatureAttributes()
    {
        int methodDefCount = _reader.GetTableRowCount(TableIndex.MethodDef);
        _methodInjections = new MethodSignatureInjections[methodDefCount + 1];

        for (int row = 1; row <= methodDefCount; row++)
        {
            var disp = _methodInfo[row].Disposition;
            if (disp == MethodDisposition.Drop) continue;

            var inputH = MetadataTokens.MethodDefinitionHandle(row);
            var md = _reader.GetMethodDefinition(inputH);

            // Param rows: SequenceNumber == 0 means return type, > 0 means
            // formal parameter. We index injection slots the same way:
            // index 0 = return, 1.. = params.
            var paramHandles = md.GetParameters();
            int sigParamCount = ReadSignatureParamCount(md);
            int injectionCount = sigParamCount + 1; // +1 for return type
            MethodSignatureInjections injections = null;

            foreach (var ph in paramHandles)
            {
                var p = _reader.GetParameter(ph);
                int slotIndex = p.SequenceNumber;
                if (slotIndex < 0 || slotIndex > sigParamCount) continue;

                BlobReader paramSigReader = GetSlotSignatureReader(md, slotIndex);
                var shape = SignatureShape.Analyse(paramSigReader);

                foreach (var caH in p.GetCustomAttributes())
                {
                    if (!TryGetAsm2ObjAttribute(caH, out var kind, out int level)) continue;

                    // Once we know this attr is recognized, mark its CA row
                    // for skipping in the output regardless of whether we
                    // accept or reject it.
                    _customAttrSkip[MetadataTokens.GetRowNumber(caH)] = true;

                    ValidateAndPlan(md, p, kind, level, shape, ref injections, injectionCount, sigParamCount);
                }
            }

            if (injections != null)
                _methodInjections[row] = injections;
        }
    }

    private int ReadSignatureParamCount(MethodDefinition md)
    {
        var r = _reader.GetBlobReader(md.Signature);
        var hdr = r.ReadSignatureHeader();
        if (hdr.IsGeneric) r.ReadCompressedInteger();
        return r.ReadCompressedInteger();
    }

    /// <summary>
    /// Returns a BlobReader positioned at the *type bytes* of the requested
    /// signature slot (index 0 = return type, 1.. = parameter k). The
    /// reader is positioned at the first byte AFTER any leading
    /// CustomMod* slots, on the type code itself, ready to be analysed by
    /// <see cref="SignatureShape.Analyse"/>.
    /// </summary>
    private BlobReader GetSlotSignatureReader(MethodDefinition md, int slotIndex)
    {
        var r = _reader.GetBlobReader(md.Signature);
        var hdr = r.ReadSignatureHeader();
        if (hdr.IsGeneric) r.ReadCompressedInteger();
        int paramCount = r.ReadCompressedInteger();

        // The signature shape analyser handles leading modopt/modreq itself.
        // Walk slots from 0 (return type) upward, advancing past each one.
        for (int i = 0; i < slotIndex; i++)
            SignatureShape.AnalyseAdvancing(ref r);
        return r;
    }

    private bool TryGetAsm2ObjAttribute(CustomAttributeHandle caH, out ModifierKind kind, out int level)
    {
        kind = default;
        level = 0;

        var ca = _reader.GetCustomAttribute(caH);
        if (ca.Constructor.Kind != HandleKind.MemberReference) return false;

        var ctorRef = _reader.GetMemberReference((MemberReferenceHandle)ca.Constructor);
        if (ctorRef.Parent.Kind != HandleKind.TypeReference) return false;
        var tr = _reader.GetTypeReference((TypeReferenceHandle)ctorRef.Parent);
        string ns = _reader.GetString(tr.Namespace);
        string nm = _reader.GetString(tr.Name);

        var maybeKind = ModifierKindHelpers.FromAttributeName(ns, nm);
        if (maybeKind == null) return false;
        kind = maybeKind.Value;

        // Parse the CA blob for an optional int32 ctor argument (IsConst/IsVolatile).
        // Blob layout per ECMA II.23.3: prolog (uint16 0x0001), fixed args,
        // named-arg count (uint16), named args.
        if (kind == ModifierKind.IsConst || kind == ModifierKind.IsVolatile)
        {
            var blobReader = _reader.GetBlobReader(ca.Value);
            if (blobReader.RemainingBytes >= 2)
            {
                ushort prolog = blobReader.ReadUInt16();
                if (prolog == 0x0001 && blobReader.RemainingBytes >= 4)
                {
                    level = blobReader.ReadInt32();
                }
            }
        }
        return true;
    }

    private void ValidateAndPlan(
        MethodDefinition md, Parameter p, ModifierKind kind, int level,
        SignatureShape.ParamShape shape, ref MethodSignatureInjections injections,
        int injectionCount, int sigParamCount)
    {
        string methodName = _reader.GetString(md.Name);
        string paramRoleText = p.SequenceNumber == 0
            ? "return value"
            : $"parameter '{_reader.GetString(p.Name)}'";

        // ─── Reject unsupported shape ─────────────────────────────────────
        if (shape.IsUnsupportedShape)
            throw new NotSupportedException(
                $"[Asm2Obj.{kind}] on {paramRoleText} of '{methodName}' targets an unsupported " +
                $"signature shape ({shape.LeafCode}). Use [DecoratedName] instead.");

        // ─── CallConv* must be on return slot only ───────────────────────
        if (kind.IsCallConv())
        {
            if (p.SequenceNumber != 0)
                throw new NotSupportedException(
                    $"[Asm2Obj.{kind}] is only allowed on the return value " +
                    $"(via [return: …]); found on {paramRoleText} of '{methodName}'.");
        }

        // ─── Leaf-kind validation ────────────────────────────────────────
        if (kind == ModifierKind.IsLong)
        {
            if (shape.LeafCode != SignatureTypeCode.Int32 && shape.LeafCode != SignatureTypeCode.UInt32)
                throw new NotSupportedException(
                    $"[Asm2Obj.IsLong] on {paramRoleText} of '{methodName}' requires an int32 / uint32 " +
                    $"leaf, but found {shape.LeafCode}.");
        }
        if (kind == ModifierKind.IsSignUnspecifiedByte)
        {
            if (shape.LeafCode != SignatureTypeCode.SByte)
                throw new NotSupportedException(
                    $"[Asm2Obj.IsSignUnspecifiedByte] on {paramRoleText} of '{methodName}' requires an " +
                    $"int8 (sbyte) leaf, but found {shape.LeafCode}.");
        }

        // ─── Level validation for IsConst / IsVolatile ───────────────────
        int slot;
        if (kind == ModifierKind.IsConst || kind == ModifierKind.IsVolatile)
        {
            if (level < 0 || level > shape.PointerDepth)
                throw new NotSupportedException(
                    $"[Asm2Obj.{kind}({level})] on {paramRoleText} of '{methodName}': level {level} " +
                    $"is out of range for a type with pointer depth {shape.PointerDepth}.");
            slot = level;
        }
        else if (kind.TargetsLeaf())
        {
            slot = shape.LeafSlot;
        }
        else
        {
            // CallConv* — return-type slot (only return slot reaches here).
            slot = 0;
        }

        // Record kind globally so Phase B can predict its TypeRef row.
        _requiredModifierKinds.Add(kind);

        // Lazy-allocate the per-method injection plan.
        injections ??= new MethodSignatureInjections(injectionCount);

        // Reject conflicting CallConv* at the same slot.
        if (kind.IsCallConv())
        {
            var existing = injections.PerParam[p.SequenceNumber];
            if (existing != null)
            {
                foreach (var inj in existing)
                {
                    if (inj.Slot == slot && inj.Kind.IsCallConv() && inj.Kind != kind)
                        throw new NotSupportedException(
                            $"[Asm2Obj.{kind}] conflicts with [Asm2Obj.{inj.Kind}] on return value " +
                            $"of '{methodName}'.");
                }
            }
        }

        // Silently dedupe identical (kind, slot).
        if (injections.PerParam[p.SequenceNumber] != null)
        {
            foreach (var inj in injections.PerParam[p.SequenceNumber])
            {
                if (inj.Slot == slot && inj.Kind == kind) return;
            }
        }

        injections.Add(p.SequenceNumber, new ModifierInjection(slot, kind));
    }
}
