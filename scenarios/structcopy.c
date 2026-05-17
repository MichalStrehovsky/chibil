// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC structcopy.c
// LINK: link structcopy.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib
//
// Tests struct assignment which generates the cpblk IL instruction.
// Also shows struct-valued locals with stloc/ldloc for value types,
// and member access via ldloca + constant offset + stind/ldind.

struct Small { int x; int y; };
struct Big { int data[16]; };

void copy_small(struct Small *dst, struct Small *src) { *dst = *src; }
void copy_big(struct Big *dst, struct Big *src) { *dst = *src; }

struct Small make_small(int a, int b)
{
    struct Small s;
    s.x = a;
    s.y = b;
    return s;
}

void assign_local(void)
{
    struct Small a;
    struct Small b;
    a.x = 1;
    a.y = 2;
    b = a;
}

int main()
{
    struct Small s1;
    struct Small s2;
    s1 = make_small(10, 20);
    copy_small(&s2, &s1);
    return s2.x + s2.y;
}
