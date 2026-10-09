using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealSense.Indexer
{
    public enum SymKind : byte { Namespace, Class, Struct, Union, Enum, Enumerator, Function, Variable, Typedef, Using, TemplateParam }

    [Flags]
    public enum DeclFlags : uint
    {
        None = 0, Definition = 1, Static = 2, Const = 4, Virtual = 8, Override = 16, Forward = 32, Scoped = 64, Anonymous = 128,
        Generated = 256, Friend = 512, Template = 1024, Ctor = 2048, Dtor = 4096, Operator = 8192, Specialization = 16384,
        InlineNs = 32768, Field = 65536, Delegate = 131072, Pure = 262144, Typename = 524288, Variadic = 1u << 20, Local = 1u << 21,
    }

    public enum Builtin : byte { None, Void, Bool, Char, Int, Float, Auto, Unknown, NonType, FuncSig, Nullptr, String }

    public sealed class NamePart
    {
        public int Name;
        public TypeExpr[] Args;     // template arguments (null: none)
        public int Tok = -1;        // token index in the file (pass 2 only)
    }

    /// <summary>A type as written (unresolved): qualified name with template arguments, cv, pointer/reference.</summary>
    public sealed class TypeExpr
    {
        public NamePart[] Parts;    // null for builtins
        public bool Global;         // leading ::
        public Builtin Builtin;
        public byte Ptr;
        public bool Ref;
        public bool Const;          // const applies to the pointee/value (outermost written const)
        public TypeExpr FuncReturn; // Builtin.FuncSig
        public TypeExpr[] FuncParams;

        public static readonly TypeExpr UnknownType = new TypeExpr { Builtin = Builtin.Unknown };

        public string Key()
        {
            var sb = new StringBuilder();
            AppendKey(sb);
            return sb.ToString();
        }

        public void AppendKey(StringBuilder sb)
        {
            if (Const) sb.Append("c ");
            if (Parts != null)
            {
                // last component only: qualification may differ between declaration and definition
                var last = Parts[Parts.Length - 1];
                sb.Append(Names.Get(last.Name));
                if (last.Args != null)
                {
                    sb.Append('<');
                    foreach (var a in last.Args) { a?.AppendKey(sb); sb.Append(','); }
                    sb.Append('>');
                }
            }
            else sb.Append(Builtin.ToString());
            for (int i = 0; i < Ptr; i++) sb.Append('*');
            if (Ref) sb.Append('&');
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Const) sb.Append("const ");
            if (Parts != null)
            {
                for (int i = 0; i < Parts.Length; i++)
                {
                    if (i > 0) sb.Append("::");
                    sb.Append(Names.Get(Parts[i].Name));
                    if (Parts[i].Args != null) sb.Append('<').Append(string.Join(", ", Array.ConvertAll(Parts[i].Args, a => a?.ToString() ?? "?"))).Append('>');
                }
            }
            else sb.Append(Builtin.ToString().ToLowerInvariant());
            sb.Append('*', Ptr);
            if (Ref) sb.Append('&');
            return sb.ToString();
        }
    }

    public struct Param
    {
        public TypeExpr Type;
        public int Name;
        public bool HasDefault;
    }

    /// <summary>A declaration as found by pass 1 in one file (merged into <see cref="Symbol"/>s afterwards).</summary>
    public sealed class Decl
    {
        public SymKind Kind;
        public int Name;            // 0 for anonymous
        public int Line, Col;
        public int Parent = -1;     // index of the enclosing Decl in the same file
        public NamePart[] Qualifier;
        public TypeExpr Type;
        public TypeExpr[] Bases;
        public Param[] Params;
        public short MinArgs, MaxArgs;
        public int[] TemplateParams;
        public DeclFlags Flags;
        public bool HasQualifiedAncestor;
    }

    public sealed class Symbol
    {
        public int Id;
        public SymKind Kind;
        public int Name;
        public Symbol Parent;
        public DeclFlags Flags;
        public Dictionary<int, object> Members;     // name → Symbol | List<Symbol>
        public TypeExpr Type;
        public TypeExpr[] BaseExprs;
        public Param[] Params;
        public short MinArgs, MaxArgs;
        public Symbol[] TemplateParams;
        public int TemplateIndex;                   // TemplateParam
        public int File = -1, Line, Col;            // first declaration
        public bool HasDefinition;
        public string SigKey;
        public List<Symbol> AnonymousChildren;      // anonymous structs/unions/namespaces whose members are visible here
        public List<Symbol> Specializations;        // explicit/partial specializations of this class template

        // caches (benign races: computed values are idempotent)
        public TypeInfo[] ResolvedBases;
        public TypeInfo[] DependentBases;           // bases naming one of the class's own template parameters (": public Base")
        public TypeInfo ResolvedType;
        public bool TypeResolved;

        public bool IsClassLike => Kind == SymKind.Class || Kind == SymKind.Struct || Kind == SymKind.Union;
        public bool IsScope => Kind == SymKind.Namespace || IsClassLike || Kind == SymKind.Enum;
        public bool IsType => IsClassLike || Kind == SymKind.Enum || Kind == SymKind.Typedef || Kind == SymKind.TemplateParam;

        public string QualifiedName
        {
            get
            {
                if (Parent == null || Parent.Parent == null && Parent.Name == 0) return Display;
                return Parent.QualifiedName + "::" + Display;
            }
        }

        string Display => Name == 0 ? (Kind == SymKind.Namespace ? "(anonymous)" : "(anonymous " + Kind + ")") : Names.Get(Name);

        public override string ToString() => $"{Kind} {QualifiedName}" + (Kind == SymKind.Function ? "(" + (Params == null ? "" : string.Join(", ", Array.ConvertAll(Params, p => p.Type?.ToString() ?? "?"))) + ")" + ((Flags & DeclFlags.Const) != 0 ? " const" : "") : "");

        public void AddMember(Symbol s, int name)
        {
            Members ??= new Dictionary<int, object>();
            if (!Members.TryGetValue(name, out var existing)) Members[name] = s;
            else if (existing is Symbol one) { if (one != s) Members[name] = new List<Symbol> { one, s }; }
            else { var list = (List<Symbol>)existing; if (!list.Contains(s)) list.Add(s); }
        }

        public object GetMember(int name)
        {
            if (Members != null && Members.TryGetValue(name, out var m)) return m;
            return null;
        }
    }

    /// <summary>A resolved type: symbol + template arguments + pointer/reference/const.</summary>
    public sealed class TypeInfo
    {
        public Symbol Sym;
        public TypeInfo[] Args;
        public Builtin Builtin;
        public byte Ptr;
        public bool Ref;
        public bool Const;
        public Symbol LambdaReturnHint; // unused for now
        public TypeInfo LambdaReturn;

        public static readonly TypeInfo Int = new TypeInfo { Builtin = Builtin.Int };
        public static readonly TypeInfo Float = new TypeInfo { Builtin = Builtin.Float };
        public static readonly TypeInfo Bool = new TypeInfo { Builtin = Builtin.Bool };
        public static readonly TypeInfo Void = new TypeInfo { Builtin = Builtin.Void };
        public static readonly TypeInfo CharPtr = new TypeInfo { Builtin = Builtin.Char, Ptr = 1, Const = true };
        public static readonly TypeInfo Nullptr = new TypeInfo { Builtin = Builtin.Nullptr };

        public TypeInfo With(byte ptr, bool reference, bool cnst)
        {
            if (ptr == Ptr && reference == Ref && cnst == Const) return this;
            return new TypeInfo { Sym = Sym, Args = Args, Builtin = Builtin, Ptr = ptr, Ref = reference, Const = cnst, LambdaReturn = LambdaReturn };
        }

        public TypeInfo Deref() => Ptr > 0 ? With((byte)(Ptr - 1), false, Ptr == 1 ? Const : false) : null;
        public TypeInfo AddrOf() => With((byte)(Ptr + 1), false, Const);
        public TypeInfo Value => Ref ? With(Ptr, false, Const) : this;

        public override string ToString()
        {
            var s = Sym != null ? Sym.QualifiedName : Builtin.ToString();
            if (Args != null && Args.Length > 0) s += "<" + string.Join(", ", Array.ConvertAll(Args, a => a?.ToString() ?? "?")) + ">";
            return (Const ? "const " : "") + s + new string('*', Ptr) + (Ref ? "&" : "");
        }
    }

    public enum RefKind : byte { Decl = 1, Def = 2, Type = 3, Read = 4, Call = 5, Member = 6, Macro = 7, Soup = 8 }

    public struct RefRec
    {
        public int Sym;
        public int Line;
        public int Col;
        public RefKind Kind;
    }

    public struct UnresolvedRec
    {
        public int Name;
        public int Line;
        public int Col;
        public byte Kind;   // 1 = member access on unknown receiver, 2 = member not found on known receiver, 3 = unqualified name not found
    }
}
