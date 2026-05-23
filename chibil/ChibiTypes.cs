using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Chibil;

// ═══════════════════════════════════════════════════════════════════
//  Enums
// ═══════════════════════════════════════════════════════════════════

public enum TokenKind
{
    Ident,   // Identifiers
    Punct,   // Punctuators
    Keyword, // Keywords
    Str,     // String literals
    Num,     // Numeric literals
    PPNum,   // Preprocessing numbers
    Eof,     // End-of-file markers
}

public enum TypeKind
{
    Void, Bool, Char, Short, Int, Long, LLong,
    Float, Double, LDouble,
    Enum, Ptr, Func, Array, Vla, Struct, Union,
}

public enum NodeKind
{
    NullExpr, Add, Sub, Mul, Div, Neg, Mod,
    BitAnd, BitOr, BitXor, Shl, Shr,
    Eq, Ne, Lt, Le,
    Assign, Cond, Comma, Member,
    Addr, Deref, Not, BitNot, LogAnd, LogOr,
    Return, If, For, Do, Switch, Case,
    Block, Goto, GotoExpr, Label, LabelVal,
    FunCall, ExprStmt, StmtExpr,
    Var, VlaPtr, Num, Cast, MemZero,
    Asm, Cas, Exch,
}

public enum CallConv
{
    Cdecl,    // __cdecl — default for C functions
    Clrcall,  // __clrcall — managed calling convention
    Stdcall,  // __stdcall — for P/Invoke (Win32 API)
}

// ═══════════════════════════════════════════════════════════════════
//  CFile (← File in C)
// ═══════════════════════════════════════════════════════════════════

public class CFile
{
    public string Name;
    public int FileNo;
    public byte[] Contents; // UTF-8 source + trailing 0x00
    public string DisplayName;
    public int LineDelta;
}

// ═══════════════════════════════════════════════════════════════════
//  Token
// ═══════════════════════════════════════════════════════════════════

public class Token
{
    public TokenKind Kind;
    public Token Next;
    public long Val;        // If kind is Num, its integer value
    public double FVal;     // If kind is Num, its float value
    public byte[] Buf;      // Source buffer (usually File.Contents)
    public int Loc;         // Offset into Buf
    public int Len;         // Byte length
    public CType Ty;        // Used if Num or Str
    public byte[] Str;      // String literal contents including terminating '\0'
    public CFile File;      // Source file
    public string FileName; // Display filename
    public int LineNo;
    public int LineDelta;
    public bool AtBol;      // True if at beginning of line
    public bool HasSpace;   // True if follows a space character
    public Hideset Hideset;
    public Token Origin;    // If expanded from macro, the original token
}

// ═══════════════════════════════════════════════════════════════════
//  Hideset (for macro expansion)
// ═══════════════════════════════════════════════════════════════════

public class Hideset
{
    public Hideset Next;
    public string Name;
}

// ═══════════════════════════════════════════════════════════════════
//  CType (← Type in C)
// ═══════════════════════════════════════════════════════════════════

public class CType
{
    public TypeKind Kind;
    public int Size;
    public int Align;
    public bool IsUnsigned;
    public bool IsAtomic;
    public bool IsConst;
    public bool IsVolatile;
    public CType Origin; // for type compatibility check

    // Pointer-to or array-of type
    public CType Base;

    // Declaration
    public Token Name;
    public Token NamePos;
    public string TagName; // struct/union/enum tag name, preserved across declarator rewrites

    // Array
    public int ArrayLen;

    // Variable-length array
    public Node VlaLen;
    public Obj VlaSize;

    // Struct
    public Member Members;
    public bool IsFlexible;
    public bool IsPacked;
    public bool IsNestedMember; // struct/union used only as a member of another struct — no TypeDef

    // Function type
    public CType ReturnTy;
    public CType Params;
    public bool IsVariadic;
    public CallConv CallConv; // __cdecl (default), __clrcall, __stdcall
    public CType Next;

    public CType() { }

