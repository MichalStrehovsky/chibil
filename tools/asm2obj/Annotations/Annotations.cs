// Source-time markers consumed by asm2obj. These attributes do NOT
// appear in the output COFF object's metadata — asm2obj recognises them
// by namespace+name and translates each into a real modopt/modreq byte
// in the rewritten ECMA signature, targeting the corresponding
// System.Runtime.CompilerServices.* TypeRef that link.exe / chibil
// expect.
//
// See tools/asm2obj/README.md "Signature modifier attributes" for the
// level semantics, canonical ordering, and validation rules.

using System;

namespace Asm2Obj
{
    /// <summary>
    /// Inject <c>modopt(System.Runtime.CompilerServices.IsConst)</c> at the
    /// pointer-level identified by <paramref name="level"/>. Level 0 (the
    /// default) targets the pointer-self slot — i.e. <c>T * const</c> for a
    /// parameter typed <c>T*</c>. Level 1 targets the pointee, level 2 the
    /// pointee of the pointee, etc., per the ECMA II.23.2.12 grammar.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = true, Inherited = false)]
    public sealed class IsConstAttribute : Attribute
    {
        public IsConstAttribute(int level = 0) { Level = level; }
        public int Level { get; }
    }

    /// <summary>
    /// Inject <c>modreq(System.Runtime.CompilerServices.IsVolatile)</c> at
    /// the pointer-level identified by <paramref name="level"/>. Note: this
    /// is emitted as a *required* modifier (modreq), matching MSVC's
    /// convention.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = true, Inherited = false)]
    public sealed class IsVolatileAttribute : Attribute
    {
        public IsVolatileAttribute(int level = 0) { Level = level; }
        public int Level { get; }
    }

    /// <summary>
    /// Inject <c>modopt(System.Runtime.CompilerServices.IsLong)</c> on the
    /// leaf integral type of the annotated parameter or return value. The
    /// leaf must be <c>int32</c> or <c>uint32</c>. There is exactly one
    /// such leaf per signature, so no <c>level</c> argument is exposed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
    public sealed class IsLongAttribute : Attribute { }

    /// <summary>
    /// Inject <c>modopt(System.Runtime.CompilerServices.IsSignUnspecifiedByte)</c>
    /// on the leaf integral type. The leaf must be <c>int8</c> (i.e. C#
    /// <c>sbyte</c>). MSVC uses this marker on plain <c>char</c> whose
    /// signedness is implementation-defined.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
    public sealed class IsSignUnspecifiedByteAttribute : Attribute { }

    /// <summary>
    /// Inject <c>modopt(System.Runtime.CompilerServices.CallConvCdecl)</c>
    /// on the return-type slot. Must be applied via <c>[return: ...]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
    public sealed class CallConvCdeclAttribute : Attribute { }

    /// <summary>
    /// Inject <c>modopt(System.Runtime.CompilerServices.CallConvStdcall)</c>
    /// on the return-type slot.
    /// </summary>
    [AttributeUsage(AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
    public sealed class CallConvStdcallAttribute : Attribute { }
}
