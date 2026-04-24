// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC float.c
// LINK: link float.obj /incremental:no /debug /entry:main /subsystem:console

float float_arith(float a, float b)
{
    float sum = a + b;
    float diff = a - b;
    float prod = a * b;
    float quot = a / b;
    return sum + diff + prod + quot;
}

double double_arith(double a, double b)
{
    double sum = a + b;
    double diff = a - b;
    double prod = a * b;
    double quot = a / b;
    return sum + diff + prod + quot;
}

int float_compare(float a, float b)
{
    int eq = (a == b);
    int lt = (a < b);
    int le = (a <= b);
    return eq + lt + le;
}

int main()
{
    float f = float_arith(3.5f, 1.5f);
    double d = double_arith(3.5, 1.5);
    int c = float_compare(1.0f, 2.0f);
    return (int)f + (int)d + c;
}
