using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealSense.Indexer
{
    /// <summary>Result of an expression: a value type, a pending overload set, a type name or a namespace.</summary>
    sealed class Val
    {
        public TypeInfo Type;
        public List<Symbol> Funcs;
        public int FuncTok = -1;
        public Symbol Owner;
        public TypeInfo[] OwnerArgs;
        public TypeInfo[] TArgs;
        public bool ConstRecv;
        public TypeInfo TypeVal;      // the expression names a type
        public Symbol Scope;          // namespace (or class used as qualifier)
        public bool Dependent;        // depends on a template parameter (overload resolution is deferred)
        public static readonly Val Unknown = new Val();
        public static Val Of(TypeInfo t) => t == null ? Unknown : new Val { Type = t };
    }

    struct MemberHit
    {
        public object Members;
        public Symbol Owner;
        public TypeInfo[] OwnerArgs;
    }

    sealed class Local
    {
        public int Name;
        public TypeInfo Type;
        public bool IsType;
    }

    /// <summary>
    /// Pass 2 for one file: re-walks declarations (same parser as pass 1) emitting declaration/type references,
    /// and resolves function bodies with a light expression type inference.
    /// </summary>
    public sealed class Pass2 : IDeclSink
    {
        readonly SymbolTable S;
        readonly FileDecls F;
        public DeclParser Parser;
        public Func<int, List<int>> GeneratedBodyIdents;   // line of GENERATED_BODY → identifiers in its expansion
        Token[] T;
        int end;
        public readonly List<RefRec> Refs = new List<RefRec>();
        public readonly List<UnresolvedRec> Unresolved = new List<UnresolvedRec>();
        readonly LexicalEnv lex;
        int[] emitted;                // last symbol id emitted at each token (dedup)
        public int ResolvedMember, UnresolvedMemberUnknownRecv, UnresolvedMemberNotFound, UnresolvedNames;

        // body context
        Symbol fnSym, thisClass, scopeStart;
        TypeInfo thisType;
        bool constThis;
        readonly List<Local> locals = new List<Local>(64);
        int macroDepth;

        public Pass2(SymbolTable s, FileDecls f)
        {
            S = s; F = f;
            lex = new LexicalEnv { AnonKey = SymbolTable.AnonKey(f.FileId) };
        }

        public void Run(Token[] tokens, int count, FileMacros macros)
        {
            Parser = new DeclParser(tokens, count, macros, this);
            T = Parser.T;
            end = count;
            emitted = new int[count + 1];
            Parser.ParseFile();
        }

        // ------------------------------------------------------------------ emission

        void Emit(Symbol s, int tok, RefKind kind)
        {
            if (s == null || tok < 0 || tok >= T.Length) return;
            var t = T[tok];
            if (t.Virtual || t.Kind != TK.Ident && !(t.Kind == TK.Punct && t.Value == '~')) return;
            if (T == Parser.T)
            {
                if (emitted[tok] == s.Id + 1) return;
                emitted[tok] = s.Id + 1;
            }
            else return; // tokens of a macro expansion
            if (s.Kind == SymKind.TemplateParam) return;
            Refs.Add(new RefRec { Sym = s.Id, Line = t.Line, Col = t.Col, Kind = kind });
        }

        void EmitUnresolved(int tok, byte kind)
        {
            if (tok < 0 || T != Parser.T) return;
            var t = T[tok];
            if (t.Virtual || t.Kind != TK.Ident) return;
            Unresolved.Add(new UnresolvedRec { Name = t.Value, Line = t.Line, Col = t.Col, Kind = kind });
        }

        SymbolTable.RefCallback emitType;
        SymbolTable.RefCallback EmitTypeCb => emitType ??= (s, tok) => Emit(s, tok, RefKind.Type);

        Symbol ScopeSym(ScopeCtx ctx)
        {
            for (var c = ctx; c != null; c = c.Outer) if (c.Sym != null) return c.Sym;
            return S.Root;
        }

        // ------------------------------------------------------------------ IDeclSink

        public Symbol OnDecl(Decl d, int index, int nameTok, ScopeCtx ctx)
        {
            var sym = index < F.Map.Length ? F.Map[index] : null;
            if (sym == null || d.Name == 0 || nameTok < 0) return sym;
            var kind = (d.Flags & DeclFlags.Definition) != 0 ? RefKind.Def : RefKind.Decl;
            if ((d.Flags & (DeclFlags.Ctor | DeclFlags.Dtor)) != 0 && sym.Parent != null && sym.Parent.IsClassLike)
            {
                Emit(sym.Parent, nameTok, RefKind.Type);
                emitted[nameTok] = 0;
            }
            if (d.Kind == SymKind.Namespace) return sym;
            Emit(sym, nameTok, kind);
            return sym;
        }

        public void OnDeclTypes(Decl d, Symbol sym, ScopeCtx ctx, int qualTokStart)
        {
            var scope = ScopeSym(ctx);
            // qualifier: A::B::name
            if (d.Qualifier != null)
            {
                object cur = null;
                Symbol sc = null;
                for (int i = 0; i < d.Qualifier.Length; i++)
                {
                    var part = d.Qualifier[i];
                    cur = i == 0 ? S.LookupUnqualified(part.Name, scope, lex, true) : (sc != null ? S.LookupIn(sc, part.Name, true) : null);
                    sc = PickScopeSym(cur);
                    if (sc == null) break;
                    Emit(sc, part.Tok, RefKind.Type);
                    if (part.Args != null) foreach (var a in part.Args) S.ResolveTypeExpr(a, scope, lex, EmitTypeCb, 0);
                    if (sc.Kind == SymKind.Typedef) sc = S.ExpandAlias(sc, null, 0)?.Sym;
                }
            }
            var inner = sym ?? scope;
            if (d.Kind == SymKind.Function)
            {
                var fscope = sym ?? scope;
                if (d.Type != null) S.ResolveTypeExpr(d.Type, fscope, lex, EmitTypeCb, 0);
                if (d.Params != null) foreach (var p in d.Params) S.ResolveTypeExpr(p.Type, fscope, lex, EmitTypeCb, 0);
            }
            else if (d.Kind == SymKind.Class || d.Kind == SymKind.Struct || d.Kind == SymKind.Union)
            {
                if (d.Bases != null)
                    foreach (var b in d.Bases) S.ResolveTypeExpr(b, scope, lex, EmitTypeCb, 0, baseOf: sym != null && sym.TemplateParams != null ? sym : null);
            }
            else if (d.Kind == SymKind.Typedef)
            {
                S.ResolveTypeExpr(d.Type, sym?.TemplateParams != null ? sym : scope, lex, EmitTypeCb, 0);
            }
            else if (d.Kind == SymKind.Using)
            {
                if (d.Type?.Parts != null)
                {
                    object cur = null; Symbol sc = null;
                    for (int i = 0; i < d.Type.Parts.Length; i++)
                    {
                        var part = d.Type.Parts[i];
                        cur = i == 0 ? (d.Type.Global ? S.LookupIn(S.Root, part.Name) : S.LookupUnqualified(part.Name, scope, lex)) : (sc != null ? S.LookupIn(sc, part.Name) : null);
                        if (cur == null) break;
                        if (cur is Symbol cs) Emit(cs, part.Tok, RefKind.Type);
                        else if (cur is List<Symbol> l && l.Count > 0)
                        {
                            // "using Base::Draw;" brings in every overload: each one is referenced (clangd: one ref per target)
                            if (i == d.Type.Parts.Length - 1) foreach (var x in l) { emitted[part.Tok] = 0; Emit(x, part.Tok, RefKind.Type); }
                            else Emit(l[0], part.Tok, RefKind.Type);
                        }
                        sc = PickScopeSym(cur);
                        if (sc != null && sc.Kind == SymKind.Typedef) sc = S.ExpandAlias(sc, null, 0)?.Sym;
                    }
                }
            }
        }

        static Symbol PickScopeSym(object r)
        {
            if (r is Symbol s) return s.IsScope || s.Kind == SymKind.Typedef || s.Kind == SymKind.Using ? s : null;
            if (r is List<Symbol> l)
            {
                foreach (var x in l) if (x.IsClassLike || x.Kind == SymKind.Namespace || x.Kind == SymKind.Enum) return x;
                foreach (var x in l) if (x.Kind == SymKind.Typedef) return x;
            }
            return null;
        }

        public void OnType(TypeExpr t, ScopeCtx ctx) => S.ResolveTypeExpr(t, ScopeSym(ctx), lex, EmitTypeCb, 0);

        public void OnUsingDirective(TypeExpr ns, ScopeCtx ctx)
        {
            if (ns?.Parts == null) return;
            var scope = ScopeSym(ctx);
            Symbol cur = null;
            for (int i = 0; i < ns.Parts.Length; i++)
            {
                var r = i == 0 ? (ns.Global ? S.LookupIn(S.Root, ns.Parts[0].Name) : S.LookupUnqualified(ns.Parts[0].Name, scope, lex)) : S.LookupIn(cur, ns.Parts[i].Name);
                cur = PickScopeSym(r);
                if (cur == null) return;
                if (cur.Kind == SymKind.Using) cur = S.ResolveUsingTarget(cur) as Symbol;
                if (cur == null) return;
                Emit(cur, ns.Parts[i].Tok, RefKind.Type);
            }
            if (cur != null && cur.Kind == SymKind.Namespace) (lex.Usings ??= new List<Symbol>()).Add(cur);
        }

        public void OnGeneratedBody(int tok, ScopeCtx ctx)
        {
            // GENERATED_BODY() expands (via the UHT .generated.h) to code naming types: clangd reports those
            // references at the macro location. Types named anywhere in the expansion are emitted here.
            if (ctx.Sym == null || !ctx.Sym.IsClassLike) return;
            var idents = GeneratedBodyIdents?.Invoke(T[tok].Line);
            if (idents != null) { EmitTypesNamed(tok, ctx, idents); return; }
            if (ctx.Sym.Kind == SymKind.Class) Emit(ctx.Sym, tok, RefKind.Type);
            var bases = S.GetBases(ctx.Sym);
            if (bases != null && bases.Length > 0 && bases[0]?.Sym != null) { emitted[tok] = 0; Emit(bases[0].Sym, tok, RefKind.Type); }
        }

        /// <summary>Types named in a macro expansion are referenced at the macro name (clangd reports expansion references there).</summary>
        void EmitTypesNamed(int tok, ScopeCtx ctx, List<int> idents)
        {
            foreach (var id in idents)
            {
                var r = S.LookupUnqualified(id, ctx.Sym, lex, true);
                var sym = r as Symbol ?? (r is List<Symbol> l ? l.FirstOrDefault(x => x.IsClassLike || x.Kind == SymKind.Enum) : null);
                if (sym == null || !(sym.IsClassLike || sym.Kind == SymKind.Enum)) continue;
                emitted[tok] = 0;
                Emit(sym, tok, RefKind.Type);
            }
        }

        /// <summary>
        /// A function-like macro invoked in a class body that declares members (HIDE_ACTOR_TRANSFORM_FUNCTIONS(), SLATE_ARGUMENT...):
        /// like GENERATED_BODY, the types its body names are referenced at the macro name. The arguments are resolved as code.
        /// </summary>
        public void OnClassBodyMacro(int tok, ScopeCtx ctx)
        {
            if (ctx.Sym == null || !ctx.Sym.IsClassLike || T[tok].Kind != TK.Ident) return;
            var m = Parser.Macros.Find(T[tok].Value);
            if (m == null || m.Body.Length == 0) return;
            var idents = new List<int>();
            var seen = new HashSet<int>();
            void Walk(MacroDef md, int depth)
            {
                if (depth > 8) return;
                foreach (var t in md.Body)
                {
                    if (t.Kind != TK.Ident || Array.IndexOf(md.Params, t.Value) >= 0 || !seen.Add(t.Value)) continue;
                    var sub = Parser.Macros.Find(t.Value);
                    if (sub != null) { Walk(sub, depth + 1); continue; }
                    idents.Add(t.Value);
                }
            }
            Walk(m, 0);
            EmitTypesNamed(tok, ctx, idents);
        }

        public void OnNamespaceAlias(int nameTok, TypeExpr target, ScopeCtx ctx) { }

        public void OnSoup(int start, int stop, ScopeCtx ctx)
        {
            var save = SaveCtx();
            SetCtx(null, ctx.IsClass ? ctx.Sym : null, ScopeSym(ctx));
            Soup(start, stop);
            RestoreCtx(save);
        }

        public void OnExpr(int start, int stop, ScopeCtx ctx, Symbol owner)
        {
            var save = SaveCtx();
            var cls = ctx.IsClass ? ctx.Sym : null;
            if (owner != null && owner.Kind == SymKind.Function) SetCtx(owner, owner.Parent != null && owner.Parent.IsClassLike ? owner.Parent : cls, owner);
            else SetCtx(null, cls, ScopeSym(ctx));
            int oldEnd = end;
            end = stop;
            int p = start;
            try
            {
                if (p < stop && (T[p].Is('{') || T[p].Is('(')) && Parser.Match[p] == stop - 1)
                {
                    int close = stop - 1;
                    p++;
                    ExprList(ref p, close);
                }
                else
                {
                    while (p < stop)
                    {
                        int before = p;
                        Assign(ref p);
                        if (p < stop && T[p].Is(',')) p++;
                        if (p == before) { SoupToken(p); p++; }
                    }
                }
            }
            finally { end = oldEnd; RestoreCtx(save); }
        }

        public void OnBody(int open, int close, ScopeCtx ctx, Symbol fn, Decl decl, TypeExpr ownerOverride, int ctorInitStart)
        {
            var save = SaveCtx();
            Symbol cls = null;
            if (fn != null && fn.Parent != null && fn.Parent.IsClassLike) cls = fn.Parent;
            else if (ownerOverride != null) cls = S.ResolveTypeExpr(ownerOverride, ScopeSym(ctx), lex, null, 0)?.Sym;
            else if (fn == null && decl != null && decl.Qualifier != null)
            {
                // unresolved out-of-line definition: try the qualifier as class context
                var q = new TypeExpr { Parts = decl.Qualifier };
                cls = S.ResolveTypeExpr(q, ScopeSym(ctx), lex, null, 0)?.Sym;
            }
            if (cls != null && !cls.IsClassLike) cls = null;
            SetCtx(fn, cls, fn ?? cls ?? ScopeSym(ctx));
            constThis = decl != null && (decl.Flags & DeclFlags.Const) != 0;
            locals.Clear();
            // parameters
            if (decl?.Params != null)
                foreach (var prm in decl.Params)
                    if (prm.Name != 0) locals.Add(new Local { Name = prm.Name, Type = S.ResolveTypeExpr(prm.Type, scopeStart, lex, null, 0) });
            if (ownerOverride != null)
            {
                // DEFINE_FUNCTION(Class::execX): void execX(UObject* Context, FFrame& Stack, void*const Z_Param__Result)
                locals.Add(new Local { Name = ContextName, Type = UObjectPtr() });
                locals.Add(new Local { Name = ResultName, Type = new TypeInfo { Builtin = Builtin.Void, Ptr = 1 } });
            }
            int oldEnd = end;
            try
            {
                if (ctorInitStart >= 0) CtorInitializers(ctorInitStart, open);
                end = close + 1;
                int p = open;
                Block(ref p);
            }
            catch (Exception) { }
            finally { end = oldEnd; locals.Clear(); RestoreCtx(save); }
        }

        static readonly int ContextName = Names.Intern("Context"), ResultName = Names.Intern("Z_Param__Result"), StackName = Names.Intern("Stack");
        TypeInfo uobjectPtr;
        TypeInfo UObjectPtr()
        {
            if (uobjectPtr != null) return uobjectPtr;
            var u = S.LookupIn(S.Root, K.UObjectName, true) as Symbol;
            return uobjectPtr = u != null ? new TypeInfo { Sym = u, Ptr = 1 } : null;
        }

        (Symbol, Symbol, Symbol, TypeInfo, bool, int) SaveCtx() => (fnSym, thisClass, scopeStart, thisType, constThis, locals.Count);
        void RestoreCtx((Symbol, Symbol, Symbol, TypeInfo, bool, int) s)
        {
            (fnSym, thisClass, scopeStart, thisType, constThis, _) = s;
            if (locals.Count > s.Item6) locals.RemoveRange(s.Item6, locals.Count - s.Item6);
        }

        void SetCtx(Symbol fn, Symbol cls, Symbol start)
        {
            fnSym = fn; thisClass = cls; scopeStart = start ?? S.Root;
            thisType = cls != null ? new TypeInfo { Sym = cls, Args = cls.TemplateParams?.Select(tp => new TypeInfo { Sym = tp }).ToArray() } : null;
            constThis = false;
        }

        void CtorInitializers(int p, int open)
        {
            int saveEnd = end;
            end = open;
            while (p < open)
            {
                if (T[p].Is(',')) { p++; continue; }
                if (T[p].Kind == TK.Ident || T[p].Is(P.Scope))
                {
                    int s = p;
                    var te = Parser.ParseType(ref p, open, false);
                    if (te?.Parts != null && te.Parts.Length == 1 && te.Parts[0].Args == null && thisClass != null)
                    {
                        // member or base
                        var hit = LookupMember(thisClass, thisType?.Args, te.Parts[0].Name, 0, false);
                        var m = First(hit.Members);
                        if (m != null && m.Kind == SymKind.Variable) Emit(m, te.Parts[0].Tok, RefKind.Member);
                        else S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
                    }
                    else if (te != null) S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
                    if (p < open && (T[p].Is('(') || T[p].Is('{')))
                    {
                        int close = Parser.Match[p] > p ? Parser.Match[p] : open;
                        int q = p + 1;
                        ExprList(ref q, close);
                        p = close + 1;
                    }
                    if (p == s) p++;
                    continue;
                }
                p++;
            }
            end = saveEnd;
        }

        // ------------------------------------------------------------------ soup (macro arguments, unparsed regions)

        void Soup(int p, int stop)
        {
            while (p < stop)
            {
                if (T[p].Kind != TK.Ident || DeclParser.IsKeyword(T[p].Value)) { p++; continue; }
                if (p > 0 && (T[p - 1].Is('.') || T[p - 1].Is(P.Arrow))) { p++; continue; }
                if (p > 0 && T[p - 1].Is(P.Scope) && p > 1 && T[p - 2].Kind == TK.Ident) { p++; continue; }
                p = SoupName(p, stop);
            }
        }

        void SoupToken(int p) { if (p < end && T[p].Kind == TK.Ident) SoupName(p, p + 1); }

        int SoupName(int p, int stop)
        {
            object cur = LookupName(T[p].Value, out Local loc);
            if (loc != null) return p + 1;
            int tok = p;
            p++;
            while (true)
            {
                Symbol s = First(cur);
                if (s == null) return p;
                if (cur is List<Symbol> l) s = l.FirstOrDefault(x => x.IsType || x.Kind == SymKind.Namespace) ?? s;
                Emit(s, tok, s.IsType || s.Kind == SymKind.Namespace ? RefKind.Type : RefKind.Soup);
                if (p + 1 < stop && T[p].Is(P.Scope) && T[p + 1].Kind == TK.Ident)
                {
                    var sc = PickScopeSym(cur);
                    if (sc == null) return p;
                    if (sc.Kind == SymKind.Typedef) sc = S.ExpandAlias(sc, null, 0)?.Sym;
                    if (sc == null) return p;
                    tok = p + 1;
                    cur = S.LookupIn(sc, T[tok].Value);
                    p += 2;
                    continue;
                }
                return p;
            }
        }

        static Symbol First(object r) => r is Symbol s ? s : r is List<Symbol> l && l.Count > 0 ? l[0] : null;

        // ------------------------------------------------------------------ lookup

        object LookupName(int name, out Local local)
        {
            for (int i = locals.Count - 1; i >= 0; i--)
                if (locals[i].Name == name) { local = locals[i]; return null; }
            local = null;
            if (fnSym != null)
            {
                var tp = fnSym.GetMember(name);
                if (tp != null) return tp;
            }
            if (thisClass != null)
            {
                var hit = LookupMember(thisClass, thisType?.Args, name, 0, false);
                if (hit.Members != null) { lastHit = hit; return hit.Members; }
            }
            lastHit = default;
            return S.LookupUnqualified(name, scopeStart, lex);
        }

        MemberHit lastHit;

        /// <param name="specializations">also search the specializations of a class template when its primary lacks the
        /// member (TDelegate&lt;Sig&gt;, TTupleBaseElement&lt;T, 0, 2&gt;::Key). Off for the implicit-this lookup of every
        /// unqualified name: there the walk made pass 2 twice as slow for no gain.</param>
        MemberHit LookupMember(Symbol cls, TypeInfo[] args, int name, int depth, bool specializations = true)
        {
            if (cls == null || depth > 20) return default;
            if (cls.Kind == SymKind.Typedef)
            {
                var t = S.ExpandAlias(cls, args, depth + 1);
                return t?.Sym != null && t.Sym != cls ? LookupMember(t.Sym, t.Args, name, depth + 1, specializations) : default;
            }
            if (cls.IsClassLike && name == cls.Name) return new MemberHit { Members = cls, Owner = cls.Parent, OwnerArgs = null };
            var m = cls.GetMember(name);
            if (m != null)
            {
                // constructors are stored under CtorKey: the class name finds the injected class itself
                return new MemberHit { Members = m, Owner = cls, OwnerArgs = args };
            }
            if (cls.AnonymousChildren != null)
                foreach (var a in cls.AnonymousChildren)
                {
                    var h = LookupMember(a, null, name, depth + 1, specializations);
                    if (h.Members != null) return h;
                }
            if (cls.IsClassLike)
            {
                var bases = S.GetBases(cls);
                if (bases != null)
                    foreach (var b in bases)
                    {
                        if (b?.Sym == null || b.Sym == cls) continue;
                        var bargs = b.Args;
                        if (bargs != null && args != null) bargs = Array.ConvertAll(bargs, x => SymbolTable.Subst(x, cls, args));
                        var h = LookupMember(b.Sym, bargs, name, depth + 1, specializations);
                        if (h.Members != null) return h;
                    }
                // the primary template lacks the member (only declared, a static_assert body as TDelegate<Sig>, or the member
                // lives in a partial specialization): the specializations are the candidates
                if (cls.Specializations != null && specializations)
                    foreach (var sp in cls.Specializations)
                    {
                        var h = LookupMember(sp, null, name, depth + 1, true);
                        if (h.Members != null) return h;
                    }
                if (cls.DependentBases != null && args != null)
                    foreach (var db in cls.DependentBases)
                    {
                        var b = SymbolTable.Subst(db, cls, args);
                        if (b?.Sym == null || b == db || !(b.Sym.IsClassLike || b.Sym.Kind == SymKind.Typedef)) continue;
                        var h = LookupMember(b.Sym, b.Args, name, depth + 1, specializations);
                        if (h.Members != null) return h;
                    }
            }
            return default;
        }

        TypeInfo MemberType(Symbol m, Symbol owner, TypeInfo[] ownerArgs)
        {
            var t = S.DeclaredType(m);
            if (t != null && owner != null && ownerArgs != null) t = SymbolTable.Subst(t, owner, ownerArgs);
            return t;
        }

        /// <summary>Normalizes a type to the class symbol used for member lookup (expands aliases).</summary>
        TypeInfo ClassOf(TypeInfo t, int depth = 0)
        {
            if (t?.Sym == null || depth > 8) return null;
            if (t.Sym.Kind == SymKind.Typedef)
            {
                var e = S.ExpandAlias(t.Sym, t.Args, depth + 1);
                if (e == null) return null;
                return ClassOf(e.With((byte)(e.Ptr + t.Ptr), t.Ref, t.Const || e.Const), depth + 1);
            }
            if (t.Sym.Kind == SymKind.TemplateParam) return null;
            return t;
        }

        // ------------------------------------------------------------------ statements

        void Block(ref int p)
        {
            // p at '{'
            int close = Parser.Match[p] > p ? Parser.Match[p] : end - 1;
            int mark = locals.Count;
            p++;
            while (p < close)
            {
                int before = p;
                Statement(ref p, close);
                if (p <= before) p = before + 1;
            }
            p = close + 1;
            if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
        }

        void Statement(ref int p, int stop)
        {
            var t = T[p];
            if (t.Kind == TK.Punct)
            {
                if (t.Value == '{') { Block(ref p); return; }
                if (t.Value == ';') { p++; return; }
                if (t.Value == '[' && T[p + 1].Is('[')) { p = Parser.After(p); return; }
            }
            if (t.Kind == TK.Ident)
            {
                int v = t.Value;
                if (v == K.If)
                {
                    p++;
                    if (IsId(p, K.Constexpr)) p++;
                    if (T[p].Is('!')) p++;   // if ! consteval
                    int mark = locals.Count;
                    if (T[p].Is('(')) Condition(ref p, true);
                    Sub(ref p, stop);
                    if (IsId(p, K.Else)) { p++; Sub(ref p, stop); }
                    if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
                    return;
                }
                if (v == K.While || v == K.Switch)
                {
                    p++;
                    int mark = locals.Count;
                    if (T[p].Is('(')) Condition(ref p, true);
                    Sub(ref p, stop);
                    if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
                    return;
                }
                if (v == K.For) { For(ref p, stop); return; }
                if (v == K.Do)
                {
                    p++;
                    Sub(ref p, stop);
                    if (IsId(p, K.While)) { p++; if (T[p].Is('(')) Condition(ref p, false); }
                    if (T[p].Is(';')) p++;
                    return;
                }
                if (v == K.Return || v == K.CoReturn || v == K.Throw)
                {
                    p++;
                    if (!T[p].Is(';')) ExprComma(ref p);
                    EndStatement(ref p, stop);
                    return;
                }
                if (v == K.Break || v == K.Continue) { p++; if (T[p].Is(';')) p++; return; }
                if (v == K.Goto) { p += 2; if (T[p].Is(';')) p++; return; }
                if (v == K.Case)
                {
                    p++;
                    Ternary(ref p);
                    if (T[p].Is(P.Ellipsis)) { p++; Ternary(ref p); }
                    if (T[p].Is(':')) p++;
                    return;
                }
                if (v == K.Default && T[p + 1].Is(':')) { p += 2; return; }
                if (v == K.Try) { p++; if (T[p].Is('{')) Block(ref p); return; }
                if (v == K.Catch)
                {
                    p++;
                    int mark = locals.Count;
                    if (T[p].Is('('))
                    {
                        int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
                        int q = p + 1;
                        if (!T[q].Is(P.Ellipsis)) TryDeclaration(ref q, close, false);
                        p = close + 1;
                    }
                    if (T[p].Is('{')) Block(ref p);
                    if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
                    return;
                }
                if (v == K.Else) { p++; Sub(ref p, stop); return; }
                if (v == K.Using || v == K.Typedef || v == K.StaticAssert || v == K.Namespace)
                {
                    LocalDeclStatement(ref p, stop);
                    return;
                }
                if ((v == K.Class || v == K.Struct || v == K.Union || v == K.Enum) && IsLocalTypeDefinition(p, stop))
                {
                    LocalDeclStatement(ref p, stop);
                    return;
                }
                if (v == K.Template) { LocalDeclStatement(ref p, stop); return; }
                // label
                if (T[p + 1].Is(':') && !DeclParser.IsKeyword(v) && !T[p + 2].Is(':')) { p += 2; return; }
            }
            int s = p;
            if (MacroStatement(ref p, stop)) return;
            if (TryDeclaration(ref p, stop, true))
            {
                EndStatement(ref p, stop);
                return;
            }
            p = s;
            ExprComma(ref p);
            EndStatement(ref p, stop);
        }

        bool IsId(int p, int name) => T[p].Kind == TK.Ident && T[p].Value == name;

        bool IsLocalTypeDefinition(int p, int stop)
        {
            for (int q = p + 1; q < stop && q < p + 32; q++)
            {
                if (T[q].Is('{')) return true;
                if (T[q].Is(';') || T[q].Is('=') || T[q].Is('(') || T[q].Is(')')) return false;
            }
            return false;
        }

        void LocalDeclStatement(ref int p, int stop)
        {
            // local type / using / typedef: reuse the token soup for references, register local typedef names loosely
            int e = Parser.SkipStatement(p, stop);
            if (IsId(p, K.Using) && T[p + 1].Kind == TK.Ident && T[p + 2].Is('='))
            {
                int q = p + 3;
                var te = Parser.ParseType(ref q, e, true);
                var ti = te != null ? S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0) : null;
                locals.Add(new Local { Name = T[p + 1].Value, Type = ti, IsType = true });
                p = e;
                return;
            }
            if (IsId(p, K.Using) && IsId(p + 1, K.Namespace))
            {
                int q = p + 2;
                var te = Parser.ParseType(ref q, e, false);
                if (te != null) OnUsingDirective(te, new ScopeCtx { Sym = scopeStart });
                p = e;
                return;
            }
            if (IsId(p, K.Typedef))
            {
                int q = p + 1;
                var te = Parser.ParseType(ref q, e, true);
                var ti = te != null ? S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0) : null;
                if (T[q].Kind == TK.Ident) locals.Add(new Local { Name = T[q].Value, Type = ti, IsType = true });
                p = e;
                return;
            }
            // local struct/class/enum: resolve members' types and inner bodies loosely
            for (int q = p; q < e; q++)
            {
                if (T[q].Is('{') && q > p && (T[q - 1].Is(')') || IsId(q - 1, K.Const) || IsId(q - 1, K.Override)))
                {
                    int qq = q;
                    Block(ref qq);
                    q = qq - 1;
                    continue;
                }
                if (T[q].Kind == TK.Ident && !DeclParser.IsKeyword(T[q].Value)) q = SoupName(q, e) - 1;
            }
            if ((IsId(p, K.Struct) || IsId(p, K.Class)) && T[p + 1].Kind == TK.Ident)
                locals.Add(new Local { Name = T[p + 1].Value, Type = null, IsType = true });
            p = e;
        }

        void Sub(ref int p, int stop)
        {
            int mark = locals.Count;
            if (p < stop) Statement(ref p, stop);
            if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
        }

        void EndStatement(ref int p, int stop)
        {
            if (p >= stop) return;
            if (T[p].Is(';')) { p++; return; }
            // recovery: resolve what remains up to ';' (blocks are resolved as statements)
            Recover(ref p, stop);
        }

        void Recover(ref int p, int stop)
        {
            int guard = 0;
            while (p < stop && guard++ < 100000)
            {
                var t = T[p];
                if (t.Is(';')) { p++; return; }
                if (t.Is('}')) return;
                if (t.Is('{')) { Block(ref p); if (T[p].Is(';')) { p++; return; } if (T[p - 1].Is('}') && !T[p].Is(')') && !T[p].Is(',')) return; continue; }
                if (t.Is('('))
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
                    int q = p + 1;
                    ExprList(ref q, close);
                    p = close + 1;
                    continue;
                }
                int before = p;
                Assign(ref p);
                if (p == before) { SoupToken(p); p++; }
            }
        }

        void Condition(ref int p, bool allowDecl)
        {
            int close = Parser.Match[p] > p ? Parser.Match[p] : end;
            int q = p + 1;
            int saveEnd = end;
            end = close;
            // init-statement: decl or expr followed by ';'
            int semi = -1;
            for (int i = q; i < close; i++)
            {
                if (T[i].Is('(') || T[i].Is('[') || T[i].Is('{')) { i = Parser.Match[i] > i ? Parser.Match[i] : i; continue; }
                if (T[i].Is(';')) { semi = i; break; }
            }
            if (semi >= 0)
            {
                int s = q;
                if (!TryDeclaration(ref q, semi, true)) { q = s; ExprComma(ref q); }
                q = semi + 1;
            }
            if (q < close)
            {
                int s = q;
                if (!(allowDecl && TryDeclaration(ref q, close, false))) { q = s; ExprComma(ref q); }
                if (q < close) { Recover(ref q, close); }
            }
            end = saveEnd;
            p = close + 1;
        }

        void For(ref int p, int stop)
        {
            p++;
            if (!T[p].Is('(')) return;
            int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
            int mark = locals.Count;
            int saveEnd = end;
            end = close;
            int q = p + 1;
            // classic or range-for?
            int semi = -1, colon = -1;
            for (int i = q; i < close; i++)
            {
                if (T[i].Is('(') || T[i].Is('[') || T[i].Is('{')) { i = Parser.Match[i] > i ? Parser.Match[i] : i; continue; }
                if (T[i].Is(';')) { semi = i; break; }
                if (T[i].Is(':') && colon < 0) colon = i;
                if (T[i].Is('?')) colon = -2;
            }
            if (semi < 0 && colon > 0)
            {
                // range-for: decl : range
                int rq = colon + 1;
                var range = ExprComma(ref rq);
                var elem = ElementOf(range?.Type);
                int dq = q;
                DeclareRangeVariable(ref dq, colon, elem);
            }
            else
            {
                if (semi >= 0)
                {
                    int s = q;
                    if (!TryDeclaration(ref q, semi, true)) { q = s; ExprComma(ref q); }
                    q = semi + 1;
                    int semi2 = -1;
                    for (int i = q; i < close; i++)
                    {
                        if (T[i].Is('(') || T[i].Is('[') || T[i].Is('{')) { i = Parser.Match[i] > i ? Parser.Match[i] : i; continue; }
                        if (T[i].Is(';')) { semi2 = i; break; }
                    }
                    if (semi2 >= 0)
                    {
                        int s2 = q;
                        if (!TryDeclaration(ref q, semi2, false)) { q = s2; ExprComma(ref q); }
                        q = semi2 + 1;
                    }
                    if (q < close) ExprComma(ref q);
                }
            }
            end = saveEnd;
            p = close + 1;
            Sub(ref p, stop);
            if (locals.Count > mark) locals.RemoveRange(mark, locals.Count - mark);
        }

        void DeclareRangeVariable(ref int p, int colon, TypeInfo elem)
        {
            // specifiers + type + declarator (or structured binding)
            int q = p;
            while (q < colon && T[q].Kind == TK.Ident && (T[q].Value == K.Const || T[q].Value == K.Constexpr || T[q].Value == K.Volatile)) q++;
            var te = Parser.ParseType(ref q, colon, false);
            TypeInfo declared = null;
            if (te != null && te.Builtin != Builtin.Auto) declared = S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
            var tmp = new TypeExpr();
            Parser.ParsePtrOps(ref q, colon, tmp);
            if (T[q].Is('['))
            {
                int close = Parser.Match[q] > q ? Parser.Match[q] : colon;
                var names = new List<int>();
                for (int i = q + 1; i < close; i++) if (T[i].Kind == TK.Ident) names.Add(T[i].Value);
                for (int i = 0; i < names.Count; i++)
                {
                    TypeInfo bt = null;
                    if (elem != null && elem.Builtin == Builtin.Unknown && elem.Args != null && elem.Args.Length == 2) bt = elem.Args[i == 0 ? 0 : 1];
                    locals.Add(new Local { Name = names[i], Type = bt });
                }
                return;
            }
            if (T[q].Kind == TK.Ident)
            {
                TypeInfo t;
                if (te == null || te.Builtin == Builtin.Auto) t = elem == null ? null : (tmp.Ptr > 0 && elem.Ptr == 0 ? elem : elem);
                else t = declared != null ? declared.With((byte)(declared.Ptr + tmp.Ptr), tmp.Ref, declared.Const) : null;
                locals.Add(new Local { Name = T[q].Value, Type = t });
            }
        }

        /// <summary>Speculative declaration statement. On success declares locals and leaves p after the declarators.</summary>
        bool TryDeclaration(ref int p, int stop, bool requireTerminator)
        {
            int q = p;
            bool sawSpecifier = false;
            while (q < stop && T[q].Kind == TK.Ident)
            {
                int v = T[q].Value;
                if (v == K.Const || v == K.Static || v == K.Constexpr || v == K.Volatile || v == K.ThreadLocal || v == K.Register || v == K.Mutable || v == K.Inline || v == K.Extern || v == K.Constinit)
                { q++; sawSpecifier = true; continue; }
                break;
            }
            if (q >= stop) return false;
            var first = T[q];
            if (first.Kind != TK.Ident && !first.Is(P.Scope)) return false;
            if (first.Kind == TK.Ident && DeclParser.IsKeyword(first.Value) && !DeclParser.IsBuiltinWord(first.Value) && first.Value != K.Typename && first.Value != K.Decltype
                && first.Value != K.Struct && first.Value != K.Class && first.Value != K.Enum && first.Value != K.Union) return false;
            // locals shadow types: "Foo Bar" where Foo is a local variable is not a declaration
            if (first.Kind == TK.Ident && !DeclParser.IsBuiltinWord(first.Value))
            {
                for (int i = locals.Count - 1; i >= 0; i--) if (locals[i].Name == first.Value) { if (!locals[i].IsType) return false; break; }
            }
            int typeStart = q;
            var te = Parser.ParseType(ref q, stop, false);
            if (te == null) return false;
            bool builtin = te.Parts == null;
            TypeInfo ti = null;
            bool isType = builtin;
            if (!builtin)
            {
                if (te.Parts.Length == 1 && te.Parts[0].Args == null)
                {
                    var lt = FindLocalType(te.Parts[0].Name);
                    if (lt != null) { isType = true; ti = lt.Type; }
                }
                if (!isType)
                {
                    ti = ResolveTypeQuiet(te);
                    isType = ti != null;
                    if (!isType && IsValueName(te)) return false;
                }
            }
            // declarator must follow
            int d = q;
            var tmp = new TypeExpr();
            Parser.ParsePtrOps(ref d, stop, tmp);
            if (d >= stop) return false;
            var nt = T[d];
            bool binding = nt.Is('[') && te.Builtin == Builtin.Auto;
            if (!binding)
            {
                if (nt.Kind != TK.Ident || DeclParser.IsKeyword(nt.Value)) return false;
                var after = T[d + 1];
                if (d + 1 > stop) return false;
                bool okAfter = d + 1 == stop || after.Is('=') || after.Is(';') || after.Is('(') || after.Is('{') || after.Is('[') || after.Is(',') || after.Is(':') || after.Is(')');
                if (!okAfter) return false;
                if (!isType)
                {
                    // unknown type: accept "A B =" / "A* B =" / "A<..> B" patterns, reject things like "a(b)" or "x y(" where x is a value
                    if (after.Is(')') && !requireTerminator) { }
                    if (te.Parts.Length == 1 && te.Parts[0].Args == null && tmp.Ptr == 0 && !tmp.Ref && after.Is('(') && !sawSpecifier)
                    {
                        // "Macro Name(...)" ambiguous; treat as declaration only if Name is not known
                    }
                }
            }
            // commit: emit refs for the type
            p = typeStart;
            var te2 = Parser.ParseType(ref p, stop, false);
            TypeInfo baseType = null;
            if (te2 != null && te2.Builtin != Builtin.Auto)
            {
                if (te2.Parts != null && te2.Parts.Length == 1 && te2.Parts[0].Args == null && FindLocalType(te2.Parts[0].Name) is Local lt2) baseType = lt2.Type;
                else
                {
                    baseType = S.ResolveTypeExpr(te2, scopeStart, lex, EmitTypeCb, 0);
                    if (baseType == null && te2.Parts != null) EmitUnresolvedType(te2);
                }
            }
            bool isAuto = te2 != null && te2.Builtin == Builtin.Auto;
            if (baseType != null && te2.Const) baseType = baseType.With(baseType.Ptr, baseType.Ref, true);
            Declarators(ref p, stop, baseType, isAuto);
            return true;
        }

        void EmitUnresolvedType(TypeExpr te)
        {
            var last = te.Parts[te.Parts.Length - 1];
            if (last.Tok >= 0) { UnresolvedNames++; EmitUnresolved(last.Tok, 3); }
        }

        Local FindLocalType(int name)
        {
            for (int i = locals.Count - 1; i >= 0; i--)
                if (locals[i].Name == name) return locals[i].IsType ? locals[i] : null;
            return null;
        }

        bool IsValueName(TypeExpr te)
        {
            if (te.Parts.Length != 1) return false;
            var r = LookupName(te.Parts[0].Name, out var loc);
            if (loc != null) return !loc.IsType;
            var s = First(r);
            return s != null && (s.Kind == SymKind.Variable || s.Kind == SymKind.Function || s.Kind == SymKind.Enumerator);
        }

        TypeInfo ResolveTypeQuiet(TypeExpr te)
        {
            if (te.Parts != null && te.Parts.Length == 1 && thisClass != null)
            {
                var hit = LookupMember(thisClass, thisType?.Args, te.Parts[0].Name, 0, false);
                var m = First(hit.Members);
                if (m != null)
                {
                    if (hit.Members is List<Symbol> l) m = l.FirstOrDefault(x => x.IsType) ?? m;
                    if (!m.IsType) return null;
                }
            }
            return S.ResolveTypeExpr(te, scopeStart, lex, null, 0);
        }

        void Declarators(ref int p, int stop, TypeInfo baseType, bool isAuto)
        {
            int guard = 0;
            while (p < stop && guard++ < 64)
            {
                var tmp = new TypeExpr();
                Parser.ParsePtrOps(ref p, stop, tmp);
                if (T[p].Is('['))
                {
                    // structured binding
                    int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
                    var names = new List<int>();
                    for (int i = p + 1; i < close; i++) if (T[i].Kind == TK.Ident) names.Add(T[i].Value);
                    p = close + 1;
                    Val init = null;
                    if (T[p].Is('=')) { p++; init = Assign(ref p); }
                    else if (T[p].Is('{') || T[p].Is('(')) { int c = Parser.Match[p] > p ? Parser.Match[p] : stop; int q = p + 1; ExprList(ref q, c); p = c + 1; }
                    foreach (var n in names) locals.Add(new Local { Name = n });
                    if (!T[p].Is(',')) return;
                    p++;
                    continue;
                }
                if (T[p].Kind != TK.Ident) return;
                int nameTok = p;
                p++;
                while (T[p].Is('['))
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
                    int q = p + 1;
                    if (q < close) ExprComma(ref q);
                    p = close + 1;
                    tmp.Ptr++;
                }
                TypeInfo type = baseType?.With((byte)(baseType.Ptr + tmp.Ptr), tmp.Ref, baseType.Const);
                var local = new Local { Name = T[nameTok].Value, Type = type };
                if (T[p].Is('='))
                {
                    p++;
                    locals.Add(local); // visible in its own initializer (lambdas capturing it)
                    var v = Assign(ref p);
                    if (isAuto) local.Type = v?.Type?.Value is TypeInfo vt ? (tmp.Ptr > 0 && vt.Ptr == 0 ? vt : vt) : null;
                }
                else if (T[p].Is('(') || T[p].Is('{'))
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : stop;
                    int q = p + 1;
                    var vals = ExprList(ref q, close);
                    p = close + 1;
                    if (isAuto && vals.Count == 1) local.Type = vals[0]?.Type?.Value;
                    locals.Add(local);
                }
                else locals.Add(local);
                // trailing macros (e.g. UE_LIFETIMEBOUND)
                if (T[p].Is(',')) { p++; continue; }
                return;
            }
        }

        // ------------------------------------------------------------------ expressions

        List<Val> ExprList(ref int p, int close)
        {
            var list = new List<Val>();
            int saveEnd = end;
            end = close;
            int guard = 0;
            while (p < close && guard++ < 10000)
            {
                if (T[p].Is(',')) { p++; continue; }
                int before = p;
                var v = Assign(ref p);
                if (T[p].Is(P.Ellipsis)) p++;
                list.Add(v);
                if (p < close && !T[p].Is(','))
                {
                    // could not parse the whole argument: resolve the rest loosely
                    while (p < close && !T[p].Is(','))
                    {
                        if (T[p].Is('{')) { Block(ref p); continue; }
                        if (T[p].Is('(') || T[p].Is('['))
                        {
                            int c = Parser.Match[p] > p ? Parser.Match[p] : close;
                            int q = p + 1;
                            ExprList(ref q, c);
                            p = c + 1;
                            continue;
                        }
                        int b2 = p;
                        Assign(ref p);
                        if (p == b2) { SoupToken(p); p++; }
                    }
                }
                if (p == before) p++;
            }
            end = saveEnd;
            p = close;
            return list;
        }

        Val ExprComma(ref int p)
        {
            var v = Assign(ref p);
            while (p < end && T[p].Is(','))
            {
                p++;
                v = Assign(ref p);
            }
            return v;
        }

        bool IsAssignOp(int p, out int len)
        {
            len = 1;
            var t = T[p];
            if (t.Kind != TK.Punct) return false;
            switch (t.Value)
            {
                case '=': case P.PlusEq: case P.MinusEq: case P.MulEq: case P.DivEq: case P.ModEq: case P.AndEq: case P.OrEq: case P.XorEq: case P.ShlEq:
                    return true;
                case '>':
                    if (T[p + 1].Is('>') && T[p + 2].Is('=') && Adj(p, p + 1) && Adj(p + 1, p + 2)) { len = 3; return true; }
                    return false;
            }
            return false;
        }

        bool Adj(int a, int b) => T[b].Pos == T[a].Pos + T[a].Len;

        Val Assign(ref int p)
        {
            if (p >= end) return Val.Unknown;
            if (IsId(p, K.Throw)) { p++; if (p < end && !T[p].Is(';') && !T[p].Is(')')) return Assign(ref p); return Val.Unknown; }
            var l = Ternary(ref p);
            if (p < end && IsAssignOp(p, out int len))
            {
                p += len;
                if (T[p].Is('{')) { int c = Parser.Match[p] > p ? Parser.Match[p] : end; int q = p + 1; ExprList(ref q, c); p = c + 1; return l; }
                Assign(ref p);
                return l;
            }
            return l;
        }

        Val Ternary(ref int p)
        {
            var c = Binary(ref p, 4);
            if (p < end && T[p].Is('?'))
            {
                p++;
                var a = Assign(ref p);
                if (T[p].Is(':')) p++;
                var b = Assign(ref p);
                return a.Type != null ? a : b;
            }
            return c;
        }

        int BinPrec(int p, out int len, out bool comparison)
        {
            len = 1; comparison = false;
            if (p >= end) return -1;
            var t = T[p];
            if (t.Kind == TK.Ident)
            {
                // alternative tokens and/or are rare in UE; ignore
                return -1;
            }
            if (t.Kind != TK.Punct) return -1;
            switch (t.Value)
            {
                case P.OrOr: comparison = true; return 4;
                case P.AndAnd: comparison = true; return 5;
                case '|': return 6;
                case '^': return 7;
                case '&': return 8;
                case P.Eq: case P.Ne: comparison = true; return 9;
                case '<': case P.Le: case P.Spaceship: comparison = true; return 10;
                case '>':
                    if (T[p + 1].Is('>') && Adj(p, p + 1))
                    {
                        if (T[p + 2].Is('=') && Adj(p + 1, p + 2)) return -1; // >>=
                        len = 2; return 11;
                    }
                    if (T[p + 1].Is('=') && Adj(p, p + 1)) { len = 2; comparison = true; return 10; }
                    if (T[p + 1].Is(P.Eq) && Adj(p, p + 1)) return -1;
                    comparison = true; return 10;
                case P.Shl: return 11;
                case '+': case '-': return 12;
                case '*': case '/': case '%': return 13;
                case P.DotStar: case P.ArrowStar: return 14;
            }
            return -1;
        }

        Val Binary(ref int p, int minPrec)
        {
            var l = Unary(ref p);
            int guard = 0;
            while (guard++ < 10000)
            {
                int prec = BinPrec(p, out int len, out bool cmp);
                if (prec < minPrec) break;
                p += len;
                var r = Binary(ref p, prec + 1);
                if (cmp) l = Val.Of(TypeInfo.Bool);
                else if (l.Type == null) l = r.Type != null && (r.Type.Sym != null) ? Val.Of(r.Type.Value) : Val.Unknown;
                else l = Val.Of(l.Type.Value);
            }
            return l;
        }

        Val Unary(ref int p)
        {
            if (p >= end) return Val.Unknown;
            var t = T[p];
            if (t.Kind == TK.Punct)
            {
                switch (t.Value)
                {
                    case '!': p++; Unary(ref p); return Val.Of(TypeInfo.Bool);
                    case '~': case '-': case '+': { p++; var v = Unary(ref p); return Val.Of(v.Type?.Value); }
                    case P.Inc: case P.Dec: { p++; return Unary(ref p); }
                    case '*': { p++; var v = Unary(ref p); return Val.Of(Deref(v.Type)); }
                    case '&':
                    {
                        p++;
                        var v = Unary(ref p);
                        if (v.Funcs != null) { EmitPending(v); return Val.Unknown; }
                        return Val.Of(v.Type?.Value.AddrOf());
                    }
                    case P.AndAnd: { p++; Unary(ref p); return Val.Unknown; } // label address
                    case '(':
                    {
                        // C-style cast?
                        int close = Parser.Match[p];
                        if (close > p && IsCast(p, close))
                        {
                            int q = p + 1;
                            var te = Parser.ParseType(ref q, close, true);
                            var ct = te != null ? S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0) : null;
                            if (ct == null && te?.Parts != null && te.Parts.Length == 1 && FindLocalType(te.Parts[0].Name) is Local lt) ct = lt.Type?.With((byte)(lt.Type.Ptr + te.Ptr), te.Ref, te.Const);
                            p = close + 1;
                            Unary(ref p);
                            return Val.Of(ct);
                        }
                        break;
                    }
                }
            }
            else if (t.Kind == TK.Ident)
            {
                int v = t.Value;
                if (v == K.Sizeof || v == K.Alignof || Names.Get(v) == "alignof")
                {
                    p++;
                    if (T[p].Is(P.Ellipsis)) p++;
                    if (T[p].Is('('))
                    {
                        int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                        int q = p + 1;
                        var te = Parser.ParseType(ref q, close, true);
                        if (te != null && q == close && (te.Parts == null || ResolveTypeQuiet(te) != null)) S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
                        else { q = p + 1; ExprList(ref q, close); }
                        p = close + 1;
                    }
                    else Unary(ref p);
                    return Val.Of(TypeInfo.Int);
                }
                if (v == K.New) return New(ref p);
                if (v == K.Delete)
                {
                    p++;
                    if (T[p].Is('[') && T[p + 1].Is(']')) p += 2;
                    Unary(ref p);
                    return Val.Of(TypeInfo.Void);
                }
                if (v == K.CoAwait) { p++; return Unary(ref p); }
            }
            return Postfix(ref p);
        }

        bool IsCast(int open, int close)
        {
            int q = open + 1;
            if (q >= close) return false;
            var f = T[q];
            if (f.Kind != TK.Ident && !f.Is(P.Scope)) return false;
            var te = Parser.ParseType(ref q, close, true);
            if (te == null || q != close) return false;
            if (te.Parts != null)
            {
                bool known = te.Parts.Length == 1 && te.Parts[0].Args == null && FindLocalType(te.Parts[0].Name) != null;
                if (!known)
                {
                    if (te.Parts.Length == 1 && te.Parts[0].Args == null && IsValueName(te)) return false;
                    if (ResolveTypeQuiet(te) == null) return false;
                }
            }
            // what follows must start an operand
            var n = T[close + 1];
            if (close + 1 >= end) return false;
            if (n.Kind == TK.Ident) return !DeclParser.IsKeyword(n.Value) || n.Value == K.This || n.Value == K.New || n.Value == K.Sizeof || n.Value == K.Nullptr || n.Value == K.True || n.Value == K.False || n.Value == K.StaticCast || n.Value == K.ConstCast || n.Value == K.ReinterpretCast || n.Value == K.DynamicCast;
            if (n.Kind == TK.Number || n.Kind == TK.String || n.Kind == TK.Char) return true;
            if (n.Kind == TK.Punct)
            {
                int c = n.Value;
                if (c == '(' || c == '!' || c == '~' || c == P.Scope || c == '*' || c == '&' || c == '-' || c == '+' || c == P.Inc || c == P.Dec || c == '{') return true;
                if (c == '[') return true; // lambda
            }
            return false;
        }

        Val New(ref int p)
        {
            p++;
            if (T[p].Is('('))
            {
                // placement args or parenthesized type
                int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                int q = p + 1;
                ExprList(ref q, close);
                p = close + 1;
            }
            var te = Parser.ParseType(ref p, end, true);
            TypeInfo t = te != null ? S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0) : null;
            while (T[p].Is('['))
            {
                int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                int q = p + 1;
                if (q < close) ExprComma(ref q);
                p = close + 1;
            }
            if (T[p].Is('(') || T[p].Is('{'))
            {
                int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                int q = p + 1;
                ExprList(ref q, close);
                p = close + 1;
            }
            return Val.Of(t?.With((byte)(t.Ptr + 1), false, t.Const));
        }

        Val Postfix(ref int p)
        {
            var v = Primary(ref p);
            int guard = 0;
            while (p < end && guard++ < 1000)
            {
                var t = T[p];
                if (t.Kind != TK.Punct) break;
                int c = t.Value;
                if (c == '(')
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                    int q = p + 1;
                    var args = ExprList(ref q, close);
                    p = close + 1;
                    v = Call(v, args);
                    continue;
                }
                if (c == '{' && (v.TypeVal != null))
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                    int q = p + 1;
                    ExprList(ref q, close);
                    p = close + 1;
                    v = Val.Of(v.TypeVal);
                    continue;
                }
                if (c == '[')
                {
                    if (T[p + 1].Is('[')) break;
                    EmitPending(v);
                    int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                    int q = p + 1;
                    if (q < close) ExprComma(ref q);
                    p = close + 1;
                    v = Val.Of(Index(v.Type));
                    continue;
                }
                if (c == '.' || c == P.Arrow)
                {
                    EmitPending(v);
                    p++;
                    if (IsId(p, K.Template)) p++;
                    TypeInfo recv = c == '.' ? v.Type?.Value : Arrow(v.Type);
                    v = MemberAccess(ref p, recv);
                    continue;
                }
                if (c == P.Inc || c == P.Dec) { p++; continue; }
                if (c == P.DotStar || c == P.ArrowStar) break;
                break;
            }
            if (v.Funcs != null && !(p < end && T[p].Is('('))) { EmitPending(v); return Val.Unknown; }
            return v;
        }

        void EmitPending(Val v)
        {
            if (v?.Funcs == null || v.Funcs.Count == 0) return;
            var f = v.Funcs.FirstOrDefault(x => x.TemplateParams == null) ?? v.Funcs[0];
            if (v.Funcs.Count > 1) f = ChooseOverload(v.Funcs, null, v.TArgs, v.ConstRecv);
            Emit(f, v.FuncTok, RefKind.Read);
            v.Funcs = null;
        }

        static readonly int Key = K.Key, ValueName = K.Value;

        Val MemberAccess(ref int p, TypeInfo recv)
        {
            // p at member name
            if (T[p].Is('~')) { p++; if (T[p].Kind == TK.Ident) p++; return Val.Unknown; }
            if (IsId(p, K.Operator))
            {
                int q = p;
                while (q < end && !T[q].Is('(')) q++;
                p = q;
                return Val.Unknown;
            }
            if (T[p].Kind != TK.Ident) return Val.Unknown;
            int nameTok = p;
            int name = T[p].Value;
            p++;
            var cls = ClassOf(recv);
            // pair produced by map iteration
            if (recv != null && recv.Sym == null && recv.Builtin == Builtin.Unknown && recv.Args != null && recv.Args.Length == 2)
            {
                SkipTemplateArgsIfAny(ref p);
                if (name == Key) return Val.Of(recv.Args[0]);
                if (name == ValueName) return Val.Of(recv.Args[1]);
                return Val.Unknown;
            }
            if (recv != null && recv.Sym != null && recv.Sym.Kind == SymKind.TemplateParam) { SkipTemplateArgsIfAny(ref p); return new Val { Dependent = true }; }
            if (cls?.Sym == null)
            {
                UnresolvedMemberUnknownRecv++;
                EmitUnresolved(nameTok, 1);
                SkipTemplateArgsIfAny(ref p);
                return Val.Unknown;
            }
            var hit = LookupMember(cls.Sym, cls.Args, name, 0);
            if (hit.Members == null)
            {
                // function-like macro used as a member (AddDynamic, AddUniqueDynamic, ...)
                if (T[p].Is('(') && Parser.Macros.Find(name) != null) return Val.Unknown;
                UnresolvedMemberNotFound++;
                EmitUnresolved(nameTok, 2);
                SkipTemplateArgsIfAny(ref p);
                return Val.Unknown;
            }
            ResolvedMember++;
            return FromMembers(ref p, hit.Members, nameTok, hit.Owner, hit.OwnerArgs, recv != null && recv.Const);
        }

        void SkipTemplateArgsIfAny(ref int p)
        {
            if (T[p].Is('<'))
            {
                int c = Parser.TryTemplateClose(p, end);
                if (c > 0 && (T[c + 1].Is('(') || T[c + 1].Is(P.Scope)))
                {
                    var args = Parser.ParseTemplateArgs(ref p, end);
                    if (args != null) foreach (var a in args) S.ResolveTypeExpr(a, scopeStart, lex, EmitTypeCb, 0);
                }
            }
        }

        TypeInfo[] TemplateArgsIfAny(ref int p, bool force)
        {
            if (!T[p].Is('<')) return null;
            int c = Parser.TryTemplateClose(p, end);
            if (c < 0) return null;
            if (!force && !(T[c + 1].Is('(') || T[c + 1].Is(P.Scope) || T[c + 1].Is('{'))) return null;
            int q = p;
            var args = Parser.ParseTemplateArgs(ref q, end);
            if (args == null) return null;
            p = q;
            var r = new TypeInfo[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Parts != null && a.Parts.Length == 1 && a.Parts[0].Args == null && FindLocalType(a.Parts[0].Name) is Local lt) { r[i] = lt.Type?.With((byte)((lt.Type?.Ptr ?? 0) + a.Ptr), a.Ref, a.Const); continue; }
                r[i] = S.ResolveTypeExpr(a, scopeStart, lex, EmitTypeCb, 0);
            }
            return r;
        }

        static bool IsTemplate(object members)
        {
            if (members is Symbol s) return s.TemplateParams != null;
            if (members is List<Symbol> l) foreach (var x in l) if (x.TemplateParams != null) return true;
            return false;
        }

        /// <summary>Builds a value from member/name lookup results (functions stay pending until the call).</summary>
        Val FromMembers(ref int p, object members, int nameTok, Symbol owner, TypeInfo[] ownerArgs, bool constRecv)
        {
            List<Symbol> funcs = null;
            Symbol other = null;
            if (members is Symbol one) { if (one.Kind == SymKind.Function) funcs = new List<Symbol> { one }; else other = one; }
            else if (members is List<Symbol> l)
            {
                foreach (var x in l)
                {
                    if (x.Kind == SymKind.Function) (funcs ??= new List<Symbol>()).Add(x);
                    else other ??= x;
                }
                if (other != null && (other.IsType || other.Kind == SymKind.Namespace) && funcs != null && !T[p].Is('(')) funcs = null;
                if (funcs != null && other != null && other.Kind == SymKind.Variable) funcs = null; // prefer variable
            }
            if (funcs != null)
            {
                var targs = IsTemplate(funcs.Count == 1 ? (object)funcs[0] : funcs) ? TemplateArgsIfAny(ref p, true) : TemplateArgsIfAny(ref p, false);
                return new Val { Funcs = funcs, FuncTok = nameTok, Owner = owner, OwnerArgs = ownerArgs, TArgs = targs, ConstRecv = constRecv };
            }
            if (other == null) return Val.Unknown;
            switch (other.Kind)
            {
                case SymKind.Variable:
                    Emit(other, nameTok, RefKind.Read);
                    SkipTemplateArgsIfAny(ref p);
                    return Val.Of(MemberType(other, owner, ownerArgs));
                case SymKind.Enumerator:
                    Emit(other, nameTok, RefKind.Read);
                    return Val.Of(new TypeInfo { Sym = other.Parent });
                case SymKind.Namespace:
                    Emit(other, nameTok, RefKind.Type);
                    return new Val { Scope = other };
                case SymKind.Using:
                {
                    Emit(other, nameTok, RefKind.Type);
                    var target = S.ResolveUsingTarget(other);
                    if (target is Symbol ts && ts != other) { p--; int pp = p + 1; var r = FromMembers(ref pp, ts, -1, owner, ownerArgs, constRecv); p = pp; return r; }
                    if (target is List<Symbol> tl) { int pp = p; return FromMembers(ref pp, tl, -1, owner, ownerArgs, constRecv); }
                    return Val.Unknown;
                }
                default:
                    if (other.IsType)
                    {
                        Emit(other, nameTok, RefKind.Type);
                        var targs = TemplateArgsIfAny(ref p, other.TemplateParams != null);
                        TypeInfo ti;
                        if (other.Kind == SymKind.Typedef)
                        {
                            ti = S.ExpandAlias(other, targs, 0);
                            if (ti != null && ownerArgs != null && owner != null) ti = SymbolTable.Subst(ti, owner, ownerArgs);
                        }
                        else if (other.Kind == SymKind.TemplateParam) ti = new TypeInfo { Sym = other };
                        else ti = new TypeInfo { Sym = other, Args = targs };
                        return new Val { TypeVal = ti, Scope = other, Dependent = other.Kind == SymKind.TemplateParam, FuncTok = nameTok };
                    }
                    return Val.Unknown;
            }
        }

        Val Primary(ref int p)
        {
            if (p >= end) return Val.Unknown;
            var t = T[p];
            switch (t.Kind)
            {
                case TK.Number: p++; return Val.Of(TypeInfo.Int);
                case TK.String:
                    p++;
                    while (p < end && (T[p].Kind == TK.String || T[p].Kind == TK.Ident && IsStringMacro(T[p].Value))) p++;
                    return Val.Of(TypeInfo.CharPtr);
                case TK.Char: p++; return Val.Of(TypeInfo.Int);
                case TK.Punct:
                    if (t.Value == '(')
                    {
                        int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                        int q = p + 1;
                        int saveEnd = end;
                        end = close;
                        var v = ExprComma(ref q);
                        if (q < close) Recover(ref q, close);
                        end = saveEnd;
                        p = close + 1;
                        return v.Funcs != null ? v : Val.Of(v.Type);
                    }
                    if (t.Value == '[') return Lambda(ref p);
                    if (t.Value == '{')
                    {
                        int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                        int q = p + 1;
                        ExprList(ref q, close);
                        p = close + 1;
                        return Val.Unknown;
                    }
                    if (t.Value == P.Scope) return IdExpr(ref p);
                    p++;
                    return Val.Unknown;
                case TK.Ident:
                {
                    int v = t.Value;
                    if (v == K.This) { p++; return Val.Of(thisType?.With(1, false, constThis)); }
                    if (v == K.True || v == K.False) { p++; return Val.Of(TypeInfo.Bool); }
                    if (v == K.Nullptr || v == K.NULL) { p++; return Val.Of(TypeInfo.Nullptr); }
                    if (v == K.StaticCast || v == K.DynamicCast || v == K.ReinterpretCast || v == K.ConstCast)
                    {
                        p++;
                        TypeInfo ct = null;
                        if (T[p].Is('<'))
                        {
                            int c = Parser.TryTemplateClose(p, end);
                            int q = p + 1;
                            var te = Parser.ParseType(ref q, c > 0 ? c : end, true);
                            if (te != null)
                            {
                                ct = S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
                                if (ct == null && te.Parts != null && te.Parts.Length == 1 && FindLocalType(te.Parts[0].Name) is Local lt) ct = lt.Type?.With((byte)((lt.Type?.Ptr ?? 0) + te.Ptr), te.Ref, te.Const);
                            }
                            p = c > 0 ? c + 1 : q;
                        }
                        if (T[p].Is('('))
                        {
                            int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                            int q = p + 1;
                            ExprList(ref q, close);
                            p = close + 1;
                        }
                        return Val.Of(ct);
                    }
                    if (v == K.Typeid)
                    {
                        p++;
                        if (T[p].Is('(')) { int close = Parser.Match[p] > p ? Parser.Match[p] : end; int q = p + 1; ExprList(ref q, close); p = close + 1; }
                        return Val.Unknown;
                    }
                    if (v == K.Decltype)
                    {
                        p++;
                        Val dv = Val.Unknown;
                        if (T[p].Is('(')) { int close = Parser.Match[p] > p ? Parser.Match[p] : end; int q = p + 1; var l = ExprList(ref q, close); p = close + 1; if (l.Count == 1) dv = l[0]; }
                        if (dv.Type != null) return new Val { TypeVal = dv.Type, Scope = dv.Type.Sym };
                        return Val.Unknown;
                    }
                    if (DeclParser.IsBuiltinWord(v))
                    {
                        int q = p;
                        var te = Parser.ParseType(ref q, end, false);
                        p = Math.Max(q, p + 1);
                        var bt = te != null ? new TypeInfo { Builtin = te.Builtin } : TypeInfo.Int;
                        return new Val { TypeVal = bt };
                    }
                    if (v == K.Typename || v == K.Template) { p++; return Primary(ref p); }
                    if (DeclParser.IsKeyword(v)) { p++; return Val.Unknown; }
                    return IdExpr(ref p);
                }
            }
            p++;
            return Val.Unknown;
        }

        static readonly HashSet<int> stringMacros = new HashSet<int> { Names.Intern("PRId64"), Names.Intern("PRIu64"), Names.Intern("PRIx64") };
        static bool IsStringMacro(int v) => stringMacros.Contains(v);

        Val Lambda(ref int p)
        {
            int capClose = Parser.Match[p] > p ? Parser.Match[p] : end;
            var save = locals.Count;
            // captures
            var initCaptures = new List<Local>();
            for (int q = p + 1; q < capClose; q++)
            {
                if (T[q].Kind == TK.Ident && T[q + 1].Is('='))
                {
                    int name = T[q].Value;
                    int r = q + 2;
                    int saveEnd = end; end = capClose;
                    var iv = Assign(ref r);
                    end = saveEnd;
                    initCaptures.Add(new Local { Name = name, Type = iv?.Type?.Value });
                    q = r - 1;
                    while (q + 1 < capClose && !T[q + 1].Is(',')) q++;
                }
                else if (T[q].Kind == TK.Ident && T[q].Value != K.This)
                {
                    // plain capture: a reference to the captured variable
                    LookupName(T[q].Value, out _);
                }
            }
            p = capClose + 1;
            if (T[p].Is('<')) { int c = Parser.TryTemplateClose(p, end); if (c > 0) p = c + 1; }
            var paramLocals = new List<Local>();
            if (T[p].Is('('))
            {
                int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                int q = p;
                var ps = Parser.ParseParams(ref q, close + 1, out _, out _, out _);
                foreach (var prm in ps)
                {
                    var pt = prm.Type != null && prm.Type.Builtin != Builtin.Auto ? S.ResolveTypeExpr(prm.Type, scopeStart, lex, EmitTypeCb, 0) : null;
                    if (pt == null && prm.Type?.Parts != null && prm.Type.Parts.Length == 1 && FindLocalType(prm.Type.Parts[0].Name) is Local lt) pt = lt.Type;
                    if (prm.Name != 0) paramLocals.Add(new Local { Name = prm.Name, Type = pt });
                }
                p = close + 1;
            }
            TypeInfo ret = null;
            while (p < end && T[p].Kind == TK.Ident && !T[p].Is('{'))
            {
                int v = T[p].Value;
                if (v == K.Mutable || v == K.Constexpr || v == K.Consteval || v == K.Static) { p++; continue; }
                if (v == K.Noexcept) { p++; if (T[p].Is('(')) p = Parser.After(p); continue; }
                var mk = Parser.Macros.Classify(v);
                if (mk == MacroKind.Decorative) { p++; continue; }
                if (mk == MacroKind.FuncDecorative && T[p + 1].Is('(')) { p = Parser.After(p + 1); continue; }
                break;
            }
            if (T[p].Is('[') && T[p + 1].Is('[')) p = Parser.After(p);
            if (T[p].Is(P.Arrow))
            {
                p++;
                var te = Parser.ParseType(ref p, end, true);
                if (te != null) ret = S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0);
            }
            if (T[p].Is('{'))
            {
                locals.AddRange(initCaptures);
                locals.AddRange(paramLocals);
                Block(ref p);
                if (locals.Count > save) locals.RemoveRange(save, locals.Count - save);
            }
            return Val.Of(new TypeInfo { Builtin = Builtin.FuncSig, LambdaReturn = ret });
        }

        static readonly int TEXT = K.TEXT;

        Val IdExpr(ref int p)
        {
            bool global = false;
            if (T[p].Is(P.Scope)) { global = true; p++; }
            if (T[p].Kind != TK.Ident) return Val.Unknown;
            int nameTok = p;
            int name = T[p].Value;
            p++;
            object r;
            Local loc = null;
            MemberHit hit = default;
            if (global) r = S.LookupIn(S.Root, name);
            else
            {
                r = LookupName(name, out loc);
                hit = lastHit;
            }
            if (loc != null)
            {
                if (loc.IsType)
                {
                    if (T[p].Is(P.Scope)) { p++; return QualifiedRest(ref p, loc.Type?.Sym, loc.Type); }
                    return new Val { TypeVal = loc.Type };
                }
                return new Val { Type = loc.Type, Dependent = loc.Type?.Sym != null && loc.Type.Sym.Kind == SymKind.TemplateParam };
            }
            if (r == null)
            {
                return UnresolvedName(ref p, nameTok, name);
            }
            // qualified name: Scope::...
            if (T[p].Is(P.Scope) || T[p].Is('<') && T[Parser.TryTemplateClose(p, end) + 1].Is(P.Scope) && Parser.TryTemplateClose(p, end) > 0)
            {
                var sc = PickScopeSym(r);
                if (sc != null)
                {
                    Emit(sc, nameTok, RefKind.Type);
                    var targs = TemplateArgsIfAny(ref p, true);
                    if (T[p].Is(P.Scope))
                    {
                        p++;
                        TypeInfo scType = null;
                        if (sc.Kind == SymKind.Typedef) { scType = S.ExpandAlias(sc, targs, 0); sc = scType?.Sym; }
                        else if (sc.Kind == SymKind.Using) { sc = S.ResolveUsingTarget(sc) as Symbol; }
                        else if (sc.IsClassLike) scType = new TypeInfo { Sym = sc, Args = targs };
                        if (sc == null) { SkipQualifiedRest(ref p); return Val.Unknown; }
                        return QualifiedRest(ref p, sc, scType);
                    }
                }
            }
            bool isMember = hit.Members != null && ReferenceEquals(hit.Members, r);
            return FromMembers(ref p, r, nameTok, isMember ? hit.Owner : null, isMember ? hit.OwnerArgs : null, constThis);
        }

        void SkipQualifiedRest(ref int p)
        {
            while (p < end && (T[p].Kind == TK.Ident || T[p].Is(P.Scope) || T[p].Is('~'))) p++;
        }

        Val QualifiedRest(ref int p, Symbol scope, TypeInfo scopeType)
        {
            // p at the name after '::'
            if (IsId(p, K.Template)) p++;
            if (T[p].Is('~')) { p++; if (T[p].Kind == TK.Ident) { Emit(scope, p, RefKind.Type); p++; } return Val.Unknown; }
            if (IsId(p, K.Operator)) { while (p < end && !T[p].Is('(')) p++; return Val.Unknown; }
            if (T[p].Kind != TK.Ident || scope == null) return Val.Unknown;
            int nameTok = p;
            int name = T[p].Value;
            p++;
            object r;
            Symbol owner = null; TypeInfo[] ownerArgs = null;
            if (scope.IsClassLike)
            {
                var hit = LookupMember(scope, scopeType?.Args, name, 0);
                r = hit.Members; owner = hit.Owner; ownerArgs = hit.OwnerArgs;
                if (r == null && name == scope.Name) r = scope; // injected class name
            }
            else if (scope.Kind == SymKind.TemplateParam) { SkipQualifiedRest(ref p); return new Val { Dependent = true }; }
            else r = S.LookupIn(scope, name);
            if (r == null)
            {
                if (scope.IsClassLike || scope.Kind == SymKind.Enum) { UnresolvedMemberNotFound++; EmitUnresolved(nameTok, 2); }
                else { UnresolvedNames++; EmitUnresolved(nameTok, 3); }
                if (T[p].Is(P.Scope)) { p++; SkipQualifiedRest(ref p); }
                return Val.Unknown;
            }
            if (T[p].Is(P.Scope) || T[p].Is('<') && Parser.TryTemplateClose(p, end) > 0 && T[Parser.TryTemplateClose(p, end) + 1].Is(P.Scope))
            {
                var sc = PickScopeSym(r);
                if (sc != null)
                {
                    Emit(sc, nameTok, RefKind.Type);
                    var targs = TemplateArgsIfAny(ref p, true);
                    if (T[p].Is(P.Scope))
                    {
                        p++;
                        TypeInfo scType = null;
                        if (sc.Kind == SymKind.Typedef)
                        {
                            scType = S.ExpandAlias(sc, targs, 0);
                            if (scType != null && owner != null && ownerArgs != null) scType = SymbolTable.Subst(scType, owner, ownerArgs);
                            sc = scType?.Sym;
                        }
                        else if (sc.Kind == SymKind.Using) sc = S.ResolveUsingTarget(sc) as Symbol;
                        else if (sc.IsClassLike) scType = new TypeInfo { Sym = sc, Args = targs };
                        if (sc == null) { SkipQualifiedRest(ref p); return Val.Unknown; }
                        return QualifiedRest(ref p, sc, scType);
                    }
                }
            }
            return FromMembers(ref p, r, nameTok, owner, ownerArgs, false);
        }

        static readonly HashSet<int> memberMacros = new HashSet<int>
        {
            Names.Intern("GET_MEMBER_NAME_CHECKED"), Names.Intern("GET_MEMBER_NAME_STRING_CHECKED"), Names.Intern("GET_FUNCTION_NAME_CHECKED"),
            Names.Intern("GET_FUNCTION_NAME_STRING_CHECKED"), Names.Intern("STRUCT_OFFSET"), Names.Intern("DOREPLIFETIME"), Names.Intern("DOREPLIFETIME_CONDITION"),
            Names.Intern("DOREPLIFETIME_WITH_PARAMS"), Names.Intern("DOREPLIFETIME_WITH_PARAMS_FAST"), Names.Intern("DOREPLIFETIME_CONDITION_NOTIFY"),
            Names.Intern("DOREPLIFETIME_ACTIVE_OVERRIDE"), Names.Intern("DOREPLIFETIME_ACTIVE_OVERRIDE_FAST"), Names.Intern("RESET_REPLIFETIME"),
            Names.Intern("RESET_REPLIFETIME_CONDITION"), Names.Intern("DOREPLIFETIME_WITH_PARAMS_FAST_STATIC_ARRAY"), Names.Intern("GET_FUNCTION_NAME_CHECKED_OneParam"),
            Names.Intern("GET_FUNCTION_NAME_CHECKED_TwoParams"), Names.Intern("GET_FUNCTION_NAME_CHECKED_ThreeParams"), Names.Intern("VTABLE_OFFSET"),
        };

        Val UnresolvedName(ref int p, int nameTok, int name)
        {
            var macro = Parser.Macros.Find(name);
            if (macro != null)
            {
                if (!macro.FunctionLike)
                {
                    if (macro.Body.Length == 0 || macroDepth > 4) return Val.Unknown;
                    if (macroDepth == 0) expansionBudget = 4000;
                    // expand: parse the body as an expression (its tokens never produce references)
                    return ExpandObjectMacro(macro);
                }
                if (T[p].Is('('))
                {
                    int close = Parser.Match[p] > p ? Parser.Match[p] : end;
                    var args = Parser.SplitArgs(p, close);
                    if (memberMacros.Contains(name) && args.Count >= 2)
                    {
                        // (Class, Member, ...): the member is looked up in the class
                        int q = args[0].Item1;
                        var te = Parser.ParseType(ref q, args[0].Item2, false);
                        var ct = te != null ? S.ResolveTypeExpr(te, scopeStart, lex, EmitTypeCb, 0) : null;
                        int m = args[1].Item1;
                        var c = ClassOf(ct);
                        if (c?.Sym != null && T[m].Kind == TK.Ident)
                        {
                            if (name == memberMacrosVtable) S.ResolveTypeExpr(new TypeExpr { Parts = new[] { new NamePart { Name = T[m].Value, Tok = m } } }, scopeStart, lex, EmitTypeCb, 0);
                            else
                            {
                                var hit = LookupMember(c.Sym, c.Args, T[m].Value, 0);
                                var mem = hit.Members is List<Symbol> l ? l.FirstOrDefault() : hit.Members as Symbol;
                                if (mem != null) Emit(mem, m, RefKind.Read);
                            }
                        }
                        for (int a = 2; a < args.Count; a++) { int qq = args[a].Item1; int saveEnd = end; end = args[a].Item2; ExprComma(ref qq); end = saveEnd; }
                        p = close + 1;
                        return Val.Unknown;
                    }
                    int q2 = p + 1;
                    var vals = ExprList(ref q2, close);
                    p = close + 1;
                    if (name == TEXT) return Val.Of(TypeInfo.CharPtr);
                    if (macroDepth == 0) expansionBudget = 4000;
                    if (macro.Body.Length > 0 && macro.Body.Length <= 128) return ParseExpansion(ExpandTokens(macro, args), false);
                    // single-argument pass-through macros (e.g. ensure(x)) keep the argument's type
                    return Val.Unknown;
                }
                return Val.Unknown;
            }
            // unknown name: maybe a template function call Name<...>(...)
            if (T[p].Is('<'))
            {
                int c = Parser.TryTemplateClose(p, end);
                if (c > 0 && (T[c + 1].Is('(') || T[c + 1].Is(P.Scope)))
                {
                    TemplateArgsIfAny(ref p, true);
                }
            }
            if (T[p].Is(P.Scope)) { p++; SkipQualifiedRest(ref p); }
            UnresolvedNames++;
            EmitUnresolved(nameTok, 3);
            return Val.Unknown;
        }

        static readonly int memberMacrosVtable = Names.Intern("VTABLE_OFFSET");

        Val ExpandObjectMacro(MacroDef macro) => ParseExpansion(ExpandTokens(macro, null), false);

        int expansionBudget;

        /// <summary>Substitutes macro arguments (token ranges of the current stream) into the body. All tokens are virtual.</summary>
        Token[] ExpandTokens(MacroDef m, List<(int, int)> args)
        {
            var output = new List<Token>(m.Body.Length + 16);
            var body = m.Body;
            for (int i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (b.Is('#') && i + 1 < body.Length && body[i + 1].Kind == TK.Ident) { output.Add(new Token { Kind = TK.String, Virtual = true }); i++; continue; }
                if (b.Is(P.HashHash))
                {
                    // token pasting of identifiers: A ## B
                    if (i + 1 < body.Length && output.Count > 0)
                    {
                        var next = new List<Token>();
                        AppendParam(m, body[i + 1], args, next);
                        var prev = output[output.Count - 1];
                        if (prev.Kind == TK.Ident && next.Count > 0 && (next[0].Kind == TK.Ident || next[0].Kind == TK.Number))
                        {
                            var text = Names.Get(prev.Value) + (next[0].Kind == TK.Ident ? Names.Get(next[0].Value) : next[0].Value.ToString());
                            prev.Value = Names.Intern(text);
                            output[output.Count - 1] = prev;
                            for (int k = 1; k < next.Count; k++) output.Add(next[k]);
                        }
                        else output.AddRange(next);
                        i++;
                    }
                    continue;
                }
                AppendParam(m, b, args, output);
            }
            var arr = new Token[output.Count + 1];
            for (int i = 0; i < output.Count; i++) { var t = output[i]; t.Virtual = true; arr[i] = t; }
            arr[output.Count] = new Token { Kind = TK.Eof };
            return arr;
        }

        void AppendParam(MacroDef m, Token b, List<(int, int)> args, List<Token> output)
        {
            if (b.Kind == TK.Ident && m.FunctionLike && args != null)
            {
                int pi = Array.IndexOf(m.Params, b.Value);
                if (pi >= 0)
                {
                    if (b.Value == Preprocessor.VaArgs || m.Variadic && pi == m.Params.Length - 1)
                    {
                        for (int a = pi; a < args.Count; a++)
                        {
                            if (a > pi) output.Add(new Token { Kind = TK.Punct, Value = ',' });
                            for (int k = args[a].Item1; k < args[a].Item2; k++) output.Add(T[k]);
                        }
                    }
                    else if (pi < args.Count) for (int k = args[pi].Item1; k < args[pi].Item2; k++) output.Add(T[k]);
                    return;
                }
            }
            output.Add(b);
        }

        Val ParseExpansion(Token[] toks, bool statements)
        {
            int n = toks.Length - 1;
            if (n <= 0 || macroDepth > 4 || expansionBudget <= 0) return Val.Unknown;
            expansionBudget -= n;
            var saveT = T; int saveEnd = end;
            var sub = new DeclParser(toks, n, Parser.Macros, null);
            var saveParser = Parser;
            macroDepth++;
            try
            {
                Parser = sub; T = sub.T; end = n;
                int q = 0;
                if (statements)
                {
                    while (q < n) { int before = q; Statement(ref q, n); if (q <= before) q = before + 1; }
                    return Val.Unknown;
                }
                var v = ExprComma(ref q);
                return v.Funcs != null ? Val.Unknown : v;
            }
            catch (Exception) { return Val.Unknown; }
            finally { Parser = saveParser; T = saveT; end = saveEnd; macroDepth--; }
        }

        /// <summary>Statement that starts with a function-like macro: resolve the spelled arguments, then run the expansion (it may declare locals).</summary>
        bool MacroStatement(ref int p, int stop)
        {
            var t = T[p];
            if (t.Kind != TK.Ident || !T[p + 1].Is('(')) return false;
            var macro = Parser.Macros.Find(t.Value);
            if (macro == null || !macro.FunctionLike) return false;
            if (LookupName(t.Value, out var loc) != null || loc != null) return false;
            int open = p + 1;
            int close = Parser.Match[open] > open ? Parser.Match[open] : stop;
            var args = Parser.SplitArgs(open, close);
            if (memberMacros.Contains(t.Value)) { int q0 = open; UnresolvedName(ref q0, open - 1, t.Value); }
            else
            {
                int q = open + 1;
                ExprList(ref q, close);
                if (macroDepth == 0) expansionBudget = 4000;
                if (macro.Body.Length > 0 && macro.Body.Length <= 256) ParseExpansion(ExpandTokens(macro, args), true);
            }
            p = close + 1;
            if (p < stop && T[p].Is(';')) p++;
            return true;
        }

        // ------------------------------------------------------------------ calls

        static readonly HashSet<int> castLike = new HashSet<int>
        {
            Names.Intern("Cast"), Names.Intern("CastChecked"), Names.Intern("ExactCast"), Names.Intern("CastField"), Names.Intern("ExactCastField"),
            Names.Intern("CastFieldChecked"), Names.Intern("NewObject"), Names.Intern("FindObject"), Names.Intern("FindObjectChecked"), Names.Intern("LoadObject"),
            Names.Intern("FindFirstObject"), Names.Intern("FindObjectFast"), Names.Intern("DuplicateObject"), Names.Intern("GetMutableDefault"),
            Names.Intern("CreateDefaultSubobject"), Names.Intern("CreateOptionalDefaultSubobject"), Names.Intern("CreateEditorOnlyDefaultSubobject"),
            Names.Intern("SpawnActor"), Names.Intern("SpawnActorDeferred"), Names.Intern("CreateWidget"), Names.Intern("GetTypedOuter"),
            Names.Intern("FindComponentByClass"), Names.Intern("GetComponentByClass"), Names.Intern("GetSubsystem"), Names.Intern("GetGameInstance"),
            Names.Intern("GetOwner"), Names.Intern("GetPawn"), Names.Intern("GetController"), Names.Intern("GetPlayerState"), Names.Intern("GetGameMode"),
            Names.Intern("GetGameState"), Names.Intern("GetLocalPlayer"), Names.Intern("GetOuter"), Names.Intern("GetWorldSubsystem"),
            Names.Intern("GetDefaultObject"), Names.Intern("NewNamedObject"), Names.Intern("MakeShared"), Names.Intern("MakeShareable"),
            Names.Intern("GetPlayerController"), Names.Intern("GetPlayerControllerChecked"), Names.Intern("GetPawnChecked"), Names.Intern("GetOwningPlayer"),
            Names.Intern("GetOwningPlayerPawn"), Names.Intern("GetOwningLocalPlayer"), Names.Intern("GetSubsystemBase"),
        };
        static readonly int GetDefaultName = Names.Intern("GetDefault"), MakeSharedName = Names.Intern("MakeShared"), MakeUniqueName = Names.Intern("MakeUnique"),
            TSharedRefName = Names.Intern("TSharedRef"), TUniquePtrName = Names.Intern("TUniquePtr");

        Val Call(Val callee, List<Val> args)
        {
            if (callee.Funcs != null)
            {
                // an argument whose type is a template parameter (Type* Obj) makes the call dependent too
                if (callee.Funcs.Count > 1 && args != null && args.Any(a => a != null && (a.Dependent || a.Type?.Sym?.Kind == SymKind.TemplateParam)))
                {
                    // dependent call: overload resolution happens at instantiation; the reference names every candidate
                    foreach (var cand in callee.Funcs) { emitted[callee.FuncTok < emitted.Length && T == Parser.T ? callee.FuncTok : 0] = 0; Emit(cand, callee.FuncTok, RefKind.Call); }
                    return new Val { Dependent = true };
                }
                var f = ChooseOverload(callee.Funcs, args, callee.TArgs, callee.ConstRecv);
                Emit(f, callee.FuncTok, RefKind.Call);
                var ret = S.DeclaredType(f);
                if (ret != null && callee.Owner != null && callee.OwnerArgs != null) ret = SymbolTable.Subst(ret, callee.Owner, callee.OwnerArgs);
                if (f.TemplateParams != null)
                {
                    var targs = new TypeInfo[f.TemplateParams.Length];
                    if (callee.TArgs != null) for (int i = 0; i < targs.Length && i < callee.TArgs.Length; i++) targs[i] = callee.TArgs[i];
                    if (f.Params != null && args != null)
                        for (int i = 0; i < f.Params.Length && i < args.Count; i++)
                        {
                            var pt = S.ResolveTypeExpr(f.Params[i].Type, f, null, null, 0);
                            Deduce(pt, args[i]?.Type, f, targs, 0);
                        }
                    if (ret != null) ret = SymbolTable.Subst(ret, f, targs);
                    if (ret != null && ContainsParam(ret, f)) ret = null;
                    // UE 5.8 casts return TCopyQualifiersFromTo_T<From, To>*: an alias that does not reduce to a class here
                    if ((ret == null || ret.Sym == null || !ret.Sym.IsClassLike) && callee.TArgs != null && callee.TArgs.Length > 0 && callee.TArgs[0]?.Sym != null)
                    {
                        int fname = f.Name;
                        if (castLike.Contains(fname)) ret = callee.TArgs[0].With(1, false, callee.TArgs[0].Const);
                        else if (fname == GetDefaultName) ret = callee.TArgs[0].With(1, false, true);
                        else if (ret != null && ret.Sym == null) { }
                        else if (ret != null && !ret.Sym.IsClassLike && ret.Sym.Kind != SymKind.Enum) ret = null;
                    }
                }
                if (ret != null && ret.Sym != null && ret.Sym.Kind == SymKind.TemplateParam) ret = null;
                return Val.Of(ret);
            }
            if (callee.TypeVal != null)
            {
                // T(args): a temporary built by one of T's constructors, which clangd references at the type name
                var cls = callee.TypeVal.Sym;
                if (callee.FuncTok >= 0 && cls != null && cls.IsClassLike && callee.TypeVal.Ptr == 0)
                {
                    var m = cls.GetMember(SymbolTable.CtorKey);
                    var ctors = m is Symbol one ? new List<Symbol> { one } : m is List<Symbol> l ? l.Where(x => x.Kind == SymKind.Function).ToList() : null;
                    if (ctors != null && ctors.Count > 0)
                    {
                        var f = ChooseOverload(ctors, args, null, false);
                        if (f != null) { emitted[callee.FuncTok] = 0; Emit(f, callee.FuncTok, RefKind.Call); }
                    }
                }
                return new Val { Type = callee.TypeVal, Dependent = callee.Dependent };
            }
            if (callee.Dependent) return new Val { Dependent = true };
            var t = callee.Type;
            if (t != null)
            {
                if (t.Builtin == Builtin.FuncSig) return Val.Of(t.LambdaReturn);
                var c = ClassOf(t.Value);
                if (c?.Sym != null && c.Ptr == 0)
                {
                    var hit = LookupMember(c.Sym, c.Args, K.OperatorCall, 0);
                    var m = First(hit.Members);
                    if (m != null && m.Kind == SymKind.Function) return Val.Of(MemberType(m, hit.Owner, hit.OwnerArgs));
                    // delegates: Execute/Broadcast are members; calling the object itself is rare
                }
            }
            return Val.Unknown;
        }

        static bool ContainsParam(TypeInfo t, Symbol owner, int depth = 0)
        {
            if (t == null || depth > 8) return false;
            if (t.Sym != null && t.Sym.Kind == SymKind.TemplateParam && t.Sym.Parent == owner) return true;
            if (t.Args != null) foreach (var a in t.Args) if (ContainsParam(a, owner, depth + 1)) return true;
            return false;
        }

        void Deduce(TypeInfo param, TypeInfo arg, Symbol fn, TypeInfo[] targs, int depth)
        {
            if (param == null || arg == null || depth > 6) return;
            if (param.Sym != null && param.Sym.Kind == SymKind.TemplateParam && param.Sym.Parent == fn)
            {
                int i = param.Sym.TemplateIndex;
                if (i < targs.Length && targs[i] == null)
                {
                    int ptr = arg.Ptr - param.Ptr;
                    if (ptr >= 0) targs[i] = arg.With((byte)ptr, false, ptr > 0 ? arg.Const : false);
                }
                return;
            }
            if (param.Sym != null && param.Args != null && arg.Sym == param.Sym && arg.Args != null)
                for (int k = 0; k < param.Args.Length && k < arg.Args.Length; k++) Deduce(param.Args[k], arg.Args[k], fn, targs, depth + 1);
        }

        Symbol ChooseOverload(List<Symbol> funcs, List<Val> args, TypeInfo[] targs, bool constRecv)
        {
            if (funcs.Count == 1) return funcs[0];
            int n = args?.Count ?? -1;
            Symbol best = null;
            int bestScore = int.MinValue;
            foreach (var f in funcs)
            {
                int score = 0;
                if (n >= 0)
                {
                    int min = f.Params == null ? 0 : f.MinArgs, max = f.Params == null ? 255 : f.MaxArgs;
                    if (n < min || n > max) score -= 1000;
                    else if (f.Params != null)
                    {
                        for (int i = 0; i < n && i < f.Params.Length; i++)
                            score += ArgScore(S.ResolveTypeExpr(f.Params[i].Type, f, null, null, 0), args[i]?.Type, f);
                    }
                }
                if (targs != null) score += f.TemplateParams != null ? 20 : -20;
                else if (f.TemplateParams != null) score -= 1;
                bool isConst = (f.Flags & DeclFlags.Const) != 0;
                if (constRecv) score += isConst ? 2 : -5;
                else score += isConst ? 0 : 1;
                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best ?? funcs[0];
        }

        int ArgScore(TypeInfo param, TypeInfo arg, Symbol fn)
        {
            if (param == null || arg == null) return 0;
            if (param.Sym != null && param.Sym.Kind == SymKind.TemplateParam) return 1;
            var pc = ClassOf(param) ?? param;
            var ac = ClassOf(arg) ?? arg;
            if (arg.Builtin == Builtin.Nullptr) return pc.Ptr > 0 ? 3 : -2;
            if (pc.Sym == null && ac.Sym == null)
            {
                if (pc.Builtin == Builtin.Unknown || ac.Builtin == Builtin.Unknown) return 0;
                if (pc.Ptr != ac.Ptr) return -3;
                return pc.Builtin == ac.Builtin ? 3 : 1;
            }
            if (pc.Sym != null && ac.Sym != null)
            {
                if (pc.Sym == ac.Sym) return pc.Ptr == ac.Ptr ? 4 : -2;
                if (IsDerived(ac.Sym, pc.Sym, 0)) return pc.Ptr == ac.Ptr ? 3 : -2;
                return -1;
            }
            // class vs builtin: implicit conversions are possible but less likely
            if (pc.Sym != null && ac.Sym == null) return ac.Builtin == Builtin.Char && ac.Ptr == 1 ? 0 : -2;
            return -2;
        }

        bool IsDerived(Symbol d, Symbol b, int depth)
        {
            if (d == null || depth > 16) return false;
            if (d == b) return true;
            if (!d.IsClassLike) return false;
            var bases = S.GetBases(d);
            if (bases == null) return false;
            foreach (var x in bases) if (x?.Sym != null && IsDerived(x.Sym, b, depth + 1)) return true;
            return false;
        }

        // ------------------------------------------------------------------ operators on types

        static readonly HashSet<int> pointerLike = new HashSet<int>
        {
            Names.Intern("TObjectPtr"), Names.Intern("TWeakObjectPtr"), Names.Intern("TSoftObjectPtr"), Names.Intern("TLazyObjectPtr"), Names.Intern("TStrongObjectPtr"),
            Names.Intern("TSharedPtr"), Names.Intern("TSharedRef"), Names.Intern("TUniquePtr"), Names.Intern("TScriptInterface"), Names.Intern("TNonNullPtr"),
            Names.Intern("TOptional"), Names.Intern("TAutoWeakObjectPtr"), Names.Intern("TWeakInterfacePtr"), Names.Intern("TObjectIterator"), Names.Intern("TActorIterator"),
            Names.Intern("TFieldIterator"), Names.Intern("TGCObjectScopeGuard"), Names.Intern("TRefCountPtr"), Names.Intern("TInterfaceInstance"),
        };
        static readonly HashSet<int> arrayLike = new HashSet<int>
        {
            Names.Intern("TArray"), Names.Intern("TArrayView"), Names.Intern("TConstArrayView"), Names.Intern("TSet"), Names.Intern("TInlineComponentArray"),
            Names.Intern("TIndirectArray"), Names.Intern("TStaticArray"), Names.Intern("TSparseArray"), Names.Intern("TChunkedArray"), Names.Intern("TArray64"),
            Names.Intern("TList"), Names.Intern("TLinkedList"), Names.Intern("TDoubleLinkedList"), Names.Intern("TObjectRange"), Names.Intern("TActorRange"),
            Names.Intern("TSortedArray"), Names.Intern("TResizableCircularQueue"), Names.Intern("TCircularBuffer"), Names.Intern("TBitArray"),
            Names.Intern("TArrayView64"), Names.Intern("TStridedView"), Names.Intern("TSubsystemArray"), Names.Intern("TFieldRange"), Names.Intern("TMultiArray"),
            Names.Intern("TStaticArrayView"), Names.Intern("TSpan"), Names.Intern("TFixedAllocatorArray"), Names.Intern("TInlineArray"),
        };
        static readonly HashSet<int> mapLike = new HashSet<int>
        {
            Names.Intern("TMap"), Names.Intern("TMultiMap"), Names.Intern("TSortedMap"), Names.Intern("TSortedMultiMap"), Names.Intern("TSmallMap"), Names.Intern("TMapBase"),
        };

        TypeInfo Deref(TypeInfo t)
        {
            if (t == null) return null;
            if (t.Ptr > 0) return t.Deref();
            var c = ClassOf(t);
            if (c?.Sym == null) return null;
            if (c.Ptr > 0) return c.Deref();
            var hit = LookupMember(c.Sym, c.Args, K.OperatorStar, 0);
            var m = First(hit.Members);
            if (m != null && m.Kind == SymKind.Function)
            {
                var r = MemberType(m, hit.Owner, hit.OwnerArgs);
                if (r != null && !(r.Sym != null && r.Sym.Kind == SymKind.TemplateParam)) return r.Value;
            }
            if (pointerLike.Contains(c.Sym.Name) && c.Args != null && c.Args.Length > 0) return c.Args[0];
            return null;
        }

        TypeInfo Arrow(TypeInfo t)
        {
            if (t == null) return null;
            if (t.Ptr > 0) return t.Deref();
            var c = ClassOf(t);
            if (c?.Sym == null) return null;
            if (c.Ptr > 0) return c.Deref();
            if (pointerLike.Contains(c.Sym.Name) && c.Args != null && c.Args.Length > 0 && c.Args[0] != null) return c.Args[0].Value;
            var hit = LookupMember(c.Sym, c.Args, K.OperatorArrow, 0);
            var m = First(hit.Members);
            if (m != null && m.Kind == SymKind.Function)
            {
                var r = MemberType(m, hit.Owner, hit.OwnerArgs);
                if (r != null && !(r.Sym != null && r.Sym.Kind == SymKind.TemplateParam))
                    return r.Ptr > 0 ? r.Deref() : Arrow(r);
            }
            return null;
        }

        TypeInfo Index(TypeInfo t)
        {
            if (t == null) return null;
            if (t.Ptr > 0) return t.Deref();
            var c = ClassOf(t);
            if (c?.Sym == null) return null;
            if (c.Ptr > 0) return c.Deref();
            var hit = LookupMember(c.Sym, c.Args, K.OperatorIndex, 0);
            var m = First(hit.Members);
            if (m != null && m.Kind == SymKind.Function)
            {
                var r = MemberType(m, hit.Owner, hit.OwnerArgs);
                if (r != null && !ContainsAnyParam(r)) return r.Value;
            }
            if (c.Args != null && c.Args.Length > 0)
            {
                if (mapLike.Contains(c.Sym.Name) && c.Args.Length > 1) return c.Args[1];
                if (arrayLike.Contains(c.Sym.Name)) return c.Args[0];
            }
            return null;
        }

        static bool ContainsAnyParam(TypeInfo t, int depth = 0)
        {
            if (t == null || depth > 8) return false;
            if (t.Sym != null && t.Sym.Kind == SymKind.TemplateParam) return true;
            if (t.Args != null) foreach (var a in t.Args) if (ContainsAnyParam(a, depth + 1)) return true;
            return false;
        }

        TypeInfo ElementOf(TypeInfo t, int depth = 0)
        {
            if (t == null || depth > 6) return null;
            var c = ClassOf(t.Value);
            if (c == null) return null;
            if (c.Ptr > 0 && c.Sym != null && (arrayLike.Contains(c.Sym.Name) || mapLike.Contains(c.Sym.Name))) c = c.Deref();
            if (c.Sym == null) return c.Ptr > 0 ? c.Deref() : null;
            if (c.Args != null && c.Args.Length > 0)
            {
                if (mapLike.Contains(c.Sym.Name) && c.Args.Length > 1) return new TypeInfo { Builtin = Builtin.Unknown, Args = new[] { c.Args[0], c.Args[1] } };
                if (arrayLike.Contains(c.Sym.Name)) return c.Args[0];
            }
            // bases (e.g. TInlineComponentArray<T> : TArray<T, ...>)
            var bases = c.Sym.IsClassLike ? S.GetBases(c.Sym) : null;
            if (bases != null)
                foreach (var b in bases)
                {
                    if (b?.Sym == null) continue;
                    var bb = c.Args != null ? SymbolTable.Subst(b, c.Sym, c.Args) : b;
                    var e = ElementOf(bb, depth + 1);
                    if (e != null) return e;
                }
            // generic: begin() then operator*
            var hit = LookupMember(c.Sym, c.Args, K.Begin, 0);
            var m = First(hit.Members);
            if (m != null && m.Kind == SymKind.Function)
            {
                var it = MemberType(m, hit.Owner, hit.OwnerArgs);
                if (it != null && !ContainsAnyParam(it)) return Deref(it);
            }
            return null;
        }
    }
}
