// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from dotnet/runtime Internal.IL.ILOpcodeHelper.

using System;

namespace Asm2Obj;

/// <summary>
/// Per-opcode metadata used by IL body emission (MetadataCopier.PhaseD).
///
/// The table is indexed by the opcode as a <c>ushort</c>. 1-byte opcodes are
/// indexed by their 1-byte value; 2-byte (<c>0xFE</c>-prefix) opcodes are
/// remapped so that the prefix becomes bit 8, i.e. <c>0xFE xx</c> in the IL
/// stream is indexed as <c>0x01xx</c>.
///
/// Each entry is either:
/// <list type="bullet">
///   <item>a small integer = the total size (in bytes) of the instruction
///         including its opcode bytes — the rewriter copies that many bytes
///         verbatim;</item>
///   <item><see cref="Token"/> — the instruction is opcode-bytes + 4-byte
///         metadata token; the rewriter remaps the token (or, for
///         <c>ldstr</c>, the UserString) and emits a CLR-token relocation;</item>
///   <item><see cref="VariableSize"/> — the only one is <c>switch</c>, which
///         needs special handling;</item>
///   <item><see cref="Invalid"/> — reserved / undefined opcode.</item>
/// </list>
/// </summary>
internal static class ILOpcodeHelper
{
    public const byte Token = 0xFD;
    public const byte Invalid = 0xFE;
    public const byte VariableSize = 0xFF;

    public static byte GetEntry(ushort opcode) => s_opcodeEntries[opcode];

