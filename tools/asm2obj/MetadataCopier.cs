using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

/// <summary>
/// Drives the metadata copier pipeline (Phases A–F) translating an input
/// <see cref="MetadataReader"/> into an output <see cref="MetadataBuilder"/>
/// plus COFF symbol-table / IL stream / NEP machinery, all coordinated through
/// a <see cref="TokenMap"/>.
///
/// Split into partial classes per phase for readability.
/// </summary>
public sealed partial class MetadataCopier
{
    private readonly MetadataReader _reader;
    private readonly MetadataBuilder _outputMd;
    private readonly Machine _machine;
    private readonly bool _is32;
    private readonly string _symPrefix;
    private readonly int _ptrSize;
    private readonly MsvcNameMangler _mangler;

    internal readonly TokenMap TokenMap;

    // ─── Classification (Phase A) ────────────────────────────────────────────
    private enum TypeDisposition { Drop, Flatten, Copy }
    private enum MethodDisposition { Drop, Regular, ForwardRefMemberRef }

    private struct TypeInfo
    {
        public TypeDisposition Disposition;
    }

    private struct MethodInfo
    {
        public MethodDisposition Disposition;
        public bool UnmanagedExport;
        public string DecoratedName; // null if none

        // The output owner — TypeDefinitionHandle of the type that owns this
        // method in the output. For flattened methods this is the output <Module>.
        // Stored as raw row number (1-based) of an *output* TypeDef.
        public int OutputOwnerRow;
    }

    // Indexed by input row (1-based; slot 0 unused).
    private TypeInfo[] _typeInfo;
    private MethodInfo[] _methodInfo;
    private bool[] _customAttrSkip; // true → don't copy this CustomAttribute row

    // Output TypeDef #1 is always <Module>.
    private const int OutputModuleTypeDefRow = 1;

    // Synthesized rows for ForwardRef methods (one MemberRef per, parented on
    // the output <Module> TypeDef).
    private List<int> _forwardRefSourceMethodRows;       // input MethodDef row
    private List<string> _forwardRefDecoratedNames;       // computed COFF symbol name
    private List<int> _forwardRefMemberRefRows;           // output MemberRef row

    // Per-output-TypeDef ordered lists of input field/method handles. Built in
    // Phase A so Phase B can predict FieldList/MethodList first-row values.
    // _orderedMembers[outputTypeDefRow] = (fieldRows, methodRows).
    private (List<int> Fields, List<int> Methods)[] _orderedMembers;

    public MetadataCopier(MetadataReader reader, MetadataBuilder outputMd, Machine machine)
    {
        _reader = reader;
        _outputMd = outputMd;
        _machine = machine;
        _is32 = machine == Machine.I386;
        _symPrefix = _is32 ? "_" : "";
        _ptrSize = _is32 ? 4 : 8;
        TokenMap = new TokenMap(reader, outputMd);
        _mangler = new MsvcNameMangler(reader, machine);
    }

    // ─── Phase entry points (defined in partial files) ───────────────────────
    public void ClassifyAndPlan()
    {
        ClassifyTypesAndMethods();
        PredictRows();
    }

    public void PopulateTables()
    {
        EmitTypeRefs();
        EmitTypeSpecs();
        EmitStandaloneSigs();
        EmitTypeDefs();
        EmitFields();
        EmitMethodDefsAndParams();
        EmitMemberRefs();
        EmitMethodSpecs();
        EmitConstants();
        EmitFieldLayouts();
        EmitClassLayouts();
        EmitFieldRvas();
        EmitInterfaceImpls();
        EmitMethodImpls();
        EmitNestedClasses();
        EmitGenericParamsAndConstraints();
        EmitPropertiesAndEvents();
        EmitCustomAttributes();
    }
}
