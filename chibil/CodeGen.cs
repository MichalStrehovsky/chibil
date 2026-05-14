using System.Text;

namespace Chibicc;

/// <summary>
/// x86-64 code generator — port of codegen.c.
/// Emits AT&T syntax x86-64 assembly from the AST.
/// </summary>
public class CodeGen
{
    private const int GpMax = 6;
    private const int FpMax = 8;

    private TextWriter _out;
    private int _depth;
    private int _labelCount = 1;
    private Obj _currentFn;
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
    private readonly Tokenizer _tokenizer;

    private static readonly string[] Argreg8 = { "%dil", "%sil", "%dl", "%cl", "%r8b", "%r9b" };
    private static readonly string[] Argreg16 = { "%di", "%si", "%dx", "%cx", "%r8w", "%r9w" };
    private static readonly string[] Argreg32 = { "%edi", "%esi", "%edx", "%ecx", "%r8d", "%r9d" };
    private static readonly string[] Argreg64 = { "%rdi", "%rsi", "%rdx", "%rcx", "%r8", "%r9" };

    public CodeGen(CompilerOptions options, Tokenizer tokenizer, TypeSystem types)
    {
        _options = options;
        _tokenizer = tokenizer;
        _types = types;
    }

    private void Println(string line) => _out.WriteLine(line);

    private int Count() => _labelCount++;

    private void Push() { Println("  push %rax"); _depth++; }
    private void Pop(string arg) { Println($"  pop {arg}"); _depth--; }
    private void Pushf() { Println("  sub $8, %rsp"); Println("  movsd %xmm0, (%rsp)"); _depth++; }
    private void Popf(int reg) { Println($"  movsd (%rsp), %xmm{reg}"); Println("  add $8, %rsp"); _depth--; }

    private static string RegDx(int sz) => sz switch { 1 => "%dl", 2 => "%dx", 4 => "%edx", 8 => "%rdx", _ => throw new ChibiccException("unreachable") };
    private static string RegAx(int sz) => sz switch { 1 => "%al", 2 => "%ax", 4 => "%eax", 8 => "%rax", _ => throw new ChibiccException("unreachable") };

    // ═══════════════════════════════════════════════════════════════
    //  Address generation
    // ═══════════════════════════════════════════════════════════════

