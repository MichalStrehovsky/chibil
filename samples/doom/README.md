# PureDOOM.NET

Builds PureDOOM with chibil. PureDOOM is the version of DOOM that only needs C: not even libc is needed. It's the DOOM you choose if you want to make DOOM run on a microwave.

doom.c includes the PureDOOM.h header and glues it with the PAL (platform abstraction layer). PureDOOM.h comes from the subrepo, so make sure you checkout recursively.

pal.c is the platform abstraction layer. It lets DOOM talk to the world (read files, push pixels). The current pal.c only works on Windows. The rules are the same: cannot include Windows.h, we likely wouldn't be able to parse it (haven't tried).

doom.c and pal.c also support two harness ifdefs:

- `REPRODUCIBLE_HARNESS` runs DOOM without creating a window, drives it with deterministic timing, and saves changed frames as BMP files.
- `VALIDATE_CHECKSUM` is used together with `REPRODUCIBLE_HARNESS`; instead of writing BMP files, it hashes the changed frame pixels and exits non-zero if the cumulative checksum does not match the known-good value.

The windowed PAL (i.e. compiled without `REPRODUCIBLE_HARNESS`) need to be compiled as native, into a DLL. You can also compile it to IL, but it won't work. I haven't investigated, but one guaranteed reason why it won't work is that it needs a reverse p/invoke for the Windows WndProc and we can't do that yet. Chibil can do normal p/invoke just fine, but not reverse.

To build with chibil, run the build.cmd batch script from a VS 2025 x64 native tools command prompt. See the batch file for samples.
