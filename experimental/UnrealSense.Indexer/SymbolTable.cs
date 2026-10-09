using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UnrealSense.Indexer
{
    public sealed class FileDecls
    {
        public int FileId;
        public List<Decl> Decls;
        public Symbol[] Map;
    }

    /// <summary>Global symbol table: merged declarations of all files + name lookup and type resolution.</summary>
    public sealed class SymbolTable
    {
        public readonly Symbol Root = new Symbol { Kind = SymKind.Namespace, Name = 0, Id = 0 };
        public readonly List<Symbol> All = new List<Symbol>();
        public static readonly int CtorKey = Names.Intern("(ctor)");
        static readonly int SuperName = K.Super, ThisClassName = K.ThisClass;

        readonly List<Symbol> pendingSpecializations = new List<Symbol>();

        public SymbolTable() { All.Add(Root); }

        Symbol New(SymKind kind, int name, Symbol parent, int file, Decl d)
        {
            var s = new Symbol { Id = All.Count, Kind = kind, Name = name, Parent = parent, File = file, Line = d?.Line ?? 0, Col = d?.Col ?? 0 };
            All.Add(s);
            return s;
        }

        public static int AnonKey(int fileId) => Names.Intern("(anonymous#" + fileId + ")");

        // ------------------------------------------------------------------ merge

        public void Merge(IList<FileDecls> files)
        {
            // Phase A: everything not under a qualified (out-of-line) declaration
            foreach (var f in files)
            {
                f.Map = new Symbol[f.Decls.Count];
                for (int i = 0; i < f.Decls.Count; i++)
                {
                    var d = f.Decls[i];
                    if (d.Qualifier != null || d.HasQualifiedAncestor) continue;
                    var parent = d.Parent >= 0 ? f.Map[d.Parent] : Root;
                    if (parent == null) continue;
                    f.Map[i] = Register(d, parent, f.FileId);
                }
            }
            // Phase B: out-of-line definitions (need the classes from all headers)
            foreach (var f in files)
            {
                for (int i = 0; i < f.Decls.Count; i++)
                {
                    if (f.Map[i] != null) continue;
                    var d = f.Decls[i];
                    var parent = d.Parent >= 0 ? f.Map[d.Parent] : Root;
                    if (parent == null) continue;
                    if (d.Qualifier != null)
                    {
                        var target = ResolveQualifier(d.Qualifier, parent);
                        if (target == null) continue;
                        parent = target;
                    }
                    f.Map[i] = Register(d, parent, f.FileId);
                }
            }
            foreach (var sp in pendingSpecializations)
            {
                var primary = FindMember<Symbol>(sp.Parent, sp.Name, x => x.IsClassLike && (x.Flags & DeclFlags.Specialization) == 0);
                if (primary != null && primary != sp) (primary.Specializations ??= new List<Symbol>()).Add(sp);
            }
            pendingSpecializations.Clear();
            // UCLASS/USTRUCT: Super, ThisClass, StaticClass()/StaticStruct()
            int count = All.Count;
            for (int i = 0; i < count; i++)
            {
                var s = All[i];
                if (!s.IsClassLike || (s.Flags & DeclFlags.Generated) == 0) continue;
                if (s.GetMember(SuperName) == null && s.BaseExprs != null && s.BaseExprs.Length > 0)
                {
                    var sup = New(SymKind.Typedef, SuperName, s, s.File, null);
                    sup.Flags = DeclFlags.Generated; sup.Line = s.Line; sup.Col = s.Col;
                    s.AddMember(sup, SuperName);
                }
                if (s.GetMember(ThisClassName) == null)
                {
                    var tc = new Symbol { Id = All.Count, Kind = SymKind.Typedef, Name = ThisClassName, Parent = s, Flags = DeclFlags.Generated, File = s.File, Line = s.Line, Col = s.Col };
                    All.Add(tc);
                    s.AddMember(tc, ThisClassName);
                }
                bool isStruct = s.Kind == SymKind.Struct;
                int fname = isStruct ? K.StaticStruct : K.StaticClass;
                if (s.GetMember(fname) == null)
                {
                    var fn = New(SymKind.Function, fname, s, s.File, null);
                    fn.Flags = DeclFlags.Static | DeclFlags.Generated; fn.Line = s.Line; fn.Col = s.Col;
                    fn.Type = new TypeExpr { Parts = new[] { new NamePart { Name = isStruct ? K.UScriptStructName : K.UClassName } }, Ptr = 1 };
                    fn.Params = Array.Empty<Param>();
                    s.AddMember(fn, fname);
                }
            }
        }

        Symbol ResolveQualifier(NamePart[] qual, Symbol lexical)
        {
            Symbol cur = null;
            for (int i = 0; i < qual.Length; i++)
            {
                object r = i == 0 ? LookupUnqualified(qual[0].Name, lexical, null, true) : LookupIn(cur, qual[i].Name, true);
                cur = PickScope(r);
                if (cur == null) return null;
                if (cur.Kind == SymKind.Typedef || cur.Kind == SymKind.Using)
                {
                    var ti = cur.Kind == SymKind.Typedef ? ExpandAlias(cur, null, 0) : null;
                    cur = ti?.Sym ?? (cur.Kind == SymKind.Using ? ResolveUsingTarget(cur) as Symbol : null);
                    if (cur == null) return null;
                }
            }
            return cur;
        }

        static Symbol PickScope(object r)
        {
            if (r is Symbol s) return s.IsScope || s.Kind == SymKind.Typedef || s.Kind == SymKind.Using ? s : null;
            if (r is List<Symbol> l)
            {
                foreach (var x in l) if (x.IsClassLike || x.Kind == SymKind.Namespace || x.Kind == SymKind.Enum) return x;
                foreach (var x in l) if (x.Kind == SymKind.Typedef || x.Kind == SymKind.Using) return x;
            }
            return null;
        }

        static T FindMember<T>(Symbol parent, int name, Func<Symbol, bool> pred) where T : class
        {
            var m = parent.GetMember(name);
            if (m is Symbol s) return pred(s) ? s as T : null;
            if (m is List<Symbol> l) foreach (var x in l) if (pred(x)) return x as T;
            return null;
        }

        Symbol Register(Decl d, Symbol parent, int file)
        {
            switch (d.Kind)
            {
                case SymKind.Namespace:
                {
                    if ((d.Flags & DeclFlags.Anonymous) != 0)
                    {
                        int key = AnonKey(file);
                        var ex = FindMember<Symbol>(parent, key, x => x.Kind == SymKind.Namespace);
                        if (ex != null) return ex;
                        var ns = New(SymKind.Namespace, 0, parent, file, d);
                        ns.Flags = DeclFlags.Anonymous;
                        parent.AddMember(ns, key);
                        return ns;
                    }
                    var e = FindMember<Symbol>(parent, d.Name, x => x.Kind == SymKind.Namespace);
                    if (e != null) return e;
                    var n = New(SymKind.Namespace, d.Name, parent, file, d);
                    n.Flags = d.Flags & DeclFlags.InlineNs;
                    parent.AddMember(n, d.Name);
                    if ((d.Flags & DeclFlags.InlineNs) != 0) (parent.AnonymousChildren ??= new List<Symbol>()).Add(n);
                    return n;
                }
                case SymKind.Class:
                case SymKind.Struct:
                case SymKind.Union:
                {
                    if ((d.Flags & DeclFlags.Specialization) != 0 || d.Name == 0)
                    {
                        var sp = New(d.Kind, d.Name, parent, file, d);
                        sp.Flags = d.Flags;
                        sp.BaseExprs = d.Bases;
                        SetTemplateParams(sp, d.TemplateParams);
                        if (d.Name == 0 && parent.IsClassLike || d.Name == 0 && parent.Kind == SymKind.Namespace && false)
                            (parent.AnonymousChildren ??= new List<Symbol>()).Add(sp);
                        // not reachable by name lookup; linked to the primary template after the merge
                        if (d.Name != 0) pendingSpecializations.Add(sp);
                        return sp;
                    }
                    var c = FindMember<Symbol>(parent, d.Name, x => x.IsClassLike);
                    if (c == null)
                    {
                        c = New(d.Kind, d.Name, parent, file, d);
                        parent.AddMember(c, d.Name);
                    }
                    if ((d.Flags & DeclFlags.Forward) == 0)
                    {
                        if (!c.HasDefinition)
                        {
                            c.HasDefinition = true;
                            c.Kind = d.Kind;
                            c.BaseExprs = d.Bases;
                            c.File = file; c.Line = d.Line; c.Col = d.Col;
                            if (d.TemplateParams != null) SetTemplateParams(c, d.TemplateParams);
                        }
                        c.Flags |= d.Flags & (DeclFlags.Generated | DeclFlags.Delegate | DeclFlags.Template);
                    }
                    else if (c.TemplateParams == null && d.TemplateParams != null) SetTemplateParams(c, d.TemplateParams);
                    return c;
                }
                case SymKind.Enum:
                {
                    var en = FindMember<Symbol>(parent, d.Name, x => x.Kind == SymKind.Enum);
                    if (en == null || d.Name == 0)
                    {
                        en = New(SymKind.Enum, d.Name, parent, file, d);
                        if (d.Name != 0) parent.AddMember(en, d.Name);
                    }
                    en.Flags |= d.Flags & (DeclFlags.Scoped | DeclFlags.Anonymous);
                    if (d.Type != null) en.Type = d.Type;
                    if ((d.Flags & DeclFlags.Forward) == 0 && !en.HasDefinition) { en.HasDefinition = true; en.File = file; en.Line = d.Line; en.Col = d.Col; }
                    return en;
                }
                case SymKind.Enumerator:
                {
                    var ex = FindMember<Symbol>(parent, d.Name, x => x.Kind == SymKind.Enumerator && x.Parent == parent);
                    if (ex != null) return ex;
                    var e = New(SymKind.Enumerator, d.Name, parent, file, d);
                    parent.AddMember(e, d.Name);
                    if ((parent.Flags & DeclFlags.Scoped) == 0 && parent.Parent != null) parent.Parent.AddMember(e, d.Name);
                    return e;
                }
                case SymKind.Function:
                    return RegisterFunction(d, parent, file);
                case SymKind.Variable:
                {
                    var v = FindMember<Symbol>(parent, d.Name, x => x.Kind == SymKind.Variable);
                    if (v == null)
                    {
                        v = New(SymKind.Variable, d.Name, parent, file, d);
                        v.Type = d.Type;
                        v.Flags = d.Flags;
                        SetTemplateParams(v, d.TemplateParams);
                        parent.AddMember(v, d.Name);
                    }
                    else
                    {
                        v.Flags |= d.Flags & (DeclFlags.Static | DeclFlags.Field);
                        if (v.Type == null) v.Type = d.Type;
                    }
                    if ((d.Flags & DeclFlags.Definition) != 0) v.HasDefinition = true;
                    return v;
                }
                case SymKind.Typedef:
                case SymKind.Using:
                {
                    var t = FindMember<Symbol>(parent, d.Name, x => x.Kind == d.Kind);
                    if (t != null) return t;
                    t = New(d.Kind, d.Name, parent, file, d);
                    t.Type = d.Type;
                    t.Flags = d.Flags;
                    SetTemplateParams(t, d.TemplateParams);
                    parent.AddMember(t, d.Name);
                    return t;
                }
            }
            return null;
        }

        void SetTemplateParams(Symbol s, int[] names)
        {
            if (names == null || names.Length == 0) return;
            var tps = new Symbol[names.Length];
            if (s.TemplateParams != null && s.TemplateParams.Length == names.Length)
            {
                // re-declaration with possibly different names: rename (definition wins)
                for (int i = 0; i < names.Length; i++)
                {
                    var old = s.TemplateParams[i];
                    if (old.Name != names[i] && names[i] != 0)
                    {
                        s.Members?.Remove(old.Name);
                        old.Name = names[i];
                        s.AddMember(old, names[i]);
                    }
                }
                return;
            }
            for (int i = 0; i < names.Length; i++)
            {
                var tp = new Symbol { Id = All.Count, Kind = SymKind.TemplateParam, Name = names[i], Parent = s, TemplateIndex = i, File = -1 };
                All.Add(tp);
                tps[i] = tp;
                if (names[i] != 0) s.AddMember(tp, names[i]);
            }
            s.TemplateParams = tps;
        }

        public static string SigKey(Param[] ps, DeclFlags flags)
        {
            var sb = new System.Text.StringBuilder();
            if (ps != null) foreach (var p in ps) { p.Type?.AppendKey(sb); sb.Append(','); }
            if ((flags & DeclFlags.Const) != 0) sb.Append(" const");
            return sb.ToString();
        }

        Symbol RegisterFunction(Decl d, Symbol parent, int file)
        {
            int key = (d.Flags & DeclFlags.Ctor) != 0 ? CtorKey : d.Name;
            var sig = SigKey(d.Params, d.Flags);
            var existing = parent.GetMember(key);
            List<Symbol> funcs = null;
            if (existing is Symbol one) { if (one.Kind == SymKind.Function) funcs = new List<Symbol> { one }; }
            else if (existing is List<Symbol> l) funcs = l.Where(x => x.Kind == SymKind.Function).ToList();
            Symbol match = null;
            if (funcs != null)
            {
                match = funcs.FirstOrDefault(f => f.SigKey == sig && ((f.TemplateParams?.Length ?? 0) == (d.TemplateParams?.Length ?? 0)));
                if (match == null && (d.Flags & DeclFlags.Definition) != 0 && d.Qualifier != null)
                {
                    int n = d.Params?.Length ?? 0;
                    bool cst = (d.Flags & DeclFlags.Const) != 0;
                    var cands = funcs.Where(f => (f.Params?.Length ?? 0) == n && ((f.Flags & DeclFlags.Const) != 0) == cst && !f.HasDefinition).ToList();
                    if (cands.Count == 1) match = cands[0];
                    else if (funcs.Count == 1 && !funcs[0].HasDefinition) match = funcs[0];
                }
            }
            if (match != null)
            {
                if ((d.Flags & DeclFlags.Definition) != 0) match.HasDefinition = true;
                match.Flags |= d.Flags & (DeclFlags.Virtual | DeclFlags.Override | DeclFlags.Static);
                if (match.Type == null && d.Type != null) match.Type = d.Type;
                if (d.MinArgs < match.MinArgs) match.MinArgs = d.MinArgs;
                return match;
            }
            var fn = New(SymKind.Function, d.Name, parent, file, d);
            fn.Type = d.Type;
            fn.Params = d.Params;
            fn.MinArgs = d.MinArgs; fn.MaxArgs = d.MaxArgs;
            fn.Flags = d.Flags;
            fn.SigKey = sig;
            fn.HasDefinition = (d.Flags & DeclFlags.Definition) != 0;
            SetTemplateParams(fn, d.TemplateParams);
            parent.AddMember(fn, key);
            return fn;
        }

        /// <summary>
        /// Fills the lazy per-symbol caches (bases, declared types, alias targets) in a fixed order before pass 2. Filled lazily by
        /// pass 2's threads, a value cut short by a recursion guard was cached as it happened to be computed first: two builds of
        /// the same sources could differ in a few references.
        /// </summary>
        public void PrecomputeTypes()
        {
            int n = All.Count;
            for (int i = 0; i < n; i++) if (All[i].IsClassLike) GetBases(All[i]);
            for (int i = 0; i < n; i++)
            {
                var s = All[i];
                switch (s.Kind)
                {
                    case SymKind.Typedef: if ((s.Flags & DeclFlags.Generated) == 0) ExpandAlias(s, null, 0); break;
                    case SymKind.Using: ResolveUsingTarget(s); break;
                    case SymKind.Variable: case SymKind.Function: case SymKind.Enumerator: DeclaredType(s); break;
                }
            }
        }

        // ------------------------------------------------------------------ lookup

        /// <summary>Name lookup inside one scope (class: with bases). Result: Symbol, List&lt;Symbol&gt; or null.</summary>
        [ThreadStatic] static int guard;

        public object LookupIn(Symbol scope, int name, bool preferTypes = false, int depth = 0)
        {
            if (scope == null || depth > 24 || guard > 48) return null;
            guard++;
            try { return LookupInCore(scope, name, preferTypes, depth); }
            finally { guard--; }
        }

        object LookupInCore(Symbol scope, int name, bool preferTypes, int depth)
        {
            if (scope.Kind == SymKind.Typedef || scope.Kind == SymKind.Using)
            {
                var t = scope.Kind == SymKind.Typedef ? ExpandAlias(scope, null, depth + 1)?.Sym : ResolveUsingTarget(scope) as Symbol;
                return t == null || t == scope ? null : LookupIn(t, name, preferTypes, depth + 1);
            }
            if (scope.IsClassLike && name == scope.Name && name != 0) return scope; // injected class name
            var m = scope.GetMember(name);
            if (m != null)
            {
                if (preferTypes && m is List<Symbol> list)
                {
                    foreach (var x in list) if (x.IsType || x.Kind == SymKind.Namespace) return x;
                }
                return m;
            }
            if (scope.AnonymousChildren != null)
                foreach (var a in scope.AnonymousChildren)
                {
                    var r = LookupIn(a, name, preferTypes, depth + 1);
                    if (r != null) return r;
                }
            if (scope.IsClassLike)
            {
                var bases = GetBases(scope);
                if (bases != null)
                    foreach (var b in bases)
                    {
                        if (b?.Sym == null || b.Sym == scope) continue;
                        var r = LookupIn(b.Sym, name, preferTypes, depth + 1);
                        if (r != null) return r;
                    }
            }
            return null;
        }

        /// <summary>Unqualified lookup walking the semantic parents of <paramref name="start"/>; usings checked last.</summary>
        public object LookupUnqualified(int name, Symbol start, LexicalEnv lex, bool preferTypes = false)
        {
            for (var s = start; s != null; s = s.Parent)
            {
                object r;
                if (s.Kind == SymKind.Function)
                {
                    r = s.GetMember(name); // template params
                    if (r != null) return r;
                    continue;
                }
                r = LookupIn(s, name, preferTypes);
                if (r != null) return r;
                if (s.Kind == SymKind.Namespace && lex != null)
                {
                    var anon = s.GetMember(lex.AnonKey);
                    if (anon is Symbol an)
                    {
                        r = LookupIn(an, name, preferTypes);
                        if (r != null) return r;
                    }
                }
            }
            if (lex?.Usings != null)
                foreach (var u in lex.Usings)
                {
                    var r = LookupIn(u, name, preferTypes);
                    if (r != null) return r;
                }
            return null;
        }

        public object ResolveUsingTarget(Symbol u, int depth = 0)
        {
            if (u.ResolvedType != null) return u.ResolvedType.Sym;
            if (u.TypeResolved || depth > 8) return null;
            var e = u.Type;
            if (e?.Parts == null) { u.TypeResolved = true; return null; }
            object cur = null;
            Symbol scope = null;
            for (int i = 0; i < e.Parts.Length; i++)
            {
                if (i == 0) cur = e.Global ? LookupIn(Root, e.Parts[0].Name) : LookupUnqualified(e.Parts[0].Name, u.Parent, null);
                else cur = scope != null ? LookupIn(scope, e.Parts[i].Name) : null;
                if (cur == null) break;
                if (i < e.Parts.Length - 1)
                {
                    scope = PickScope(cur);
                    if (scope != null && scope.Kind == SymKind.Typedef) scope = ExpandAlias(scope, null, depth + 1)?.Sym;
                    if (scope == null) { cur = null; break; }
                }
            }
            u.TypeResolved = true;
            if (cur is Symbol cs && cs != u)
            {
                u.ResolvedType = new TypeInfo { Sym = cs };
                return cs;
            }
            if (cur is List<Symbol> l) return l;
            return null;
        }

        // ------------------------------------------------------------------ types

        public TypeInfo[] GetBases(Symbol cls)
        {
            var rb = cls.ResolvedBases;
            if (rb != null) return rb;
            if (cls.BaseExprs == null) return cls.ResolvedBases = Array.Empty<TypeInfo>();
            if (resolvingBases.Contains(cls)) return null;
            resolvingBases.Add(cls);
            try
            {
                var arr = new TypeInfo[cls.BaseExprs.Length];
                List<TypeInfo> dependent = null;
                for (int i = 0; i < arr.Length; i++)
                {
                    // bases are looked up from the enclosing scope, with the class's own template parameters visible
                    arr[i] = ResolveTypeExpr(cls.BaseExprs[i], cls, null, null, 0, baseOf: cls);
                    if (arr[i]?.Sym != null && !arr[i].Sym.IsClassLike)
                    {
                        // kept apart: resolvable only through an instance's template arguments (member lookup substitutes them)
                        if (arr[i].Sym.Kind == SymKind.TemplateParam && arr[i].Sym.Parent == cls)
                            (dependent ??= new List<TypeInfo>()).Add(arr[i]);
                        arr[i] = null;
                    }
                }
                if (dependent != null) cls.DependentBases = dependent.ToArray();
                cls.ResolvedBases = arr;
                return arr;
            }
            finally { resolvingBases.Remove(cls); }
        }

        [ThreadStatic] static HashSet<Symbol> resolvingBasesTls;
        static HashSet<Symbol> resolvingBases => resolvingBasesTls ??= new HashSet<Symbol>();

        /// <summary>Callback for reference emission during type resolution (pass 2).</summary>
        public delegate void RefCallback(Symbol s, int tok);

        public TypeInfo ResolveTypeExpr(TypeExpr e, Symbol scope, LexicalEnv lex, RefCallback emit, int depth, Symbol baseOf = null)
        {
            if (e == null || depth > 16) return null;
            if (e.Parts == null)
            {
                switch (e.Builtin)
                {
                    case Builtin.Unknown: case Builtin.NonType: case Builtin.FuncSig:
                        if (e.Builtin == Builtin.FuncSig && emit != null)
                        {
                            ResolveTypeExpr(e.FuncReturn, scope, lex, emit, depth + 1);
                            if (e.FuncParams != null) foreach (var fp in e.FuncParams) ResolveTypeExpr(fp, scope, lex, emit, depth + 1);
                        }
                        if (e.Builtin == Builtin.FuncSig) return new TypeInfo { Builtin = Builtin.FuncSig, LambdaReturn = ResolveTypeExpr(e.FuncReturn, scope, lex, null, depth + 1), Ptr = e.Ptr };
                        return null;
                    default:
                        return new TypeInfo { Builtin = e.Builtin, Ptr = e.Ptr, Ref = e.Ref, Const = e.Const };
                }
            }
            object cur = null;
            Symbol curSym = null;
            TypeInfo curType = null;
            for (int i = 0; i < e.Parts.Length; i++)
            {
                var part = e.Parts[i];
                if (i == 0)
                {
                    if (e.Global) cur = LookupIn(Root, part.Name, true);
                    else if (baseOf != null)
                    {
                        cur = baseOf.GetMember(part.Name);
                        if (cur is Symbol tp0 && tp0.Kind != SymKind.TemplateParam) cur = null;
                        if (cur == null) cur = LookupUnqualified(part.Name, baseOf.Parent, lex, true);
                    }
                    else cur = LookupUnqualified(part.Name, scope, lex, true);
                }
                else
                {
                    if (curSym == null) return null;
                    if (curSym.Kind == SymKind.TemplateParam) return null; // dependent
                    var scopeSym = curType?.Sym ?? curSym;
                    cur = LookupIn(scopeSym, part.Name, true);
                    if (cur == null && curType?.Sym != null && curType.Sym.IsClassLike)
                    {
                        // member typedef of a template instance handled below via substitution
                    }
                }
                curSym = PickType(cur);
                if (curSym == null) return null;
                if (emit != null && part.Tok >= 0) emit(curSym, part.Tok);
                TypeInfo[] args = null;
                if (part.Args != null)
                {
                    args = new TypeInfo[part.Args.Length];
                    for (int a = 0; a < args.Length; a++) args[a] = ResolveTypeExpr(part.Args[a], scope, lex, emit, depth + 1, baseOf);
                }
                if (curSym.Kind == SymKind.Typedef || curSym.Kind == SymKind.Using)
                {
                    var exp = curSym.Kind == SymKind.Typedef ? ExpandAlias(curSym, args, depth + 1) : (ResolveUsingTarget(curSym) is Symbol us ? new TypeInfo { Sym = us } : null);
                    // member typedef reached through a template instance: substitute the instance's arguments
                    if (exp != null && curType != null && curType.Args != null && curSym.Parent != null)
                        exp = Subst(exp, curSym.Parent, curType.Args);
                    curType = exp;
                    if (curType == null) return null;
                    curSym = curType.Sym ?? curSym;
                    if (curType.Sym == null) { if (i == e.Parts.Length - 1) break; return null; }
                }
                else if (curSym.Kind == SymKind.TemplateParam)
                {
                    curType = new TypeInfo { Sym = curSym };
                }
                else curType = new TypeInfo { Sym = curSym, Args = args };
            }
            if (curType == null) return null;
            if (e.Ptr == 0 && !e.Ref && !e.Const) return curType;
            return curType.With((byte)(curType.Ptr + e.Ptr), e.Ref || curType.Ref, e.Const || curType.Const);
        }

        static Symbol PickType(object r)
        {
            if (r is Symbol s) return s.IsType || s.Kind == SymKind.Namespace || s.Kind == SymKind.Using ? s : null;
            if (r is List<Symbol> l)
            {
                foreach (var x in l) if (x.IsClassLike || x.Kind == SymKind.Enum) return x;
                foreach (var x in l) if (x.IsType || x.Kind == SymKind.Namespace) return x;
            }
            return null;
        }

        /// <summary>Expands a typedef (alias template: substituting <paramref name="args"/>).</summary>
        public TypeInfo ExpandAlias(Symbol td, TypeInfo[] args, int depth)
        {
            if (depth > 16 || guard > 48) return null;
            guard++;
            try { return ExpandAliasCore(td, args, depth); }
            finally { guard--; }
        }

        TypeInfo ExpandAliasCore(Symbol td, TypeInfo[] args, int depth)
        {
            TypeInfo target;
            if ((td.Flags & DeclFlags.Generated) != 0)
            {
                var cls = td.Parent;
                if (td.Name == SuperName) { var b = GetBases(cls); target = b != null && b.Length > 0 ? b[0] : null; }
                else target = new TypeInfo { Sym = cls, Args = cls.TemplateParams?.Select(tp => new TypeInfo { Sym = tp }).ToArray() };
                return target;
            }
            if (!td.TypeResolved)
            {
                var r = ResolveTypeExpr(td.Type, td.TemplateParams != null ? td : td.Parent, null, null, depth + 1);
                if (r?.Sym == td) r = null;
                td.ResolvedType = r;
                td.TypeResolved = true;
            }
            target = td.ResolvedType;
            if (target != null && args != null && td.TemplateParams != null) target = Subst(target, td, args);
            return target;
        }

        public static TypeInfo Subst(TypeInfo t, Symbol owner, TypeInfo[] args, int depth = 0)
        {
            if (t == null || args == null || depth > 12) return t;
            if (t.Sym != null && t.Sym.Kind == SymKind.TemplateParam && t.Sym.Parent == owner)
            {
                int i = t.Sym.TemplateIndex;
                if (i < args.Length && args[i] != null)
                {
                    var a = args[i];
                    return a.With((byte)(a.Ptr + t.Ptr), t.Ref || a.Ref, t.Const || a.Const);
                }
                return t;
            }
            if (t.Args == null) return t;
            TypeInfo[] na = null;
            for (int k = 0; k < t.Args.Length; k++)
            {
                var s = Subst(t.Args[k], owner, args, depth + 1);
                if (s != t.Args[k]) { na ??= (TypeInfo[])t.Args.Clone(); na[k] = s; }
            }
            if (na == null) return t;
            return new TypeInfo { Sym = t.Sym, Args = na, Builtin = t.Builtin, Ptr = t.Ptr, Ref = t.Ref, Const = t.Const };
        }

        /// <summary>The declared type of a variable / return type of a function (raw: may contain template params).</summary>
        public TypeInfo DeclaredType(Symbol s)
        {
            if (s.TypeResolved) return s.ResolvedType;
            TypeInfo r = null;
            if (s.Kind == SymKind.Variable || s.Kind == SymKind.Function)
                r = ResolveTypeExpr(s.Type, s.Kind == SymKind.Function ? s : (s.TemplateParams != null ? s : s.Parent), null, null, 0);
            else if (s.Kind == SymKind.Enumerator) r = new TypeInfo { Sym = s.Parent };
            s.ResolvedType = r;
            s.TypeResolved = true;
            return r;
        }
    }

    /// <summary>Lexical information of the current file position used by unqualified lookup.</summary>
    public sealed class LexicalEnv
    {
        public int AnonKey;
        public List<Symbol> Usings;
    }
}
