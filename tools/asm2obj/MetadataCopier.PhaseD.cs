// Phase D — IL body emission. Pre-registers all surviving MethodDef COFF
// symbols and external MemberRef CLR tokens, then walks each MethodDef with a
// body, runs IlBodyRewriter, and finalises the body via AddMethodBody.

using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

public sealed partial class MetadataCopier
{
    // COFF symbol name (decorated name) per *output* MethodDef row.
    private string[] _outputMethodDecoratedNames;
    // Bare native name per output MethodDef row (used for UnmanagedExport NEP alias).
    private string[] _outputMethodBareNames;

    public void EmitMethodBodies(
        ManagedCoffSymbolTableBuilder symtab,
        RelocatableMethodBodyStreamEncoder bodyEncoder,
        PEReader peReader)
    {
        // Allocate symbol-name arrays indexed by output method row.
        _outputMethodDecoratedNames = new string[_outMethodRow + 1];
        _outputMethodBareNames = new string[_outMethodRow + 1];

        // ─── Pre-register all surviving MethodDef COFF symbols ──────────────
        // This must happen before any IL is emitted because the symbol-table
        // guards in coffobjectemitter throw if an undefined CLR-token symbol
        // already exists when AddFunctionClrToken is called.
        var seenNames = new System.Collections.Generic.HashSet<string>();
        for (int inputRow = 1; inputRow < _methodInfo.Length; inputRow++)
        {
            if (_methodInfo[inputRow].Disposition != MethodDisposition.Regular) continue;

            var inputH = MetadataTokens.MethodDefinitionHandle(inputRow);
            var outputH = TokenMap.MapMethodDef(inputH);
            int outRow = MetadataTokens.GetRowNumber(outputH);

            string decoratedName = ComputeFunctionSymbolName(inputRow);

            // Collision-handling: if the auto-mangled name (or an explicit
            // [DecoratedName]) collides with one we already issued, append
            // a uniquifying suffix. The body symbol just needs to be unique
            // inside this .obj; native callers reach methods via
            // [DecoratedName] or NEP bare-name aliases instead.
            if (!seenNames.Add(decoratedName))
            {
                string baseName = decoratedName;
                int suffix = 0;
                do
                {
                    suffix++;
                    decoratedName = $"{baseName}${suffix}";
                } while (!seenNames.Add(decoratedName));
            }

            _outputMethodDecoratedNames[outRow] = decoratedName;
            _outputMethodBareNames[outRow] = ComputeBareName(inputRow);

            symtab.PreRegisterFunctionClrToken(decoratedName, outputH);
        }

        // ─── Register external CLR tokens for MemberRefs that need native
        //     link-time resolution. A MemberRef needs an External COFF
        //     symbol only when it represents an unresolved C function call
        //     parented on <Module> (the chibil/MSVC convention for extern-C
        //     references). MemberRefs whose parent is a TypeRef pointing
        //     into mscorlib (managed-only calls like Attribute::.ctor or
        //     Marshal::FreeHGlobal) are resolved by the CLR loader at
        //     runtime; for those we skip External registration. The IL
        //     writer still creates a CLR-token COFF symbol (storage
        //     class 107) on demand the first time the token is referenced.
        for (int r = 1; r <= _reader.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            var inputH = MetadataTokens.MemberReferenceHandle(r);
            var outputH = TokenMap.MapMemberRef(inputH);
            var mr = _reader.GetMemberReference(inputH);

            // Only Module-parented MemberRefs need a native external symbol.
            EntityHandle outParent = TokenMap.MapEntity(mr.Parent);
            if (outParent.Kind != HandleKind.TypeDefinition) continue;
            if (MetadataTokens.GetRowNumber(outParent) != OutputModuleTypeDefRow) continue;

            string name = ComputeMemberRefSymbolName(inputH);
            if (name != null)
                symtab.AddExternalClrToken(name, outputH);
        }
        // And for the synthesized ForwardRef MemberRefs.
        for (int i = 0; i < _forwardRefSourceMethodRows.Count; i++)
        {
            int methodRow = _forwardRefSourceMethodRows[i];
            string name = _forwardRefDecoratedNames[i];
            var memberRef = MetadataTokens.MemberReferenceHandle(_forwardRefMemberRefRows[i]);
            symtab.AddExternalClrToken(name, memberRef);
        }

        // ─── Emit IL bodies ─────────────────────────────────────────────────
        for (int inputRow = 1; inputRow < _methodInfo.Length; inputRow++)
        {
            if (_methodInfo[inputRow].Disposition != MethodDisposition.Regular) continue;
            var inputH = MetadataTokens.MethodDefinitionHandle(inputRow);
            var md = _reader.GetMethodDefinition(inputH);
            if (md.RelativeVirtualAddress == 0) continue; // abstract/runtime/no body

            EmitBody(symtab, bodyEncoder, peReader, inputRow, md);
        }
    }