    private void GenAddr(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.Ty.Kind == TypeKind.Vla) { Println($"  mov {node.Var.Offset}(%rbp), %rax"); return; }
                if (node.Var.IsLocal) { Println($"  lea {node.Var.Offset}(%rbp), %rax"); return; }
                if (_options.OptFpic)
                {
                    if (node.Var.IsTls) { Println($"  data16 lea {node.Var.Name}@tlsgd(%rip), %rdi"); Println("  .value 0x6666"); Println("  rex64"); Println("  call __tls_get_addr@PLT"); return; }
                    Println($"  mov {node.Var.Name}@GOTPCREL(%rip), %rax"); return;
                }
                if (node.Var.IsTls) { Println("  mov %fs:0, %rax"); Println($"  add ${node.Var.Name}@tpoff, %rax"); return; }
                if (node.Ty.Kind == TypeKind.Func)
                {
                    if (node.Var.IsDefinition) Println($"  lea {node.Var.Name}(%rip), %rax");
                    else Println($"  mov {node.Var.Name}@GOTPCREL(%rip), %rax");
                    return;
                }
                Println($"  lea {node.Var.Name}(%rip), %rax"); return;
            case NodeKind.Deref: GenExpr(node.Lhs); return;
            case NodeKind.Comma: GenExpr(node.Lhs); GenAddr(node.Rhs); return;
            case NodeKind.Member: GenAddr(node.Lhs); Println($"  add ${node.Member.Offset}, %rax"); return;
            case NodeKind.FunCall:
                if (node.RetBuffer != null) { GenExpr(node); return; }
                break;
            case NodeKind.Assign:
            case NodeKind.Cond:
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union) { GenExpr(node); return; }
                break;
            case NodeKind.VlaPtr: Println($"  lea {node.Var.Offset}(%rbp), %rax"); return;
        }
        Util.ErrorTok(node.Tok, "not an lvalue");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Load and Store
    // ═══════════════════════════════════════════════════════════════

    private void Load(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Array: case TypeKind.Struct: case TypeKind.Union: case TypeKind.Func: case TypeKind.Vla: return;
            case TypeKind.Float: Println("  movss (%rax), %xmm0"); return;
            case TypeKind.Double: Println("  movsd (%rax), %xmm0"); return;
            case TypeKind.LDouble: if (ty.Size == 8) Println("  movsd (%rax), %xmm0"); else Println("  fldt (%rax)"); return;
        }
        string insn = ty.IsUnsigned ? "movz" : "movs";
        if (ty.Size == 1) Println($"  {insn}bl (%rax), %eax");
        else if (ty.Size == 2) Println($"  {insn}wl (%rax), %eax");
        else if (ty.Size == 4) Println("  movsxd (%rax), %rax");
        else Println("  mov (%rax), %rax");
    }

    private void Store(CType ty)
    {
        Pop("%rdi");
        switch (ty.Kind)
        {
            case TypeKind.Struct: case TypeKind.Union:
                for (int i = 0; i < ty.Size; i++) { Println($"  mov {i}(%rax), %r8b"); Println($"  mov %r8b, {i}(%rdi)"); }
                return;
            case TypeKind.Float: Println("  movss %xmm0, (%rdi)"); return;
            case TypeKind.Double: Println("  movsd %xmm0, (%rdi)"); return;
            case TypeKind.LDouble: if (ty.Size == 8) Println("  movsd %xmm0, (%rdi)"); else Println("  fstpt (%rdi)"); return;
        }
        if (ty.Size == 1) Println("  mov %al, (%rdi)");
        else if (ty.Size == 2) Println("  mov %ax, (%rdi)");
        else if (ty.Size == 4) Println("  mov %eax, (%rdi)");
        else Println("  mov %rax, (%rdi)");
    }

    private void CmpZero(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float: Println("  xorps %xmm1, %xmm1"); Println("  ucomiss %xmm1, %xmm0"); return;
            case TypeKind.Double: Println("  xorpd %xmm1, %xmm1"); Println("  ucomisd %xmm1, %xmm0"); return;
            case TypeKind.LDouble: if (ty.Size == 8) { Println("  xorpd %xmm1, %xmm1"); Println("  ucomisd %xmm1, %xmm0"); } else { Println("  fldz"); Println("  fucomip"); Println("  fstp %st(0)"); } return;
        }
        if (TypeSystem.IsInteger(ty) && ty.Size <= 4) Println("  cmp $0, %eax");
        else Println("  cmp $0, %rax");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type cast table
    // ═══════════════════════════════════════════════════════════════

    private const int I8 = 0, I16 = 1, I32 = 2, I64 = 3, U8 = 4, U16 = 5, U32 = 6, U64 = 7, F32 = 8, F64 = 9, F80 = 10;

    private static int GetTypeId(CType ty) => ty.Kind switch
    {
        TypeKind.Char => ty.IsUnsigned ? U8 : I8,
        TypeKind.Short => ty.IsUnsigned ? U16 : I16,
        TypeKind.Int => ty.IsUnsigned ? U32 : I32,
        TypeKind.Long => ty.Size == 8 ? (ty.IsUnsigned ? U64 : I64) : (ty.IsUnsigned ? U32 : I32),
        TypeKind.LLong => ty.IsUnsigned ? U64 : I64,
        TypeKind.Float => F32,
        TypeKind.Double => F64,
        TypeKind.LDouble => ty.Size == 8 ? F64 : F80,
        _ => U64,
    };

    private static readonly string[][] CastTable = {
        // to:  i8    i16     i32   i64      u8     u16     u32   u64      f32       f64       f80
        /*i8 */ new[]{null,  null,   null,  "movsxd %eax, %rax", "movzbl %al, %eax", "movzwl %ax, %eax", null,  "movsxd %eax, %rax", "cvtsi2ssl %eax, %xmm0", "cvtsi2sdl %eax, %xmm0", "mov %eax, -4(%rsp); fildl -4(%rsp)"},
        /*i16*/ new[]{"movsbl %al, %eax", null, null, "movsxd %eax, %rax", "movzbl %al, %eax", "movzwl %ax, %eax", null, "movsxd %eax, %rax", "cvtsi2ssl %eax, %xmm0", "cvtsi2sdl %eax, %xmm0", "mov %eax, -4(%rsp); fildl -4(%rsp)"},
        /*i32*/ new[]{"movsbl %al, %eax", "movswl %ax, %eax", null, "movsxd %eax, %rax", "movzbl %al, %eax", "movzwl %ax, %eax", null, "movsxd %eax, %rax", "cvtsi2ssl %eax, %xmm0", "cvtsi2sdl %eax, %xmm0", "mov %eax, -4(%rsp); fildl -4(%rsp)"},
        /*i64*/ new[]{"movsbl %al, %eax", "movswl %ax, %eax", null, null, "movzbl %al, %eax", "movzwl %ax, %eax", null, null, "cvtsi2ssq %rax, %xmm0", "cvtsi2sdq %rax, %xmm0", "movq %rax, -8(%rsp); fildll -8(%rsp)"},
        /*u8 */ new[]{"movsbl %al, %eax", null, null, "movsxd %eax, %rax", null, null, null, "movsxd %eax, %rax", "cvtsi2ssl %eax, %xmm0", "cvtsi2sdl %eax, %xmm0", "mov %eax, -4(%rsp); fildl -4(%rsp)"},
        /*u16*/ new[]{"movsbl %al, %eax", "movswl %ax, %eax", null, "movsxd %eax, %rax", "movzbl %al, %eax", null, null, "movsxd %eax, %rax", "cvtsi2ssl %eax, %xmm0", "cvtsi2sdl %eax, %xmm0", "mov %eax, -4(%rsp); fildl -4(%rsp)"},
        /*u32*/ new[]{"movsbl %al, %eax", "movswl %ax, %eax", null, "mov %eax, %eax", "movzbl %al, %eax", "movzwl %ax, %eax", null, "mov %eax, %eax", "mov %eax, %eax; cvtsi2ssq %rax, %xmm0", "mov %eax, %eax; cvtsi2sdq %rax, %xmm0", "mov %eax, %eax; mov %rax, -8(%rsp); fildll -8(%rsp)"},
        /*u64*/ new[]{"movsbl %al, %eax", "movswl %ax, %eax", null, null, "movzbl %al, %eax", "movzwl %ax, %eax", null, null, "cvtsi2ssq %rax, %xmm0",
            "test %rax,%rax; js 1f; pxor %xmm0,%xmm0; cvtsi2sd %rax,%xmm0; jmp 2f; 1: mov %rax,%rdi; and $1,%eax; pxor %xmm0,%xmm0; shr %rdi; or %rax,%rdi; cvtsi2sd %rdi,%xmm0; addsd %xmm0,%xmm0; 2:",
            "mov %rax, -8(%rsp); fildq -8(%rsp); test %rax, %rax; jns 1f;mov $1602224128, %eax; mov %eax, -4(%rsp); fadds -4(%rsp); 1:"},
        /*f32*/ new[]{"cvttss2sil %xmm0, %eax; movsbl %al, %eax", "cvttss2sil %xmm0, %eax; movswl %ax, %eax", "cvttss2sil %xmm0, %eax", "cvttss2siq %xmm0, %rax", "cvttss2sil %xmm0, %eax; movzbl %al, %eax", "cvttss2sil %xmm0, %eax; movzwl %ax, %eax", "cvttss2siq %xmm0, %rax", "cvttss2siq %xmm0, %rax", null, "cvtss2sd %xmm0, %xmm0", "movss %xmm0, -4(%rsp); flds -4(%rsp)"},
        /*f64*/ new[]{"cvttsd2sil %xmm0, %eax; movsbl %al, %eax", "cvttsd2sil %xmm0, %eax; movswl %ax, %eax", "cvttsd2sil %xmm0, %eax", "cvttsd2siq %xmm0, %rax", "cvttsd2sil %xmm0, %eax; movzbl %al, %eax", "cvttsd2sil %xmm0, %eax; movzwl %ax, %eax", "cvttsd2siq %xmm0, %rax", "cvttsd2siq %xmm0, %rax", "cvtsd2ss %xmm0, %xmm0", null, "movsd %xmm0, -8(%rsp); fldl -8(%rsp)"},
        /*f80*/ new[]{
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistps -24(%rsp); fldcw -10(%rsp); movsbl -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistps -24(%rsp); fldcw -10(%rsp); movzbl -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistpl -24(%rsp); fldcw -10(%rsp); mov -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistpq -24(%rsp); fldcw -10(%rsp); mov -24(%rsp), %rax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistps -24(%rsp); fldcw -10(%rsp); movzbl -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistpl -24(%rsp); fldcw -10(%rsp); movswl -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistpl -24(%rsp); fldcw -10(%rsp); mov -24(%rsp), %eax",
            "fnstcw -10(%rsp); movzwl -10(%rsp), %eax; or $12, %ah; mov %ax, -12(%rsp); fldcw -12(%rsp); fistpq -24(%rsp); fldcw -10(%rsp); mov -24(%rsp), %rax",
            "fstps -8(%rsp); movss -8(%rsp), %xmm0",
            "fstpl -8(%rsp); movsd -8(%rsp), %xmm0",
            null},
    };

    private void Cast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) return;
        if (to.Kind == TypeKind.Bool) { CmpZero(from); Println("  setne %al"); Println("  movzx %al, %eax"); return; }
        int t1 = GetTypeId(from), t2 = GetTypeId(to);
        if (CastTable[t1][t2] != null) Println($"  {CastTable[t1][t2]}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Struct passing helpers
    // ═══════════════════════════════════════════════════════════════

    private static bool HasFlonum(CType ty, int lo, int hi, int offset)
    {
        if (ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union)
        {
            for (Member mem = ty.Members; mem != null; mem = mem.Next)
                if (!HasFlonum(mem.Ty, lo, hi, offset + mem.Offset)) return false;
            return true;
        }
        if (ty.Kind == TypeKind.Array)
        {
            for (int i = 0; i < ty.ArrayLen; i++)
                if (!HasFlonum(ty.Base, lo, hi, offset + ty.Base.Size * i)) return false;
            return true;
        }
        return offset < lo || hi <= offset || ty.Kind == TypeKind.Float || ty.Kind == TypeKind.Double
            || (ty.Kind == TypeKind.LDouble && ty.Size == 8);
    }

    private static bool HasFlonum1(CType ty) => HasFlonum(ty, 0, 8, 0);
    private static bool HasFlonum2(CType ty) => HasFlonum(ty, 8, 16, 0);

    private void PushStruct(CType ty)
    {
        int sz = Util.AlignTo(ty.Size, 8);
        Println($"  sub ${sz}, %rsp");
        _depth += sz / 8;
        for (int i = 0; i < ty.Size; i++) { Println($"  mov {i}(%rax), %r10b"); Println($"  mov %r10b, {i}(%rsp)"); }
    }

    private void PushArgs2(Node args, bool firstPass)
    {
        if (args == null) return;
        PushArgs2(args.Next, firstPass);
        if ((firstPass && !args.PassByStack) || (!firstPass && args.PassByStack)) return;
        GenExpr(args);
        switch (args.Ty.Kind)
        {
            case TypeKind.Struct: case TypeKind.Union: PushStruct(args.Ty); break;
            case TypeKind.Float: case TypeKind.Double: Pushf(); break;
            case TypeKind.LDouble: if (args.Ty.Size == 8) { Pushf(); } else { Println("  sub $16, %rsp"); Println("  fstpt (%rsp)"); _depth += 2; } break;
            default: Push(); break;
        }
    }

    private int PushArgs(Node node)
    {
        int stack = 0, gp = 0, fp = 0;
        if (node.RetBuffer != null && node.Ty.Size > 16) gp++;

        for (Node arg = node.Args; arg != null; arg = arg.Next)
        {
            CType ty = arg.Ty;
            switch (ty.Kind)
            {
                case TypeKind.Struct: case TypeKind.Union:
                    if (ty.Size > 16) { arg.PassByStack = true; stack += Util.AlignTo(ty.Size, 8) / 8; }
                    else
                    {
                        bool fp1 = HasFlonum1(ty), fp2 = HasFlonum2(ty);
                        if (fp + (fp1?1:0) + (fp2?1:0) < FpMax && gp + (fp1?0:1) + (fp2?0:1) < GpMax) { fp += (fp1?1:0) + (fp2?1:0); gp += (fp1?0:1) + (fp2?0:1); }
                        else { arg.PassByStack = true; stack += Util.AlignTo(ty.Size, 8) / 8; }
                    }
                    break;
                case TypeKind.Float: case TypeKind.Double:
                    if (fp++ >= FpMax) { arg.PassByStack = true; stack++; }
                    break;
                case TypeKind.LDouble:
                    if (arg.Ty.Size == 8) { if (fp++ >= FpMax) { arg.PassByStack = true; stack++; } }
                    else { arg.PassByStack = true; stack += 2; }
                    break;
                default:
                    if (gp++ >= GpMax) { arg.PassByStack = true; stack++; }
                    break;
            }
        }

        if ((_depth + stack) % 2 == 1) { Println("  sub $8, %rsp"); _depth++; stack++; }
        PushArgs2(node.Args, true);
        PushArgs2(node.Args, false);
        if (node.RetBuffer != null && node.Ty.Size > 16) { Println($"  lea {node.RetBuffer.Offset}(%rbp), %rax"); Push(); }
        return stack;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Struct copy helpers
    // ═══════════════════════════════════════════════════════════════

    private void CopyRetBuffer(Obj var)
    {
        CType ty = var.Ty;
        int gp = 0, fp = 0;
        if (HasFlonum1(ty))
        {
            if (ty.Size == 4) Println($"  movss %xmm0, {var.Offset}(%rbp)");
            else Println($"  movsd %xmm0, {var.Offset}(%rbp)");
            fp++;
        }
        else
        {
            for (int i = 0; i < Math.Min(8, ty.Size); i++) { Println($"  mov %al, {var.Offset + i}(%rbp)"); Println("  shr $8, %rax"); }
            gp++;
        }
        if (ty.Size > 8)
        {
            if (HasFlonum2(ty))
            {
                if (ty.Size == 12) Println($"  movss %xmm{fp}, {var.Offset + 8}(%rbp)");
                else Println($"  movsd %xmm{fp}, {var.Offset + 8}(%rbp)");
            }
            else
            {
                string r1 = gp == 0 ? "%al" : "%dl", r2 = gp == 0 ? "%rax" : "%rdx";
                for (int i = 8; i < Math.Min(16, ty.Size); i++) { Println($"  mov {r1}, {var.Offset + i}(%rbp)"); Println($"  shr $8, {r2}"); }
            }
        }
    }

    private void CopyStructReg()
    {
        CType ty = _currentFn.Ty.ReturnTy;
        int gp = 0, fp = 0;
        Println("  mov %rax, %rdi");
        if (HasFlonum(ty, 0, 8, 0))
        {
            if (ty.Size == 4) Println("  movss (%rdi), %xmm0");
            else Println("  movsd (%rdi), %xmm0");
            fp++;
        }
        else
        {
            Println("  mov $0, %rax");
            for (int i = Math.Min(8, ty.Size) - 1; i >= 0; i--) { Println("  shl $8, %rax"); Println($"  mov {i}(%rdi), %al"); }
            gp++;
        }
        if (ty.Size > 8)
        {
            if (HasFlonum(ty, 8, 16, 0))
            {
                if (ty.Size == 4) Println($"  movss 8(%rdi), %xmm{fp}");
                else Println($"  movsd 8(%rdi), %xmm{fp}");
            }
            else
            {
                string r1 = gp == 0 ? "%al" : "%dl", r2 = gp == 0 ? "%rax" : "%rdx";
                Println($"  mov $0, {r2}");
                for (int i = Math.Min(16, ty.Size) - 1; i >= 8; i--) { Println($"  shl $8, {r2}"); Println($"  mov {i}(%rdi), {r1}"); }
            }
        }
    }

    private void CopyStructMem()
    {
        CType ty = _currentFn.Ty.ReturnTy;
        Obj var = _currentFn.Params;
        Println($"  mov {var.Offset}(%rbp), %rdi");
        for (int i = 0; i < ty.Size; i++) { Println($"  mov {i}(%rax), %dl"); Println($"  mov %dl, {i}(%rdi)"); }
    }

    private void BuiltinAlloca()
    {
        Println("  add $15, %rdi"); Println("  and $0xfffffff0, %edi");
        Println($"  mov {_currentFn.AllocaBottom.Offset}(%rbp), %rcx");
        Println("  sub %rsp, %rcx"); Println("  mov %rsp, %rax"); Println("  sub %rdi, %rsp"); Println("  mov %rsp, %rdx");
        Println("1:"); Println("  cmp $0, %rcx"); Println("  je 2f");
        Println("  mov (%rax), %r8b"); Println("  mov %r8b, (%rdx)"); Println("  inc %rdx"); Println("  inc %rax"); Println("  dec %rcx"); Println("  jmp 1b");
        Println("2:");
        Println($"  mov {_currentFn.AllocaBottom.Offset}(%rbp), %rax");
        Println("  sub %rdi, %rax");
        Println($"  mov %rax, {_currentFn.AllocaBottom.Offset}(%rbp)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression code generation
    // ═══════════════════════════════════════════════════════════════

    private void GenExpr(Node node)
    {
        Println($"  .loc {node.Tok.File.FileNo} {node.Tok.LineNo}");
        switch (node.Kind)
        {
            case NodeKind.NullExpr: return;
            case NodeKind.Num:
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float:
                        uint fu = BitConverter.SingleToUInt32Bits((float)node.FVal);
                        Println($"  mov ${fu}, %eax  # float {node.FVal:F6}"); Println("  movq %rax, %xmm0"); return;
                    case TypeKind.Double:
                        ulong du = BitConverter.DoubleToUInt64Bits(node.FVal);
                        Println($"  mov ${du}, %rax  # double {node.FVal:F6}"); Println("  movq %rax, %xmm0"); return;
                    case TypeKind.LDouble:
                        if (node.Ty.Size == 8)
                        {
                            ulong ldu = BitConverter.DoubleToUInt64Bits(node.FVal);
                            Println($"  mov ${ldu}, %rax  # long double {node.FVal:F6}"); Println("  movq %rax, %xmm0");
                        }
                        else
                        {
                            var (u0, u1) = Util.DoubleToF80Words(node.FVal);
                            Println($"  mov ${u0}, %rax  # long double {node.FVal:F6}"); Println("  mov %rax, -16(%rsp)");
                            Println($"  mov ${u1}, %rax"); Println("  mov %rax, -8(%rsp)"); Println("  fldt -16(%rsp)");
                        }
                        return;
                }
                Println($"  mov ${node.Val}, %rax"); return;

            case NodeKind.Neg:
                GenExpr(node.Lhs);
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float: Println("  mov $1, %rax"); Println("  shl $31, %rax"); Println("  movq %rax, %xmm1"); Println("  xorps %xmm1, %xmm0"); return;
                    case TypeKind.Double: Println("  mov $1, %rax"); Println("  shl $63, %rax"); Println("  movq %rax, %xmm1"); Println("  xorpd %xmm1, %xmm0"); return;
                    case TypeKind.LDouble: if (node.Ty.Size == 8) { Println("  mov $1, %rax"); Println("  shl $63, %rax"); Println("  movq %rax, %xmm1"); Println("  xorpd %xmm1, %xmm0"); } else Println("  fchs"); return;
                }
                Println("  neg %rax"); return;

            case NodeKind.Var: GenAddr(node); Load(node.Ty); return;
            case NodeKind.Member:
                GenAddr(node); Load(node.Ty);
                if (node.Member.IsBitfield)
                {
                    Println($"  shl ${64 - node.Member.BitWidth - node.Member.BitOffset}, %rax");
                    if (node.Member.Ty.IsUnsigned) Println($"  shr ${64 - node.Member.BitWidth}, %rax");
                    else Println($"  sar ${64 - node.Member.BitWidth}, %rax");
                }
                return;
            case NodeKind.Deref: GenExpr(node.Lhs); Load(node.Ty); return;
            case NodeKind.Addr: GenAddr(node.Lhs); return;

            case NodeKind.Assign:
                GenAddr(node.Lhs); Push(); GenExpr(node.Rhs);
                if (node.Lhs.Kind == NodeKind.Member && node.Lhs.Member.IsBitfield)
                {
                    Println("  mov %rax, %r8");
                    Member mem = node.Lhs.Member;
                    Println("  mov %rax, %rdi");
                    Println($"  and ${(1L << mem.BitWidth) - 1}, %rdi");
                    Println($"  shl ${mem.BitOffset}, %rdi");
                    Println("  mov (%rsp), %rax"); Load(mem.Ty);
                    long mask = ((1L << mem.BitWidth) - 1) << mem.BitOffset;
                    Println($"  mov ${~mask}, %r9"); Println("  and %r9, %rax"); Println("  or %rdi, %rax");
                    Store(node.Ty); Println("  mov %r8, %rax"); return;
                }
                Store(node.Ty); return;

            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next) GenStmt(n);
                return;
            case NodeKind.Comma: GenExpr(node.Lhs); GenExpr(node.Rhs); return;
            case NodeKind.Cast: GenExpr(node.Lhs); Cast(node.Lhs.Ty, node.Ty); return;

            case NodeKind.MemZero:
                Println($"  mov ${node.Var.Ty.Size}, %rcx"); Println($"  lea {node.Var.Offset}(%rbp), %rdi");
                Println("  mov $0, %al"); Println("  rep stosb"); return;

            case NodeKind.Cond:
                int cc = Count(); GenExpr(node.Cond); CmpZero(node.Cond.Ty);
                Println($"  je .L.else.{cc}"); GenExpr(node.Then); Println($"  jmp .L.end.{cc}");
                Println($".L.else.{cc}:"); GenExpr(node.Els); Println($".L.end.{cc}:"); return;

            case NodeKind.Not: GenExpr(node.Lhs); CmpZero(node.Lhs.Ty); Println("  sete %al"); Println("  movzx %al, %rax"); return;
            case NodeKind.BitNot: GenExpr(node.Lhs); Println("  not %rax"); return;

            case NodeKind.LogAnd:
                cc = Count(); GenExpr(node.Lhs); CmpZero(node.Lhs.Ty); Println($"  je .L.false.{cc}");
                GenExpr(node.Rhs); CmpZero(node.Rhs.Ty); Println($"  je .L.false.{cc}");
                Println("  mov $1, %rax"); Println($"  jmp .L.end.{cc}");
                Println($".L.false.{cc}:"); Println("  mov $0, %rax"); Println($".L.end.{cc}:"); return;

            case NodeKind.LogOr:
                cc = Count(); GenExpr(node.Lhs); CmpZero(node.Lhs.Ty); Println($"  jne .L.true.{cc}");
                GenExpr(node.Rhs); CmpZero(node.Rhs.Ty); Println($"  jne .L.true.{cc}");
                Println("  mov $0, %rax"); Println($"  jmp .L.end.{cc}");
                Println($".L.true.{cc}:"); Println("  mov $1, %rax"); Println($".L.end.{cc}:"); return;

            case NodeKind.FunCall:
                if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.Name == "alloca")
                {
                    GenExpr(node.Args); Println("  mov %rax, %rdi"); BuiltinAlloca(); return;
                }
                int stackArgs = PushArgs(node);
                GenExpr(node.Lhs);
                int gp = 0, fp = 0;
                if (node.RetBuffer != null && node.Ty.Size > 16) Pop(Argreg64[gp++]);
                for (Node arg = node.Args; arg != null; arg = arg.Next)
                {
                    switch (arg.Ty.Kind)
                    {
                        case TypeKind.Struct: case TypeKind.Union:
                            if (arg.Ty.Size > 16) continue;
                            bool fp1 = HasFlonum1(arg.Ty), fp2 = HasFlonum2(arg.Ty);
                            if (fp + (fp1?1:0) + (fp2?1:0) < FpMax && gp + (fp1?0:1) + (fp2?0:1) < GpMax)
                            {
                                if (fp1) Popf(fp++); else Pop(Argreg64[gp++]);
                                if (arg.Ty.Size > 8) { if (fp2) Popf(fp++); else Pop(Argreg64[gp++]); }
                            }
                            break;
                        case TypeKind.Float: case TypeKind.Double:
                            if (fp < FpMax) Popf(fp++); break;
                        case TypeKind.LDouble:
                            if (arg.Ty.Size == 8 && fp < FpMax) Popf(fp++);
                            break;
                        default:
                            if (gp < GpMax) Pop(Argreg64[gp++]); break;
                    }
                }
                Println("  mov %rax, %r10"); Println($"  mov ${fp}, %rax"); Println("  call *%r10");
                Println($"  add ${stackArgs * 8}, %rsp"); _depth -= stackArgs;

                switch (node.Ty.Kind)
                {
                    case TypeKind.Bool: Println("  movzx %al, %eax"); return;
                    case TypeKind.Char:
                        Println(node.Ty.IsUnsigned ? "  movzbl %al, %eax" : "  movsbl %al, %eax"); return;
                    case TypeKind.Short:
                        Println(node.Ty.IsUnsigned ? "  movzwl %ax, %eax" : "  movswl %ax, %eax"); return;
                }
                if (node.RetBuffer != null && node.Ty.Size <= 16)
                {
                    CopyRetBuffer(node.RetBuffer);
                    Println($"  lea {node.RetBuffer.Offset}(%rbp), %rax");
                }
                return;

            case NodeKind.LabelVal: Println($"  lea {node.UniqueLabel}(%rip), %rax"); return;

            case NodeKind.Cas:
                GenExpr(node.CasAddr); Push(); GenExpr(node.CasNew); Push(); GenExpr(node.CasOld);
                Println("  mov %rax, %r8"); Load(node.CasOld.Ty.Base);
                Pop("%rdx"); Pop("%rdi");
                int sz2 = node.CasAddr.Ty.Base.Size;
                Println($"  lock cmpxchg {RegDx(sz2)}, (%rdi)");
                Println("  sete %cl"); Println("  je 1f"); Println($"  mov {RegAx(sz2)}, (%r8)");
                Println("1:"); Println("  movzbl %cl, %eax"); return;

            case NodeKind.Exch:
                GenExpr(node.Lhs); Push(); GenExpr(node.Rhs); Pop("%rdi");
                int sz3 = node.Lhs.Ty.Base.Size;
                Println($"  xchg {RegAx(sz3)}, (%rdi)"); return;
        }

        // Binary operations on float/double
        switch (node.Lhs.Ty.Kind)
        {
            case TypeKind.Float: case TypeKind.Double:
                GenExpr(node.Rhs); Pushf(); GenExpr(node.Lhs); Popf(1);
                string sz = node.Lhs.Ty.Kind == TypeKind.Float ? "ss" : "sd";
                switch (node.Kind)
                {
                    case NodeKind.Add: Println($"  add{sz} %xmm1, %xmm0"); return;
                    case NodeKind.Sub: Println($"  sub{sz} %xmm1, %xmm0"); return;
                    case NodeKind.Mul: Println($"  mul{sz} %xmm1, %xmm0"); return;
                    case NodeKind.Div: Println($"  div{sz} %xmm1, %xmm0"); return;
                    case NodeKind.Eq: case NodeKind.Ne: case NodeKind.Lt: case NodeKind.Le:
                        Println($"  ucomi{sz} %xmm0, %xmm1");
                        if (node.Kind == NodeKind.Eq) { Println("  sete %al"); Println("  setnp %dl"); Println("  and %dl, %al"); }
                        else if (node.Kind == NodeKind.Ne) { Println("  setne %al"); Println("  setp %dl"); Println("  or %dl, %al"); }
                        else if (node.Kind == NodeKind.Lt) Println("  seta %al");
                        else Println("  setae %al");
                        Println("  and $1, %al"); Println("  movzb %al, %rax"); return;
                }
                Util.ErrorTok(node.Tok, "invalid expression");
                return;
            case TypeKind.LDouble:
                if (node.Lhs.Ty.Size == 8)
                {
                    // 8-byte long double (LLP64) — use SSE like double
                    GenExpr(node.Rhs); Pushf(); GenExpr(node.Lhs); Popf(1);
                    switch (node.Kind)
                    {
                        case NodeKind.Add: Println("  addsd %xmm1, %xmm0"); return;
                        case NodeKind.Sub: Println("  subsd %xmm1, %xmm0"); return;
                        case NodeKind.Mul: Println("  mulsd %xmm1, %xmm0"); return;
                        case NodeKind.Div: Println("  divsd %xmm1, %xmm0"); return;
                        case NodeKind.Eq: case NodeKind.Ne: case NodeKind.Lt: case NodeKind.Le:
                            Println("  ucomisd %xmm0, %xmm1");
                            if (node.Kind == NodeKind.Eq) { Println("  sete %al"); Println("  setnp %dl"); Println("  and %dl, %al"); }
                            else if (node.Kind == NodeKind.Ne) { Println("  setne %al"); Println("  setp %dl"); Println("  or %dl, %al"); }
                            else if (node.Kind == NodeKind.Lt) Println("  seta %al");
                            else Println("  setae %al");
                            Println("  and $1, %al"); Println("  movzb %al, %rax"); return;
                    }
                    Util.ErrorTok(node.Tok, "invalid expression");
                    return;
                }
                // 16-byte long double (LP64) — use x87
                GenExpr(node.Lhs); GenExpr(node.Rhs);
                switch (node.Kind)
                {
                    case NodeKind.Add: Println("  faddp"); return;
                    case NodeKind.Sub: Println("  fsubrp"); return;
                    case NodeKind.Mul: Println("  fmulp"); return;
                    case NodeKind.Div: Println("  fdivrp"); return;
                    case NodeKind.Eq: case NodeKind.Ne: case NodeKind.Lt: case NodeKind.Le:
                        Println("  fcomip"); Println("  fstp %st(0)");
                        if (node.Kind == NodeKind.Eq) Println("  sete %al");
                        else if (node.Kind == NodeKind.Ne) Println("  setne %al");
                        else if (node.Kind == NodeKind.Lt) Println("  seta %al");
                        else Println("  setae %al");
                        Println("  movzb %al, %rax"); return;
                }
                Util.ErrorTok(node.Tok, "invalid expression");
                return;
        }

        // Integer binary ops
        GenExpr(node.Rhs); Push(); GenExpr(node.Lhs); Pop("%rdi");
        string ax, di, dx;
        if (node.Lhs.Ty.Size == 8 || node.Lhs.Ty.Base != null) { ax = "%rax"; di = "%rdi"; dx = "%rdx"; }
        else { ax = "%eax"; di = "%edi"; dx = "%edx"; }

        switch (node.Kind)
        {
            case NodeKind.Add: Println($"  add {di}, {ax}"); return;
            case NodeKind.Sub: Println($"  sub {di}, {ax}"); return;
            case NodeKind.Mul: Println($"  imul {di}, {ax}"); return;
            case NodeKind.Div: case NodeKind.Mod:
                if (node.Ty.IsUnsigned) { Println($"  mov $0, {dx}"); Println($"  div {di}"); }
                else { Println(node.Lhs.Ty.Size == 8 ? "  cqo" : "  cdq"); Println($"  idiv {di}"); }
                if (node.Kind == NodeKind.Mod) Println("  mov %rdx, %rax");
                return;
            case NodeKind.BitAnd: Println($"  and {di}, {ax}"); return;
            case NodeKind.BitOr: Println($"  or {di}, {ax}"); return;
            case NodeKind.BitXor: Println($"  xor {di}, {ax}"); return;
            case NodeKind.Eq: case NodeKind.Ne: case NodeKind.Lt: case NodeKind.Le:
                Println($"  cmp {di}, {ax}");
                if (node.Kind == NodeKind.Eq) Println("  sete %al");
                else if (node.Kind == NodeKind.Ne) Println("  setne %al");
                else if (node.Kind == NodeKind.Lt) Println(node.Lhs.Ty.IsUnsigned ? "  setb %al" : "  setl %al");
                else Println(node.Lhs.Ty.IsUnsigned ? "  setbe %al" : "  setle %al");
                Println("  movzb %al, %rax"); return;
            case NodeKind.Shl: Println("  mov %rdi, %rcx"); Println($"  shl %cl, {ax}"); return;
            case NodeKind.Shr: Println("  mov %rdi, %rcx"); Println(node.Lhs.Ty.IsUnsigned ? $"  shr %cl, {ax}" : $"  sar %cl, {ax}"); return;
        }
        Util.ErrorTok(node.Tok, "invalid expression");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statement code generation
    // ═══════════════════════════════════════════════════════════════

    private void GenStmt(Node node)
    {
        Println($"  .loc {node.Tok.File.FileNo} {node.Tok.LineNo}");
        switch (node.Kind)
        {
            case NodeKind.If:
                int c = Count(); GenExpr(node.Cond); CmpZero(node.Cond.Ty);
                Println($"  je  .L.else.{c}"); GenStmt(node.Then); Println($"  jmp .L.end.{c}");
                Println($".L.else.{c}:"); if (node.Els != null) GenStmt(node.Els); Println($".L.end.{c}:"); return;
            case NodeKind.For:
                c = Count();
                if (node.Init != null) GenStmt(node.Init);
                Println($".L.begin.{c}:");
                if (node.Cond != null) { GenExpr(node.Cond); CmpZero(node.Cond.Ty); Println($"  je {node.BrkLabel}"); }
                GenStmt(node.Then); Println($"{node.ContLabel}:");
                if (node.Inc != null) GenExpr(node.Inc);
                Println($"  jmp .L.begin.{c}"); Println($"{node.BrkLabel}:"); return;
            case NodeKind.Do:
                c = Count(); Println($".L.begin.{c}:"); GenStmt(node.Then);
                Println($"{node.ContLabel}:"); GenExpr(node.Cond); CmpZero(node.Cond.Ty);
                Println($"  jne .L.begin.{c}"); Println($"{node.BrkLabel}:"); return;
            case NodeKind.Switch:
                GenExpr(node.Cond);
                for (Node n = node.CaseNext; n != null; n = n.CaseNext)
                {
                    string cax = node.Cond.Ty.Size == 8 ? "%rax" : "%eax";
                    string cdi = node.Cond.Ty.Size == 8 ? "%rdi" : "%edi";
                    if (n.Begin == n.End) { Println($"  cmp ${n.Begin}, {cax}"); Println($"  je {n.Label}"); }
                    else { Println($"  mov {cax}, {cdi}"); Println($"  sub ${n.Begin}, {cdi}"); Println($"  cmp ${n.End - n.Begin}, {cdi}"); Println($"  jbe {n.Label}"); }
                }
                if (node.DefaultCase != null) Println($"  jmp {node.DefaultCase.Label}");
                Println($"  jmp {node.BrkLabel}"); GenStmt(node.Then); Println($"{node.BrkLabel}:"); return;
            case NodeKind.Case: Println($"{node.Label}:"); GenStmt(node.Lhs); return;
            case NodeKind.Block: for (Node n = node.Body; n != null; n = n.Next) GenStmt(n); return;
            case NodeKind.Goto: Println($"  jmp {node.UniqueLabel}"); return;
            case NodeKind.GotoExpr: GenExpr(node.Lhs); Println("  jmp *%rax"); return;
            case NodeKind.Label: Println($"{node.UniqueLabel}:"); GenStmt(node.Lhs); return;
            case NodeKind.Return:
                if (node.Lhs != null)
                {
                    GenExpr(node.Lhs);
                    CType ty = node.Lhs.Ty;
                    if (ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union)
                    {
                        if (ty.Size <= 16) CopyStructReg(); else CopyStructMem();
                    }
                }
                Println($"  jmp .L.return.{_currentFn.Name}"); return;
            case NodeKind.ExprStmt: GenExpr(node.Lhs); return;
            case NodeKind.Asm: Println($"  {node.AsmStr}"); return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Variable offset assignment
    // ═══════════════════════════════════════════════════════════════

    private void AssignLvarOffsets(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction) continue;
            int top = 16, bottom = 0, gp = 0, fp = 0;

            for (Obj var = fn.Params; var != null; var = var.Next)
            {
                CType ty = var.Ty;
                switch (ty.Kind)
                {
                    case TypeKind.Struct: case TypeKind.Union:
                        if (ty.Size <= 16)
                        {
                            bool fp1 = HasFlonum(ty, 0, 8, 0), fp2 = HasFlonum(ty, 8, 16, 8);
                            if (fp + (fp1?1:0) + (fp2?1:0) < FpMax && gp + (fp1?0:1) + (fp2?0:1) < GpMax)
                            { fp += (fp1?1:0) + (fp2?1:0); gp += (fp1?0:1) + (fp2?0:1); continue; }
                        }
                        break;
                    case TypeKind.Float: case TypeKind.Double:
                        if (fp++ < FpMax) continue; break;
                    case TypeKind.LDouble:
                        if (ty.Size == 8) { if (fp++ < FpMax) continue; }
                        break;
                    default:
                        if (gp++ < GpMax) continue; break;
                }
                top = Util.AlignTo(top, 8); var.Offset = top; top += var.Ty.Size;
            }

            for (Obj var = fn.Locals; var != null; var = var.Next)
            {
                if (var.Offset != 0) continue;
                int align = (var.Ty.Kind == TypeKind.Array && var.Ty.Size >= 16) ? Math.Max(16, var.Align) : var.Align;
                bottom += var.Ty.Size; bottom = Util.AlignTo(bottom, align); var.Offset = -bottom;
            }
            fn.StackSize = Util.AlignTo(bottom, 16);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Data emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitData(Obj prog)
    {
        for (Obj var = prog; var != null; var = var.Next)
        {
            if (var.IsFunction || !var.IsDefinition) continue;
            if (var.IsStatic) Println($"  .local {var.Name}"); else Println($"  .globl {var.Name}");

            int align = (var.Ty.Kind == TypeKind.Array && var.Ty.Size >= 16) ? Math.Max(16, var.Align) : var.Align;

            if (_options.OptFcommon && var.IsTentative) { Println($"  .comm {var.Name}, {var.Ty.Size}, {align}"); continue; }

            if (var.InitData != null)
            {
                if (var.IsTls) Println("  .section .tdata,\"awT\",@progbits"); else Println("  .data");
                Println($"  .type {var.Name}, @object"); Println($"  .size {var.Name}, {var.Ty.Size}");
                Println($"  .align {align}"); Println($"{var.Name}:");

                Relocation rel = var.Rel;
                int pos = 0;
                while (pos < var.Ty.Size)
                {
                    if (rel != null && rel.Offset == pos)
                    {
                        Println($"  .quad {rel.Label()}{rel.Addend:+0;-#}");
                        rel = rel.Next; pos += 8;
                    }
                    else
                    {
                        Println($"  .byte {(sbyte)var.InitData[pos++]}");
                    }
                }
                continue;
            }

            if (var.IsTls) Println("  .section .tbss,\"awT\",@nobits"); else Println("  .bss");
            Println($"  .align {align}"); Println($"{var.Name}:"); Println($"  .zero {var.Ty.Size}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Store helpers for function parameters
    // ═══════════════════════════════════════════════════════════════

    private void StoreFp(int r, int offset, int sz)
    {
        if (sz == 4) Println($"  movss %xmm{r}, {offset}(%rbp)");
        else if (sz == 8) Println($"  movsd %xmm{r}, {offset}(%rbp)");
        else Util.Unreachable();
    }

    private void StoreGp(int r, int offset, int sz)
    {
        switch (sz)
        {
            case 1: Println($"  mov {Argreg8[r]}, {offset}(%rbp)"); return;
            case 2: Println($"  mov {Argreg16[r]}, {offset}(%rbp)"); return;
            case 4: Println($"  mov {Argreg32[r]}, {offset}(%rbp)"); return;
            case 8: Println($"  mov {Argreg64[r]}, {offset}(%rbp)"); return;
            default:
                for (int i = 0; i < sz; i++) { Println($"  mov {Argreg8[r]}, {offset + i}(%rbp)"); Println($"  shr $8, {Argreg64[r]}"); }
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Text emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitText(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            if (fn.IsStatic) Println($"  .local {fn.Name}"); else Println($"  .globl {fn.Name}");
            Println("  .text"); Println($"  .type {fn.Name}, @function"); Println($"{fn.Name}:");
            _currentFn = fn;

            Println("  push %rbp"); Println("  mov %rsp, %rbp");
            Println($"  sub ${fn.StackSize}, %rsp");
            Println($"  mov %rsp, {fn.AllocaBottom.Offset}(%rbp)");

            if (fn.VaArea != null)
            {
                int gp = 0, fp = 0;
                for (Obj var = fn.Params; var != null; var = var.Next)
                { if (TypeSystem.IsFlonum(var.Ty)) fp++; else gp++; }
                int off = fn.VaArea.Offset;
                Println($"  movl ${gp * 8}, {off}(%rbp)");
                Println($"  movl ${fp * 8 + 48}, {off + 4}(%rbp)");
                Println($"  movq %rbp, {off + 8}(%rbp)"); Println($"  addq $16, {off + 8}(%rbp)");
                Println($"  movq %rbp, {off + 16}(%rbp)"); Println($"  addq ${off + 24}, {off + 16}(%rbp)");
                Println($"  movq %rdi, {off + 24}(%rbp)"); Println($"  movq %rsi, {off + 32}(%rbp)");
                Println($"  movq %rdx, {off + 40}(%rbp)"); Println($"  movq %rcx, {off + 48}(%rbp)");
                Println($"  movq %r8, {off + 56}(%rbp)"); Println($"  movq %r9, {off + 64}(%rbp)");
                for (int i = 0; i < 8; i++) Println($"  movsd %xmm{i}, {off + 72 + i * 8}(%rbp)");
            }

            int gp2 = 0, fp2 = 0;
            for (Obj var = fn.Params; var != null; var = var.Next)
            {
                if (var.Offset > 0) continue;
                CType ty = var.Ty;
                switch (ty.Kind)
                {
                    case TypeKind.Struct: case TypeKind.Union:
                        if (HasFlonum(ty, 0, 8, 0)) StoreFp(fp2++, var.Offset, Math.Min(8, ty.Size));
                        else StoreGp(gp2++, var.Offset, Math.Min(8, ty.Size));
                        if (ty.Size > 8) { if (HasFlonum(ty, 8, 16, 0)) StoreFp(fp2++, var.Offset + 8, ty.Size - 8); else StoreGp(gp2++, var.Offset + 8, ty.Size - 8); }
                        break;
                    case TypeKind.Float: case TypeKind.Double: StoreFp(fp2++, var.Offset, ty.Size); break;
                    case TypeKind.LDouble:
                        if (ty.Size == 8) StoreFp(fp2++, var.Offset, ty.Size);
                        else StoreGp(gp2++, var.Offset, ty.Size);
                        break;
                    default: StoreGp(gp2++, var.Offset, ty.Size); break;
                }
            }

            GenStmt(fn.Body);
            System.Diagnostics.Debug.Assert(_depth == 0);

            if (fn.Name == "main") Println("  mov $0, %rax");
            Println($".L.return.{fn.Name}:"); Println("  mov %rbp, %rsp"); Println("  pop %rbp"); Println("  ret");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public void Generate(Obj prog, TextWriter output)
    {
        _out = output;
        CFile[] files = _tokenizer.GetInputFiles();
        foreach (CFile f in files)
            Println($"  .file {f.FileNo} \"{f.Name}\"");

        AssignLvarOffsets(prog);
        EmitData(prog);
        EmitText(prog);
    }
}
