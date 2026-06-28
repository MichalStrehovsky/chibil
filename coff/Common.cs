using System;

namespace Coff
{
    public enum CodeViewChecksumType : byte
    {
        None = 0,
        SHA256 = 3,
    }

    public enum CodeViewLanguage : byte
    {
        C = 0x00,
        Cpp = 0x01,
    }

    public enum CodeViewMachine : ushort
    {
        I386 = 0x0007,    // CV_CFL_PENTIUMIII — matches MSVC /clr:pure output
        Amd64 = 0x00D0,   // CV_CFL_X64
        Arm64 = 0x00F6,   // CV_CFL_ARM64
    }

    [Flags]
    public enum CodeViewCompileFlags : uint
    {
        None = 0,
        EditAndContinue = 0x0100,
        NoDebugInfo = 0x0200,
        LTCG = 0x0400,
        NoDataAlign = 0x0800,
        ManagedPresent = 0x1000,
        SecurityChecks = 0x2000,
        HotPatch = 0x4000,
        CVTCIL = 0x8000,
        MSILModule = 0x10000,
    }

    [Flags]
    public enum ObjectFeatures : ushort
    {
        None,
        PureMsil = 2,
        SafeMsil = 4,
    }

    // CodeView symbol record kinds (SYM_ENUM_e in cvinfo.h). Only the records
    // actually emitted by the writer are listed here.
    public enum CodeViewSymbolKind : ushort
    {
        End = 0x0006,        // S_END
        FrameProc = 0x1012,  // S_FRAMEPROC
        ObjName = 0x1101,    // S_OBJNAME
        Block32 = 0x1103,    // S_BLOCK32
        ManSlot = 0x1120,    // S_MANSLOT
        GManProc = 0x112A,   // S_GMANPROC
        Compile3 = 0x113C,   // S_COMPILE3
        ProcIdEnd = 0x114F,  // S_PROC_ID_END
    }

    // CodeView debug subsection kinds (DEBUG_S_SUBSECTION_TYPE in cvinfo.h).
    public enum CodeViewSubsectionKind : uint
    {
        Symbols = 0xF1,       // DEBUG_S_SYMBOLS
        Lines = 0xF2,         // DEBUG_S_LINES
        StringTable = 0xF3,   // DEBUG_S_STRINGTABLE
        FileChecksums = 0xF4, // DEBUG_S_FILECHKSMS
    }

    // Flags for the S_FRAMEPROC record (FRAMEPROCSYM flags bitfield in cvinfo.h).
    // Only the bits MSVC /clr:pure emits are listed here.
    [Flags]
    public enum CodeViewFrameProcFlags : uint
    {
        None = 0,
        AsyncEH = 0x00000200,  // fAsyncEH: function compiled with /EHa
        OptSpeed = 0x00100000, // fOptSpeed: optimized for speed
    }

    public static class CodeView
    {
        // CV_SIGNATURE_C13: the debug-information format version written at the
        // start of each .debug$S section.
        public const uint SignatureC13 = 4;

        // CV_Line_t.fStatement: the high bit of a line-number entry's flags marks
        // a statement (as opposed to an expression) line number.
        public const uint LineIsStatement = 0x80000000;
    }

    public enum CoffSymbolType : short
    {
        Null = 0x00,
        Function = 0x20,
    }

    public enum CoffSymbolStorageClass : byte
    {
        External = 2,
        Static = 3,
        ClrToken = 107,
    }

    // Relocation type values as defined in winnt.h. Only the values actually
    // used by the emitter are listed here.
    public enum ImageRelocation : ushort
    {
        I386_DIR32 = 0x0006,
        I386_DIR32NB = 0x0007,
        I386_SECTION = 0x000A,
        I386_SECREL = 0x000B,
        I386_TOKEN = 0x000C,

        Amd64_ADDR64 = 0x0001,
        Amd64_ADDR32NB = 0x0003,
        Amd64_REL32 = 0x0004,
        Amd64_SECTION = 0x000A,
        Amd64_SECREL = 0x000B,
        Amd64_TOKEN = 0x000D,

        Arm64_ADDR32NB = 0x0002,
        Arm64_PAGEBASE_REL21 = 0x0004,
        Arm64_PAGEOFFSET_12L = 0x0007,
        Arm64_SECREL = 0x0008,
        Arm64_TOKEN = 0x000C,
        Arm64_SECTION = 0x000D,
        Arm64_ADDR64 = 0x000E,
    }

    public enum CoffComdatSelection : byte
    {
        NoDuplicates = 1,
        Any = 2,
        SameSize = 3,
        ExactMatch = 4,
        Associative = 5,
        Largest = 6,
    }
}
