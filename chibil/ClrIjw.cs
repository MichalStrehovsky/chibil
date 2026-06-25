using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;

/// <summary>
/// Shared helpers for /clr (mixed-mode) IJW emission used by scenario emitters.
/// See <c>scenarios/NOTES.md</c> § "/clr mixed-mode IJW entry-point thunks" for
/// the conceptual background.
/// </summary>
internal static class ClrIjw
{
    /// <summary>
    /// Writes the <c>cmod_opt(CallConvCdecl)</c> prefix bytes into the
    /// return-type slot of a method signature, returning the underlying
    /// <see cref="SignatureTypeEncoder"/> positioned to encode the actual
    /// return type next (e.g. <c>.Int64()</c>, <c>.Single()</c>,
    /// <c>.Type(structDef, isValueType: true)</c>, etc.).
    ///
    /// /clr methods marked with the <c>UnmanagedExport</c> flag (i.e.
    /// user-defined functions exposed via the IJW NEP thunk) carry this
    /// modopt on their return type so the runtime knows the C calling
    /// convention applies at the boundary. For <c>void</c> returns, write
    /// the <c>Void</c> type code directly into <c>.Builder</c> after this
    /// call (there is no <c>.Void()</c> on <see cref="SignatureTypeEncoder"/>).
    /// </summary>
    public static SignatureTypeEncoder WriteCdeclModOpt(ReturnTypeEncoder retEnc, TypeReferenceHandle callConvCdeclRef)
    {
        var t = retEnc.Type();
        t.Builder.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        t.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(callConvCdeclRef));
        return t;
    }

    /// <summary>
    /// Convenience for the common case of <c>cmod_opt(CallConvCdecl) int32</c>
    /// return types. Equivalent to <c>WriteCdeclModOpt(...).Int32()</c>.
    /// </summary>
    public static void EncodeCdeclI4Return(ReturnTypeEncoder retEnc, TypeReferenceHandle callConvCdeclRef)
        => WriteCdeclModOpt(retEnc, callConvCdeclRef).Int32();

    /// <summary>
    /// Writes <c>cmod_opt(CallConvCdecl) void</c> into a return-type slot.
    /// </summary>
    public static void EncodeCdeclVoidReturn(ReturnTypeEncoder retEnc, TypeReferenceHandle callConvCdeclRef)
    {
        var t = WriteCdeclModOpt(retEnc, callConvCdeclRef);
        t.Builder.WriteByte((byte)SignatureTypeCode.Void);
    }

    /// <summary>
    /// Emits the minimal /clr IJW machinery for a single managed function:
    /// a <c>__mep@?fn</c> data slot stamped with a TOKEN reloc to the
    /// method's MethodDef CLR-token symbol, a single indirect-jump
    /// <c>.nep</c> thunk that targets the slot (per-arch: <c>FF 25 [imm32]</c>
    /// on x86 with a DIR32 reloc; <c>FF 25 [rel32]</c> on x64 with a REL32
    /// reloc; <c>ADRP X9 / LDR X9, [X9, #off] / BR X9</c> on arm64 with
    /// PAGEBASE_REL21 + PAGEOFFSET_12L relocs), a bare-name COFF alias for
    /// the thunk (e.g. <c>get</c> on x64/arm64, <c>_get</c> on x86), and
    /// one <c>.rdata$ilfixup</c> entry of type <c>0x0009</c> (32-bit) /
    /// <c>0x000A</c> (64-bit) telling the CLR loader to resolve the token
    /// in the slot into a from-unmanaged stub address at load time.
    /// </summary>
    /// <returns>The COFF symbol handle for the bare-name NEP thunk alias
    /// (e.g. <c>add</c> / <c>_add</c>). Use this as the target of an
    /// <c>AddAddressRelocation</c> from an <c>__unep@?fn</c> slot.</returns>
    /// <remarks>
    /// We skip MSVC's x64 double-thunk-avoidance optimization (the
    /// <c>__m2mep@?fn</c> companion slot + second <c>jmp [__m2mep@?fn]</c>
    /// inside the thunk) and the loader-populated <c>__unep@?fn</c>
    /// declaration field. They're pure optimizations for managed→managed
    /// transitions and aren't required for linker or runtime correctness in
    /// the single-indirect-jump path. <see cref="ObjDumper.IsClrThunkSymbol"/>
    /// hides them when comparing against MSVC reference objects.
    /// </remarks>
    public static CoffSymbolHandle EmitNepMachinery(
        Machine machine, int ptrSize, string symPrefix,
        CoffHeaderBuilder coffHeader, ManagedCoffSymbolTableBuilder symtab,
        CoffSectionWithContentBuilder dataSectionBuilder,
        CoffSectionWithContentBuilder nepSectionBuilder,
        CoffSectionWithContentBuilder ilFixupSectionBuilder,
        int methodToken, string bareName, string mangledSuffix)
    {
        // (1) __mep@?fn slot in .data, zero-initialized. The linker stamps
        //     the MethodDef token bytes here via the TOKEN reloc below.
        int slotOffset = dataSectionBuilder.Content.Count;
        dataSectionBuilder.Content.WriteBytes(0, ptrSize);

        var mepDataSym = symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, dataSectionBuilder, slotOffset);

        var tokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataSectionBuilder.Relocations).AddTokenRelocation(slotOffset, tokenSym);

        // (2) NEP thunk in .nep, single indirect jump through the __mep@?fn slot.
        int thunkOffset = nepSectionBuilder.Content.Count;
        if (machine == Machine.Arm64)
        {
            // ADRP X9, page-of-slot   ; 09 00 00 90   (placeholder, linker patches via PAGEBASE_REL21)
            // LDR  X9, [X9, #off]     ; 29 01 40 F9   (placeholder, linker patches via PAGEOFFSET_12L)
            // BR   X9                 ; 20 01 1F D6
            nepSectionBuilder.Content.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
            nepSectionBuilder.Relocations.WriteInt32(thunkOffset + 0);
            nepSectionBuilder.Relocations.WriteInt32(mepDataSym._value);
            nepSectionBuilder.Relocations.WriteUInt16(0x0004);                                  // IMAGE_REL_ARM64_PAGEBASE_REL21
            nepSectionBuilder.Relocations.WriteInt32(thunkOffset + 4);
            nepSectionBuilder.Relocations.WriteInt32(mepDataSym._value);
            nepSectionBuilder.Relocations.WriteUInt16(0x0007);                                  // IMAGE_REL_ARM64_PAGEOFFSET_12L
        }
        else
        {
            // FF 25 [4-byte operand placeholder] — linker fills the operand via the reloc.
            nepSectionBuilder.Content.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepSectionBuilder.Relocations.WriteInt32(thunkOffset + 2);
            nepSectionBuilder.Relocations.WriteInt32(mepDataSym._value);
            nepSectionBuilder.Relocations.WriteUInt16(machine == Machine.I386 ? (ushort)0x0006 : (ushort)0x0004);  // I386 DIR32 / AMD64 REL32
        }

        // (3) Bare-name COFF alias for the thunk (e.g. `foo` / `_foo`).
        //     Externally linked — other translation units reference C functions
        //     by this bare name, and `__unep@?fn` slots ADDR-reloc against it.
        var bareSym = symtab.AddExternalDataSymbol(symPrefix + bareName, nepSectionBuilder, thunkOffset);

        // (4) One 8-byte ILFixup entry pointing at the slot.
        int ilfixupOffset = ilFixupSectionBuilder.Content.Count;
        ilFixupSectionBuilder.Content.WriteInt32(0);                                        // RVA placeholder (ADDR32NB reloc below)
        ilFixupSectionBuilder.Content.WriteInt16(1);                                        // Count
        ilFixupSectionBuilder.Content.WriteInt16(ptrSize == 4 ? (short)0x0009 : (short)0x000A);     // COR_VTABLE_*BIT | FROM_UNMANAGED_RETAIN_APPDOMAIN
        new CoffRelocationEncoder(coffHeader, ilFixupSectionBuilder.Relocations).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);

        return bareSym;
    }

    /// <summary>
    /// Emits the minimal IJW machinery using MSVC-like per-function COMDAT
    /// sections: one pick-any <c>__mep@?fn</c> data slot, one no-duplicates
    /// <c>.nep</c> thunk keyed by the bare C symbol, and one associative
    /// <c>.rdata$ilfixup</c> entry tied to the <c>__mep@</c> slot section.
    /// </summary>
    public static CoffSymbolHandle EmitComdatNepMachinery(
        Machine machine, int ptrSize, string symPrefix,
        CoffHeaderBuilder coffHeader, ManagedCoffSymbolTableBuilder symtab,
        ICollection<CoffSectionBuilder> sections,
        int methodToken, string bareName, string mangledSuffix)
    {
        SectionCharacteristics pointerAlign = CoffSectionBuilder.AlignmentCharacteristics(ptrSize);

        var dataSection = new CoffSectionWithContentBuilder(
            ".data",
            SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.MemWrite | pointerAlign,
            CoffComdatSelection.Any);
        sections.Add(dataSection);

        dataSection.Content.WriteBytes(0, ptrSize);
        symtab.AddComdatSectionSymbol(dataSection);
        var mepDataSym = symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, dataSection, 0);

        var tokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataSection.Relocations).AddTokenRelocation(0, tokenSym);

        var nepSection = new CoffSectionWithContentBuilder(
            ".nep",
            SectionCharacteristics.ContainsCode | SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.Align4Bytes,
            CoffComdatSelection.NoDuplicates);
        sections.Add(nepSection);

        if (machine == Machine.Arm64)
        {
            nepSection.Content.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
            nepSection.Relocations.WriteInt32(0);
            nepSection.Relocations.WriteInt32(mepDataSym._value);
            nepSection.Relocations.WriteUInt16(0x0004);
            nepSection.Relocations.WriteInt32(4);
            nepSection.Relocations.WriteInt32(mepDataSym._value);
            nepSection.Relocations.WriteUInt16(0x0007);
        }
        else
        {
            nepSection.Content.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepSection.Relocations.WriteInt32(2);
            nepSection.Relocations.WriteInt32(mepDataSym._value);
            nepSection.Relocations.WriteUInt16(machine == Machine.I386 ? (ushort)0x0006 : (ushort)0x0004);
        }

        symtab.AddComdatSectionSymbol(nepSection);
        var bareSym = symtab.AddExternalDataSymbol(symPrefix + bareName, nepSection, 0);

        var ilfixupSection = new CoffSectionWithContentBuilder(
            ".rdata$ilfixup",
            SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead | SectionCharacteristics.Align4Bytes,
            CoffComdatSelection.Associative,
            dataSection);
        sections.Add(ilfixupSection);

        ilfixupSection.Content.WriteInt32(0);
        ilfixupSection.Content.WriteInt16(1);
        ilfixupSection.Content.WriteInt16(ptrSize == 4 ? (short)0x0009 : (short)0x000A);
        new CoffRelocationEncoder(coffHeader, ilfixupSection.Relocations).AddImageRelativeRelocation(0, mepDataSym);
        symtab.AddComdatSectionSymbol(ilfixupSection);

        return bareSym;
    }
}