    private void EmitBody(
        ManagedCoffSymbolTableBuilder symtab,
        RelocatableMethodBodyStreamEncoder bodyEncoder,
        PEReader peReader,
        int inputMethodRow,
        MethodDefinition md)
    {
        var body = peReader.GetMethodBody(md.RelativeVirtualAddress);

        // v1 supports Finally and Fault exception handlers (the typesafe ones
        // generated by C# try/finally / using). Catch and Filter handlers
        // remain unsupported — they require type-token rewriting for the
        // CatchType slot, and exception-throw paths that asm2obj's primary
        // CRT-shim audience doesn't exercise.
        foreach (var region in body.ExceptionRegions)
        {
            if (region.Kind != ExceptionRegionKind.Finally && region.Kind != ExceptionRegionKind.Fault)
                throw new NotSupportedException(
                    $"Method '{_reader.GetString(md.Name)}' has a {region.Kind} exception region. " +
                    "v1 supports only Finally and Fault handlers.");
        }

        var outputMethodH = TokenMap.MapMethodDef(MetadataTokens.MethodDefinitionHandle(inputMethodRow));
        int outRow = MetadataTokens.GetRowNumber(outputMethodH);
        string symName = _outputMethodDecoratedNames[outRow];

        // Map the local-var sig handle through TokenMap. Default (nil) if none.
        StandaloneSignatureHandle outputLocalSig = default;
        if (!body.LocalSignature.IsNil)
        {
            outputLocalSig = TokenMap.MapStandaloneSig(body.LocalSignature);
        }

        // A non-null control-flow builder is needed only when there are
        // exception regions to attach. The IL rewriter never produces
        // branches/switches through the builder (it copies raw bytes), so
        // HasFixups stays false and AddMethodBody's fast WriteContentTo path
        // is preserved.
        var controlFlowBuilder = body.ExceptionRegions.Length > 0
            ? new RelocatableControlFlowBuilder()
            : null;

        var ilReader = body.GetILReader();
        var enc = new RelocatableInstructionEncoder(
            new BlobBuilder(),
            new MethodRelocationBuilder(),
            controlFlowBuilder: controlFlowBuilder,
            lineNumberBuilder: null);
        IlBodyRewriter.Rewrite(ilReader, TokenMap, enc);

        // Register exception regions. Input IL offsets equal output IL offsets
        // because the rewriter is byte-for-byte (all token operand slots are
        // exactly 4 bytes both before and after rewriting).
        foreach (var region in body.ExceptionRegions)
        {
            var tryStart = controlFlowBuilder.AddLabel();
            var tryEnd = controlFlowBuilder.AddLabel();
            var handlerStart = controlFlowBuilder.AddLabel();
            var handlerEnd = controlFlowBuilder.AddLabel();
            controlFlowBuilder.MarkLabel(region.TryOffset, tryStart);
            controlFlowBuilder.MarkLabel(region.TryOffset + region.TryLength, tryEnd);
            controlFlowBuilder.MarkLabel(region.HandlerOffset, handlerStart);
            controlFlowBuilder.MarkLabel(region.HandlerOffset + region.HandlerLength, handlerEnd);
            if (region.Kind == ExceptionRegionKind.Finally)
                controlFlowBuilder.AddFinallyRegion(tryStart, tryEnd, handlerStart, handlerEnd);
            else
                controlFlowBuilder.AddFaultRegion(tryStart, tryEnd, handlerStart, handlerEnd);
        }

        MethodBodyAttributes attrs = body.LocalVariablesInitialized
            ? MethodBodyAttributes.InitLocals
            : 0;

        bodyEncoder.AddMethodBody(
            outputMethodH,
            symName,
            enc,
            maxStack: body.MaxStack,
            localVariablesSignature: outputLocalSig,
            attributes: attrs,
            debugName: symName);
    }

