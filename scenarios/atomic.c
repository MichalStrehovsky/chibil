// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC atomic.c
// LINK: link atomic.obj /incremental:no /debug /entry:main /subsystem:console

long _InterlockedExchange(long volatile* target, long value);
long _InterlockedCompareExchange(long volatile* destination, long exchange, long comparand);
#pragma intrinsic(_InterlockedExchange)
#pragma intrinsic(_InterlockedCompareExchange)

int atomic_xchg(int volatile* p, int val)
{
    return _InterlockedExchange((long volatile*)p, val);
}

int atomic_cas(int volatile* p, int expected, int desired)
{
    return _InterlockedCompareExchange((long volatile*)p, desired, expected);
}

int main()
{
    int volatile v = 0;
    atomic_xchg(&v, 42);
    atomic_cas(&v, 42, 100);
    return v;
}