    private static readonly byte[] s_opcodeEntries = new byte[]
    {
        1,            // nop = 0x00
        1,            // break = 0x01
        1,            // ldarg.0 = 0x02
        1,            // ldarg.1 = 0x03
        1,            // ldarg.2 = 0x04
        1,            // ldarg.3 = 0x05
        1,            // ldloc.0 = 0x06
        1,            // ldloc.1 = 0x07
        1,            // ldloc.2 = 0x08
        1,            // ldloc.3 = 0x09
        1,            // stloc.0 = 0x0a
        1,            // stloc.1 = 0x0b
        1,            // stloc.2 = 0x0c
        1,            // stloc.3 = 0x0d
        2,            // ldarg.s = 0x0e
        2,            // ldarga.s = 0x0f
        2,            // starg.s = 0x10
        2,            // ldloc.s = 0x11
        2,            // ldloca.s = 0x12
        2,            // stloc.s = 0x13
        1,            // ldnull = 0x14
        1,            // ldc.i4.m1 = 0x15
        1,            // ldc.i4.0 = 0x16
        1,            // ldc.i4.1 = 0x17
        1,            // ldc.i4.2 = 0x18
        1,            // ldc.i4.3 = 0x19
        1,            // ldc.i4.4 = 0x1a
        1,            // ldc.i4.5 = 0x1b
        1,            // ldc.i4.6 = 0x1c
        1,            // ldc.i4.7 = 0x1d
        1,            // ldc.i4.8 = 0x1e
        2,            // ldc.i4.s = 0x1f
        5,            // ldc.i4 = 0x20
        9,            // ldc.i8 = 0x21
        5,            // ldc.r4 = 0x22
        9,            // ldc.r8 = 0x23
        Invalid,      // 0x24
        1,            // dup = 0x25
        1,            // pop = 0x26
        Token,        // jmp = 0x27
        Token,        // call = 0x28
        Token,        // calli = 0x29
        1,            // ret = 0x2a
        2,            // br.s = 0x2b
        2,            // brfalse.s = 0x2c
        2,            // brtrue.s = 0x2d
        2,            // beq.s = 0x2e
        2,            // bge.s = 0x2f
        2,            // bgt.s = 0x30
        2,            // ble.s = 0x31
        2,            // blt.s = 0x32
        2,            // bne.un.s = 0x33
        2,            // bge.un.s = 0x34
        2,            // bgt.un.s = 0x35
        2,            // ble.un.s = 0x36
        2,            // blt.un.s = 0x37
        5,            // br = 0x38
        5,            // brfalse = 0x39
        5,            // brtrue = 0x3a
        5,            // beq = 0x3b
        5,            // bge = 0x3c
        5,            // bgt = 0x3d
        5,            // ble = 0x3e
        5,            // blt = 0x3f
        5,            // bne.un = 0x40
        5,            // bge.un = 0x41
        5,            // bgt.un = 0x42
        5,            // ble.un = 0x43
        5,            // blt.un = 0x44
        VariableSize, // switch = 0x45
        1,            // ldind.i1 = 0x46
        1,            // ldind.u1 = 0x47
        1,            // ldind.i2 = 0x48
        1,            // ldind.u2 = 0x49
        1,            // ldind.i4 = 0x4a
        1,            // ldind.u4 = 0x4b
        1,            // ldind.i8 = 0x4c
        1,            // ldind.i = 0x4d
        1,            // ldind.r4 = 0x4e
        1,            // ldind.r8 = 0x4f
        1,            // ldind.ref = 0x50
        1,            // stind.ref = 0x51
        1,            // stind.i1 = 0x52
        1,            // stind.i2 = 0x53
        1,            // stind.i4 = 0x54
        1,            // stind.i8 = 0x55
        1,            // stind.r4 = 0x56
        1,            // stind.r8 = 0x57
        1,            // add = 0x58
        1,            // sub = 0x59
        1,            // mul = 0x5a
        1,            // div = 0x5b
        1,            // div.un = 0x5c
        1,            // rem = 0x5d
        1,            // rem.un = 0x5e
        1,            // and = 0x5f
        1,            // or = 0x60
        1,            // xor = 0x61
        1,            // shl = 0x62
        1,            // shr = 0x63
        1,            // shr.un = 0x64
        1,            // neg = 0x65
        1,            // not = 0x66
        1,            // conv.i1 = 0x67
        1,            // conv.i2 = 0x68
        1,            // conv.i4 = 0x69
        1,            // conv.i8 = 0x6a
        1,            // conv.r4 = 0x6b
        1,            // conv.r8 = 0x6c
        1,            // conv.u4 = 0x6d
        1,            // conv.u8 = 0x6e
        Token,        // callvirt = 0x6f
        Token,        // cpobj = 0x70
        Token,        // ldobj = 0x71
        Token,        // ldstr = 0x72 — UserString (rewriter special-cases)
        Token,        // newobj = 0x73
        Token,        // castclass = 0x74
        Token,        // isinst = 0x75
        1,            // conv.r.un = 0x76
        Invalid,      // 0x77
        Invalid,      // 0x78
        Token,        // unbox = 0x79
        1,            // throw = 0x7a
        Token,        // ldfld = 0x7b
        Token,        // ldflda = 0x7c
        Token,        // stfld = 0x7d
        Token,        // ldsfld = 0x7e
        Token,        // ldsflda = 0x7f
        Token,        // stsfld = 0x80
        Token,        // stobj = 0x81
        1,            // conv.ovf.i1.un = 0x82
        1,            // conv.ovf.i2.un = 0x83
        1,            // conv.ovf.i4.un = 0x84
        1,            // conv.ovf.i8.un = 0x85
        1,            // conv.ovf.u1.un = 0x86
        1,            // conv.ovf.u2.un = 0x87
        1,            // conv.ovf.u4.un = 0x88
        1,            // conv.ovf.u8.un = 0x89
        1,            // conv.ovf.i.un = 0x8a
        1,            // conv.ovf.u.un = 0x8b
        Token,        // box = 0x8c
        Token,        // newarr = 0x8d
        1,            // ldlen = 0x8e
        Token,        // ldelema = 0x8f
        1,            // ldelem.i1 = 0x90
        1,            // ldelem.u1 = 0x91
        1,            // ldelem.i2 = 0x92
        1,            // ldelem.u2 = 0x93
        1,            // ldelem.i4 = 0x94
        1,            // ldelem.u4 = 0x95
        1,            // ldelem.i8 = 0x96
        1,            // ldelem.i = 0x97
        1,            // ldelem.r4 = 0x98
        1,            // ldelem.r8 = 0x99
        1,            // ldelem.ref = 0x9a
        1,            // stelem.i = 0x9b
        1,            // stelem.i1 = 0x9c
        1,            // stelem.i2 = 0x9d
        1,            // stelem.i4 = 0x9e
        1,            // stelem.i8 = 0x9f
        1,            // stelem.r4 = 0xa0
        1,            // stelem.r8 = 0xa1
        1,            // stelem.ref = 0xa2
        Token,        // ldelem = 0xa3
        Token,        // stelem = 0xa4
        Token,        // unbox.any = 0xa5
        Invalid,      // 0xa6
        Invalid,      // 0xa7
        Invalid,      // 0xa8
        Invalid,      // 0xa9
        Invalid,      // 0xaa
        Invalid,      // 0xab
        Invalid,      // 0xac
        Invalid,      // 0xad
        Invalid,      // 0xae
        Invalid,      // 0xaf
        Invalid,      // 0xb0
        Invalid,      // 0xb1
        Invalid,      // 0xb2
        1,            // conv.ovf.i1 = 0xb3
        1,            // conv.ovf.u1 = 0xb4
        1,            // conv.ovf.i2 = 0xb5
        1,            // conv.ovf.u2 = 0xb6
        1,            // conv.ovf.i4 = 0xb7
        1,            // conv.ovf.u4 = 0xb8
        1,            // conv.ovf.i8 = 0xb9
        1,            // conv.ovf.u8 = 0xba
        Invalid,      // 0xbb
        Invalid,      // 0xbc
        Invalid,      // 0xbd
        Invalid,      // 0xbe
        Invalid,      // 0xbf
        Invalid,      // 0xc0
        Invalid,      // 0xc1
        Token,        // refanyval = 0xc2
        1,            // ckfinite = 0xc3
        Invalid,      // 0xc4
        Invalid,      // 0xc5
        Token,        // mkrefany = 0xc6
        Invalid,      // 0xc7
        Invalid,      // 0xc8
        Invalid,      // 0xc9
        Invalid,      // 0xca
        Invalid,      // 0xcb
        Invalid,      // 0xcc
        Invalid,      // 0xcd
        Invalid,      // 0xce
        Invalid,      // 0xcf
        Token,        // ldtoken = 0xd0
        1,            // conv.u2 = 0xd1
        1,            // conv.u1 = 0xd2
        1,            // conv.i = 0xd3
        1,            // conv.ovf.i = 0xd4
        1,            // conv.ovf.u = 0xd5
        1,            // add.ovf = 0xd6
        1,            // add.ovf.un = 0xd7
        1,            // mul.ovf = 0xd8
        1,            // mul.ovf.un = 0xd9
        1,            // sub.ovf = 0xda
        1,            // sub.ovf.un = 0xdb
        1,            // endfinally = 0xdc
        5,            // leave = 0xdd
        2,            // leave.s = 0xde
        1,            // stind.i = 0xdf
        1,            // conv.u = 0xe0
        Invalid,      // 0xe1
        Invalid,      // 0xe2
        Invalid,      // 0xe3
        Invalid,      // 0xe4
        Invalid,      // 0xe5
        Invalid,      // 0xe6
        Invalid,      // 0xe7
        Invalid,      // 0xe8
        Invalid,      // 0xe9
        Invalid,      // 0xea
        Invalid,      // 0xeb
        Invalid,      // 0xec
        Invalid,      // 0xed
        Invalid,      // 0xee
        Invalid,      // 0xef
        Invalid,      // 0xf0
        Invalid,      // 0xf1
        Invalid,      // 0xf2
        Invalid,      // 0xf3
        Invalid,      // 0xf4
        Invalid,      // 0xf5
        Invalid,      // 0xf6
        Invalid,      // 0xf7
        Invalid,      // 0xf8
        Invalid,      // 0xf9
        Invalid,      // 0xfa
        Invalid,      // 0xfb
        Invalid,      // 0xfc
        Invalid,      // 0xfd
        1,            // prefix1 (0xfe) — entry never consulted; the rewriter
                      //                  dispatches 2-byte opcodes itself
        Invalid,      // 0xff
        // ── 0xFE-prefix opcodes (indexed as 0x100 | low byte) ──
        2,            // arglist = 0x100
        2,            // ceq = 0x101
        2,            // cgt = 0x102
        2,            // cgt.un = 0x103
        2,            // clt = 0x104
        2,            // clt.un = 0x105
        Token,        // ldftn = 0x106
        Token,        // ldvirtftn = 0x107
        Invalid,      // 0x108
        4,            // ldarg = 0x109
        4,            // ldarga = 0x10a
        4,            // starg = 0x10b
        4,            // ldloc = 0x10c
        4,            // ldloca = 0x10d
        4,            // stloc = 0x10e
        2,            // localloc = 0x10f
        Invalid,      // 0x110
        2,            // endfilter = 0x111
        3,            // unaligned. = 0x112
        2,            // volatile. = 0x113
        2,            // tail. = 0x114
        Token,        // initobj = 0x115
        Token,        // constrained. = 0x116
        2,            // cpblk = 0x117
        2,            // initblk = 0x118
        3,            // no. = 0x119
        2,            // rethrow = 0x11a
        Invalid,      // 0x11b
        Token,        // sizeof = 0x11c
        2,            // refanytype = 0x11d
        2,            // readonly. = 0x11e
    };
}