    /// <summary>
    /// Computes the COFF symbol name for a function body.
    /// Precedence: [DecoratedName] explicit value > MSVC auto-mangler > synthetic.
    /// </summary>
    private string ComputeFunctionSymbolName(int inputMethodRow)
    {
        string explicitName = _methodInfo[inputMethodRow].DecoratedName;
        if (explicitName != null)
            return ApplyX86UnderscoreRule(explicitName);

        var mh = MetadataTokens.MethodDefinitionHandle(inputMethodRow);
        try
        {
            return _mangler.MangleMethod(mh, _methodInjections[inputMethodRow]);
        }
        catch (NotSupportedException)
        {
            // Method has a signature we can't auto-mangle (e.g. managed
            // reference types). Synthesize a unique name. Such methods are
            // not externally linker-callable, but the COFF symbol still
            // needs *some* unique name to anchor the body.
            var md = _reader.GetMethodDefinition(mh);
            string name = _reader.GetString(md.Name);
            return $"$asm2obj.{name}.{inputMethodRow}";
        }
    }

    private string ComputeBareName(int inputMethodRow)
    {
        var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(inputMethodRow));
        return _reader.GetString(md.Name);
    }

    private string ComputeMemberRefSymbolName(MemberReferenceHandle inputH)
    {
        var mr = _reader.GetMemberReference(inputH);
        if (mr.GetKind() != MemberReferenceKind.Method) return null;

        // Check for [DecoratedName] on the MemberRef itself.
        // CustomAttributeHandleCollection isn't directly exposed on MemberRef
        // in older SRM; we iterate the CustomAttribute table.
        foreach (var caH in _reader.CustomAttributes)
        {
            var ca = _reader.GetCustomAttribute(caH);
            if (ca.Parent != (EntityHandle)inputH) continue;
            if (GetCustomAttributeTypeFullName(caH) != DecoratedNameAttrFullName) continue;
            _customAttrSkip[MetadataTokens.GetRowNumber(caH)] = true;
            var blobReader = _reader.GetBlobReader(ca.Value);
            ushort prolog = blobReader.ReadUInt16();
            if (prolog != 0x0001) continue;
            string s = blobReader.ReadSerializedString();
            return ApplyX86UnderscoreRule(s);
        }

        // No DecoratedName: auto-mangle.
        try
        {
            return _mangler.MangleMemberRef(inputH);
        }
        catch (NotSupportedException)
        {
            // Method involves managed reference types; can't auto-mangle.
            // Skip — the MemberRef will only be referenceable via its CLR
            // token in IL, not by external name. This is acceptable for
            // mscorlib methods called from managed IL.
            return null;
        }
    }

    /// <summary>
    /// On x86, link.exe expects plain C symbols (no <c>?</c> prefix) to start
    /// with an underscore. Auto-applies the prefix when DecoratedName-string
    /// doesn't already use C++ decoration.
    /// </summary>
    private string ApplyX86UnderscoreRule(string name)
    {
        if (!_is32) return name;
        if (name.StartsWith("?")) return name; // already decorated
        if (name.StartsWith("_")) return name; // already prefixed
        return "_" + name;
    }
}
