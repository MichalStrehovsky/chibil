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
