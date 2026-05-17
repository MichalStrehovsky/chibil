// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC return-struct.c
// LINK: link return-struct.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

typedef struct _Point { int x; int y; } Point;

Point make_point(int x, int y)
{
    Point p;
    p.x = x;
    p.y = y;
    return p;
}

int main()
{
    Point p = make_point(10, 20);
    return p.x + p.y;
}
