// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC sparse-switch.c
// LINK: link sparse-switch.obj /incremental:no /debug /entry:main /subsystem:console
//
// Tests how MSVC compiles switch statements with sparse case values.
// Dense switches (consecutive 0..N) use the IL 'switch' instruction.
// Sparse switches (widely separated values) use a binary tree of
// compare-and-branch: bgt.s for pivot, beq.s for individual cases.
// The compiler creates balanced binary search trees over the case values.

int sparse_switch(int x)
{
    switch (x) {
    case 1:     return 10;
    case 100:   return 20;
    case 1000:  return 30;
    case 10000: return 40;
    default:    return -1;
    }
}

int dense_switch(int x)
{
    switch (x) {
    case 0: return 100;
    case 1: return 101;
    case 2: return 102;
    case 3: return 103;
    case 4: return 104;
    case 5: return 105;
    case 6: return 106;
    case 7: return 107;
    case 8: return 108;
    case 9: return 109;
    default: return -1;
    }
}

int main()
{
    return sparse_switch(100) + dense_switch(5);
}