    public CType(TypeKind kind, int size, int align, bool isUnsigned = false)
    {
        Kind = kind;
        Size = size;
        Align = align;
        IsUnsigned = isUnsigned;
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Member (struct/union member)
// ═══════════════════════════════════════════════════════════════════

public class Member
{
    public Member Next;
    public CType Ty;
    public Token Tok;
    public Token Name;
    public int Idx;
    public int Align;
    public int Offset;
    public bool IsBitfield;
    public int BitOffset;
    public int BitWidth;
}

// ═══════════════════════════════════════════════════════════════════
//  Obj (variable or function)
// ═══════════════════════════════════════════════════════════════════

public class Obj
{
    public Obj Next;
    public string Name;
    public CType Ty;
    public Token Tok;
    public bool IsLocal;
    public int Align;

    // Local variable
    public int Offset;

    // Global variable or function
    public bool IsFunction;
    public bool IsDefinition;
    public bool IsStatic;

    // Global variable
    public bool IsTentative;
    public bool IsTls;
    public byte[] InitData;
    public Relocation Rel;

    // Function
    public bool IsInline;
    public Obj Params;
    public Node Body;
    public Obj Locals;
    public Obj VaArea;
    public Obj AllocaBottom;
    public int StackSize;

    // Static inline function
    public bool IsLive;
    public bool IsRoot;
    public List<string> Refs = new();
}

// ═══════════════════════════════════════════════════════════════════
//  Relocation
// ═══════════════════════════════════════════════════════════════════

public class Relocation
{
    public Relocation Next;
    public int Offset;
    // In C, this is char **label — a pointer to a string that can be
    // updated after the Relocation is created. We use a Func<string>
    // to capture deferred reads (e.g., () => obj.Name).
    public Func<string> Label;
    public long Addend;
}

// ═══════════════════════════════════════════════════════════════════
//  Node (AST node)
// ═══════════════════════════════════════════════════════════════════

public class Node
{
    public NodeKind Kind;
    public Node Next;
    public CType Ty;
    public Token Tok;

    public Node Lhs;
    public Node Rhs;

    // "if" or "for" statement
    public Node Cond;
    public Node Then;
    public Node Els;
    public Node Init;
    public Node Inc;

    // "break" and "continue" labels
    public string BrkLabel;
    public string ContLabel;

    // Block or statement expression
    public Node Body;

    // Struct member access
    public Member Member;

    // Function call
    public CType FuncTy;
    public Node Args;

    // Goto or labeled statement, or labels-as-values
    public string Label;
    public string UniqueLabel;
    public Node GotoNext;

    // Switch
    public Node CaseNext;
    public Node DefaultCase;

    // Case
    public long Begin;
    public long End;

    // "asm" string literal
    public string AsmStr;

    // Atomic compare-and-swap
    public Node CasAddr;
    public Node CasOld;
    public Node CasNew;

    // Atomic op= operators
    public Obj AtomicAddr;
    public Node AtomicExpr;

    // Variable
    public Obj Var;

    // Numeric literal
    public long Val;
    public double FVal;
}

// ═══════════════════════════════════════════════════════════════════
//  Scope (parser)
// ═══════════════════════════════════════════════════════════════════

public class Scope
{
    public Scope Next;
    public Dictionary<string, VarScope> Vars = new();
    public Dictionary<string, CType> Tags = new();
}

public class VarScope
{
    public Obj Var;
    public CType TypeDef;
    public CType EnumTy;
    public int EnumVal;
}

public class VarAttr
{
    public bool IsTypedef;
    public bool IsStatic;
    public bool IsExtern;
    public bool IsInline;
    public bool IsTls;
    public int Align;
    /// <summary>
    /// Calling convention from declspec position (GCC/MinGW compat).
    /// GCC accepts __stdcall/__cdecl as __attribute__ equivalents in
    /// declspec position; MSVC rejects this but MinGW headers use it.
    /// Null = no cc specified; Declarator-position cc overrides this.
    /// </summary>
    public CallConv? PendingCallConv;
}

// ═══════════════════════════════════════════════════════════════════
//  Initializer (parser)
// ═══════════════════════════════════════════════════════════════════

public class Initializer
{
    public Initializer Next;
    public CType Ty;
    public Token Tok;
    public bool IsFlexible;
    public Node Expr;
    public Initializer[] Children;
    public Member Mem;
}

public class InitDesg
{
    public InitDesg Next;
    public int Idx;
    public Member Member;
    public Obj Var;
}

// ═══════════════════════════════════════════════════════════════════
//  Preprocessor types
// ═══════════════════════════════════════════════════════════════════

public class MacroParam
{
    public MacroParam Next;
    public string Name;
}

public class MacroArg
{
    public MacroArg Next;
    public string Name;
    public bool IsVaArgs;
    public Token Tok;
}

public class Macro
{
    public string Name;
    public bool IsObjlike;
    public MacroParam Params;
    public string VaArgsName;
    public Token Body;
    public Func<Token, Token> Handler;
}

public class CondIncl
{
    public CondIncl Next;
    public enum Context { InThen, InElif, InElse }
    public Context Ctx;
    public Token Tok;
    public bool Included;
}

// ═══════════════════════════════════════════════════════════════════
//  CompilerOptions (cross-module shared state)
// ═══════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════
//  DataModel
// ═══════════════════════════════════════════════════════════════════

public class DataModel(int longSize, int ldoubleSize, int ldoubleAlign, int pointerSize, int wcharSize)
{
    public int LongSize => longSize;
    public int LDoubleSize => ldoubleSize;
    public int LDoubleAlign => ldoubleAlign;
    public int PointerSize => pointerSize;
    public int WcharSize => wcharSize;

    public static readonly DataModel LP64  = new(longSize: 8, ldoubleSize: 16, ldoubleAlign: 16, pointerSize: 8, wcharSize: 4);
    public static readonly DataModel LLP64 = new(longSize: 4, ldoubleSize: 8,  ldoubleAlign: 8,  pointerSize: 8, wcharSize: 2);
}

// ═══════════════════════════════════════════════════════════════════
//  CompilerOptions
// ═══════════════════════════════════════════════════════════════════

public class CompilerOptions
{
    public List<string> IncludePaths = new();
    public bool OptFpic;
    public bool OptFcommon = true;
    public string BaseFile;
    public DataModel DataModel = DataModel.LLP64;
}

// ═══════════════════════════════════════════════════════════════════
//  ChibiException
// ═══════════════════════════════════════════════════════════════════

public class ChibiException : Exception
{
    public ChibiException(string message) : base(message) { }
}
