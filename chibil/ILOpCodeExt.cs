using System;
using System.Diagnostics;
using System.Reflection.Metadata;

namespace Chibil;

// Helpers over ILOpCode that the basic-block IL builder needs but that
// System.Reflection.Metadata.ILOpCodeExtensions does not expose publicly.
// Ported from dotnet/roslyn
// (src/Compilers/Core/Portable/CodeGen/ILOpCodeExtensions.cs), which is MIT
// licensed. SRM already provides IsBranch/GetBranchOperandSize/GetShortBranch/
// GetLongBranch, so those are intentionally not duplicated here.
internal static class ILOpCodeExt
{
    public static int Size(this ILOpCode opcode) => (int)opcode <= 0xff ? 1 : 2;

    public static ILOpCode GetLeaveOpcode(this ILOpCode opcode) => opcode switch
    {
        ILOpCode.Br => ILOpCode.Leave,
        ILOpCode.Br_s => ILOpCode.Leave_s,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, null),
    };

    public static bool HasVariableStackBehavior(this ILOpCode opcode) =>
        opcode is ILOpCode.Call or ILOpCode.Calli or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ret;

    /// <summary>
    /// These opcodes represent control transfer.
    /// They should not appear inside basic blocks.
    /// </summary>
    public static bool IsControlTransfer(this ILOpCode opcode) =>
        opcode.IsBranch() ||
        opcode is ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Rethrow or ILOpCode.Endfilter
            or ILOpCode.Endfinally or ILOpCode.Switch or ILOpCode.Jmp;

    // Conditional branches occupy two contiguous ranges in the ECMA-335 opcode
    // table (the branch block minus the unconditional Br_s/Br): the short forms
    // Brfalse_s..Blt_un_s and the long forms Brfalse..Blt_un.
    public static bool IsConditionalBranch(this ILOpCode opcode) =>
        opcode is (>= ILOpCode.Brfalse_s and <= ILOpCode.Blt_un_s)
               or (>= ILOpCode.Brfalse and <= ILOpCode.Blt_un);

    // Relational branches are the conditional branches minus Brtrue/Brfalse, again
    // two contiguous ranges: Beq_s..Blt_un_s and Beq..Blt_un.
    public static bool IsRelationalBranch(this ILOpCode opcode) =>
        opcode is (>= ILOpCode.Beq_s and <= ILOpCode.Blt_un_s)
               or (>= ILOpCode.Beq and <= ILOpCode.Blt_un);

    /// <summary>
    /// Most instructions can allow control to fall through after their execution.
    /// Only unconditional branches, ret, jmp, leave(.s), endfinally, endfault,
    /// throw, and rethrow do not.
    /// </summary>
    public static bool CanFallThrough(this ILOpCode opcode) =>
        opcode is not (ILOpCode.Br or ILOpCode.Br_s or ILOpCode.Ret or ILOpCode.Jmp or ILOpCode.Throw
            or ILOpCode.Endfinally or ILOpCode.Leave or ILOpCode.Leave_s or ILOpCode.Rethrow);

    public static int NetStackBehavior(this ILOpCode opcode)
    {
        Debug.Assert(!opcode.HasVariableStackBehavior());
        return opcode.StackPushCount() - opcode.StackPopCount();
    }

    // Stack pop/push counts per opcode, derived from CoreCLR's opcode.def: each
    // Pop*/Push* column maps to a count equal to the number of '+'-joined terms
    // (Pop0/Push0 = 0; VarPop/VarPush = the 0xFF "variable" sentinel). Stored as
    // interleaved (pop, push) byte pairs, indexed by the opcode byte (1-byte
    // opcodes) or its low byte (0xFExx 2-byte opcodes). Mechanically derived from
    // opcode.def's Pop*/Push* columns (validated against the per-opcode counts).
    private const byte Variable = 0xFF;

    private static ReadOnlySpan<byte> StackBehavior1Byte =>
    [
        0,0, 0,0, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 1,0, 1,0, 1,0, 1,0, 0,1, 0,1, // 0x00
        1,0, 0,1, 0,1, 1,0, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, 0,1, // 0x10
        0,1, 0,1, 0,1, 0,1, 0,0, 1,2, 1,0, 0,0, 255,255, 255,255, 255,0, 0,0, 1,0, 1,0, 2,0, 2,0, // 0x20
        2,0, 2,0, 2,0, 2,0, 2,0, 2,0, 2,0, 2,0, 0,0, 1,0, 1,0, 2,0, 2,0, 2,0, 2,0, 2,0, // 0x30
        2,0, 2,0, 2,0, 2,0, 2,0, 1,0, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, // 0x40
        1,1, 2,0, 2,0, 2,0, 2,0, 2,0, 2,0, 2,0, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, // 0x50
        2,1, 2,1, 2,1, 2,1, 2,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 255,255, // 0x60
        2,0, 1,1, 0,1, 255,1, 1,1, 1,1, 1,1, 0,0, 0,0, 1,1, 1,0, 1,1, 1,1, 2,0, 0,1, 0,1, // 0x70
        1,0, 2,0, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 2,1, // 0x80
        2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 3,0, 3,0, 3,0, 3,0, 3,0, // 0x90
        3,0, 3,0, 3,0, 2,1, 3,0, 1,1, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, // 0xA0
        0,0, 0,0, 0,0, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 1,1, 0,0, 0,0, 0,0, 0,0, 0,0, // 0xB0
        0,0, 0,0, 1,1, 1,1, 0,0, 0,0, 1,1, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, // 0xC0
        0,1, 1,1, 1,1, 1,1, 1,1, 1,1, 2,1, 2,1, 2,1, 2,1, 2,1, 2,1, 0,0, 0,0, 0,0, 2,0, // 0xD0
        1,1, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, // 0xE0
        0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, // 0xF0
    ];

    private static ReadOnlySpan<byte> StackBehavior2Byte =>
    [
        0,1, 2,1, 2,1, 2,1, 2,1, 2,1, 0,1, 1,1, 0,0, 0,1, 0,1, 1,0, 0,1, 0,1, 1,0, 1,1, // 0xFE00
        0,0, 1,0, 0,0, 0,0, 0,0, 1,0, 0,0, 3,0, 3,0, 0,0, 0,0, 0,0, 0,1, 1,1, 0,0, 0,0, // 0xFE10
        0,0, 0,0, 0,0, // 0xFE20
    ];

    public static int StackPopCount(this ILOpCode opcode) => StackCount(opcode, pushOffset: 0);

    public static int StackPushCount(this ILOpCode opcode) => StackCount(opcode, pushOffset: 1);

    private static int StackCount(ILOpCode opcode, int pushOffset)
    {
        int code = (int)opcode;
        ReadOnlySpan<byte> table = code <= 0xFF ? StackBehavior1Byte : StackBehavior2Byte;
        byte b = table[(code & 0xFF) * 2 + pushOffset];
        return b == Variable ? -1 : b;
    }
}
