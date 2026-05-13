using System;
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
        Machine machine, bool is32, int ptrSize, string symPrefix,
        CoffHeaderBuilder coffHeader, ManagedCoffSymbolTableBuilder symtab,
        BlobBuilder dataStream, BlobBuilder dataRelocs,
        BlobBuilder nepStream, BlobBuilder nepRelocs,
        BlobBuilder ilFixupStream, BlobBuilder ilFixupRelocs,
        int methodToken, string bareName, string mangledSuffix)
    {
        // (1) __mep@?fn slot in .data, zero-initialized. The linker stamps
        //     the MethodDef token bytes here via the TOKEN reloc below.
        int slotOffset = dataStream.Count;
        for (int i = 0; i < ptrSize; i++) dataStream.WriteByte(0);

        var mepDataSym = symtab.AddExternalDataSymbol("__mep@" + mangledSuffix, LogicalSection.Data, slotOffset);

        var tokenSym = symtab.GetOrAddUndefinedClrTokenSymbol(methodToken.ToString("X8"));
        new CoffRelocationEncoder(coffHeader, dataRelocs).AddTokenRelocation(slotOffset, tokenSym);

        // (2) NEP thunk in .nep, single indirect jump through the __mep@?fn slot.
        int thunkOffset = nepStream.Count;
        if (machine == Machine.Arm64)
        {
            // ADRP X9, page-of-slot   ; 09 00 00 90   (placeholder, linker patches via PAGEBASE_REL21)
            // LDR  X9, [X9, #off]     ; 29 01 40 F9   (placeholder, linker patches via PAGEOFFSET_12L)
            // BR   X9                 ; 20 01 1F D6
            nepStream.WriteBytes(new byte[] { 0x09, 0x00, 0x00, 0x90, 0x29, 0x01, 0x40, 0xF9, 0x20, 0x01, 0x1F, 0xD6 });
            nepRelocs.WriteInt32(thunkOffset + 0);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0004);                                  // IMAGE_REL_ARM64_PAGEBASE_REL21
            nepRelocs.WriteInt32(thunkOffset + 4);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(0x0006);                                  // IMAGE_REL_ARM64_PAGEOFFSET_12L
        }
        else
        {
            // FF 25 [4-byte operand placeholder] — linker fills the operand via the reloc.
            nepStream.WriteBytes(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            nepRelocs.WriteInt32(thunkOffset + 2);
            nepRelocs.WriteInt32(mepDataSym._value);
            nepRelocs.WriteUInt16(is32 ? (ushort)0x0006 : (ushort)0x0004);  // I386 DIR32 / AMD64 REL32
        }

        // (3) Bare-name COFF alias for the thunk (e.g. `foo` / `_foo`).
        //     Externally linked — other translation units reference C functions
        //     by this bare name, and `__unep@?fn` slots ADDR-reloc against it.
        var bareSym = symtab.AddExternalDataSymbol(symPrefix + bareName, LogicalSection.Nep, thunkOffset);

        // (4) One 8-byte ILFixup entry pointing at the slot.
        int ilfixupOffset = ilFixupStream.Count;
        ilFixupStream.WriteInt32(0);                                        // RVA placeholder (ADDR32NB reloc below)
        ilFixupStream.WriteInt16(1);                                        // Count
        ilFixupStream.WriteInt16(is32 ? (short)0x0009 : (short)0x000A);     // COR_VTABLE_*BIT | FROM_UNMANAGED_RETAIN_APPDOMAIN
        new CoffRelocationEncoder(coffHeader, ilFixupRelocs).AddImageRelativeRelocation(ilfixupOffset, mepDataSym);

        return bareSym;
    }
}
