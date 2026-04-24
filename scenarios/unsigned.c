// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC unsigned.c
// LINK: link unsigned.obj /incremental:no /debug /entry:main /subsystem:console
//
// Tests unsigned-specific IL instructions that differ from their signed
// counterparts: div.un, rem.un, shr.un, bge.un.s, bgt.un.s, ble.un.s,
// blt.un.s, and bne.un.s.  Also confirms that MSVC encodes unsigned
// parameter / return types as uint32 in method signatures.

unsigned int udiv(unsigned int a, unsigned int b) { return a / b; }
unsigned int umod(unsigned int a, unsigned int b) { return a % b; }
unsigned int ushr(unsigned int a, int n) { return a >> n; }

int ult(unsigned int a, unsigned int b) { return a < b; }
int ule(unsigned int a, unsigned int b) { return a <= b; }
int ugt(unsigned int a, unsigned int b) { return a > b; }
int uge(unsigned int a, unsigned int b) { return a >= b; }

int main()
{
    return (int)udiv(100U, 7U) + (int)umod(100U, 7U)
         + (int)ushr(0xFFFFFFFFU, 1)
         + ult(3U, 5U) + uge(5U, 3U);
}
