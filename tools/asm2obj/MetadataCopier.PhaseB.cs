// Phase B — Row prediction. Walks input tables in deterministic order and
// populates the TokenMap with predicted output row numbers for every entity
// that survives Phase A. Also computes per-output-TypeDef ordered member
// lists, synthesizes reference rows for local definitions, and pre-allocates
// parameter rows.

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    // Running output row counters per table.
    private int _outTypeRefRow;
    private int _outTypeDefRow;
    private int _outFieldRow;
    private int _outMethodRow;
    private int _outParamRow;
    private int _outMemberRefRow;
    private int _outTypeSpecRow;
    private int _outMethodSpecRow;
    private int _outStandaloneSigRow;
    private int _outAssemblyRefRow;
    private int _outModuleRefRow;

    // First-row of FieldList/MethodList per output TypeDef row (0 means "use next-row sentinel").
    private int[] _outTypeDefFieldFirst;
    private int[] _outTypeDefMethodFirst;
    private int[] _outTypeDefInputRow; // input row for each output TypeDef row (0 = synthesized <Module>)

    private void PredictRows()
    {
        int typeDefCount = _reader.GetTableRowCount(TableIndex.TypeDef);

        // ─── ModuleRef, AssemblyRef (1:1 copies) ────────────────────────────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.AssemblyRef); r++)
        {
            _outAssemblyRefRow++;
            TokenMap.SetAssemblyRef(MetadataTokens.AssemblyReferenceHandle(r), _outAssemblyRefRow);
        }
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.ModuleRef); r++)
        {
            _outModuleRefRow++;
            TokenMap.SetModuleRef(MetadataTokens.ModuleReferenceHandle(r), _outModuleRefRow);
        }

        // ─── TypeRef (1:1 copies) ───────────────────────────────────────────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.TypeRef); r++)
        {
            _outTypeRefRow++;
            TokenMap.SetTypeRef(MetadataTokens.TypeReferenceHandle(r), _outTypeRefRow);
        }

        // ─── Synthesized TypeRefs for required signature modifiers ──────────
        // Each recognized modifier kind needs a TypeRef to its
        // System.Runtime.CompilerServices.* target in the output. Reuse an
        // existing input TypeRef when present so we don't bloat the output;
        // synthesize new rows for missing ones, allocated after the copied
        // block so the 1:1 prediction for input TypeRefs stays correct.
        PredictSignatureModifierTypeRefs();

        // ─── Local TypeRefs for copied TypeDefs ─────────────────────────────
        // Metadata references name local types through TypeRefs. Top-level
        // local TypeRefs have a nil scope; nested TypeRefs are scoped through
        // the enclosing local TypeRef when populated in Phase C.
        _localTypeRefSourceRows = new List<int>();
        for (int r = 1; r <= typeDefCount; r++)
        {
            if (_typeInfo[r].Disposition != TypeDisposition.Copy) continue;
            _outTypeRefRow++;
            _localTypeRefSourceRows.Add(r);
            TokenMap.SetTypeDefReference(
                MetadataTokens.TypeDefinitionHandle(r),
                _outTypeRefRow);
        }

        // ─── TypeSpec (signature rewritten, 1:1) ────────────────────────────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.TypeSpec); r++)
        {
            _outTypeSpecRow++;
            TokenMap.SetTypeSpec(MetadataTokens.TypeSpecificationHandle(r), _outTypeSpecRow);
        }

        // ─── StandaloneSig (1:1) ────────────────────────────────────────────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.StandAloneSig); r++)
        {
            _outStandaloneSigRow++;
            TokenMap.SetStandaloneSig(MetadataTokens.StandaloneSignatureHandle(r), _outStandaloneSigRow);
        }

        // ─── TypeDef order: <Module> first, then surviving copy types ───────
        var outputTypeDefs = new List<int> { 0 }; // 0 = synthesized <Module>
        for (int r = 1; r <= typeDefCount; r++)
        {
            if (_typeInfo[r].Disposition == TypeDisposition.Copy)
                outputTypeDefs.Add(r);
        }

        _outTypeDefInputRow = new int[outputTypeDefs.Count + 1];
        _outTypeDefFieldFirst = new int[outputTypeDefs.Count + 1];
        _outTypeDefMethodFirst = new int[outputTypeDefs.Count + 1];
        _orderedMembers = new (List<int>, List<int>)[outputTypeDefs.Count + 1];

        for (int i = 0; i < outputTypeDefs.Count; i++)
        {
            int outRow = i + 1;
            int inputRow = outputTypeDefs[i];
            _outTypeDefRow++;
            _outTypeDefInputRow[outRow] = inputRow;
            _orderedMembers[outRow] = (new List<int>(), new List<int>());
            if (inputRow != 0)
                TokenMap.SetTypeDef(MetadataTokens.TypeDefinitionHandle(inputRow), outRow);
        }

        // ─── Build per-output-TypeDef member lists ──────────────────────────
        // <Module> (output row 1) collects: all input <Module>'s members
        // (if any survived — they don't, since we dropped input row 1) plus
        // every member of every flattened class.
        var moduleMembers = _orderedMembers[OutputModuleTypeDefRow];

        // Flattened-type members go to <Module>, in input-type order.
        for (int inputRow = 2; inputRow <= typeDefCount; inputRow++)
        {
            if (_typeInfo[inputRow].Disposition != TypeDisposition.Flatten) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            foreach (var fh in td.GetFields())
                moduleMembers.Fields.Add(MetadataTokens.GetRowNumber(fh));
            foreach (var mh in td.GetMethods())
            {
                int mrow = MetadataTokens.GetRowNumber(mh);
                if (_methodInfo[mrow].Disposition == MethodDisposition.Regular)
                    moduleMembers.Methods.Add(mrow);
                // ForwardRef methods don't get a MethodDef row.
            }
        }

        // Regular copy-types: their own members in input order.
        for (int outRow = 2; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            int inputRow = _outTypeDefInputRow[outRow];
            if (inputRow == 0) continue;
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(inputRow));
            foreach (var fh in td.GetFields())
                _orderedMembers[outRow].Fields.Add(MetadataTokens.GetRowNumber(fh));
            foreach (var mh in td.GetMethods())
            {
                int mrow = MetadataTokens.GetRowNumber(mh);
                if (_methodInfo[mrow].Disposition == MethodDisposition.Regular)
                    _orderedMembers[outRow].Methods.Add(mrow);
            }
        }

        // ─── Predict Field and MethodDef rows + per-method Param ranges ────
        for (int outRow = 1; outRow < _outTypeDefInputRow.Length; outRow++)
        {
            _outTypeDefFieldFirst[outRow] = _outFieldRow + 1;
            foreach (int inputFieldRow in _orderedMembers[outRow].Fields)
            {
                _outFieldRow++;
                TokenMap.SetField(MetadataTokens.FieldDefinitionHandle(inputFieldRow), _outFieldRow);
            }

            _outTypeDefMethodFirst[outRow] = _outMethodRow + 1;
            foreach (int inputMethodRow in _orderedMembers[outRow].Methods)
            {
                _outMethodRow++;
                TokenMap.SetMethodDef(MetadataTokens.MethodDefinitionHandle(inputMethodRow), _outMethodRow);
                _methodInfo[inputMethodRow].OutputOwnerRow = outRow;

                var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(inputMethodRow));
                foreach (var ph in md.GetParameters())
                {
                    _outParamRow++;
                    TokenMap.SetParam(ph, _outParamRow);
                }
            }
        }

        PlanMethodSymbolNames();

        // ─── MemberRef predictions: copies, then local fields and methods ────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            _outMemberRefRow++;
            TokenMap.SetMemberRef(MetadataTokens.MemberReferenceHandle(r), _outMemberRefRow);
        }

        _localFieldRefSourceRows = new List<int>();
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.Field); r++)
        {
            var fieldH = MetadataTokens.FieldDefinitionHandle(r);
            var owner = _reader.GetFieldDefinition(fieldH).GetDeclaringType();
            if (_typeInfo[MetadataTokens.GetRowNumber(owner)].Disposition == TypeDisposition.Drop)
                continue;
            _outMemberRefRow++;
            _localFieldRefSourceRows.Add(r);
            TokenMap.SetFieldReference(fieldH, _outMemberRefRow);
        }

        _localMethodRefSourceRows = new List<int>();
        for (int r = 1; r < _methodInfo.Length; r++)
        {
            if (_methodInfo[r].Disposition == MethodDisposition.Drop) continue;
            _outMemberRefRow++;
            _localMethodRefSourceRows.Add(r);
            TokenMap.SetMethodDefReference(
                MetadataTokens.MethodDefinitionHandle(r),
                _outMemberRefRow);
        }

        // ─── MethodSpec (1:1) ───────────────────────────────────────────────
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MethodSpec); r++)
        {
            _outMethodSpecRow++;
            TokenMap.SetMethodSpec(MetadataTokens.MethodSpecificationHandle(r), _outMethodSpecRow);
        }

        // ─── GenericParam, GenericParamConstraint, Property, Event predictions ─
        // We copy these 1:1 for surviving owners. Predictions only set TokenMap
        // entries; sort/owner remapping happens in Phase C.
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.GenericParam); r++)
        {
            var h = MetadataTokens.GenericParameterHandle(r);
            var gp = _reader.GetGenericParameter(h);
            if (!OwnerSurvives(gp.Parent)) continue;
            TokenMap.SetGenericParam(h, r); // 1:1 since order matches input here
        }
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.GenericParamConstraint); r++)
        {
            var h = MetadataTokens.GenericParameterConstraintHandle(r);
            var gpc = _reader.GetGenericParameterConstraint(h);
            // Owner is a GenericParam handle.
            TokenMap.SetGenericParamConstraint(h, r);
        }
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.Property); r++)
            TokenMap.SetProperty(MetadataTokens.PropertyDefinitionHandle(r), r);
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.Event); r++)
            TokenMap.SetEvent(MetadataTokens.EventDefinitionHandle(r), r);
    }

    private void PlanMethodSymbolNames()
    {
        _outputMethodDecoratedNames = new string[_outMethodRow + 1];
        _outputMethodBareNames = new string[_outMethodRow + 1];
        _methodReferenceDecoratedNames = new string[_methodInfo.Length];

        var seenDefinitionNames = new HashSet<string>();
        for (int inputRow = 1; inputRow < _methodInfo.Length; inputRow++)
        {
            var disposition = _methodInfo[inputRow].Disposition;
            if (disposition == MethodDisposition.Drop) continue;

            string decoratedName;
            if (disposition == MethodDisposition.Regular)
            {
                decoratedName = ComputeFunctionSymbolName(inputRow);
                if (!seenDefinitionNames.Add(decoratedName))
                {
                    string baseName = decoratedName;
                    int suffix = 0;
                    do
                    {
                        suffix++;
                        decoratedName = $"{baseName}${suffix}";
                    } while (!seenDefinitionNames.Add(decoratedName));
                }

                int outputRow = MetadataTokens.GetRowNumber(
                    TokenMap.MapMethodDef(MetadataTokens.MethodDefinitionHandle(inputRow)));
                _outputMethodDecoratedNames[outputRow] = decoratedName;
                _outputMethodBareNames[outputRow] = ComputeBareName(inputRow);
            }
            else
            {
                string explicitName = _methodInfo[inputRow].DecoratedName;
                decoratedName = explicitName != null
                    ? ApplyX86UnderscoreRule(explicitName)
                    : _mangler.MangleMethod(
                        MetadataTokens.MethodDefinitionHandle(inputRow),
                        _methodInjections[inputRow]);
            }

            _methodReferenceDecoratedNames[inputRow] = decoratedName;
        }
    }

    // Per-modifier-kind output TypeRef row, populated in Phase B's
    // PredictSignatureModifierTypeRefs (mapping a kind to either an
    // existing TypeRef in the input, or a synthesized row to be emitted
    // after the TypeRef copy block in Phase C).
    private readonly Dictionary<ModifierKind, int> _modifierTypeRefOutputRow = new();

    // Synthesized TypeRefs to emit after the regular TypeRef copy. Each
    // entry is the kind to emit at the next available row.
    private readonly List<ModifierKind> _synthesizedModifierTypeRefs = new();

    private void PredictSignatureModifierTypeRefs()
    {
        if (_requiredModifierKinds.Count == 0) return;

        // Index input TypeRefs by (namespace, name) → input row, restricted
        // to TypeRefs that resolve through the mscorlib AssemblyRef (so we
        // don't accidentally bind a modifier to a same-named TypeRef in a
        // different assembly).
        var inputCorlibTypeRefs = new Dictionary<(string ns, string name), int>();
        int corlibInputRow = FindMscorlibInputAssemblyRefRow();
        if (corlibInputRow > 0)
        {
            for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.TypeRef); r++)
            {
                var tr = _reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(r));
                if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
                if (MetadataTokens.GetRowNumber(tr.ResolutionScope) != corlibInputRow) continue;
                var key = (_reader.GetString(tr.Namespace), _reader.GetString(tr.Name));
                if (!inputCorlibTypeRefs.ContainsKey(key))
                    inputCorlibTypeRefs[key] = r;
            }
        }

        foreach (var kind in _requiredModifierKinds)
        {
            var key = kind.BclTypeRef();
            if (inputCorlibTypeRefs.TryGetValue(key, out int inputRow))
            {
                // Reuse the existing input TypeRef row's predicted output row
                // (1:1 from the copy block above).
                _modifierTypeRefOutputRow[kind] = inputRow;
            }
            else
            {
                _outTypeRefRow++;
                _modifierTypeRefOutputRow[kind] = _outTypeRefRow;
                _synthesizedModifierTypeRefs.Add(kind);
            }
        }
    }

    /// <summary>
    /// Looks up the input AssemblyRef row whose name is "mscorlib".
    /// Returns 0 when none is found (e.g. an input with no external types).
    /// Phase A's ValidateCoreLibReferences guarantees a non-mscorlib core lib
    /// has already been rejected.
    /// </summary>
    private int FindMscorlibInputAssemblyRefRow()
    {
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.AssemblyRef); r++)
        {
            var ar = _reader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(r));
            if (_reader.GetString(ar.Name) == "mscorlib") return r;
        }
        return 0;
    }

    private bool OwnerSurvives(EntityHandle owner)
    {
        switch (owner.Kind)
        {
            case HandleKind.TypeDefinition:
                {
                    int inputRow = MetadataTokens.GetRowNumber((TypeDefinitionHandle)owner);
                    var disp = _typeInfo[inputRow].Disposition;
                    return disp == TypeDisposition.Copy || disp == TypeDisposition.Flatten;
                }
            case HandleKind.MethodDefinition:
                {
                    int inputRow = MetadataTokens.GetRowNumber((MethodDefinitionHandle)owner);
                    return _methodInfo[inputRow].Disposition == MethodDisposition.Regular;
                }
            default:
                return false;
        }
    }
}
