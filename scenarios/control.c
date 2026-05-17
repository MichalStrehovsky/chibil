// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr /BC control.c
// LINK: link control.obj /incremental:no /debug /entry:main /subsystem:console mscoree.lib

int sum_loop(int n)
{
    int sum = 0;
    int i;
    if (n > 0)
    {
        for (i = 0; i < n; i = i + 1)
            sum = sum + i;
    }
    else
    {
        sum = -1;
    }
    return sum;
}

int count_while(int n)
{
    int count = 0;
    while (n > 0)
    {
        count = count + 1;
        n = n - 1;
    }
    return count;
}

int count_do(int n)
{
    int count = 0;
    do
    {
        count = count + 1;
    } while (count < n);
    return count;
}

int use_goto(int n)
{
    int result = 0;
loop:
    if (n <= 0)
        goto done;
    result = result + n;
    n = n - 1;
    goto loop;
done:
    return result;
}

int main()
{
    return sum_loop(5) + count_while(3) + count_do(4) + use_goto(3);
}
