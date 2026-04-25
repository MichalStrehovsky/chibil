// COMPILE: cl /c /Z7 /Zl /d1clrNoPureCRT /clr:pure /BC self-ref-struct.c
// LINK: link self-ref-struct.obj /incremental:no /debug /entry:main /subsystem:console

struct Node {
    int val;
    struct Node* next;
};

int sum_list(struct Node* head)
{
    int sum = 0;
    while (head != 0)
    {
        sum = sum + head->val;
        head = head->next;
    }
    return sum;
}

int main()
{
    struct Node c = { 30, 0 };
    struct Node b = { 20, &c };
    struct Node a = { 10, &b };
    return sum_list(&a);
}
