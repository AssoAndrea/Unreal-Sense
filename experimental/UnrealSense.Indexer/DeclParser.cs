using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealSense.Indexer
{
    public sealed class ScopeCtx
    {
        public ScopeCtx Outer;
        public SymKind Kind;            // Namespace (also file root), Class/Struct/Union, Enum
        public int DeclIndex = -1;
        public int ClassName;
        public Symbol Sym;              // pass 2
        public List<Symbol> Usings;     // pass 2: using-directives active in this lexical scope
        public bool Generated;
        public bool IsClass => Kind == SymKind.Class || Kind == SymKind.Struct || Kind == SymKind.Union;
    }

    /// <summary>Pass-2 hooks (resolution). Pass 1 runs the same parser with a null sink.</summary>
    public interface IDeclSink
    {
        Symbol OnDecl(Decl d, int index, int nameTok, ScopeCtx ctx);
        void OnDeclTypes(Decl d, Symbol sym, ScopeCtx ctx, int qualTokStart);
        void OnBody(int open, int close, ScopeCtx ctx, Symbol fn, Decl decl, TypeExpr ownerOverride, int ctorInitStart);
        void OnExpr(int start, int end, ScopeCtx ctx, Symbol owner);
        void OnSoup(int start, int end, ScopeCtx ctx);
        void OnType(TypeExpr t, ScopeCtx ctx);
        void OnUsingDirective(TypeExpr ns, ScopeCtx ctx);
        void OnGeneratedBody(int tok, ScopeCtx ctx);
        void OnClassBodyMacro(int tok, ScopeCtx ctx);
        void OnNamespaceAlias(int nameTok, TypeExpr target, ScopeCtx ctx);
    }

    public enum MacroKind : byte { None, Decorative, FuncDecorative, ObjectOther, FuncOther }

    /// <summary>Macro knowledge for one file: global environment + the file's own #defines.</summary>
    public sealed class FileMacros
    {
        readonly MacroEnv env;
        readonly Dictionary<int, MacroDef> local;

        public FileMacros(MacroEnv env, List<MacroDef> defines)
        {
            this.env = env;
            if (defines != null && defines.Count > 0)
            {
                local = new Dictionary<int, MacroDef>();
                foreach (var d in defines) local[d.Name] = d;
            }
        }

        public MacroDef Find(int name)
        {
            if (local != null && local.TryGetValue(name, out var m)) return m;
            return env.Find(name);
        }

        static readonly HashSet<int> decorativeKeywords = new HashSet<int>
        {
            K.Inline, K.Static, K.Constexpr, K.Consteval, K.Constinit, K.Forceinline, K.Virtual, K.Explicit, K.Extern, K.Restrict,
            K.Cdecl, K.Stdcall, K.Fastcall, K.Vectorcall, K.Mutable, K.ThreadLocal, Names.Intern("noexcept"), Names.Intern("__inline"),
            Names.Intern("__forceinline"), Names.Intern("__unaligned"), Names.Intern("__ptr64"), Names.Intern("__w64"), Names.Intern("final"),
        };
        static readonly HashSet<int> groupKeywords = new HashSet<int>
        {
            K.Declspec, K.Attribute, K.Alignas, K.Pragma, K.PragmaOp, Names.Intern("__declspec"), Names.Intern("_declspec"), Names.Intern("__asm"),
        };

        public MacroKind Classify(int name) => Classify(name, 0);

        MacroKind Classify(int name, int depth)
        {
            var m = Find(name);
            if (m == null)
            {
                return IsApiName(name) ? MacroKind.Decorative : MacroKind.None;
            }
            if (m.Kind >= 0 && local == null) return (MacroKind)m.Kind;
            var kind = ComputeKind(m, depth);
            if (local == null || !local.ContainsKey(name)) m.Kind = (int)kind;
            return kind;
        }

        static readonly Dictionary<int, bool> apiCache = new Dictionary<int, bool>();
        public static bool IsApiName(int name)
        {
            var s = Names.Get(name);
            if (s.Length < 5 || !s.EndsWith("_API", StringComparison.Ordinal)) return false;
            foreach (var c in s) if (!(c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }

        MacroKind ComputeKind(MacroDef m, int depth)
        {
            if (depth > 12) return m.FunctionLike ? MacroKind.FuncOther : MacroKind.ObjectOther;
            var b = m.Body;
            bool deco = true;
            for (int i = 0; i < b.Length && deco; i++)
            {
                var t = b[i];
                if (t.Kind == TK.Ident)
                {
                    if (decorativeKeywords.Contains(t.Value)) continue;
                    if (groupKeywords.Contains(t.Value))
                    {
                        i = SkipGroup(b, i + 1);
                        continue;
                    }
                    if (m.FunctionLike && Array.IndexOf(m.Params, t.Value) >= 0) { deco = false; break; }
                    var sub = Find(t.Value);
                    if (sub != null && sub != m)
                    {
                        var k = Classify(t.Value, depth + 1);
                        if (k == MacroKind.Decorative) continue;
                        if (k == MacroKind.FuncDecorative && i + 1 < b.Length && b[i + 1].Is('(')) { i = SkipGroup(b, i + 1); continue; }
                    }
                    else if (sub == null && IsApiName(t.Value)) continue;
                    deco = false;
                }
                else if (t.Is('[') && i + 1 < b.Length && b[i + 1].Is('['))
                {
                    int d = 0, j = i;
                    for (; j < b.Length; j++) { if (b[j].Is('[')) d++; else if (b[j].Is(']')) { d--; if (d == 0) break; } }
                    i = j;
                }
                else if (t.Is(';')) { deco = false; }
                else deco = false;
            }
            if (deco) return m.FunctionLike ? MacroKind.FuncDecorative : MacroKind.Decorative;
            return m.FunctionLike ? MacroKind.FuncOther : MacroKind.ObjectOther;
        }

        static int SkipGroup(Token[] b, int i)
        {
            // b[i] should be '(' : skip to matching ')' (returns index of ')')
            if (i >= b.Length || !b[i].Is('(')) return i - 1;
            int d = 0;
            for (int j = i; j < b.Length; j++)
            {
                if (b[j].Is('(')) d++;
                else if (b[j].Is(')')) { d--; if (d == 0) return j; }
            }
            return b.Length - 1;
        }
    }

    /// <summary>
    /// Declaration parser shared by pass 1 (collects <see cref="Decl"/>s, skips bodies) and pass 2 (same walk,
    /// with a sink that resolves types/bodies and emits references). Purely syntactic decisions so that both passes
    /// see the same sequence of declarations.
    /// </summary>
    public sealed class DeclParser
    {
        public readonly Token[] T;
        public readonly int N;
        public readonly int[] Match;
        public readonly FileMacros Macros;
        readonly IDeclSink sink;
        public readonly List<Decl> Decls = new List<Decl>();

        public DeclParser(Token[] tokens, int count, FileMacros macros, IDeclSink sink)
        {
            T = tokens; N = count; Macros = macros; this.sink = sink;
            if (T.Length <= N) Array.Resize(ref T, N + 1);
            T[N] = new Token { Kind = TK.Eof, Pos = N > 0 ? T[N - 1].Pos + T[N - 1].Len : 0, Line = N > 0 ? T[N - 1].Line : 1 };
            Match = ComputeMatches(T, N);
        }

        public static int[] ComputeMatches(Token[] t, int n)
        {
            var match = new int[n + 1];
            var stack = new List<int>(64);
            for (int i = 0; i < n; i++)
            {
                match[i] = -1;
                if (t[i].Kind != TK.Punct) continue;
                int v = t[i].Value;
                if (v == '(' || v == '[' || v == '{') stack.Add(i);
                else if (v == ')' || v == ']' || v == '}')
                {
                    int open = v == ')' ? '(' : v == ']' ? '[' : '{';
                    for (int s = stack.Count - 1; s >= 0 && s >= stack.Count - 4; s--)
                    {
                        if (t[stack[s]].Value == open)
                        {
                            match[stack[s]] = i; match[i] = stack[s];
                            stack.RemoveRange(s, stack.Count - s);
                            break;
                        }
                        if (v != '}' ) break; // ')' or ']' only match the top
                    }
                }
            }
            match[n] = -1;
            return match;
        }

        // ------------------------------------------------------------------ helpers

        public bool IsId(int p, int name) => T[p].Kind == TK.Ident && T[p].Value == name;
        public bool IsP(int p, int c) => T[p].Kind == TK.Punct && T[p].Value == c;

        /// <summary>Index after the group opened at p ('(' '[' '{'); if unmatched, p+1.</summary>
        public int After(int p)
        {
            int m = Match[p];
            return m > p ? m + 1 : p + 1;
        }

        public MacroKind MacroOf(int p) => T[p].Kind == TK.Ident ? Macros.Classify(T[p].Value) : MacroKind.None;

        static readonly HashSet<int> builtinTypeWords = new HashSet<int>
        {
            K.Void, K.Bool, K.Char, K.WcharT, K.Char8, K.Char16, K.Char32, K.Short, K.Int, K.Long, K.Float, K.Double, K.Signed, K.Unsigned,
            K.Int64, K.Int32k, K.Int16k, K.Int8k, K.Auto,
        };
        public static bool IsBuiltinWord(int v) => builtinTypeWords.Contains(v);

        static readonly HashSet<int> keywords = new HashSet<int>
        {
            K.Class, K.Struct, K.Union, K.Enum, K.Namespace, K.Template, K.Typename, K.Typedef, K.Using, K.Public, K.Private, K.Protected,
            K.Virtual, K.Static, K.Inline, K.Const, K.Constexpr, K.Consteval, K.Constinit, K.Volatile, K.Mutable, K.Explicit, K.Extern, K.Friend,
            K.Operator, K.Noexcept, K.ThreadLocal, K.Register, K.Decltype, K.Sizeof, K.Alignof, K.Alignas, K.StaticAssert, K.Requires, K.Concept,
            K.If, K.Else, K.For, K.While, K.Do, K.Switch, K.Case, K.Default, K.Return, K.Break, K.Continue, K.Goto, K.Try, K.Catch, K.Throw,
            K.New, K.Delete, K.This, K.True, K.False, K.Nullptr, K.StaticCast, K.DynamicCast, K.ReinterpretCast, K.ConstCast, K.Typeid, K.CoReturn,
        };
        public static bool IsKeyword(int v) => keywords.Contains(v) || builtinTypeWords.Contains(v);

        /// <summary>Skips an expression until one of the stop punctuators at depth 0 (balanced groups; template args heuristic).</summary>
        public int SkipExpr(int p, int end, int stop1, int stop2 = -1, int stop3 = -1)
        {
            while (p < end)
            {
                var t = T[p];
                if (t.Kind == TK.Punct)
                {
                    int v = t.Value;
                    if (v == stop1 || v == stop2 || v == stop3) return p;
                    if (v == '(' || v == '[' || v == '{') { p = After(p); continue; }
                    if (v == ')' || v == ']' || v == '}' || v == ';') return p;
                    if (v == '<' && p > 0 && T[p - 1].Kind == TK.Ident)
                    {
                        int q = TryTemplateClose(p, end);
                        if (q > 0) { p = q + 1; continue; }
                    }
                }
                p++;
            }
            return p;
        }

        /// <summary>If '<' at p plausibly opens template arguments, returns the index of the matching '>', else -1.</summary>
        public int TryTemplateClose(int p, int end)
        {
            int depth = 0;
            for (int q = p; q < end && q < p + 400; q++)
            {
                var t = T[q];
                if (t.Kind == TK.Punct)
                {
                    int v = t.Value;
                    if (v == '<') depth++;
                    else if (v == '>') { depth--; if (depth == 0) return q; }
                    else if (v == '(' || v == '[') { int m = Match[q]; if (m < 0) return -1; q = m; }
                    else if (v == ';' || v == '{' || v == '}' || v == ')' || v == ']' || v == P.AndAnd || v == P.OrOr) return -1;
                }
            }
            return -1;
        }

        // ------------------------------------------------------------------ types

        /// <summary>Parses a type (cv, builtin or qualified name with template args, optional ptr/ref). Returns null if no type here.</summary>
        public TypeExpr ParseType(ref int p, int end, bool allowPtr)
        {
            int start = p;
            var t = new TypeExpr();
            bool elaborated = false;
            while (p < end && T[p].Kind == TK.Ident)
            {
                int v = T[p].Value;
                if (v == K.Const || v == K.Volatile) { t.Const |= v == K.Const; p++; continue; }
                if (v == K.Typename || v == K.Register || v == K.Mutable || v == K.Constexpr || v == K.Static || v == K.Inline || v == K.Extern || v == K.ThreadLocal) { p++; continue; }
                if (v == K.Class || v == K.Struct || v == K.Union || v == K.Enum) { elaborated = true; p++; if (IsId(p, K.Class) || IsId(p, K.Struct)) p++; continue; }
                if (v == K.UPARAM && IsP(p + 1, '(')) { p = After(p + 1); continue; }
                if (!builtinTypeWords.Contains(v) && !keywords.Contains(v))
                {
                    var mk = Macros.Classify(v);
                    if (mk == MacroKind.Decorative) { p++; continue; }
                    if (mk == MacroKind.FuncDecorative && IsP(p + 1, '(')) { p = After(p + 1); continue; }
                }
                break;
            }
            if (p >= end) { p = start; return null; }
            var tok = T[p];
            if (tok.Kind == TK.Ident && builtinTypeWords.Contains(tok.Value))
            {
                Builtin b = Builtin.Int;
                while (p < end && T[p].Kind == TK.Ident && builtinTypeWords.Contains(T[p].Value))
                {
                    int v = T[p].Value;
                    if (v == K.Void) b = Builtin.Void;
                    else if (v == K.Bool) b = Builtin.Bool;
                    else if (v == K.Float || v == K.Double) b = Builtin.Float;
                    else if (v == K.Char || v == K.WcharT || v == K.Char8 || v == K.Char16 || v == K.Char32) b = Builtin.Char;
                    else if (v == K.Auto) b = Builtin.Auto;
                    p++;
                }
                t.Builtin = b;
            }
            else if (tok.Kind == TK.Ident && tok.Value == K.Decltype && IsP(p + 1, '('))
            {
                t.Builtin = Builtin.Unknown;
                t.Parts = null;
                t.FuncReturn = null;
                t.FuncParams = null;
                t.Ptr = 0;
                // keep the expression range for pass 2: store start in Builtin? (kept simple: unknown)
                p = After(p + 1);
            }
            else if (tok.Kind == TK.Ident && !keywords.Contains(tok.Value) || tok.Is(P.Scope))
            {
                if (tok.Is(P.Scope)) { t.Global = true; p++; }
                var parts = new List<NamePart>(2);
                while (p < end)
                {
                    if (IsId(p, K.Template)) p++;
                    if (T[p].Kind != TK.Ident || keywords.Contains(T[p].Value) && T[p].Value != K.Typename) break;
                    var part = new NamePart { Name = T[p].Value, Tok = p };
                    p++;
                    if (IsP(p, '<'))
                    {
                        int save = p;
                        var args = ParseTemplateArgs(ref p, end);
                        if (args == null) { p = save; parts.Add(part); break; }
                        part.Args = args;
                    }
                    parts.Add(part);
                    if (IsP(p, P.Scope) && (T[p + 1].Kind == TK.Ident && T[p + 1].Value != K.Operator || IsId(p + 1, K.Template)))
                    {
                        // A::B — but stop before "A::~A" / "A::operator" / "A::*"
                        p++;
                        continue;
                    }
                    break;
                }
                if (parts.Count == 0) { p = start; return null; }
                t.Parts = parts.ToArray();
            }
            else { p = start; return null; }

            while (p < end && T[p].Kind == TK.Ident && (T[p].Value == K.Const || T[p].Value == K.Volatile)) { t.Const |= T[p].Value == K.Const; p++; }
            if (allowPtr) ParsePtrOps(ref p, end, t);
            return t;
        }

        public void ParsePtrOps(ref int p, int end, TypeExpr t)
        {
            while (p < end)
            {
                var x = T[p];
                if (x.Kind == TK.Punct)
                {
                    if (x.Value == '*') { t.Ptr++; p++; continue; }
                    if (x.Value == '&' || x.Value == P.AndAnd) { t.Ref = true; p++; continue; }
                    break;
                }
                if (x.Kind == TK.Ident)
                {
                    int v = x.Value;
                    if (v == K.Const || v == K.Volatile || v == K.Restrict || v == K.Cdecl || v == K.Stdcall || v == K.Fastcall || v == K.Vectorcall) { p++; continue; }
                    var mk = Macros.Classify(v);
                    if (mk == MacroKind.Decorative && !keywords.Contains(v)) { p++; continue; }
                }
                break;
            }
        }

        /// <summary>Parses '&lt;' ... '&gt;' at p. Returns null if it does not look like a template argument list.</summary>
        public TypeExpr[] ParseTemplateArgs(ref int p, int end)
        {
            int close = TryTemplateClose(p, end);
            if (close < 0) return null;
            var args = new List<TypeExpr>(2);
            p++;
            while (p < close)
            {
                int s = p;
                var a = ParseType(ref p, close, true);
                if (a != null && IsP(p, '('))
                {
                    // function type: R(Args...)
                    var ps = ParseParams(ref p, close, out _, out _, out _);
                    var f = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = a, FuncParams = Array.ConvertAll(ps, x => x.Type) };
                    a = f;
                }
                if (IsP(p, P.Ellipsis)) p++;
                if (a == null || !(IsP(p, ',') || p == close))
                {
                    // non-type argument (expression)
                    p = SkipExpr(s, close, ',');
                    if (p == s) p++;
                    a = new TypeExpr { Builtin = Builtin.NonType };
                    // keep expression range for pass 2 soup resolution
                    a.FuncParams = null;
                    a.Parts = null;
                    a.Ptr = 0;
                    nonTypeRanges?.Add((s, p));
                }
                args.Add(a);
                if (IsP(p, ',')) p++;
            }
            p = close + 1;
            return args.ToArray();
        }

        /// <summary>Pass 2: non-type template argument ranges (resolved as token soup).</summary>
        public List<(int, int)> nonTypeRanges;

        public Param[] ParseParams(ref int p, int end, out short min, out short max, out bool variadic)
        {
            // p at '('
            int close = Match[p] > p ? Match[p] : end;
            p++;
            var list = new List<Param>(4);
            variadic = false;
            min = 0;
            while (p < close)
            {
                if (IsP(p, P.Ellipsis)) { variadic = true; p++; continue; }
                if (IsP(p, ',')) { p++; continue; }
                if (IsId(p, K.Void) && p + 1 == close) { p++; break; }
                // attributes / UPARAM
                while (p < close && (IsP(p, '[') && IsP(p + 1, '[') || IsId(p, K.UPARAM) && IsP(p + 1, '('))) p = After(p + (IsP(p, '[') ? 0 : 1));
                int s = p;
                var t = ParseType(ref p, close, true);
                var prm = new Param { Type = t ?? TypeExpr.UnknownType };
                if (t == null) { p = SkipExpr(s, close, ','); if (p == s) p++; list.Add(prm); continue; }
                if (IsP(p, P.Ellipsis)) { variadic = true; p++; }
                if (IsP(p, '(') && (IsP(p + 1, '*') || IsP(p + 1, '&')))
                {
                    // function pointer parameter: R (*Name)(Args)
                    int inner = p + 1;
                    while (inner < close && (IsP(inner, '*') || IsP(inner, '&'))) inner++;
                    if (T[inner].Kind == TK.Ident) prm.Name = T[inner].Value;
                    p = After(p);
                    if (IsP(p, '(')) p = After(p);
                    prm.Type = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = t };
                }
                else if (T[p].Kind == TK.Ident && !keywords.Contains(T[p].Value))
                {
                    var mk = Macros.Classify(T[p].Value);
                    if (mk == MacroKind.Decorative) p++;
                    if (T[p].Kind == TK.Ident && !keywords.Contains(T[p].Value)) { prm.Name = T[p].Value; p++; }
                }
                while (IsP(p, '[')) p = After(p);
                if (IsP(p, '='))
                {
                    int ds = p + 1;
                    p = SkipExpr(p + 1, close, ',');
                    prm.HasDefault = true;
                    defaultArgRanges?.Add((ds, p));
                }
                else min++;
                // anything else up to ',' (e.g. macros)
                if (!IsP(p, ',') && p < close) p = SkipExpr(p, close, ',');
                list.Add(prm);
            }
            p = close + 1;
            max = variadic ? short.MaxValue : (short)list.Count;
            if (variadic) min = (short)Math.Min(min, list.Count);
            return list.ToArray();
        }

        public List<(int, int)> defaultArgRanges;

        // ------------------------------------------------------------------ declarations

        int AddDecl(Decl d, ScopeCtx ctx, int nameTok, out Symbol sym)
        {
            d.Parent = ctx.DeclIndex;
            if (d.Parent >= 0 && (Decls[d.Parent].Qualifier != null || Decls[d.Parent].HasQualifiedAncestor)) d.HasQualifiedAncestor = true;
            if (nameTok >= 0) { d.Line = T[nameTok].Line; d.Col = T[nameTok].Col; }
            Decls.Add(d);
            int idx = Decls.Count - 1;
            sym = sink?.OnDecl(d, idx, nameTok, ctx);
            return idx;
        }

        public void ParseFile()
        {
            var root = new ScopeCtx { Kind = SymKind.Namespace };
            if (sink != null) root.Sym = null; // sink fills root lazily
            int p = 0;
            ParseScope(ref p, N, root);
        }

        void ParseScope(ref int p, int end, ScopeCtx ctx)
        {
            while (p < end)
            {
                int before = p;
                if (IsP(p, ';')) { p++; continue; }
                if (IsP(p, '}')) { p++; continue; } // stray
                ParseDeclaration(ref p, end, ctx, null);
                if (p <= before) p = before + 1;
            }
        }

        static readonly int AccessPublic = K.Public;

        void ParseDeclaration(ref int p, int end, ScopeCtx ctx, int[] templateParams)
        {
            var t = T[p];
            bool isSpecialization = false;
            if (t.Kind == TK.Ident)
            {
                int v = t.Value;
                if (v == K.Template)
                {
                    if (IsP(p + 1, '<'))
                    {
                        int close = TryTemplateClose(p + 1, end);
                        if (close < 0) { p = SkipStatement(p, end); return; }
                        if (close == p + 2) isSpecialization = true;
                        var names = new List<int>();
                        ParseTemplateParamNames(p + 2, close, names);
                        p = close + 1;
                        // requires-clause
                        if (IsId(p, K.Requires)) p = SkipRequires(p + 1, end);
                        if (p >= end) return;
                        int[] tp = names.ToArray();
                        if (templateParams != null && templateParams.Length > 0)
                        {
                            var merged = new int[templateParams.Length + tp.Length];
                            templateParams.CopyTo(merged, 0); tp.CopyTo(merged, templateParams.Length);
                            tp = merged;
                        }
                        ParseDeclarationInner(ref p, end, ctx, tp, isSpecialization || tp.Length == 0);
                        return;
                    }
                    // explicit instantiation
                    p = SkipStatement(p, end);
                    return;
                }
            }
            ParseDeclarationInner(ref p, end, ctx, templateParams, false);
        }

        int SkipRequires(int p, int end)
        {
            // skip a requires expression: primary expressions joined by && / ||
            while (p < end)
            {
                if (IsP(p, '(')) p = After(p);
                else if (T[p].Kind == TK.Ident || IsP(p, P.Scope)) { p++; if (IsP(p, '<')) { int c = TryTemplateClose(p, end); if (c > 0) p = c + 1; } }
                else if (IsP(p, '!')) p++;
                else break;
                if (IsP(p, P.Scope)) { p++; continue; }
                if (IsP(p, P.AndAnd) || IsP(p, P.OrOr)) { p++; continue; }
                break;
            }
            return p;
        }

        void ParseTemplateParamNames(int p, int close, List<int> names)
        {
            while (p < close)
            {
                int s = p;
                int argEnd = SkipExpr(p, close, ',');
                // name = last identifier before '=' (or before end) that is not a keyword
                int nameTok = -1;
                for (int q = s; q < argEnd; q++)
                {
                    if (IsP(q, '=')) break;
                    if (IsP(q, '<')) { int c = TryTemplateClose(q, argEnd); if (c > 0) { q = c; continue; } }
                    if (IsP(q, '(')) { q = Match[q] > q ? Match[q] : q; continue; }
                    if (T[q].Kind == TK.Ident && !keywords.Contains(T[q].Value)) nameTok = q;
                }
                names.Add(nameTok >= 0 && nameTok > s ? T[nameTok].Value : nameTok == s ? T[s].Value : 0);
                p = argEnd + 1;
            }
        }

        public int SkipStatement(int p, int end)
        {
            while (p < end)
            {
                if (IsP(p, ';')) return p + 1;
                if (IsP(p, '{')) return After(p);
                if (IsP(p, '(') || IsP(p, '[')) { p = After(p); continue; }
                if (IsP(p, '}')) return p;
                p++;
            }
            return p;
        }

        static readonly int NS_Inline = K.Inline;
        static readonly int DelegatePrefix1 = Names.Intern("DECLARE_DELEGATE");

        void ParseDeclarationInner(ref int p, int end, ScopeCtx ctx, int[] templateParams, bool specialization)
        {
            var t = T[p];
            if (t.Kind == TK.Punct)
            {
                if (t.Value == '[' && IsP(p + 1, '[')) { p = After(p); return; }
                if (t.Value == '{')
                {
                    // stray block at scope level (e.g. after an unknown macro): treat as function body without owner
                    int close = Match[p];
                    if (close > p) { sink?.OnBody(p, close, ctx, null, null, null, -1); p = close + 1; } else p++;
                    return;
                }
                if (t.Value == '~' || t.Value == P.Scope) { ParseSimpleDeclaration(ref p, end, ctx, templateParams, specialization); return; }
                p = SkipStatement(p, end);
                return;
            }
            if (t.Kind != TK.Ident) { p = SkipStatement(p, end); return; }
            int v = t.Value;

            if (v == K.Namespace) { ParseNamespace(ref p, end, ctx, false); return; }
            if (v == K.Inline && IsId(p + 1, K.Namespace)) { p++; ParseNamespace(ref p, end, ctx, true); return; }
            if (v == K.Extern && T[p + 1].Kind == TK.String)
            {
                p += 2;
                if (IsP(p, '{'))
                {
                    int close = Match[p] > p ? Match[p] : end;
                    p++;
                    ParseScope(ref p, close, ctx);
                    p = close + 1;
                }
                return;
            }
            if (v == K.Extern && IsId(p + 1, K.Template)) { p = SkipStatement(p, end); return; }
            if (v == K.Using) { ParseUsing(ref p, end, ctx, templateParams); return; }
            if (v == K.Typedef) { ParseTypedef(ref p, end, ctx); return; }
            if (v == K.StaticAssert) { int e = SkipStatement(p, end); if (sink != null && IsP(p + 1, '(')) sink.OnExpr(p + 2, Math.Max(p + 2, Match[p + 1]), ctx, null); p = e; return; }
            if ((v == K.Public || v == K.Private || v == K.Protected) && IsP(p + 1, ':')) { p += 2; return; }
            if (v == K.Friend)
            {
                // friend class X; / friend void F(...); — no declaration in our model, but types are references
                int s = p;
                int e = SkipStatement(p, end);
                if (sink != null) sink.OnSoup(s + 1, e, ctx);
                p = e;
                return;
            }
            if (v == K.Class || v == K.Struct || v == K.Union)
            {
                if (ParseClass(ref p, end, ctx, templateParams, specialization)) return;
            }
            if (v == K.Enum)
            {
                if (ParseEnum(ref p, end, ctx)) return;
            }

            // Unreal / macro handling at declaration level
            if (!keywords.Contains(v) && !builtinTypeWords.Contains(v))
            {
                if (v == K.GENERATED_BODY || v == K.GENERATED_UCLASS_BODY || v == K.GENERATED_USTRUCT_BODY || v == K.GENERATED_IINTERFACE_BODY ||
                    v == K.GENERATED_UINTERFACE_BODY || v == K.GENERATED_BODY_LEGACY)
                {
                    ctx.Generated = true;
                    if (ctx.DeclIndex >= 0) Decls[ctx.DeclIndex].Flags |= DeclFlags.Generated;
                    sink?.OnGeneratedBody(p, ctx);
                    p++;
                    if (IsP(p, '(')) p = After(p);
                    return;
                }
                if (IsP(p + 1, '('))
                {
                    var mk = Macros.Classify(v);
                    if (mk == MacroKind.FuncDecorative && v != K.UCLASS && v != K.USTRUCT && v != K.UENUM && v != K.UINTERFACE && v != K.UFUNCTION && v != K.UPROPERTY && v != K.UDELEGATE)
                    {
                        // e.g. UE_DEPRECATED(...) before a declaration: skip and continue with the declaration
                        p = After(p + 1);
                        return;
                    }
                    if (mk == MacroKind.FuncDecorative) { p = After(p + 1); return; }
                    if (mk == MacroKind.FuncOther || IsUnrealDeclMacro(v))
                    {
                        if (HandleDeclMacro(ref p, end, ctx)) return;
                    }
                }
                else if (!IsP(p + 1, P.Scope) && !IsP(p + 1, '<'))
                {
                    var mk = Macros.Classify(v);
                    if (mk == MacroKind.Decorative || mk == MacroKind.ObjectOther && LooksLikeStatementMacro(p))
                    {
                        p++;
                        return;
                    }
                }
            }
            ParseSimpleDeclaration(ref p, end, ctx, templateParams, specialization);
        }

        bool LooksLikeStatementMacro(int p)
        {
            // An object-like macro used alone (PRAGMA_DISABLE_..., UE_DISABLE_OPTIMIZATION): next token starts something else.
            var n = T[p + 1];
            if (n.Kind == TK.Ident) return T[p + 1].Line != T[p].Line;
            return n.Is(';') || n.Is('}') || n.Kind == TK.Eof || n.Is('#');
        }

        static readonly Dictionary<int, int> declMacroKinds = BuildDeclMacros();
        static Dictionary<int, int> BuildDeclMacros()
        {
            var d = new Dictionary<int, int>();
            void Add(int kind, params string[] names) { foreach (var n in names) d[Names.Intern(n)] = kind; }
            Add(1, "UE_DECLARE_GAMEPLAY_TAG_EXTERN");
            Add(2, "UE_DEFINE_GAMEPLAY_TAG", "UE_DEFINE_GAMEPLAY_TAG_COMMENT", "UE_DEFINE_GAMEPLAY_TAG_STATIC", "UE_DEFINE_GAMEPLAY_TAG_TYPED", "UE_DEFINE_GAMEPLAY_TAG_TYPED_COMMENT");
            Add(3, "DECLARE_LOG_CATEGORY_EXTERN", "DECLARE_LOG_CATEGORY_CLASS");
            Add(4, "DEFINE_LOG_CATEGORY", "DEFINE_LOG_CATEGORY_STATIC", "DEFINE_LOG_CATEGORY_CLASS");
            Add(5, "DEFINE_FUNCTION");
            return d;
        }

        static bool IsUnrealDeclMacro(int v)
        {
            if (declMacroKinds.ContainsKey(v)) return true;
            var s = Names.Get(v);
            return s.StartsWith("DECLARE_", StringComparison.Ordinal);
        }

        static readonly int TMulticastDelegate = Names.Intern("TMulticastDelegate"), TDelegate = Names.Intern("TDelegate"),
            TBaseDynamicDelegate = Names.Intern("TBaseDynamicDelegate"), TBaseDynamicMulticastDelegate = Names.Intern("TBaseDynamicMulticastDelegate"),
            TSparseDynamicDelegate = Names.Intern("TSparseDynamicDelegate"), TTSMulticastDelegate = Names.Intern("TTSMulticastDelegate");

        /// <summary>Declaration-level function-like macro. Returns true when handled (p advanced).</summary>
        bool HandleDeclMacro(ref int p, int end, ScopeCtx ctx)
        {
            int v = T[p].Value;
            int open = p + 1;
            int close = Match[open];
            if (close < 0) return false;
            var args = SplitArgs(open, close);
            string s = Names.Get(v);
            declMacroKinds.TryGetValue(v, out int kind);
            if (kind == 1 || kind == 2 || kind == 3 || kind == 4)
            {
                // tag / log category variable
                if (args.Count > 0 && T[args[0].Item1].Kind == TK.Ident)
                {
                    int nameTok = args[0].Item1;
                    var d = new Decl
                    {
                        Kind = SymKind.Variable, Name = T[nameTok].Value,
                        Type = new TypeExpr { Parts = new[] { new NamePart { Name = kind <= 2 ? K.FNativeGameplayTag : K.FLogCategoryBase } } },
                        Flags = (kind == 2 || kind == 4 ? DeclFlags.Definition : 0) | (ctx.IsClass ? DeclFlags.Static : 0),
                    };
                    AddDecl(d, ctx, nameTok, out _);
                    if (sink != null && args.Count > 1) sink.OnSoup(args[1].Item1, close, ctx);
                }
                p = close + 1;
                return true;
            }
            if (kind == 5)
            {
                // DEFINE_FUNCTION(Class::execName) { body }
                int q = open + 1;
                var owner = ParseType(ref q, close, false);
                if (owner?.Parts != null && owner.Parts.Length > 1)
                {
                    var cls = new TypeExpr { Parts = owner.Parts[..^1] };
                    sink?.OnType(cls, ctx);
                    p = close + 1;
                    if (IsP(p, '{') && Match[p] > p)
                    {
                        sink?.OnBody(p, Match[p], ctx, null, null, cls, -1);
                        p = Match[p] + 1;
                    }
                    return true;
                }
                return false;
            }
            if (s.StartsWith("DECLARE_", StringComparison.Ordinal) && s.Contains("DELEGATE") || s.StartsWith("DECLARE_EVENT", StringComparison.Ordinal))
            {
                int nameArg = 0;
                if (s.Contains("_RetVal")) nameArg = 1;
                if (s.StartsWith("DECLARE_EVENT", StringComparison.Ordinal)) nameArg = 1;
                if (s.Contains("SPARSE")) nameArg = 0;
                if (args.Count > nameArg && T[args[nameArg].Item1].Kind == TK.Ident)
                {
                    int nameTok = args[nameArg].Item1;
                    int baseName = s.Contains("DYNAMIC_MULTICAST") ? TBaseDynamicMulticastDelegate : s.Contains("DYNAMIC") ? TBaseDynamicDelegate
                        : s.Contains("TS_MULTICAST") ? TTSMulticastDelegate : s.Contains("MULTICAST") || s.StartsWith("DECLARE_EVENT", StringComparison.Ordinal) ? TMulticastDelegate : TDelegate;
                    var d = new Decl
                    {
                        Kind = SymKind.Class, Name = T[nameTok].Value, Flags = DeclFlags.Definition | DeclFlags.Delegate,
                        Bases = new[] { new TypeExpr { Parts = new[] { new NamePart { Name = baseName } } } },
                    };
                    AddDecl(d, ctx, nameTok, out _);
                    if (sink != null)
                    {
                        // parameter types (every other argument after the name)
                        int first = nameArg + 1;
                        if (s.StartsWith("DECLARE_EVENT", StringComparison.Ordinal)) { sink.OnSoup(args[0].Item1, args[0].Item2, ctx); }
                        if (s.Contains("_RetVal")) sink.OnSoup(args[0].Item1, args[0].Item2, ctx);
                        if (s.Contains("SPARSE")) { if (args.Count > 1) sink.OnSoup(args[1].Item1, args[1].Item2, ctx); first = 3; }
                        bool dynamic = s.Contains("DYNAMIC");
                        for (int a = first; a < args.Count; a++)
                        {
                            // dynamic delegates: (Type, Name) pairs; others: types only
                            if (dynamic && ((a - first) % 2 == 1)) continue;
                            sink.OnSoup(args[a].Item1, args[a].Item2, ctx);
                        }
                    }
                }
                p = close + 1;
                return true;
            }
            // generic: skip the invocation; resolve arguments loosely; a following '{' is a body (macro-generated function header)
            if (sink != null && close > open + 1) sink.OnSoup(open + 1, close, ctx);
            if (sink != null && ctx.IsClass) sink.OnClassBodyMacro(open - 1, ctx);
            p = close + 1;
            if (IsP(p, '{') && Match[p] > p && !ctx.IsClass)
            {
                sink?.OnBody(p, Match[p], ctx, null, null, null, -1);
                p = Match[p] + 1;
            }
            return true;
        }

        public List<(int, int)> SplitArgs(int open, int close)
        {
            var list = new List<(int, int)>();
            int s = open + 1;
            int p = s;
            while (p < close)
            {
                if (IsP(p, '(') || IsP(p, '[') || IsP(p, '{')) { p = After(p); continue; }
                if (IsP(p, ',')) { list.Add((s, p)); s = p + 1; }
                p++;
            }
            if (close > open + 1) list.Add((s, close));
            return list;
        }

        void ParseNamespace(ref int p, int end, ScopeCtx ctx, bool isInline)
        {
            p++; // 'namespace'
            // attributes / macros
            while (T[p].Kind == TK.Ident && (Macros.Classify(T[p].Value) == MacroKind.Decorative) && !IsP(p + 1, '{') && !IsP(p + 1, P.Scope) && !IsP(p + 1, '=')) p++;
            var nameToks = new List<int>();
            while (T[p].Kind == TK.Ident)
            {
                if (IsId(p, K.Inline)) { p++; continue; }
                nameToks.Add(p); p++;
                if (IsP(p, P.Scope)) { p++; continue; }
                break;
            }
            if (IsP(p, '='))
            {
                // namespace alias
                int q = p + 1;
                var target = ParseType(ref q, end, false);
                if (target != null && nameToks.Count == 1)
                {
                    var d = new Decl { Kind = SymKind.Using, Name = T[nameToks[0]].Value, Type = target, Flags = DeclFlags.Typename };
                    AddDecl(d, ctx, nameToks[0], out var sym);
                    if (sink != null) sink.OnDeclTypes(d, sym, ctx, -1);
                }
                p = SkipStatement(q, end);
                return;
            }
            if (!IsP(p, '{')) { p = SkipStatement(p, end); return; }
            int close = Match[p] > p ? Match[p] : end;
            var inner = ctx;
            var created = new List<ScopeCtx>();
            if (nameToks.Count == 0)
            {
                var d = new Decl { Kind = SymKind.Namespace, Name = 0, Flags = DeclFlags.Anonymous };
                int idx = AddDecl(d, inner, p, out var sym);
                inner = new ScopeCtx { Outer = inner, Kind = SymKind.Namespace, DeclIndex = idx, Sym = sym };
            }
            foreach (var nt in nameToks)
            {
                var d = new Decl { Kind = SymKind.Namespace, Name = T[nt].Value, Flags = isInline ? DeclFlags.InlineNs : 0 };
                int idx = AddDecl(d, inner, nt, out var sym);
                inner = new ScopeCtx { Outer = inner, Kind = SymKind.Namespace, DeclIndex = idx, Sym = sym };
            }
            p++;
            ParseScope(ref p, close, inner);
            p = close + 1;
        }

        void ParseUsing(ref int p, int end, ScopeCtx ctx, int[] templateParams)
        {
            int s = p;
            p++;
            if (IsId(p, K.Namespace))
            {
                p++;
                var ns = ParseType(ref p, end, false);
                if (ns != null) sink?.OnUsingDirective(ns, ctx);
                p = SkipStatement(p, end);
                return;
            }
            if (IsId(p, K.Enum)) { p = SkipStatement(p, end); return; }
            if (T[p].Kind == TK.Ident && IsP(p + 1, '=') || T[p].Kind == TK.Ident && IsP(p + 1, '[') && IsP(p + 2, '['))
            {
                int nameTok = p;
                p += 1;
                while (IsP(p, '[')) p = After(p);
                p++; // '='
                var target = ParseType(ref p, end, true);
                if (target != null && IsP(p, '('))
                {
                    var ps = ParseParams(ref p, end, out _, out _, out _);
                    target = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = target, FuncParams = Array.ConvertAll(ps, x => x.Type) };
                }
                var d = new Decl { Kind = SymKind.Typedef, Name = T[nameTok].Value, Type = target ?? TypeExpr.UnknownType, TemplateParams = templateParams };
                if (templateParams != null && templateParams.Length > 0) d.Flags |= DeclFlags.Template;
                AddDecl(d, ctx, nameTok, out var sym);
                if (sink != null) sink.OnDeclTypes(d, sym, ctx, -1);
                p = SkipStatement(p, end);
                return;
            }
            // using-declaration: using A::B; / using typename A::B;
            if (IsId(p, K.Typename)) p++;
            int q = p;
            var path = ParseType(ref q, end, false);
            int nameTok2 = -1;
            if (path?.Parts != null && path.Parts.Length >= 2)
            {
                nameTok2 = path.Parts[path.Parts.Length - 1].Tok;
                var d = new Decl { Kind = SymKind.Using, Name = path.Parts[path.Parts.Length - 1].Name, Type = path };
                AddDecl(d, ctx, nameTok2, out var sym);
                if (sink != null) sink.OnDeclTypes(d, sym, ctx, -1);
            }
            else if (path?.Parts != null && path.Parts.Length == 1 && path.Global)
            {
                var d = new Decl { Kind = SymKind.Using, Name = path.Parts[0].Name, Type = path };
                AddDecl(d, ctx, path.Parts[0].Tok, out var sym);
                if (sink != null) sink.OnDeclTypes(d, sym, ctx, -1);
            }
            else if (IsP(q, P.Scope) && IsId(q + 1, K.Operator) && sink != null) sink.OnSoup(p, q, ctx);
            p = SkipStatement(q, end);
        }

        void ParseTypedef(ref int p, int end, ScopeCtx ctx)
        {
            p++;
            TypeExpr baseType;
            if ((IsId(p, K.Struct) || IsId(p, K.Class) || IsId(p, K.Union) || IsId(p, K.Enum)) && HasBodyAhead(p, end))
            {
                // typedef struct X { ... } Y;
                int before = Decls.Count;
                int q = p;
                bool isEnum = IsId(p, K.Enum);
                bool ok = isEnum ? ParseEnum(ref q, end, ctx, true) : ParseClass(ref q, end, ctx, null, false, true);
                if (!ok) { p = SkipStatement(p, end); return; }
                p = q;
                var created = Decls.Count > before ? Decls[before] : null;
                baseType = created != null && created.Name != 0 ? new TypeExpr { Parts = new[] { new NamePart { Name = created.Name } } } : TypeExpr.UnknownType;
            }
            else
            {
                baseType = ParseType(ref p, end, false);
                if (baseType == null) { p = SkipStatement(p, end); return; }
                sink?.OnType(baseType, ctx);
            }
            // declarators
            while (p < end && !IsP(p, ';'))
            {
                var t = new TypeExpr { Parts = baseType.Parts, Builtin = baseType.Builtin, Const = baseType.Const, Global = baseType.Global, Ptr = baseType.Ptr, FuncParams = baseType.FuncParams, FuncReturn = baseType.FuncReturn };
                ParsePtrOps(ref p, end, t);
                int nameTok = -1;
                if (IsP(p, '('))
                {
                    // typedef R (*Name)(Args); / typedef R (Class::*Name)(Args);
                    int inner = p + 1;
                    int close = Match[p] > p ? Match[p] : end;
                    for (int q = inner; q < close; q++) if (T[q].Kind == TK.Ident && !keywords.Contains(T[q].Value)) nameTok = q;
                    p = close + 1;
                    if (IsP(p, '(')) { var ps = ParseParams(ref p, end, out _, out _, out _); t = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = t, FuncParams = Array.ConvertAll(ps, x => x.Type) }; }
                }
                else if (T[p].Kind == TK.Ident)
                {
                    nameTok = p; p++;
                    if (IsP(p, '(')) { var ps = ParseParams(ref p, end, out _, out _, out _); t = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = t, FuncParams = Array.ConvertAll(ps, x => x.Type) }; }
                }
                while (IsP(p, '[')) p = After(p);
                if (nameTok >= 0)
                {
                    var d = new Decl { Kind = SymKind.Typedef, Name = T[nameTok].Value, Type = t };
                    AddDecl(d, ctx, nameTok, out var sym);
                    if (sink != null && t.Builtin == Builtin.FuncSig) sink.OnDeclTypes(d, sym, ctx, -1);
                }
                // skip macros / attributes
                while (p < end && !IsP(p, ',') && !IsP(p, ';')) { if (IsP(p, '(') || IsP(p, '[') || IsP(p, '{')) p = After(p); else p++; }
                if (IsP(p, ',')) p++;
            }
            p++;
        }

        bool HasBodyAhead(int p, int end)
        {
            for (int q = p + 1; q < end && q < p + 64; q++)
            {
                if (IsP(q, '{')) return true;
                if (IsP(q, ';') || IsP(q, '(') || IsP(q, '*') || IsP(q, '&')) return false;
            }
            return false;
        }

        /// <summary>class/struct/union head. Returns false when it is an elaborated type in a declaration (caller parses).</summary>
        bool ParseClass(ref int p, int end, ScopeCtx ctx, int[] templateParams, bool specialization, bool fromTypedef = false)
        {
            int start = p;
            int kw = T[p].Value;
            var kind = kw == K.Class ? SymKind.Class : kw == K.Struct ? SymKind.Struct : SymKind.Union;
            int q = p + 1;
            // attributes, API macros, alignas, __declspec, UE_DEPRECATED(...)
            NamePart[] nameParts = null;
            int nameTok = -1;
            while (q < end)
            {
                var t = T[q];
                if (t.Is('[') && IsP(q + 1, '[')) { q = After(q); continue; }
                if (t.Kind != TK.Ident) break;
                if (t.Value == K.Alignas || t.Value == K.Declspec) { q++; if (IsP(q, '(')) q = After(q); continue; }
                if (t.Value == K.Final || keywords.Contains(t.Value)) break;
                var mk = Macros.Classify(t.Value);
                if (IsP(q + 1, '(') && (mk == MacroKind.FuncDecorative || mk == MacroKind.FuncOther)) { q = After(q + 1); continue; }
                // candidate name (the last identifier before ':' '{' ';' final)
                int s = q;
                var ty = ParseType(ref q, end, false);
                if (ty?.Parts == null) break;
                // decorative macro used as name? keep going if more identifiers follow
                if (T[q].Kind == TK.Ident && !IsId(q, K.Final) && ty.Parts.Length == 1 && ty.Parts[0].Args == null && (mk != MacroKind.None || IsAllCaps(t.Value)))
                {
                    continue;
                }
                nameParts = ty.Parts;
                nameTok = ty.Parts[ty.Parts.Length - 1].Tok;
                break;
            }
            while (IsId(q, K.Final) || T[q].Kind == TK.Ident && Macros.Classify(T[q].Value) == MacroKind.Decorative) q++;
            bool hasBody = IsP(q, '{') || IsP(q, ':') && !IsP(q + 1, ':');
            if (!hasBody)
            {
                if (IsP(q, ';') && nameParts != null && !fromTypedef)
                {
                    // forward declaration
                    if (nameParts.Length == 1 && nameParts[0].Args == null && !specialization)
                    {
                        var d = new Decl { Kind = kind, Name = nameParts[0].Name, Flags = DeclFlags.Forward, TemplateParams = templateParams };
                        if (templateParams != null && templateParams.Length > 0) d.Flags |= DeclFlags.Template;
                        AddDecl(d, ctx, nameTok, out _);
                    }
                    else if (sink != null) sink.OnType(new TypeExpr { Parts = nameParts }, ctx);
                    p = q + 1;
                    return true;
                }
                p = start;
                return false; // elaborated type specifier in a declaration
            }
            var decl = new Decl { Kind = kind, Name = nameParts != null ? nameParts[nameParts.Length - 1].Name : 0, Flags = DeclFlags.Definition, TemplateParams = templateParams };
            if (templateParams != null && templateParams.Length > 0) decl.Flags |= DeclFlags.Template;
            if (nameParts == null) decl.Flags |= DeclFlags.Anonymous;
            if (nameParts != null && nameParts.Length > 1) decl.Qualifier = nameParts[..^1];
            bool isSpec = specialization || nameParts != null && nameParts[nameParts.Length - 1].Args != null;
            if (isSpec) decl.Flags |= DeclFlags.Specialization;
            var bases = new List<TypeExpr>();
            if (IsP(q, ':'))
            {
                q++;
                while (q < end && !IsP(q, '{'))
                {
                    while (T[q].Kind == TK.Ident && (T[q].Value == K.Public || T[q].Value == K.Private || T[q].Value == K.Protected || T[q].Value == K.Virtual)) q++;
                    int s = q;
                    var b = ParseType(ref q, end, false);
                    if (b != null) bases.Add(b);
                    if (IsP(q, P.Ellipsis)) q++;
                    if (q == s) q++;
                    while (q < end && !IsP(q, ',') && !IsP(q, '{')) { if (IsP(q, '(')) q = After(q); else q++; }
                    if (IsP(q, ',')) q++;
                }
            }
            decl.Bases = bases.Count > 0 ? bases.ToArray() : null;
            if (!IsP(q, '{')) { p = SkipStatement(q, end); return true; }
            int idx = AddDecl(decl, ctx, nameTok >= 0 ? nameTok : start, out var sym);
            if (sink != null)
            {
                sink.OnDeclTypes(decl, sym, ctx, -1);
                if (isSpec && nameParts != null) sink.OnType(new TypeExpr { Parts = nameParts }, ctx);
            }
            int close = Match[q] > q ? Match[q] : end;
            var inner = new ScopeCtx { Outer = ctx, Kind = kind, DeclIndex = idx, ClassName = decl.Name, Sym = sym };
            q++;
            ParseScope(ref q, close, inner);
            p = close + 1;
            if (fromTypedef) return true;
            // trailing declarators: } Var, *Ptr;
            if (!IsP(p, ';'))
            {
                var vt = new TypeExpr { Parts = decl.Name != 0 ? new[] { new NamePart { Name = decl.Name } } : null, Builtin = decl.Name != 0 ? Builtin.None : Builtin.Unknown };
                ParseDeclarators(ref p, end, ctx, vt, DeclFlags.None, templateParams, false);
            }
            else p++;
            return true;
        }

        static bool IsAllCaps(int name)
        {
            var s = Names.Get(name);
            bool letter = false;
            foreach (var c in s) { if (c >= 'a' && c <= 'z') return false; if (c >= 'A' && c <= 'Z') letter = true; }
            return letter && s.Length > 2;
        }

        bool ParseEnum(ref int p, int end, ScopeCtx ctx, bool fromTypedef = false)
        {
            int start = p;
            int q = p + 1;
            bool scoped = false;
            if (IsId(q, K.Class) || IsId(q, K.Struct)) { scoped = true; q++; }
            while (T[q].Kind == TK.Ident && (Macros.Classify(T[q].Value) == MacroKind.Decorative) && (T[q + 1].Kind == TK.Ident)) q++;
            if (IsP(q, '[') && IsP(q + 1, '[')) q = After(q);
            int nameTok = -1;
            NamePart[] qual = null;
            if (T[q].Kind == TK.Ident && !keywords.Contains(T[q].Value))
            {
                int s = q;
                var ty = ParseType(ref q, end, false);
                if (ty?.Parts != null)
                {
                    nameTok = ty.Parts[ty.Parts.Length - 1].Tok;
                    if (ty.Parts.Length > 1) qual = ty.Parts[..^1];
                }
            }
            TypeExpr underlying = null;
            if (IsP(q, ':') )
            {
                q++;
                underlying = ParseType(ref q, end, false);
            }
            if (IsP(q, ';') && nameTok >= 0)
            {
                var fd = new Decl { Kind = SymKind.Enum, Name = T[nameTok].Value, Flags = DeclFlags.Forward | (scoped ? DeclFlags.Scoped : 0), Type = underlying, Qualifier = qual };
                AddDecl(fd, ctx, nameTok, out var fs);
                if (sink != null && underlying != null) sink.OnType(underlying, ctx);
                p = q + 1;
                return true;
            }
            if (!IsP(q, '{')) { p = start; return false; }
            var d = new Decl { Kind = SymKind.Enum, Name = nameTok >= 0 ? T[nameTok].Value : 0, Flags = DeclFlags.Definition | (scoped ? DeclFlags.Scoped : 0) | (nameTok < 0 ? DeclFlags.Anonymous : 0), Type = underlying, Qualifier = qual };
            int idx = AddDecl(d, ctx, nameTok >= 0 ? nameTok : q, out var sym);
            if (sink != null) { if (underlying != null) sink.OnType(underlying, ctx); if (qual != null) sink.OnDeclTypes(d, sym, ctx, -1); }
            var inner = new ScopeCtx { Outer = ctx, Kind = SymKind.Enum, DeclIndex = idx, Sym = sym };
            int close = Match[q] > q ? Match[q] : end;
            q++;
            while (q < close)
            {
                if (IsP(q, ',')) { q++; continue; }
                if (T[q].Kind == TK.Ident)
                {
                    var mk = Macros.Classify(T[q].Value);
                    if ((mk == MacroKind.FuncDecorative || mk == MacroKind.FuncOther) && IsP(q + 1, '(') && T[q].Value != K.UMETA) { q = After(q + 1); continue; }
                    int et = q;
                    var ed = new Decl { Kind = SymKind.Enumerator, Name = T[et].Value, Flags = DeclFlags.Definition };
                    AddDecl(ed, inner, et, out var esym);
                    q++;
                    while (q < close && !IsP(q, ',') && !IsP(q, '='))
                    {
                        if (IsP(q, '(') || IsP(q, '[')) q = After(q); else q++;
                    }
                    if (IsP(q, '='))
                    {
                        int s = q + 1;
                        q = SkipExpr(s, close, ',');
                        if (sink != null) sink.OnExpr(s, q, inner, null);
                    }
                    continue;
                }
                q++;
            }
            p = close + 1;
            if (fromTypedef) return true;
            if (!IsP(p, ';'))
            {
                var vt = new TypeExpr { Parts = d.Name != 0 ? new[] { new NamePart { Name = d.Name } } : null, Builtin = d.Name != 0 ? Builtin.None : Builtin.Int };
                ParseDeclarators(ref p, end, ctx, vt, DeclFlags.None, null, false);
            }
            else p++;
            return true;
        }

        // ------------------------------------------------------------------ simple declarations

        void ParseSimpleDeclaration(ref int p, int end, ScopeCtx ctx, int[] templateParams, bool specialization)
        {
            int start = p;
            DeclFlags flags = DeclFlags.None;
            // specifiers
            while (p < end)
            {
                var t = T[p];
                if (t.Is('[') && IsP(p + 1, '[')) { p = After(p); continue; }
                if (t.Kind != TK.Ident) break;
                int v = t.Value;
                if (v == K.Virtual) { flags |= DeclFlags.Virtual; p++; continue; }
                if (v == K.Static) { flags |= DeclFlags.Static; p++; continue; }
                if (v == K.Inline || v == K.Constexpr || v == K.Consteval || v == K.Constinit || v == K.Extern || v == K.Mutable || v == K.ThreadLocal || v == K.Forceinline || v == K.Register) { p++; continue; }
                if (v == K.Explicit) { p++; if (IsP(p, '(')) p = After(p); continue; }
                if (v == K.Friend) { flags |= DeclFlags.Friend; p++; continue; }
                if (v == K.Declspec || v == K.Alignas || v == K.Attribute) { p++; if (IsP(p, '(')) p = After(p); continue; }
                if (keywords.Contains(v) || builtinTypeWords.Contains(v)) break;
                var mk = Macros.Classify(v);
                if (mk == MacroKind.Decorative && !IsP(p + 1, P.Scope) && !IsP(p + 1, '<')) { p++; continue; }
                if ((mk == MacroKind.FuncDecorative) && IsP(p + 1, '(')) { p = After(p + 1); continue; }
                break;
            }
            if (p >= end) return;

            // destructor in class: ~Name(
            if (IsP(p, '~') && T[p + 1].Kind == TK.Ident && IsP(p + 2, '('))
            {
                int nameTok = p + 1;
                p += 2;
                ParseFunctionRest(ref p, end, ctx, null, nameTok, null, flags | DeclFlags.Dtor, templateParams, T[nameTok].Value);
                return;
            }
            // constructor in class: Name(
            if (ctx.IsClass && T[p].Kind == TK.Ident && T[p].Value == ctx.ClassName && IsP(p + 1, '('))
            {
                int nameTok = p;
                p++;
                ParseFunctionRest(ref p, end, ctx, null, nameTok, null, flags | DeclFlags.Ctor, templateParams, T[nameTok].Value);
                return;
            }
            // conversion operator: operator Type()
            if (IsId(p, K.Operator))
            {
                int nameTok = p;
                int q = p;
                int opName = ParseOperatorName(ref q, end);
                if (opName != 0 && IsP(q, '('))
                {
                    p = q;
                    ParseFunctionRest(ref p, end, ctx, null, nameTok, null, flags | DeclFlags.Operator, templateParams, opName);
                    return;
                }
            }
            int typeStart = p;
            var type = ParseType(ref p, end, false);
            if (type == null)
            {
                int e = SkipStatement(p, end);
                if (sink != null) sink.OnSoup(start, e, ctx);
                p = e;
                return;
            }
            // out-of-line constructor: A::A( or A<T>::A(
            if (type.Parts != null && type.Parts.Length >= 2 && IsP(p, '(') && type.Parts[^1].Name == type.Parts[^2].Name)
            {
                int nameTok = type.Parts[^1].Tok;
                ParseFunctionRest(ref p, end, ctx, type.Parts[..^1], nameTok, null, flags | DeclFlags.Ctor, templateParams, type.Parts[^1].Name);
                return;
            }
            // out-of-line destructor: A::~A(
            if (type.Parts != null && IsP(p, P.Scope) && IsP(p + 1, '~') && T[p + 2].Kind == TK.Ident && IsP(p + 3, '('))
            {
                int nameTok = p + 2;
                p += 3;
                ParseFunctionRest(ref p, end, ctx, type.Parts, nameTok, null, flags | DeclFlags.Dtor, templateParams, T[nameTok].Value);
                return;
            }
            // out-of-line conversion operator / operator: A::operator...
            if (type.Parts != null && IsP(p, P.Scope) && IsId(p + 1, K.Operator))
            {
                int q = p + 1;
                int opName = ParseOperatorName(ref q, end);
                if (opName != 0 && IsP(q, '('))
                {
                    int nameTok = p + 1;
                    p = q;
                    ParseFunctionRest(ref p, end, ctx, type.Parts, nameTok, null, flags | DeclFlags.Operator, templateParams, opName);
                    return;
                }
            }
            // a lone macro-like call at scope level: NAME(args); (unknown macro) — no declarator follows
            if (type.Parts != null && type.Parts.Length == 1 && type.Parts[0].Args == null && IsP(p, '(') && !ctx.IsClass)
            {
                int close = Match[p];
                if (close > p && (IsP(close + 1, ';') || T[close + 1].Line > T[close].Line && !IsP(close + 1, '{') && !IsP(close + 1, ':') && !IsId(close + 1, K.Const)))
                {
                    if (sink != null) sink.OnSoup(start, close + 1, ctx);
                    p = close + 1;
                    if (IsP(p, ';')) p++;
                    return;
                }
            }
            sink?.OnType(type, ctx);
            ParseDeclarators(ref p, end, ctx, type, flags, templateParams, specialization);
        }

        int ParseOperatorName(ref int q, int end)
        {
            // q at 'operator'
            int s = q;
            q++;
            var sb = new StringBuilder("operator");
            var x = T[q];
            if (x.Is('(') && IsP(q + 1, ')')) { sb.Append("()"); q += 2; }
            else if (x.Is('[') && IsP(q + 1, ']')) { sb.Append("[]"); q += 2; }
            else if (x.IsId(K.New) || x.IsId(K.Delete))
            {
                sb.Append(' ').Append(Names.Get(x.Value)); q++;
                if (IsP(q, '[') && IsP(q + 1, ']')) { sb.Append("[]"); q += 2; }
            }
            else if (x.Kind == TK.Punct)
            {
                // possibly two adjacent '>' tokens (>>, >=, >>=)
                sb.Append(PunctText(x.Value));
                q++;
                while (T[q].Kind == TK.Punct && T[q].Pos == T[q - 1].Pos + T[q - 1].Len && (T[q - 1].Value == '>' && (T[q].Value == '>' || T[q].Value == '=' || T[q].Value == P.Eq)))
                { sb.Append(PunctText(T[q].Value)); q++; }
            }
            else if (x.Kind == TK.String && T[q + 1].Kind == TK.Ident) { sb.Append("\"\"").Append(Names.Get(T[q + 1].Value)); q += 2; }
            else
            {
                // conversion operator
                var ty = ParseType(ref q, end, true);
                if (ty == null) { q = s; return 0; }
                sb.Append(' ').Append(ty.ToString());
                conversionType = ty;
            }
            return Names.Intern(sb.ToString());
        }

        TypeExpr conversionType;

        public static string PunctText(int v)
        {
            switch (v)
            {
                case P.Scope: return "::"; case P.Arrow: return "->"; case P.Inc: return "++"; case P.Dec: return "--"; case P.Shl: return "<<";
                case P.Le: return "<="; case P.Eq: return "=="; case P.Ne: return "!="; case P.AndAnd: return "&&"; case P.OrOr: return "||";
                case P.PlusEq: return "+="; case P.MinusEq: return "-="; case P.MulEq: return "*="; case P.DivEq: return "/="; case P.ModEq: return "%=";
                case P.AndEq: return "&="; case P.OrEq: return "|="; case P.XorEq: return "^="; case P.ShlEq: return "<<="; case P.Ellipsis: return "...";
                case P.ArrowStar: return "->*"; case P.DotStar: return ".*"; case P.Spaceship: return "<=>"; case P.HashHash: return "##";
                default: return ((char)v).ToString();
            }
        }

        bool LooksLikeParamList(int open, ScopeCtx ctx)
        {
            int close = Match[open];
            if (close < 0) return true;
            if (close == open + 1) return true; // ()
            if (ctx.IsClass) return true;
            var t = T[open + 1];
            if (t.Kind == TK.String || t.Kind == TK.Number || t.Kind == TK.Char) return false;
            if (t.Kind == TK.Punct) return t.Is(P.Scope) || t.Is(P.Ellipsis) || t.Is('[');
            int v = t.Value;
            if (v == K.Const || v == K.Volatile || v == K.Typename || v == K.Struct || v == K.Class || v == K.Enum || v == K.UPARAM || builtinTypeWords.Contains(v)) return true;
            if (v == K.This || v == K.True || v == K.False || v == K.Nullptr || v == K.New || v == K.Sizeof || v == K.StaticCast || v == K.TEXT) return false;
            // Ident followed by: Ident, *, &, ::, <, ',', ')' → parameter-ish ; '(' '.' '->' operators → expression
            var n = T[open + 2];
            if (n.Kind == TK.Ident) return true;
            if (n.Kind == TK.Punct)
            {
                int c = n.Value;
                if (c == '*' || c == '&' || c == P.AndAnd || c == P.Scope || c == '<' || c == ',' || c == ')' || c == P.Ellipsis || c == '[' || c == '=') return true;
                return false;
            }
            return false;
        }

        void ParseDeclarators(ref int p, int end, ScopeCtx ctx, TypeExpr baseType, DeclFlags flags, int[] templateParams, bool specialization)
        {
            int guard = 0;
            while (p < end && guard++ < 1000)
            {
                var type = new TypeExpr { Parts = baseType.Parts, Builtin = baseType.Builtin, Const = baseType.Const, Global = baseType.Global, FuncParams = baseType.FuncParams, FuncReturn = baseType.FuncReturn };
                ParsePtrOps(ref p, end, type);
                // function pointer declarator: (*Name)(args)
                if (IsP(p, '(') && (IsP(p + 1, '*') || IsP(p + 1, '&') || T[p + 1].Kind == TK.Ident && IsP(p + 2, P.Scope) && IsP(p + 3, '*')))
                {
                    int close = Match[p] > p ? Match[p] : end;
                    int nameTok = -1;
                    for (int q = p + 1; q < close; q++) if (T[q].Kind == TK.Ident && !keywords.Contains(T[q].Value)) nameTok = q;
                    p = close + 1;
                    TypeExpr ft = new TypeExpr { Builtin = Builtin.FuncSig, FuncReturn = type };
                    if (IsP(p, '(')) { var ps = ParseParams(ref p, end, out _, out _, out _); ft.FuncParams = Array.ConvertAll(ps, x => x.Type); }
                    if (nameTok >= 0)
                    {
                        var d = new Decl { Kind = SymKind.Variable, Name = T[nameTok].Value, Type = ft, Flags = flags | (ctx.IsClass ? DeclFlags.Field : 0) };
                        AddDecl(d, ctx, nameTok, out var vs);
                    }
                    if (!FinishVariable(ref p, end, ctx, null)) return;
                    continue;
                }
                // declarator name (possibly qualified)
                NamePart[] qual = null;
                int nameTok2 = -1;
                int name = 0;
                bool isGlobalQual = false;
                if (IsP(p, P.Scope)) { isGlobalQual = true; p++; }
                if (T[p].Kind == TK.Ident && !keywords.Contains(T[p].Value) || IsId(p, K.Operator))
                {
                    var parts = new List<NamePart>();
                    while (true)
                    {
                        if (IsId(p, K.Operator))
                        {
                            int q = p;
                            int op = ParseOperatorName(ref q, end);
                            if (op == 0) break;
                            nameTok2 = p; name = op; flags |= DeclFlags.Operator; p = q;
                            break;
                        }
                        if (T[p].Kind != TK.Ident) break;
                        var part = new NamePart { Name = T[p].Value, Tok = p };
                        int save = p;
                        p++;
                        if (IsP(p, '<') && IsP(TryTemplateClose(p, end) + 1, P.Scope))
                        {
                            part.Args = ParseTemplateArgs(ref p, end);
                        }
                        else if (IsP(p, '<') && specialization)
                        {
                            // explicit specialization of a function template: Name<Args>(
                            int c = TryTemplateClose(p, end);
                            if (c > 0 && IsP(c + 1, '(')) { part.Args = ParseTemplateArgs(ref p, end); }
                        }
                        if (IsP(p, P.Scope))
                        {
                            parts.Add(part);
                            p++;
                            if (IsP(p, '~') && T[p + 1].Kind == TK.Ident) { nameTok2 = p + 1; name = T[p + 1].Value; flags |= DeclFlags.Dtor; p += 2; break; }
                            continue;
                        }
                        nameTok2 = save; name = part.Name;
                        break;
                    }
                    if (parts.Count > 0) qual = parts.ToArray();
                }
                if (nameTok2 < 0)
                {
                    // no declarator (e.g. "struct X;" handled elsewhere, or garbage)
                    int e = SkipStatement(p, end);
                    if (sink != null) sink.OnSoup(p, e, ctx);
                    p = e;
                    return;
                }
                // decorative macros after the name (e.g. UE_DEPRECATED, attribute macros)
                while (T[p].Kind == TK.Ident && !IsP(p + 1, P.Scope))
                {
                    var mk = Macros.Classify(T[p].Value);
                    if (mk == MacroKind.Decorative) { p++; continue; }
                    if (mk == MacroKind.FuncDecorative && IsP(p + 1, '(')) { p = After(p + 1); continue; }
                    break;
                }
                if (IsP(p, '(') && LooksLikeParamList(p, ctx))
                {
                    ParseFunctionRest(ref p, end, ctx, qual, nameTok2, type, flags, templateParams, name);
                    return;
                }
                // variable / field
                while (IsP(p, '[')) p = After(p);
                var vd = new Decl { Kind = SymKind.Variable, Name = name, Type = type, Qualifier = qual, Flags = flags | (ctx.IsClass ? DeclFlags.Field : 0) };
                if (qual != null || !IsId(p - 1, K.Extern)) { }
                if (IsP(p, '=') || IsP(p, '{') || IsP(p, '(') || qual != null || !ctx.IsClass && (flags & DeclFlags.Static) != 0 || !ctx.IsClass) vd.Flags |= DeclFlags.Definition;
                if (templateParams != null && templateParams.Length > 0) { vd.TemplateParams = templateParams; vd.Flags |= DeclFlags.Template; }
                AddDecl(vd, ctx, nameTok2, out var vsym);
                if (sink != null && qual != null) sink.OnDeclTypes(vd, vsym, ctx, -1);
                if (!FinishVariable(ref p, end, ctx, vsym)) return;
            }
        }

        /// <summary>After a variable declarator: initializer, bitfield, then ',' (returns true to continue) or ';'.</summary>
        bool FinishVariable(ref int p, int end, ScopeCtx ctx, Symbol vsym)
        {
            if (IsP(p, ':') && !IsP(p + 1, ':'))
            {
                // bitfield
                int s = p + 1;
                p = SkipExpr(s, end, ',', ';', '=');
            }
            if (IsP(p, '='))
            {
                int s = p + 1;
                p = SkipExpr(s, end, ',', ';');
                sink?.OnExpr(s, p, ctx, vsym);
            }
            else if (IsP(p, '{') || IsP(p, '('))
            {
                int s = p;
                p = After(p);
                sink?.OnExpr(s, p, ctx, vsym);
            }
            // trailing decorative macros
            while (T[p].Kind == TK.Ident)
            {
                var mk = Macros.Classify(T[p].Value);
                if (mk == MacroKind.Decorative) { p++; continue; }
                if ((mk == MacroKind.FuncDecorative || mk == MacroKind.FuncOther) && IsP(p + 1, '(')) { p = After(p + 1); continue; }
                break;
            }
            if (IsP(p, ',')) { p++; return true; }
            if (IsP(p, ';')) { p++; return false; }
            // garbage: skip statement
            int e = SkipStatement(p, end);
            if (sink != null) sink.OnSoup(p, e, ctx);
            p = e;
            return false;
        }

        void ParseFunctionRest(ref int p, int end, ScopeCtx ctx, NamePart[] qual, int nameTok, TypeExpr ret, DeclFlags flags, int[] templateParams, int name)
        {
            // p at '('
            var conv = conversionType; conversionType = null;
            if ((flags & DeclFlags.Dtor) != 0) name = Names.Intern("~" + Names.Get(name));
            if ((flags & DeclFlags.Operator) != 0 && ret == null && conv != null) ret = conv;
            int paramOpen = p;
            var defaults = sink != null ? (defaultArgRanges = new List<(int, int)>()) : null;
            var ps = ParseParams(ref p, end, out short min, out short max, out bool variadic);
            defaultArgRanges = null;
            var d = new Decl { Kind = SymKind.Function, Name = name, Type = ret, Params = ps, MinArgs = min, MaxArgs = max, Qualifier = qual, Flags = flags, TemplateParams = templateParams };
            if (variadic) d.Flags |= DeclFlags.Variadic;
            if (templateParams != null && templateParams.Length > 0) d.Flags |= DeclFlags.Template;
            // trailing: const, noexcept, override, final, = 0, -> ret, requires, macros
            int ctorInit = -1;
            while (p < end)
            {
                var t = T[p];
                if (t.Kind == TK.Ident)
                {
                    int v = t.Value;
                    if (v == K.Const) { d.Flags |= DeclFlags.Const; p++; continue; }
                    if (v == K.Volatile || v == K.Final) { p++; continue; }
                    if (v == K.Override) { d.Flags |= DeclFlags.Override; p++; continue; }
                    if (v == K.Noexcept || v == K.Throw) { p++; if (IsP(p, '(')) p = After(p); continue; }
                    if (v == K.Requires) { p = SkipRequires(p + 1, end); continue; }
                    if (v == K.Try) { p++; continue; }
                    var mk = Macros.Classify(v);
                    if (mk == MacroKind.Decorative) { p++; continue; }
                    if ((mk == MacroKind.FuncDecorative || mk == MacroKind.FuncOther) && IsP(p + 1, '('))
                    {
                        // PURE_VIRTUAL(...) and friends act as a body
                        if (Names.Get(v).StartsWith("PURE_VIRTUAL", StringComparison.Ordinal)) d.Flags |= DeclFlags.Definition | DeclFlags.Pure;
                        p = After(p + 1); continue;
                    }
                    if (mk == MacroKind.ObjectOther) { p++; continue; }
                    break;
                }
                if (t.Is('&') || t.Is(P.AndAnd)) { p++; continue; }
                if (t.Is('[') && IsP(p + 1, '[')) { p = After(p); continue; }
                if (t.Is(P.Arrow))
                {
                    p++;
                    var tr = ParseType(ref p, end, true);
                    if (tr != null) d.Type = tr;
                    continue;
                }
                break;
            }
            if (IsP(p, '='))
            {
                // = 0 / = default / = delete
                if (IsId(p + 1, K.Default) || IsId(p + 1, K.Delete)) d.Flags |= DeclFlags.Definition;
                if (T[p + 1].Kind == TK.Number) d.Flags |= DeclFlags.Pure;
                p = SkipExpr(p + 1, end, ';', '{');
            }
            if (IsP(p, ':') && !IsP(p + 1, ':'))
            {
                ctorInit = p + 1;
                // skip to the body '{' at depth 0: initializers are Name(args) or Name{args}
                int q = p + 1;
                while (q < end && !IsP(q, ';'))
                {
                    if (IsP(q, '(') || IsP(q, '[')) { q = After(q); continue; }
                    if (IsP(q, '{'))
                    {
                        // Name{...} initializer vs body: body follows ')' or '}' of the previous initializer, not an identifier/'>'
                        var prev = T[q - 1];
                        if (prev.Kind == TK.Ident || prev.Is('>')) { q = After(q); continue; }
                        break;
                    }
                    if (IsP(q, '<')) { int c = TryTemplateClose(q, end); if (c > 0) { q = c + 1; continue; } }
                    q++;
                }
                p = q;
            }
            bool hasBody = IsP(p, '{') && Match[p] > p;
            if (hasBody) d.Flags |= DeclFlags.Definition;
            int idx = AddDecl(d, ctx, nameTok, out var sym);
            if (sink != null)
            {
                sink.OnDeclTypes(d, sym, ctx, -1);
                if (defaults != null) foreach (var r in defaults) sink.OnExpr(r.Item1, r.Item2, ctx, sym);
            }
            if (hasBody)
            {
                int close = Match[p];
                sink?.OnBody(p, close, ctx, sym, d, null, ctorInit);
                p = close + 1;
                // function-try-block handlers
                while (IsId(p, K.Catch) && IsP(p + 1, '(')) { p = After(p + 1); if (IsP(p, '{')) p = After(p); }
                return;
            }
            if (IsP(p, ';')) { p++; return; }
            if (IsP(p, ',')) { p++; return; }
            int e = SkipStatement(p, end);
            if (sink != null) sink.OnSoup(p, e, ctx);
            p = e;
        }
    }
}
