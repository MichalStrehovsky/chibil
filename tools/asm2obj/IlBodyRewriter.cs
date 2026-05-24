using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Asm2Obj;

/// <summary>
/// Walks the IL byte stream of an input method body and re-emits it through a
/// <see cref="RelocatableInstructionEncoder"/>, rewriting every metadata-token
/// operand via a <see cref="TokenMap"/> and every UserString (ldstr) operand
/// via <see cref="TokenMap.MapUserString"/>.
///
/// Token slots are always exactly 4 bytes wide both before and after
/// rewriting, so relative branch operands remain valid — the IL stream is
/// copied verbatim except at metadata-token slots, which the rewriter
/// recognises through <see cref="ILOpcodeHelper"/>'s per-opcode table.
/// </summary>
public static class IlBodyRewriter
{
    private const ushort LdstrOpcode = 0x72;

    public static void Rewrite(
        BlobReader ilReader,
        TokenMap tokenMap,
        RelocatableInstructionEncoder encoder)
    {
        while (ilReader.RemainingBytes > 0)
        {
            int b = ilReader.ReadByte();
            ushort opcode;
            int opcodeBytes;
            if (b == 0xFE)
            {
                int b2 = ilReader.ReadByte();
                opcode = (ushort)(0x100 | b2);
                opcodeBytes = 2;
                encoder.OpCode((ILOpCode)((0xFE << 8) | b2));
            }
            else
            {
                opcode = (ushort)b;
                opcodeBytes = 1;
                encoder.OpCode((ILOpCode)b);
            }

            byte entry = ILOpcodeHelper.GetEntry(opcode);
            switch (entry)
            {
                case ILOpcodeHelper.Invalid:
                    throw new BadImageFormatException($"Invalid IL opcode 0x{opcode:X4}.");

                case ILOpcodeHelper.VariableSize:
                    // The only variable-size opcode is `switch` (0x45):
                    //   switch <uint32 n> <int32 target>{n}
                    {
                        uint n = ilReader.ReadUInt32();
                        encoder.CodeBuilder.WriteUInt32(n);
                        for (uint i = 0; i < n; i++)
                            encoder.CodeBuilder.WriteInt32(ilReader.ReadInt32());
                    }
                    break;

                case ILOpcodeHelper.Token:
                    {
                        int inputToken = ilReader.ReadInt32();
                        int outputToken = opcode == LdstrOpcode
                            ? tokenMap.MapUserStringToken(inputToken)
                            : tokenMap.MapToken(inputToken);
                        // Always go through encoder.Token so the slot is
                        // zeroed and a CLR-token relocation is recorded.
                        // link.exe needs the relocation for both metadata
                        // tokens and UserString (#US) tokens — MSVC /clr
                        // emits a CLR-token COFF symbol (class 107) named
                        // "70xxxxxx" for ldstr operands the same way it
                        // emits "06xxxxxx" / "0Axxxxxx" for MethodDef /
                        // MemberRef references.
                        encoder.Token(outputToken);
                    }
                    break;

                default:
                    // Fixed-size instruction: total size = `entry` bytes;
                    // copy the remaining (entry - opcodeBytes) operand bytes
                    // verbatim through the encoder.
                    {
                        int operandBytes = entry - opcodeBytes;
                        for (int i = 0; i < operandBytes; i++)
                            encoder.CodeBuilder.WriteByte(ilReader.ReadByte());
                    }
                    break;
            }
        }
    }
}
