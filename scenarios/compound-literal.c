// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC compound-literal.c
// LINK: link compound-literal.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

typedef struct _Point { int x; int y; } Point;

int sum_point(Point* p) { return p->x + p->y; }

int main()
{
    Point p = (Point){10, 20};
    return sum_point(&p);
}
