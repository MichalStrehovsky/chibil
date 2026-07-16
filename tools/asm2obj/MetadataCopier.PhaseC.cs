// Phase C — Table population. Emits each output table in the order predicted
// by Phase B, asserting that the handle returned by MetadataBuilder.AddX
// matches the prediction recorded in TokenMap.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    // ─── AssemblyRef, ModuleRef ─────────────────────────────────────────────
    private void EmitAssemblyAndModuleRefs()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.AssemblyRef); r++)
        {
            var ar = _reader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(r));
            var outH = _outputMd.AddAssemblyReference(
                _outputMd.GetOrAddString(_reader.GetString(ar.Name)),
                ar.Version,
                _outputMd.GetOrAddString(_reader.GetString(ar.Culture)),
                ar.PublicKeyOrToken.IsNil ? default : _outputMd.GetOrAddBlob(_reader.GetBlobBytes(ar.PublicKeyOrToken)),
                ar.Flags,
                ar.HashValue.IsNil ? default : _outputMd.GetOrAddBlob(_reader.GetBlobBytes(ar.HashValue)));
            TokenMap.AssertHandle(r, outH);
        }
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.ModuleRef); r++)
        {
            var mr = _reader.GetModuleReference(MetadataTokens.ModuleReferenceHandle(r));
            var outH = _outputMd.AddModuleReference(_outputMd.GetOrAddString(_reader.GetString(mr.Name)));
            TokenMap.AssertHandle(r, outH);
        }
    }

    // ─── TypeRef (copied and synthesized) ──────────────────────────────────
    private void EmitTypeRefs()
    {
        // Has to be preceded by Assembly/ModuleRef so ResolutionScope mapping works.
        EmitAssemblyAndModuleRefs();

        int copyCount = _reader.GetTableRowCount(TableIndex.TypeRef);
        for (int r = 1; r <= copyCount; r++)
        {
            var tr = _reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(r));
            EntityHandle outScope = tr.ResolutionScope.IsNil
                ? default
                : TokenMap.MapReference(tr.ResolutionScope);
            var outH = _outputMd.AddTypeReference(
                outScope,
                _outputMd.GetOrAddString(_reader.GetString(tr.Namespace)),
                _outputMd.GetOrAddString(_reader.GetString(tr.Name)));
            TokenMap.AssertHandle(r, outH);
        }

        // Synthesized TypeRefs for required signature modifiers, in the
        // order recorded by Phase B's PredictSignatureModifierTypeRefs.
        if (_synthesizedModifierTypeRefs.Count > 0)
        {
            var corlibRef = _coreLibAssemblyRef;
            foreach (var kind in _synthesizedModifierTypeRefs)
            {
                var (ns, name) = kind.BclTypeRef();
                var outH = _outputMd.AddTypeReference(
                    corlibRef,
                    _outputMd.GetOrAddString(ns),
                    _outputMd.GetOrAddString(name));
                int expectedRow = _modifierTypeRefOutputRow[kind];
                TokenMap.AssertHandle(expectedRow, outH);
            }
        }

        foreach (int inputRow in _localTypeRefSourceRows)
        {
            var inputH = MetadataTokens.TypeDefinitionHandle(inputRow);
            var td = _reader.GetTypeDefinition(inputH);
            var enclosing = td.GetDeclaringType();
            EntityHandle scope = enclosing.IsNil
                ? default
                : TokenMap.MapTypeDefReference(enclosing);
            var outH = _outputMd.AddTypeReference(
                scope,
                _outputMd.GetOrAddString(_reader.GetString(td.Namespace)),
                _outputMd.GetOrAddString(_reader.GetString(td.Name)));
            TokenMap.AssertHandle(
                MetadataTokens.GetRowNumber(TokenMap.MapTypeDefReference(inputH)),
                outH);
        }
    }

    // ─── TypeSpec ───────────────────────────────────────────────────────────
    private void EmitTypeSpecs()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.TypeSpec); r++)
        {
            var ts = _reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(r));
            var sigReader = _reader.GetBlobReader(ts.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteTypeSpecSignature(sigReader, TokenMap, sigBuilder);
            var outH = _outputMd.AddTypeSpecification(_outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(r, outH);
        }
    }

    // ─── StandaloneSig ──────────────────────────────────────────────────────
    private void EmitStandaloneSigs()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.StandAloneSig); r++)
        {
            var ss = _reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(r));
            var sigReader = _reader.GetBlobReader(ss.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteStandaloneSignatureBlob(sigReader, TokenMap, sigBuilder);
            var outH = _outputMd.AddStandaloneSignature(_outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(r, outH);
        }
    }

    // ─── TypeDef ────────────────────────────────────────────────────────────
    private void EmitTypeDefs()
    {
        // The TypeDef field/method first-row sentinels must point at the first
        // row of the next TypeDef's run (or one past the end of the table for
        // the last row) for empty runs to be encoded correctly. We use the
        // values predicted in Phase B and rely on MetadataBuilder to write the
        // right "end" for the final row automatically (per ECMA-335 convention).

        // Output row 1 = <Module>: TypeAttributes.Class with no base, no namespace.
        var moduleH = _outputMd.AddTypeDefinition(
            TypeAttributes.Class,
            default,
            _outputMd.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_outTypeDefFieldFirst[1]),
            MetadataTokens.MethodDefinitionHandle(_outTypeDefMethodFirst[1]));
        TokenMap.AssertHandle(1, moduleH);

        for (int outRow = 2; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            var inputH = MetadataTokens.TypeDefinitionHandle(inputRow);
            var td = _reader.GetTypeDefinition(inputH);

            var outH = _outputMd.AddTypeDefinition(
                td.Attributes,
                _outputMd.GetOrAddString(_reader.GetString(td.Namespace)),
                _outputMd.GetOrAddString(_reader.GetString(td.Name)),
                td.BaseType.IsNil ? default : TokenMap.MapReference(td.BaseType),
                MetadataTokens.FieldDefinitionHandle(_outTypeDefFieldFirst[outRow]),
                MetadataTokens.MethodDefinitionHandle(_outTypeDefMethodFirst[outRow]));
            TokenMap.AssertHandle(outRow, outH);
        }
    }

    // ─── Field ──────────────────────────────────────────────────────────────
    private void EmitFields()
    {
        for (int outRow = 1; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            foreach (int inputFieldRow in _orderedMembers[outRow].Fields)
            {
                var inputH = MetadataTokens.FieldDefinitionHandle(inputFieldRow);
                var fd = _reader.GetFieldDefinition(inputH);
                var sigReader = _reader.GetBlobReader(fd.Signature);
                var sigBuilder = new BlobBuilder();
                EcmaSignatureRewriter.RewriteFieldSignature(sigReader, TokenMap, sigBuilder);

                var outH = _outputMd.AddFieldDefinition(
                    fd.Attributes,
                    _outputMd.GetOrAddString(_reader.GetString(fd.Name)),
                    _outputMd.GetOrAddBlob(sigBuilder));
                TokenMap.AssertHandle(MetadataTokens.GetRowNumber(TokenMap.MapField(inputH)), outH);
            }
        }
    }

    // ─── MethodDef + Param ──────────────────────────────────────────────────
    private void EmitMethodDefsAndParams()
    {
        // Running counter of Param rows emitted so far. For empty-param methods
        // we set ParamList to (current count + 1), i.e. the row the NEXT
        // method's params will start at — matching the ECMA convention where
        // a method's param range ends at the next method's ParamList.
        int paramRowSoFar = 0;

        for (int outRow = 1; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            foreach (int inputMethodRow in _orderedMembers[outRow].Methods)
            {
                var inputH = MetadataTokens.MethodDefinitionHandle(inputMethodRow);
                var md = _reader.GetMethodDefinition(inputH);
                var sigReader = _reader.GetBlobReader(md.Signature);
                var sigBuilder = new BlobBuilder();
                var injections = _methodInjections[inputMethodRow];
                var injector = injections != null
                    ? new MethodSignatureInjector(injections, _modifierTypeRefOutputRow)
                    : null;
                EcmaSignatureRewriter.RewriteMethodSignature(sigReader, TokenMap, sigBuilder, injector);

                int firstParamRow = paramRowSoFar + 1;

                // Body RVA stays at 0 for now; AddMethodBody in Phase D records
                // the actual offset via the COFF symbol's value patch.
                var outH = _outputMd.AddMethodDefinition(
                    md.Attributes,
                    md.ImplAttributes,
                    _outputMd.GetOrAddString(_reader.GetString(md.Name)),
                    _outputMd.GetOrAddBlob(sigBuilder),
                    bodyOffset: -1,
                    MetadataTokens.ParameterHandle(firstParamRow));
                TokenMap.AssertHandle(MetadataTokens.GetRowNumber(TokenMap.MapMethodDef(inputH)), outH);

                foreach (var ph in md.GetParameters())
                {
                    var p = _reader.GetParameter(ph);
                    var outParamH = _outputMd.AddParameter(
                        p.Attributes,
                        _outputMd.GetOrAddString(_reader.GetString(p.Name)),
                        p.SequenceNumber);
                    TokenMap.AssertHandle(MetadataTokens.GetRowNumber(TokenMap.MapEntity(ph)), outParamH);
                    paramRowSoFar++;
                }
            }
        }
    }

    // ─── MemberRef (input copies + synthesized local references) ───────────
    private void EmitMemberRefs()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            var mr = _reader.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
            var sigReader = _reader.GetBlobReader(mr.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteMemberReferenceSignature(sigReader, TokenMap, sigBuilder);

            EntityHandle parent = mr.Parent.Kind == HandleKind.MethodDefinition
                ? TokenMap.MapMethodDef((MethodDefinitionHandle)mr.Parent)
                : TokenMap.MapReference(mr.Parent);
            var outH = _outputMd.AddMemberReference(
                parent,
                _outputMd.GetOrAddString(_reader.GetString(mr.Name)),
                _outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(r, outH);
        }

        foreach (int fieldRow in _localFieldRefSourceRows)
        {
            var fieldH = MetadataTokens.FieldDefinitionHandle(fieldRow);
            var fd = _reader.GetFieldDefinition(fieldH);
            var sigReader = _reader.GetBlobReader(fd.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteFieldSignature(sigReader, TokenMap, sigBuilder);

            var outH = _outputMd.AddMemberReference(
                GetLocalMemberReferenceParent(fd.GetDeclaringType()),
                _outputMd.GetOrAddString(_reader.GetString(fd.Name)),
                _outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(
                MetadataTokens.GetRowNumber(TokenMap.MapFieldReference(fieldH)),
                outH);
        }

        foreach (int methodRow in _localMethodRefSourceRows)
        {
            var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodRow));
            string name = _reader.GetString(md.Name);
            var sigReader = _reader.GetBlobReader(md.Signature);
            var sigBuilder = new BlobBuilder();
            var injections = _methodInjections[methodRow];
            var injector = injections != null
                ? new MethodSignatureInjector(injections, _modifierTypeRefOutputRow)
                : null;
            EcmaSignatureRewriter.RewriteMethodSignature(sigReader, TokenMap, sigBuilder, injector);

            var outH = _outputMd.AddMemberReference(
                GetLocalMemberReferenceParent(md.GetDeclaringType()),
                _outputMd.GetOrAddString(name),
                _outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(
                MetadataTokens.GetRowNumber(TokenMap.MapMethodDefReference(
                    MetadataTokens.MethodDefinitionHandle(methodRow))),
                outH);

            _synthesizedDecoratedNameCAs.Add(
                (outH, _methodReferenceDecoratedNames[methodRow]));
        }
    }

    private EntityHandle GetLocalMemberReferenceParent(TypeDefinitionHandle inputOwner)
    {
        int ownerRow = MetadataTokens.GetRowNumber(inputOwner);
        return _typeInfo[ownerRow].Disposition == TypeDisposition.Flatten
            ? MetadataTokens.TypeDefinitionHandle(OutputModuleTypeDefRow)
            : TokenMap.MapTypeDefReference(inputOwner);
    }

    // List of (MemberRef parent, decorated-name value) for synthesized method
    // references, queued for the sorted CustomAttribute pass.
    private readonly List<(EntityHandle parent, string value)> _synthesizedDecoratedNameCAs = new();

    private AssemblyReferenceHandle _coreLibAssemblyRefField;
    private AssemblyReferenceHandle _coreLibAssemblyRef
    {
        get
        {
            if (_coreLibAssemblyRefField.IsNil)
            {
                // Locate the AssemblyRef whose name is "mscorlib". Phase A's
                // ValidateCoreLibReferences guarantees one such row exists
                // (and rejects System.Runtime / System.Private.CoreLib /
                // netstandard inputs upfront).
                for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.AssemblyRef); r++)
                {
                    var ar = _reader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(r));
                    if (_reader.GetString(ar.Name) == "mscorlib")
                    {
                        _coreLibAssemblyRefField = (AssemblyReferenceHandle)TokenMap.MapEntity(
                            MetadataTokens.AssemblyReferenceHandle(r));
                        break;
                    }
                }
                if (_coreLibAssemblyRefField.IsNil)
                    throw new NotSupportedException(
                        "Input has no AssemblyRef row named 'mscorlib'. " +
                        "v1 requires inputs compiled against mscorlib.");
            }
            return _coreLibAssemblyRefField;
        }
    }
    private TypeReferenceHandle _decoratedNameAttrTypeRef;
    private MemberReferenceHandle _decoratedNameAttrCtor;

    // ─── MethodSpec ─────────────────────────────────────────────────────────
    private void EmitMethodSpecs()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MethodSpec); r++)
        {
            var ms = _reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(r));
            var sigReader = _reader.GetBlobReader(ms.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteMethodSpecSignature(sigReader, TokenMap, sigBuilder);

            EntityHandle method = TokenMap.MapReference(ms.Method);
            var outH = _outputMd.AddMethodSpecification(method, _outputMd.GetOrAddBlob(sigBuilder));
            TokenMap.AssertHandle(r, outH);
        }
    }

    // ─── Constant ───────────────────────────────────────────────────────────
    private void EmitConstants()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.Constant); r++)
        {
            var c = _reader.GetConstant(MetadataTokens.ConstantHandle(r));
            EntityHandle parent = MapConstantParent(c.Parent);
            if (parent.IsNil) continue;
            _outputMd.AddConstant(parent, GetConstantValue(c));
        }
    }

    private EntityHandle MapConstantParent(EntityHandle parent)
    {
        return parent.Kind switch
        {
            HandleKind.FieldDefinition => TokenMap.MapField((FieldDefinitionHandle)parent),
            HandleKind.Parameter => TokenMap.MapEntity(parent),
            HandleKind.PropertyDefinition => TokenMap.MapEntity(parent),
            _ => throw new BadImageFormatException(
                $"Unexpected Constant parent kind {parent.Kind}.")
        };
    }

    private static object GetConstantValue(Constant c)
    {
        // Re-read the raw blob bytes — easier than typed dispatch since
        // MetadataBuilder.AddConstant accepts an object box of the value.
        // For null constants the value is null.
        if (c.TypeCode == ConstantTypeCode.NullReference) return null;
        return c.Value;
    }

    // ─── FieldLayout, ClassLayout, FieldRVA ─────────────────────────────────
    private void EmitFieldLayouts()
    {
        // The FieldLayout table is keyed by FieldDef; iterate FieldDefs and
        // read their layout via the API.
        for (int inputRow = 1; inputRow <= _reader.GetTableRowCount(TableIndex.Field); inputRow++)
        {
            var inputH = MetadataTokens.FieldDefinitionHandle(inputRow);
            var fd = _reader.GetFieldDefinition(inputH);
            int offset = fd.GetOffset();
            if (offset < 0) continue;
            var outH = TokenMap.MapField(inputH);
            if (outH.IsNil || MetadataTokens.GetRowNumber(outH) == 0) continue;
            _outputMd.AddFieldLayout(outH, offset);
        }
    }

    private void EmitClassLayouts()
    {
        // ClassLayout is per-TypeDef; iterate TypeDefs.
        for (int outRow = 2; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            if (inputRow == 0) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            var layout = td.GetLayout();
            if (layout.IsDefault) continue;
            _outputMd.AddTypeLayout(
                MetadataTokens.TypeDefinitionHandle(outRow),
                (ushort)layout.PackingSize,
                (uint)layout.Size);
        }
    }

    private void EmitFieldRvas()
    {
        // Walk fields in input row order and emit a FieldRVA row for each
        // HasFieldRVA field that survives. The RVA value is a placeholder (0)
        // — the linker resolves it via the field's CLR-token data symbol
        // registered in Phase D's EmitFieldData.
        for (int inputRow = 1; inputRow <= _reader.GetTableRowCount(TableIndex.Field); inputRow++)
        {
            var inputH = MetadataTokens.FieldDefinitionHandle(inputRow);
            var fd = _reader.GetFieldDefinition(inputH);
            if ((fd.Attributes & FieldAttributes.HasFieldRVA) == 0) continue;
            var outH = TokenMap.MapField(inputH);
            if (outH.IsNil || MetadataTokens.GetRowNumber(outH) == 0) continue;
            _outputMd.AddFieldRelativeVirtualAddress(outH, offset: 0);
        }
    }

    // ─── InterfaceImpl ──────────────────────────────────────────────────────
    private void EmitInterfaceImpls()
    {
        // Sort-required by Class (the implementing TypeDef). We bucket per
        // output TypeDef row and emit in order.
        for (int outRow = 2; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            if (inputRow == 0) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            foreach (var iiH in td.GetInterfaceImplementations())
            {
                var ii = _reader.GetInterfaceImplementation(iiH);
                _outputMd.AddInterfaceImplementation(
                    MetadataTokens.TypeDefinitionHandle(outRow),
                    TokenMap.MapReference(ii.Interface));
            }
        }
    }

    // ─── MethodImpl ─────────────────────────────────────────────────────────
    private void EmitMethodImpls()
    {
        // Iterate per output TypeDef in ascending row order — natural sort order.
        for (int outRow = 1; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            if (inputRow == 0) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            foreach (var miH in td.GetMethodImplementations())
            {
                var mi = _reader.GetMethodImplementation(miH);
                EntityHandle body = mi.MethodBody.Kind == HandleKind.MethodDefinition
                    ? TokenMap.MapMethodDef((MethodDefinitionHandle)mi.MethodBody)
                    : TokenMap.MapEntity(mi.MethodBody);
                _outputMd.AddMethodImplementation(
                    MetadataTokens.TypeDefinitionHandle(outRow),
                    body,
                    TokenMap.MapEntity(mi.MethodDeclaration));
            }
        }
    }

    // ─── NestedClass ────────────────────────────────────────────────────────
    private void EmitNestedClasses()
    {
        // Iterate output TypeDefs in ascending row order. The NestedClass
        // table is sort-required by NestedClass; output-row order satisfies
        // that since we kept TypeDefs in input order (after <Module>).
        for (int outRow = 2; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            if (inputRow == 0) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            var enclosing = td.GetDeclaringType();
            if (enclosing.IsNil) continue;
            var enclosingOut = TokenMap.MapTypeDef(enclosing);
            if (enclosingOut.IsNil) continue;
            _outputMd.AddNestedType(
                MetadataTokens.TypeDefinitionHandle(outRow),
                enclosingOut);
        }
    }

    // ─── GenericParam + GenericParamConstraint ──────────────────────────────
    private void EmitGenericParamsAndConstraints()
    {
        // GenericParam: sort-required by (Owner, Number). We emit in input order
        // which preserves the relative ordering, and the *output owner* coded
        // index is monotonic across surviving types/methods.
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.GenericParam); r++)
        {
            var inputH = MetadataTokens.GenericParameterHandle(r);
            var gp = _reader.GetGenericParameter(inputH);
            if (!OwnerSurvives(gp.Parent)) continue;
            EntityHandle owner = gp.Parent.Kind == HandleKind.TypeDefinition
                ? TokenMap.MapTypeDef((TypeDefinitionHandle)gp.Parent)
                : TokenMap.MapMethodDef((MethodDefinitionHandle)gp.Parent);
            var outH = _outputMd.AddGenericParameter(
                owner,
                gp.Attributes,
                _outputMd.GetOrAddString(_reader.GetString(gp.Name)),
                gp.Index);
            // We pre-set 1:1 in TokenMap; that may not hold if some were dropped.
            // Re-set with the actual returned row.
            TokenMap.SetGenericParam(inputH, MetadataTokens.GetRowNumber(outH));
        }
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.GenericParamConstraint); r++)
        {
            var inputH = MetadataTokens.GenericParameterConstraintHandle(r);
            var gpc = _reader.GetGenericParameterConstraint(inputH);
            var ownerOut = (GenericParameterHandle)TokenMap.MapEntity(gpc.Parameter);
            if (ownerOut.IsNil)
            {
                // Constraint dropped because its owner was dropped — clear
                // the predicted 1:1 entry so later remaps (e.g. CustomAttribute
                // parents via HasCustomAttribute, which allows
                // GenericParamConstraint) don't refer to a stale row number.
                TokenMap.SetGenericParamConstraint(inputH, 0);
                continue;
            }
            var outH = _outputMd.AddGenericParameterConstraint(ownerOut, TokenMap.MapReference(gpc.Type));
            // Re-set the TokenMap with the actually-issued row, in case any
            // earlier constraint was dropped and the running counter diverged
            // from the predicted 1:1 mapping.
            TokenMap.SetGenericParamConstraint(inputH, MetadataTokens.GetRowNumber(outH));
        }
    }

    // ─── Property + Event tables ────────────────────────────────────────────
    private void EmitPropertiesAndEvents()
    {
        // v1 omits Property/Event support — these are unusual on C-style CRT code.
        // If present, fail loudly so we don't silently corrupt metadata.
        if (_reader.GetTableRowCount(TableIndex.Property) > 0)
            throw new NotSupportedException("v1 does not support Property rows.");
        if (_reader.GetTableRowCount(TableIndex.Event) > 0)
            throw new NotSupportedException("v1 does not support Event rows.");
    }

    // ─── CustomAttribute ────────────────────────────────────────────────────
    private void EmitCustomAttributes()
    {
        // CustomAttribute is sort-required by Parent (a HasCustomAttribute coded
        // index). We bucket per output-parent and emit in the coded-index sort
        // order.
        int caCount = _reader.GetTableRowCount(TableIndex.CustomAttribute);
        var entries = new List<(uint sortKey, EntityHandle outParent, EntityHandle outCtor, BlobHandle outValue)>();

        for (int r = 1; r <= caCount; r++)
        {
            if (_customAttrSkip[r]) continue;
            var ca = _reader.GetCustomAttribute(MetadataTokens.CustomAttributeHandle(r));

            string fullName = GetCustomAttributeTypeFullName(MetadataTokens.CustomAttributeHandle(r));
            if (fullName == CompilerGlobalScopeAttrFullName) continue;

            EntityHandle outParent = RemapCustomAttributeParent(ca.Parent);
            if (outParent.IsNil) continue;

            EntityHandle outCtor = TokenMap.MapReference(ca.Constructor);
            if (outCtor.IsNil) continue;

            BlobHandle outValue = ca.Value.IsNil
                ? default
                : _outputMd.GetOrAddBlob(_reader.GetBlobBytes(ca.Value));

            uint sortKey = (uint)CodedIndex.HasCustomAttribute(outParent);
            entries.Add((sortKey, outParent, outCtor, outValue));
        }

        // Add synthesized DecoratedName CAs for local method references. These
        // must participate in the same sort to preserve HasCustomAttribute
        // monotonicity required by ECMA.
        if (_synthesizedDecoratedNameCAs.Count > 0)
        {
            var ctorRef = EnsureDecoratedNameCtorRef();
            foreach (var (parent, value) in _synthesizedDecoratedNameCAs)
            {
                var valueBlob = new BlobBuilder();
                valueBlob.WriteUInt16(0x0001);
                valueBlob.WriteSerializedString(value);
                valueBlob.WriteUInt16(0x0000);
                uint sortKey = (uint)CodedIndex.HasCustomAttribute(parent);
                entries.Add((sortKey, parent, ctorRef, _outputMd.GetOrAddBlob(valueBlob)));
            }
        }

        entries.Sort((a, b) => a.sortKey.CompareTo(b.sortKey));
        foreach (var e in entries)
        {
            _outputMd.AddCustomAttribute(e.outParent, e.outCtor, e.outValue);
        }
    }

    private EntityHandle EnsureDecoratedNameCtorRef()
    {
        if (!_decoratedNameAttrTypeRef.IsNil)
            return _decoratedNameAttrCtor;

        var coreLib = _coreLibAssemblyRef;
        _decoratedNameAttrTypeRef = _outputMd.AddTypeReference(
            coreLib,
            _outputMd.GetOrAddString("System.Runtime.CompilerServices"),
            _outputMd.GetOrAddString("DecoratedNameAttribute"));

        var ctorSig = new BlobBuilder();
        new BlobEncoder(ctorSig).MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: true)
            .Parameters(1, out var retEnc, out var parEnc);
        retEnc.Void();
        parEnc.AddParameter().Type().String();
        _decoratedNameAttrCtor = _outputMd.AddMemberReference(
            _decoratedNameAttrTypeRef,
            _outputMd.GetOrAddString(".ctor"),
            _outputMd.GetOrAddBlob(ctorSig));
        return _decoratedNameAttrCtor;
    }

    /// <summary>
    /// Remap a CustomAttribute parent handle. Definition parents stay attached
    /// to surviving definitions; reference-only methods use their MemberRef.
    /// </summary>
    private EntityHandle RemapCustomAttributeParent(EntityHandle inputParent)
    {
        if (inputParent.Kind == HandleKind.MethodDefinition)
        {
            int row = MetadataTokens.GetRowNumber((MethodDefinitionHandle)inputParent);
            if (row > 0 && _methodInfo[row].Disposition == MethodDisposition.ForwardRefMemberRef)
                return TokenMap.MapMethodDefReference((MethodDefinitionHandle)inputParent);
            if (row > 0 && _methodInfo[row].Disposition == MethodDisposition.Drop)
                return default;
            return TokenMap.MapMethodDef((MethodDefinitionHandle)inputParent);
        }
        if (inputParent.Kind == HandleKind.TypeDefinition)
        {
            int row = MetadataTokens.GetRowNumber((TypeDefinitionHandle)inputParent);
            var disp = _typeInfo[row].Disposition;
            if (disp == TypeDisposition.Drop) return default;
            if (disp == TypeDisposition.Flatten) return default;
            return TokenMap.MapTypeDef((TypeDefinitionHandle)inputParent);
        }
        if (inputParent.Kind == HandleKind.FieldDefinition)
            return TokenMap.MapField((FieldDefinitionHandle)inputParent);
        // Drop CAs whose parent is the Assembly/Module level — we're not
        // producing an assembly, so assembly-level attributes have no home.
        if (inputParent.Kind == HandleKind.AssemblyDefinition) return default;
        if (inputParent.Kind == HandleKind.ModuleDefinition) return default;

        return TokenMap.MapEntity(inputParent);
    }
}
