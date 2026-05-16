using Xunit;

namespace Chibil.Tests;

/// <summary>
/// Comprehensive tests for MSVC ↔ chibil name mangling compatibility.
/// Each test defines a function in chibil and consumes it from MSVC (or vice
/// versa), exercising a specific area of the MSVC decorated-name grammar.
/// The function body is always <c>return 42;</c> — we're testing linkage only.
///
/// Tests that fail due to known mangling bugs are marked with
/// <c>[Fact(Skip = "BUG: ...")]</c>.
/// </summary>
public class ManglingInteropTests : ChibiTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  1. Primitive types — single-parameter smoke tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prim_Int()
    {
        Compile("int prim_int(int a) { return 42; }")
        .MsvcCompile("int prim_int(int); int main(void) { return prim_int(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Char()
    {
        Compile("int prim_char(char a) { return 42; }")
        .MsvcCompile("int prim_char(char); int main(void) { return prim_char('x'); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_SignedChar()
    {
        Compile("int prim_schar(signed char a) { return 42; }")
        .MsvcCompile("int prim_schar(signed char); int main(void) { return prim_schar(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_UnsignedChar()
    {
        Compile("int prim_uchar(unsigned char a) { return 42; }")
        .MsvcCompile("int prim_uchar(unsigned char); int main(void) { return prim_uchar(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Short()
    {
        Compile("int prim_short(short a) { return 42; }")
        .MsvcCompile("int prim_short(short); int main(void) { return prim_short(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_UnsignedShort()
    {
        Compile("int prim_ushort(unsigned short a) { return 42; }")
        .MsvcCompile("int prim_ushort(unsigned short); int main(void) { return prim_ushort(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_UnsignedInt()
    {
        Compile("int prim_uint(unsigned int a) { return 42; }")
        .MsvcCompile("int prim_uint(unsigned int); int main(void) { return prim_uint(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Long()
    {
        Compile("int prim_long(long a) { return 42; }")
        .MsvcCompile("int prim_long(long); int main(void) { return prim_long(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_UnsignedLong()
    {
        Compile("int prim_ulong(unsigned long a) { return 42; }")
        .MsvcCompile("int prim_ulong(unsigned long); int main(void) { return prim_ulong(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_LongLong()
    {
        Compile("int prim_llong(long long a) { return 42; }")
        .MsvcCompile("int prim_llong(long long); int main(void) { return prim_llong(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_UnsignedLongLong()
    {
        Compile("int prim_ullong(unsigned long long a) { return 42; }")
        .MsvcCompile("int prim_ullong(unsigned long long); int main(void) { return prim_ullong(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Float()
    {
        Compile("int prim_float(float a) { return 42; }")
        .MsvcCompile("int prim_float(float); int main(void) { return prim_float(1.0f); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Double()
    {
        Compile("int prim_double(double a) { return 42; }")
        .MsvcCompile("int prim_double(double); int main(void) { return prim_double(1.0); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_Void()
    {
        // void-returning function — main calls it then returns 42
        Compile("void prim_void(int a) { }")
        .MsvcCompile("void prim_void(int); int main(void) { prim_void(1); return 42; }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_MixedNonRepeating()
    {
        // Multiple distinct primitives — no backrefs needed (all 1-char manglings)
        Compile("int mixed_prim(int a, char b, double c) { return 42; }")
        .MsvcCompile("int mixed_prim(int, char, double); int main(void) { return mixed_prim(1, 'x', 1.0); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  2. Backreferences — repeated multi-char parameter types
    //     BUG H-2: chibil doesn't emit backreference digits (0-9)
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — _J repeated as _J_J instead of _J0")]
    public void Backref_TwoLongLongs()
    {
        // long long = _J (2 chars) → first gets slot 0, second should be '0'
        Compile("int br_llong2(long long a, long long b) { return 42; }")
        .MsvcCompile("int br_llong2(long long, long long); int main(void) { return br_llong2(1, 2); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — PEAH repeated instead of PEAH0")]
    public void Backref_TwoIntPtrs()
    {
        // int* = PEAH (4 chars on x64) → backref
        Compile("int br_intp2(int *a, int *b) { return 42; }")
        .MsvcCompile("int br_intp2(int*, int*); int main(void) { int x; return br_intp2(&x, &x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_IntThenTwoIntPtrs()
    {
        // int (H, 1 char → no slot), int* (PEAH → slot 0), int* (→ '0')
        Compile("int br_h_pp(int a, int *b, int *c) { return 42; }")
        .MsvcCompile("int br_h_pp(int, int*, int*); int main(void) { int x; return br_h_pp(1, &x, &x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_IntPtrDoublePtrIntPtr()
    {
        // int* → slot 0 (PEAH), double* → slot 1 (PEAN), int* → '0'
        Compile("int br_pdp(int *a, double *b, int *c) { return 42; }")
        .MsvcCompile("int br_pdp(int*, double*, int*); int main(void) { int x; double d; return br_pdp(&x, &d, &x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_TwoVoidPtrs()
    {
        // void* = PEAX (4 chars) → backref
        Compile("int br_vp2(void *a, void *b) { return 42; }")
        .MsvcCompile("int br_vp2(void*, void*); int main(void) { int x; return br_vp2(&x, &x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_TwoUnsignedLongLongs()
    {
        // unsigned long long = _K (2 chars) → backref
        Compile("int br_ullong2(unsigned long long a, unsigned long long b) { return 42; }")
        .MsvcCompile("int br_ullong2(unsigned long long, unsigned long long); int main(void) { return br_ullong2(1, 2); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_TwoConstCharPtrs()
    {
        // const char* = PEBD (4 chars) → backref
        Compile("int br_ccp2(const char *a, const char *b) { return 42; }")
        .MsvcCompile("""
            int br_ccp2(const char*, const char*);
            int main(void) { return br_ccp2("a", "b"); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_ThreeDistinctMultiCharTypes()
    {
        // int* → slot 0, double* → slot 1, long long → slot 2
        // then int* again → '0', double* again → '1'
        Compile("int br_3types(int *a, double *b, long long c, int *d, double *e) { return 42; }")
        .MsvcCompile("""
            int br_3types(int*, double*, long long, int*, double*);
            int main(void) { int x; double d; return br_3types(&x, &d, 1, &x, &d); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Backref_InterleavedWithSingleCharTypes()
    {
        // int* (slot 0), int (no slot), int* (→ '0'), double (no slot)
        Compile("int br_interleave(int *a, int b, int *c, double d) { return 42; }")
        .MsvcCompile("int br_interleave(int*, int, int*, double); int main(void) { int x; return br_interleave(&x, 1, &x, 2.0); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — _N is 2 chars and gets a slot")]
    public void Backref_TwoBools()
    {
        // _Bool = _N (2 chars) → gets backref slot
        Compile("int br_bool2(_Bool a, _Bool b) { return 42; }")
        .MsvcCompile("int br_bool2(_Bool, _Bool); int main(void) { return br_bool2(1, 0); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  3. Struct and union parameters
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Struct_ByValue()
    {
        Compile("""
            struct Point { int x; int y; };
            int st_byval(struct Point p) { return 42; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            int st_byval(struct Point);
            int main(void) { struct Point p = {1, 2}; return st_byval(p); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Struct_Pointer()
    {
        Compile("""
            struct Point { int x; int y; };
            int st_ptr(struct Point *p) { return 42; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            int st_ptr(struct Point*);
            int main(void) { struct Point p = {1, 2}; return st_ptr(&p); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — repeated struct UPoint@@ should use backref")]
    public void Struct_RepeatedByValue()
    {
        // struct Point → UPoint@@ (8 chars) → backref
        Compile("""
            struct Point { int x; int y; };
            int st_rep(struct Point a, struct Point b) { return 42; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            int st_rep(struct Point, struct Point);
            int main(void) { struct Point p = {1, 2}; return st_rep(p, p); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-1: chibil mangles union as 'U' instead of 'T'")]
    public void Union_ByValue()
    {
        Compile("""
            union Data { int i; float f; };
            int un_byval(union Data d) { return 42; }
            """)
        .MsvcCompile("""
            union Data { int i; float f; };
            int un_byval(union Data);
            int main(void) { union Data d; d.i = 1; return un_byval(d); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-1: chibil mangles union as 'U' instead of 'T'")]
    public void Union_Pointer()
    {
        Compile("""
            union Data { int i; float f; };
            int un_ptr(union Data *d) { return 42; }
            """)
        .MsvcCompile("""
            union Data { int i; float f; };
            int un_ptr(union Data*);
            int main(void) { union Data d; d.i = 1; return un_ptr(&d); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Struct_AndPrimitive()
    {
        // struct + primitives — no backrefs if struct appears once
        Compile("""
            struct Pair { int a; int b; };
            int st_prim(struct Pair p, int x) { return 42; }
            """)
        .MsvcCompile("""
            struct Pair { int a; int b; };
            int st_prim(struct Pair, int);
            int main(void) { struct Pair p = {1, 2}; return st_prim(p, 3); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  4. Enum parameters
    //     BUG: chibil mangles enums as H/I (int) instead of W4name@@
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: chibil mangles enum as H (int) instead of W4Color@@")]
    public void Enum_SingleParam()
    {
        Compile("""
            enum Color { RED, GREEN, BLUE };
            int en_single(enum Color c) { return 42; }
            """)
        .MsvcCompile("""
            enum Color { RED, GREEN, BLUE };
            int en_single(enum Color);
            int main(void) { return en_single(RED); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil mangles enum as H (int) instead of W4Color@@ + BUG H-2 backreferences")]
    public void Enum_Repeated()
    {
        // enum Color → W4Color@@ (9 chars) → backref
        Compile("""
            enum Color { RED, GREEN, BLUE };
            int en_rep(enum Color a, enum Color b) { return 42; }
            """)
        .MsvcCompile("""
            enum Color { RED, GREEN, BLUE };
            int en_rep(enum Color, enum Color);
            int main(void) { return en_rep(RED, GREEN); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil mangles enum as H (int) instead of W4Color@@")]
    public void Enum_WithPrimitive()
    {
        Compile("""
            enum Color { RED, GREEN, BLUE };
            int en_prim(enum Color c, int x) { return 42; }
            """)
        .MsvcCompile("""
            enum Color { RED, GREEN, BLUE };
            int en_prim(enum Color, int);
            int main(void) { return en_prim(RED, 1); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: void params produce X@Z instead of XZ in mangled name")]
    public void VoidParams_IntReturn()
    {
        // Simplest case of void params bug
        // MSVC: ?vp_int@@$$J0YAHXZ  (XZ)
        // chibil: ?vp_int@@$$J0YAHX@Z  (X@Z — wrong)
        Compile("int vp_int(void) { return 42; }")
        .MsvcCompile("int vp_int(void); int main(void) { return vp_int(); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: void params produce X@Z instead of XZ in mangled name")]
    public void VoidParams_VoidReturn()
    {
        // void return + void params
        // MSVC: ?vp_void@@$$J0YAXXZ  (XZ)
        // chibil: ?vp_void@@$$J0YAXX@Z  (X@Z — wrong)
        Compile("void vp_void(void) { }")
        .MsvcCompile("void vp_void(void); int main(void) { vp_void(); return 42; }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  5. Pointer variations
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ptr_Int()
    {
        Compile("int ptr_int(int *p) { return 42; }")
        .MsvcCompile("int ptr_int(int*); int main(void) { int x; return ptr_int(&x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_ConstInt()
    {
        Compile("int ptr_cint(const int *p) { return 42; }")
        .MsvcCompile("int ptr_cint(const int*); int main(void) { int x = 1; return ptr_cint(&x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_IntPtrPtr()
    {
        Compile("int ptr_pp(int **p) { return 42; }")
        .MsvcCompile("int ptr_pp(int**); int main(void) { int x; int *px = &x; return ptr_pp(&px); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_ConstChar()
    {
        Compile("int ptr_cc(const char *s) { return 42; }")
        .MsvcCompile("""
            int ptr_cc(const char*);
            int main(void) { return ptr_cc("hello"); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_CharPtrPtr()
    {
        Compile("int ptr_cpp(char **p) { return 42; }")
        .MsvcCompile("""
            int ptr_cpp(char**);
            int main(void) { char *s = "a"; return ptr_cpp(&s); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_Void()
    {
        Compile("int ptr_void(void *p) { return 42; }")
        .MsvcCompile("int ptr_void(void*); int main(void) { int x; return ptr_void(&x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: void params produce X@Z instead of XZ in mangled name")]
    public void Ptr_VoidReturn()
    {
        // void* return type with void params
        // MSVC: ?ptr_vret@@$$J0YAPEAXXZ  (void params = XZ)
        // chibil: ?ptr_vret@@$$J0YAPEAXX@Z  (void params = X@Z — wrong)
        Compile("""
            int dummy;
            void *ptr_vret(void) { return &dummy; }
            """)
        .MsvcCompile("""
            void *ptr_vret(void);
            int main(void) { return ptr_vret() ? 42 : 0; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  6. Function pointer parameters
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: function pointer parameter MSIL signature metadata inconsistent with MSVC")]
    public void FuncPtr_Simple()
    {
        // int (*fn)(int) as parameter — mangled names match but MSIL metadata differs
        Compile("int fp_simple(int (*fn)(int)) { return 42; }")
        .MsvcCompile("""
            int fp_simple(int (*)(int));
            int main(void) { return fp_simple(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: function pointer parameter MSIL signature metadata inconsistent with MSVC")]
    public void FuncPtr_WithExtraParams()
    {
        // int (*fn)(int, int) + int + int — mangled names match but MSIL metadata differs
        Compile("int fp_extra(int (*fn)(int, int), int x, int y) { return 42; }")
        .MsvcCompile("""
            int fp_extra(int (*)(int, int), int, int);
            int main(void) { return fp_extra(0, 1, 2); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — repeated function pointer type needs backref")]
    public void FuncPtr_Repeated()
    {
        // Two identical function pointer params → backref
        Compile("int fp_rep(int (*a)(int), int (*b)(int)) { return 42; }")
        .MsvcCompile("""
            int fp_rep(int (*)(int), int (*)(int));
            int main(void) { return fp_rep(0, 0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: function pointer parameter MSIL signature metadata inconsistent with MSVC")]
    public void FuncPtr_VoidReturn()
    {
        // void (*fn)(int) — function pointer with void return
        // Mangled names match but MSIL metadata differs
        Compile("int fp_vr(void (*fn)(int)) { return 42; }")
        .MsvcCompile("""
            int fp_vr(void (*)(int));
            int main(void) { return fp_vr(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void FuncPtr_NoParams()
    {
        // int (*fn)(void) — function pointer with void params
        Compile("int fp_np(int (*fn)(void)) { return 42; }")
        .MsvcCompile("""
            int fp_np(int (*)(void));
            int main(void) { return fp_np(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  7. Array parameter decay
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Array_UnsizedDecay()
    {
        // int arr[] decays to int* in parameter — should link as pointer
        Compile("int arr_unsized(int arr[]) { return 42; }")
        .MsvcCompile("""
            int arr_unsized(int[]);
            int main(void) { int a[] = {1, 2}; return arr_unsized(a); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Array_SizedDecay()
    {
        // int arr[10] decays to int* — size lost
        Compile("int arr_sized(int arr[10]) { return 42; }")
        .MsvcCompile("""
            int arr_sized(int[10]);
            int main(void) { int a[10] = {0}; return arr_sized(a); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  8. Mixed/complex signatures
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Mixed_StructAndPointer()
    {
        // struct + pointer — no repeated types, no backrefs
        Compile("""
            struct Vec2 { int x; int y; };
            int mx_sp(struct Vec2 v, double *d) { return 42; }
            """)
        .MsvcCompile("""
            struct Vec2 { int x; int y; };
            int mx_sp(struct Vec2, double*);
            int main(void) { struct Vec2 v = {1, 2}; double d = 1.0; return mx_sp(v, &d); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Mixed_RepeatedPtrWithPrimitives()
    {
        // int* (slot 0), int (no slot), int* (→ '0'), double (no slot)
        Compile("int mx_rpp(int *a, int b, int *c, double d) { return 42; }")
        .MsvcCompile("""
            int mx_rpp(int*, int, int*, double);
            int main(void) { int x; return mx_rpp(&x, 1, &x, 2.0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Mixed_ManyDistinctTypes()
    {
        // Test with many distinct multi-char types to exercise slot allocation
        // int* (slot 0), double* (slot 1), long long (slot 2), void* (slot 3),
        // const char* (slot 4), then repeat int* (→ '0')
        Compile("""
            int mx_many(int *a, double *b, long long c, void *d, const char *e, int *f) { return 42; }
            """)
        .MsvcCompile("""
            int mx_many(int*, double*, long long, void*, const char*, int*);
            int main(void) { int x; double d; return mx_many(&x, &d, 1, &x, "s", &x); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences")]
    public void Mixed_StructPtrAndIntPtr()
    {
        // struct* (slot 0), int* (slot 1), struct* (→ '0')
        Compile("""
            struct Rec { int v; };
            int mx_sip(struct Rec *a, int *b, struct Rec *c) { return 42; }
            """)
        .MsvcCompile("""
            struct Rec { int v; };
            int mx_sip(struct Rec*, int*, struct Rec*);
            int main(void) { struct Rec r = {1}; int x; return mx_sip(&r, &x, &r); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  9. Calling convention (__clrcall)
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — __clrcall + repeated type")]
    public void ClrCall_WithBackref()
    {
        // __clrcall with repeated long long — tests clrcall mangling (M) + backref
        Compile("int __clrcall cc_br(long long a, long long b) { return 42; }")
        .MsvcCompile("""
            int __clrcall cc_br(long long, long long);
            int main(void) { return cc_br(1, 2); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ClrCall_MixedPrimitives()
    {
        // __clrcall with non-repeating primitives — should pass
        Compile("int __clrcall cc_prim(int a, double b) { return 42; }")
        .MsvcCompile("""
            int __clrcall cc_prim(int, double);
            int main(void) { return cc_prim(1, 2.0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  10. Return type variations
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ret_LongLong()
    {
        Compile("long long ret_ll(int a) { return 42; }")
        .MsvcCompile("""
            long long ret_ll(int);
            int main(void) { return (int)ret_ll(1); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ret_Struct()
    {
        // struct return uses ?AU prefix in mangled name
        Compile("""
            struct Point { int x; int y; };
            struct Point ret_st(int a, int b) { struct Point p; p.x = a; p.y = b; return p; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            struct Point ret_st(int, int);
            int main(void) { struct Point p = ret_st(21, 21); return p.x + p.y; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: void params produce X@Z instead of XZ in mangled name")]
    public void Ret_Pointer()
    {
        // MSVC: ?ret_ptr@@$$J0YAPEAHXZ  (void params = XZ)
        // chibil: ?ret_ptr@@$$J0YAPEAHX@Z  (void params = X@Z — wrong)
        Compile("""
            int g_val = 42;
            int *ret_ptr(void) { return &g_val; }
            """)
        .MsvcCompile("""
            int *ret_ptr(void);
            int main(void) { return *ret_ptr(); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences — return type does not create slot but params do")]
    public void Ret_StructWithRepeatedStructParam()
    {
        // Return type struct Point doesn't create a backref slot,
        // but param struct Point does → the second struct Point param uses backref
        Compile("""
            struct Point { int x; int y; };
            struct Point ret_st_rep(struct Point a, struct Point b) {
                struct Point r; r.x = 42; r.y = 0; return r;
            }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            struct Point ret_st_rep(struct Point, struct Point);
            int main(void) { struct Point p = {1, 2}; struct Point r = ret_st_rep(p, p); return r.x; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  11. MSVC-define, chibil-consume (reverse direction)
    //      Tests that chibil can correctly declare and call MSVC functions
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG H-2: chibil doesn't emit backreferences in extern declarations")]
    public void Reverse_TwoIntPtrs()
    {
        // MSVC defines, chibil declares and calls — tests extern mangling
        MsvcCompile("int rev_ip2(int *a, int *b) { return 42; }")
        .Compile("""
            int rev_ip2(int*, int*);
            int main(void) { int x; return rev_ip2(&x, &x); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil mangles enum as H instead of W4Color@@")]
    public void Reverse_Enum()
    {
        MsvcCompile("""
            enum Color { RED, GREEN, BLUE };
            int rev_enum(enum Color c) { return 42; }
            """)
        .Compile("""
            enum Color { RED, GREEN, BLUE };
            int rev_enum(enum Color);
            int main(void) { return rev_enum(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG H-1: chibil mangles union as 'U' instead of 'T'")]
    public void Reverse_Union()
    {
        MsvcCompile("""
            union Blob { int i; float f; };
            int rev_union(union Blob b) { return 42; }
            """)
        .Compile("""
            union Blob { int i; float f; };
            int rev_union(union Blob);
            int main(void) { union Blob b; b.i = 1; return rev_union(b); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  12. Global/extern data symbol linkage
    //      Data symbols use bare names (not function-style decorated
    //      names) under /clr /BC. These tests verify cross-TU global
    //      variable linkage.
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: chibil data COFF symbols not resolvable cross-TU — unresolved external symbol")]
    public void Data_ChibiDefine_MsvcConsume()
    {
        // chibil defines a global, MSVC reads it
        Compile("""
            int g_answer = 42;
            """)
        .MsvcCompile("""
            extern int g_answer;
            int main(void) { return g_answer; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Data_MsvcDefine_ChibiConsume()
    {
        // MSVC defines a global, chibil reads it
        MsvcCompile("int g_msvc_val = 42;")
        .Compile("""
            extern int g_msvc_val;
            int main(void) { return g_msvc_val; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil data COFF symbols not resolvable cross-TU — unresolved external symbol")]
    public void Data_PointerGlobal()
    {
        // Pointer global defined by chibil, consumed by MSVC
        Compile("""
            int g_val = 42;
            int *g_ptr = &g_val;
            """)
        .MsvcCompile("""
            extern int *g_ptr;
            int main(void) { return *g_ptr; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil data COFF symbols not resolvable cross-TU — unresolved external symbol")]
    public void Data_StructGlobal()
    {
        // Struct global defined by chibil, consumed by MSVC
        Compile("""
            struct Pair { int a; int b; };
            struct Pair g_pair = { 20, 22 };
            """)
        .MsvcCompile("""
            struct Pair { int a; int b; };
            extern struct Pair g_pair;
            int main(void) { return g_pair.a + g_pair.b; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  13. Typedef transparency
    //      Typedefs must be transparent to mangling — typedef'd types
    //      must produce the same decorated name as the underlying type.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Typedef_PrimitiveAlias()
    {
        // typedef int MyInt — must mangle identically to int (H)
        Compile("""
            typedef int MyInt;
            int td_prim(MyInt a) { return 42; }
            """)
        .MsvcCompile("""
            int td_prim(int);
            int main(void) { return td_prim(1); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Typedef_PointerAlias()
    {
        // typedef int *IntPtr — must mangle identically to int*
        Compile("""
            typedef int *IntPtr;
            int td_ptr(IntPtr p) { return 42; }
            """)
        .MsvcCompile("""
            int td_ptr(int*);
            int main(void) { return td_ptr(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Typedef_NamedStruct()
    {
        // typedef struct Point Point — must match struct Point mangling
        Compile("""
            typedef struct Point { int x; int y; } Point;
            int td_st(Point p) { return 42; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            int td_st(struct Point);
            int main(void) { struct Point p = {1, 2}; return td_st(p); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Typedef_AnonymousStruct()
    {
        // typedef struct { int x; } Foo — MSVC synthesizes tag name "Foo"
        // Both sides must agree on the tag used in mangling
        Compile("""
            typedef struct { int x; int y; } Foo;
            int td_anon(Foo f) { return 42; }
            """)
        .MsvcCompile("""
            typedef struct { int x; int y; } Foo;
            int td_anon(Foo);
            int main(void) { Foo f = {1, 2}; return td_anon(f); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  14. Additional primitive types: _Bool, long double
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prim_Bool()
    {
        // _Bool = _N (2 chars) — standalone test independent of backref
        Compile("int prim_bool(_Bool a) { return 42; }")
        .MsvcCompile("int prim_bool(_Bool); int main(void) { return prim_bool(1); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Prim_LongDouble()
    {
        // long double = O in mangling (MSVC treats as 64-bit double)
        Compile("int prim_ldbl(long double a) { return 42; }")
        .MsvcCompile("int prim_ldbl(long double); int main(void) { return prim_ldbl(1.0L); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  15. Char-type pointer variations
    //      char*, signed char*, unsigned char* produce distinct
    //      mangling codes (D/C/E) and different MSIL modifiers.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ptr_SignedChar()
    {
        // signed char* → PEAC on x64 (distinct from char* → PEAD)
        Compile("int ptr_sc(signed char *p) { return 42; }")
        .MsvcCompile("int ptr_sc(signed char*); int main(void) { signed char c = 1; return ptr_sc(&c); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_UnsignedChar()
    {
        // unsigned char* → PEAE on x64 (distinct from char* → PEAD)
        Compile("int ptr_uc(unsigned char *p) { return 42; }")
        .MsvcCompile("int ptr_uc(unsigned char*); int main(void) { unsigned char c = 1; return ptr_uc(&c); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_PlainChar()
    {
        // char* → PEAD on x64 (with IsSignUnspecifiedByte MSIL modifier)
        Compile("int ptr_char(char *p) { return 42; }")
        .MsvcCompile("""
            int ptr_char(char*);
            int main(void) { char c = 'x'; return ptr_char(&c); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  16. Const/volatile pointer qualifiers on the pointer itself
    //      P = unqualified, Q = const ptr, R = volatile ptr, S = const volatile ptr
    //      Note: MSVC drops top-level qualifiers on function parameters,
    //      so int * const p mangles the same as int * p in a param list.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ptr_VolatilePointee()
    {
        // volatile int* → PEC on x64
        Compile("int ptr_vi(volatile int *p) { return 42; }")
        .MsvcCompile("int ptr_vi(volatile int*); int main(void) { volatile int x = 1; return ptr_vi(&x); }")
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ptr_ConstVolatilePointee()
    {
        // const volatile int* → PED on x64
        Compile("int ptr_cvi(const volatile int *p) { return 42; }")
        .MsvcCompile("""
            int ptr_cvi(const volatile int*);
            int main(void) { volatile int x = 1; return ptr_cvi(&x); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  17. Multi-level pointer indirection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ptr_TripleIndirection()
    {
        // int*** — three levels of pointer
        Compile("int ptr_tri(int ***p) { return 42; }")
        .MsvcCompile("""
            int ptr_tri(int***);
            int main(void) { int x; int *px = &x; int **ppx = &px; return ptr_tri(&ppx); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact(Skip = "BUG: chibil emits P (unqualified ptr) instead of Q (const ptr) for const-pointer-to-const — PEBPEBD vs PEBQEBD")]
    public void Ptr_ConstCharConstPtr()
    {
        // const char * const * — pointer to const-pointer to const-char
        // MSVC: PEBQEBD (P=ptr, EB=const-pointee, Q=const-ptr, EB=const-pointee, D=char)
        // chibil: PEBPEBD (P instead of Q — wrong)
        Compile("int ptr_cccp(const char * const *p) { return 42; }")
        .MsvcCompile("""
            int ptr_cccp(const char * const *);
            int main(void) { const char *s = "hi"; return ptr_cccp(&s); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  18. Forward-declared / opaque struct pointer
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Struct_ForwardDeclaredPtr()
    {
        // struct Opaque is forward-declared (never defined) — used only as pointer
        // Generates TypeRef with null ResolutionScope
        Compile("""
            struct Opaque;
            int st_fwd(struct Opaque *p) { return 42; }
            """)
        .MsvcCompile("""
            struct Opaque;
            int st_fwd(struct Opaque*);
            int main(void) { return st_fwd(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  19. Struct return + struct parameter (non-repeated)
    //      Return type uses ?AU prefix; return type must NOT create
    //      a backref slot.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Ret_StructWithStructParam()
    {
        // struct Point return + struct Point param — single occurrence, no backref needed
        // Tests that ?AU prefix on return doesn't interfere with param encoding
        Compile("""
            struct Point { int x; int y; };
            struct Point ret_st_p(struct Point p) {
                struct Point r; r.x = 42; r.y = p.y; return r;
            }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            struct Point ret_st_p(struct Point);
            int main(void) { struct Point p = {1, 2}; struct Point r = ret_st_p(p); return r.x; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void Ret_StructPtrWithVoidParams()
    {
        // struct Point* return + void params — combines pointer-to-struct return
        // with the void params termination (XZ vs X@Z)
        Compile("""
            struct Point { int x; int y; };
            struct Point g_pt = { 42, 0 };
            struct Point *ret_stp(void) { return &g_pt; }
            """)
        .MsvcCompile("""
            struct Point { int x; int y; };
            struct Point *ret_stp(void);
            int main(void) { return ret_stp()->x; }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  20. Multi-dimensional array parameters
    //      int arr[3][4] decays to int (*)[4] which uses Y encoding
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: multi-dimensional array param produces TypeDef metadata mismatch + mangling inconsistency")]
    public void Array_MultiDim()
    {
        // int arr[3][4] → outer dimension decays, inner preserved
        // MSVC uses Y encoding: QEAY03H (const ptr to int[4])
        // Also causes metadata error: differing number of fields in duplicated $ArrayType$ TypeDefs
        Compile("""
            int arr_2d(int arr[3][4]) { return 42; }
            """)
        .MsvcCompile("""
            int arr_2d(int[3][4]);
            int main(void) { int a[3][4] = {{0}}; return arr_2d(a); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  21. Mixed calling conventions in function pointers
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: function pointer parameter MSIL signature metadata inconsistent with MSVC")]
    public void FuncPtr_ClrcallInCdecl()
    {
        // __clrcall function pointer inside a cdecl function
        // Outer function uses YA (cdecl), inner func ptr uses P6M (clrcall)
        Compile("int fp_clr(int (__clrcall *fn)(int)) { return 42; }")
        .MsvcCompile("""
            int fp_clr(int (__clrcall *)(int));
            int main(void) { return fp_clr(0); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  22. Variadic function consumption
    //      chibil rejects variadic definitions but should be able to
    //      declare and call MSVC-defined variadic functions.
    // ═══════════════════════════════════════════════════════════════

    [Fact(Skip = "BUG: variadic call site generates invalid IL — InvalidProgramException at runtime")]
    public void Variadic_MsvcDefine_ChibiConsume()
    {
        // MSVC defines a variadic function, chibil declares and calls it
        // Links successfully but crashes with InvalidProgramException
        MsvcCompile("""
            int var_sum(int n, ...) {
                return 42;
            }
            """)
        .Compile("""
            int var_sum(int, ...);
            int main(void) { return var_sum(1, 10); }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}

