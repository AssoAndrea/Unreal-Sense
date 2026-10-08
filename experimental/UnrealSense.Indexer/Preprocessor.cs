using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealSense.Indexer
{
    public sealed class MacroDef
    {
        public int Name;
        public bool FunctionLike;
        public int[] Params = Array.Empty<int>();
        public bool Variadic;
        public Token[] Body = Array.Empty<Token>();   // Number tokens: Value = parsed integer (clamped)
        public bool Ambiguous;                        // defined differently in different files
        public string BodyKey;                        // for comparing definitions
        public int Kind = -1;                         // cached MacroKind (see DeclParser)

        public static readonly MacroDef Undefined = new MacroDef { Name = -1 };
    }

    /// <summary>Global macro environment: predefined + compile database defines (win) + definitions found in all files.</summary>
    public sealed class MacroEnv
    {
        public readonly Dictionary<int, MacroDef> Base = new Dictionary<int, MacroDef>();
        public Dictionary<int, MacroDef> Global = new Dictionary<int, MacroDef>();

        public MacroDef Find(int name)
        {
            if (Base.TryGetValue(name, out var m)) return m;
            if (Global.TryGetValue(name, out m)) return m;
            return null;
        }

        public void DefineBase(string name, string value)
        {
            var def = Preprocessor.ParseDefineText(name + " " + (value ?? "1"));
            if (def != null) Base[def.Name] = def;
        }

        public static MacroEnv CreateDefault()
        {
            var env = new MacroEnv();
            // What clangd sees for an MSVC-style command line (clang-cl driver mode).
            foreach (var kv in new[]
            {
                ("__clang__", "1"), ("__clang_major__", "20"), ("_MSC_VER", "1944"), ("_MSC_FULL_VER", "194435207"), ("_WIN32", "1"), ("_WIN64", "1"),
                ("_M_X64", "100"), ("_M_AMD64", "100"), ("__x86_64__", "1"), ("_MSVC_LANG", "202002L"), ("__cplusplus", "202002L"), ("_CPPUNWIND", "1"),
                ("__SSE__", "1"), ("__SSE2__", "1"), ("_MT", "1"), ("_DLL", "1"), ("__STDC_HOSTED__", "1"),
            })
                env.DefineBase(kv.Item1, kv.Item2);
            return env;
        }
    }

    public sealed class PreprocessedFile
    {
        public Token[] Tokens;
        public int Count;
        public List<MacroDef> MacroRefs = new List<MacroDef>();  // Token.Value of macro-flagged tokens is kept; lookup via MacroAt
        public Dictionary<int, MacroDef> LocalMacros;              // final local overlay (null if none)
        public List<string> Includes;
        public List<MacroDef> Defines;                             // active #defines (for the global table)
        public HashSet<int> Undefs;
        public List<(string Include, Dictionary<int, MacroDef> Macros)> IncludeContexts;                                // names #undef'd in this file (file-scoped helper macros)
        public int Lines;
    }

    /// <summary>
    /// Lightweight preprocessor: conditionals evaluated with the macro environment, #define/#undef tracked per file,
    /// #include only recorded (headers are parsed once, globally). Macros are NOT expanded in the token stream:
    /// the parser decides what to do with macro names (skip decorations, parse arguments as code, expand P_THIS...).
    /// </summary>
    public sealed class Preprocessor
    {
        readonly MacroEnv env;
        Dictionary<int, MacroDef> local;
        readonly bool collectDefines;

        public Preprocessor(MacroEnv env, bool collectDefines)
        {
            this.env = env; this.collectDefines = collectDefines;
        }

        MacroDef Find(int name)
        {
            if (local != null && local.TryGetValue(name, out var m)) return m == MacroDef.Undefined ? null : m;
            return env.Find(name);
        }

        public MacroDef FindMacro(int name) => Find(name);

        static readonly int D_if = Names.Intern("if"), D_ifdef = Names.Intern("ifdef"), D_ifndef = Names.Intern("ifndef"), D_elif = Names.Intern("elif"),
            D_elifdef = Names.Intern("elifdef"), D_elifndef = Names.Intern("elifndef"), D_else = Names.Intern("else"), D_endif = Names.Intern("endif"),
            D_define = Names.Intern("define"), D_undef = Names.Intern("undef"), D_include = Names.Intern("include"), D_include_next = Names.Intern("include_next"),
            D_import = Names.Intern("import");

        public PreprocessedFile Run(byte[] src, bool wantTokens, bool wantIncludes, Dictionary<int, MacroDef> context = null)
        {
            local = context != null ? new Dictionary<int, MacroDef>(context) : null;
            var result = new PreprocessedFile();
            var toks = wantTokens ? new Token[Math.Max(64, src.Length / 5)] : null;
            int count = 0;
            int start = 0;
            if (src.Length >= 3 && src[0] == 0xEF && src[1] == 0xBB && src[2] == 0xBF) start = 3;
            var lx = new Lexer(src, start, src.Length);
            // conditional stack: state 0 = active, 1 = looking for a true branch, 2 = done (a branch was taken), 3 = parent inactive
            var stack = new List<byte>();
            bool active = true;
            var dir = new List<Token>(32);
            if (wantIncludes) result.Includes = new List<string>();
            result.Defines = new List<MacroDef>();

            while (true)
            {
                if (!active)
                {
                    if (!lx.SkipToNextDirectiveCandidate()) break;
                }
                if (!lx.Next(out var t, out bool directive)) break;
                if (directive)
                {
                    dir.Clear();
                    lx.ReadDirectiveTokens(dir);
                    if (dir.Count == 0 || dir[0].Kind != TK.Ident) { if (!active) lx.SkipLine(); continue; }
                    int d = dir[0].Value;
                    if (d == D_if || d == D_ifdef || d == D_ifndef)
                    {
                        if (!active) { stack.Add(3); continue; }
                        bool v = d == D_if ? Eval(dir, 1, src) : (dir.Count > 1 && dir[1].Kind == TK.Ident && Find(dir[1].Value) != null) == (d == D_ifdef);
                        stack.Add(v ? (byte)0 : (byte)1);
                        active = v;
                    }
                    else if (d == D_elif || d == D_elifdef || d == D_elifndef)
                    {
                        if (stack.Count == 0) continue;
                        byte st = stack[stack.Count - 1];
                        if (st == 0) { stack[stack.Count - 1] = 2; active = false; }
                        else if (st == 1)
                        {
                            bool v = d == D_elif ? Eval(dir, 1, src) : (dir.Count > 1 && dir[1].Kind == TK.Ident && Find(dir[1].Value) != null) == (d == D_elifdef);
                            if (v) { stack[stack.Count - 1] = 0; active = true; }
                        }
                    }
                    else if (d == D_else)
                    {
                        if (stack.Count == 0) continue;
                        byte st = stack[stack.Count - 1];
                        if (st == 0) { stack[stack.Count - 1] = 2; active = false; }
                        else if (st == 1) { stack[stack.Count - 1] = 0; active = true; }
                    }
                    else if (d == D_endif)
                    {
                        if (stack.Count == 0) continue;
                        stack.RemoveAt(stack.Count - 1);
                        active = stack.Count == 0 || stack[stack.Count - 1] == 0;
                    }
                    else if (!active) { }
                    else if (d == D_define)
                    {
                        var def = ParseDefine(dir, src);
                        if (def != null)
                        {
                            (local ??= new Dictionary<int, MacroDef>())[def.Name] = def;
                            result.Defines?.Add(def);
                        }
                    }
                    else if (d == D_undef)
                    {
                        if (dir.Count > 1 && dir[1].Kind == TK.Ident) { (local ??= new Dictionary<int, MacroDef>())[dir[1].Value] = MacroDef.Undefined; (result.Undefs ??= new HashSet<int>()).Add(dir[1].Value); }
                    }
                    else if ((d == D_include || d == D_include_next || d == D_import) && wantIncludes && dir.Count > 1)
                    {
                        if (dir[1].Kind == TK.String)
                        {
                            var s = Encoding.UTF8.GetString(src, dir[1].Pos + 1, Math.Max(0, dir[1].Len - 2));
                            result.Includes.Add(s);
                            Snapshot(result, s);
                        }
                        else if (dir[1].Is('<'))
                        {
                            int e = dir.Count - 1;
                            while (e > 1 && !dir[e].Is('>')) e--;
                            if (e > 1) { var s = Encoding.UTF8.GetString(src, dir[2].Pos, dir[e].Pos - dir[2].Pos); result.Includes.Add(s); Snapshot(result, s); }
                        }
                    }
                    continue;
                }
                if (!active) { lx.SkipLine(); continue; }
                if (toks != null)
                {
                    if (count == toks.Length) Array.Resize(ref toks, toks.Length * 2);
                    toks[count++] = t;
                }
            }
            result.Tokens = toks;
            result.Count = count;
            result.LocalMacros = local;
            result.Lines = lx.Line;
            return result;
        }

        /// <summary>Macros defined by this file and active at an #include (for headers included "with parameters", e.g. Map.h.inl).</summary>
        void Snapshot(PreprocessedFile result, string include)
        {
            if (local == null || local.Count == 0) return;
            Dictionary<int, MacroDef> snap = null;
            foreach (var kv in local)
                if (kv.Value != MacroDef.Undefined) (snap ??= new Dictionary<int, MacroDef>())[kv.Key] = kv.Value;
            if (snap != null) (result.IncludeContexts ??= new List<(string, Dictionary<int, MacroDef>)>()).Add((include, snap));
        }

        // ------------------------------------------------------------------ #define

        static MacroDef ParseDefine(List<Token> dir, byte[] src)
        {
            if (dir.Count < 2 || dir[1].Kind != TK.Ident) return null;
            var def = new MacroDef { Name = dir[1].Value };
            int i = 2;
            if (dir.Count > 2 && dir[2].Is('(') && dir[2].Pos == dir[1].Pos + dir[1].Len)
            {
                def.FunctionLike = true;
                var ps = new List<int>();
                i = 3;
                while (i < dir.Count && !dir[i].Is(')'))
                {
                    if (dir[i].Kind == TK.Ident) ps.Add(dir[i].Value);
                    else if (dir[i].Is(P.Ellipsis)) { def.Variadic = true; ps.Add(VaArgs); }
                    i++;
                }
                i++;
                def.Params = ps.ToArray();
            }
            var body = new Token[Math.Max(0, dir.Count - i)];
            var key = new StringBuilder();
            for (int j = 0; j < body.Length; j++)
            {
                var t = dir[i + j];
                if (t.Kind == TK.Number) t.Value = ParseInt(src, t.Pos, t.Len);
                else if (t.Kind == TK.String || t.Kind == TK.Char) t.Value = 0;
                t.Virtual = true;
                body[j] = t;
                key.Append((int)t.Kind).Append(':').Append(t.Value).Append(' ');
            }
            def.Body = body;
            def.BodyKey = (def.FunctionLike ? "F" + def.Params.Length : "O") + key;
            return def;
        }

        public static readonly int VaArgs = Names.Intern("__VA_ARGS__");

        public static MacroDef ParseDefineText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes("#define " + text + "\n");
            var lx = new Lexer(bytes, 0, bytes.Length);
            lx.Next(out _, out _);
            var dir = new List<Token>();
            lx.ReadDirectiveTokens(dir);
            return ParseDefine(dir, bytes);
        }

        static int ParseInt(byte[] s, int pos, int len)
        {
            long v = 0;
            int i = pos, end = pos + len;
            if (len > 2 && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                i += 2;
                for (; i < end; i++)
                {
                    int c = s[i], d;
                    if (c >= '0' && c <= '9') d = c - '0'; else if (c >= 'a' && c <= 'f') d = c - 'a' + 10; else if (c >= 'A' && c <= 'F') d = c - 'A' + 10; else if (c == '\'') continue; else break;
                    v = v * 16 + d; if (v > int.MaxValue) v = int.MaxValue;
                }
            }
            else
            {
                for (; i < end; i++)
                {
                    int c = s[i];
                    if (c == '\'') continue;
                    if (c < '0' || c > '9') break;
                    v = v * 10 + (c - '0'); if (v > int.MaxValue) v = int.MaxValue;
                }
            }
            return (int)v;
        }

        // ------------------------------------------------------------------ #if evaluation

        bool Eval(List<Token> dir, int start, byte[] src)
        {
            var list = new List<Token>(dir.Count);
            // numbers in the directive itself
            for (int i = start; i < dir.Count; i++)
            {
                var t = dir[i];
                if (t.Kind == TK.Number) t.Value = ParseInt(src, t.Pos, t.Len);
                list.Add(t);
            }
            var expanded = new List<Token>(list.Count * 2);
            Expand(list, expanded, 0);
            int p = 0;
            try { return EvalCond(expanded, ref p) != 0; }
            catch (Exception) { return false; }
        }

        void Expand(List<Token> input, List<Token> output, int depth)
        {
            for (int i = 0; i < input.Count; i++)
            {
                var t = input[i];
                if (t.Kind != TK.Ident) { output.Add(t); continue; }
                if (t.Value == K.Defined || t.Value == K.HasInclude || t.Value == K.HasIncludeNext)
                {
                    // defined X / defined(X): evaluate now (before expansion)
                    int j = i + 1; bool paren = false;
                    if (j < input.Count && input[j].Is('(')) { paren = true; j++; }
                    bool v;
                    if (t.Value == K.Defined)
                        v = j < input.Count && input[j].Kind == TK.Ident && Find(input[j].Value) != null;
                    else v = true;
                    if (paren) { int d = 0; for (; j < input.Count; j++) { if (input[j].Is('(')) d++; else if (input[j].Is(')')) { if (d == 0) break; d--; } } }
                    i = j;
                    output.Add(new Token { Kind = TK.Number, Value = v ? 1 : 0 });
                    continue;
                }
                var m = depth < 24 ? Find(t.Value) : null;
                if (m == null || m.Ambiguous && !m.FunctionLike && m.Body.Length == 1 && m.Body[0].Kind == TK.Number && false)
                {
                    if (t.Value == K.True) output.Add(new Token { Kind = TK.Number, Value = 1 });
                    else if (t.Value == K.False) output.Add(new Token { Kind = TK.Number, Value = 0 });
                    else if (IsHasFeature(t.Value) && i + 1 < input.Count && input[i + 1].Is('('))
                    {
                        int d = 0, j = i + 1;
                        for (; j < input.Count; j++) { if (input[j].Is('(')) d++; else if (input[j].Is(')')) { d--; if (d == 0) break; } }
                        i = j; output.Add(new Token { Kind = TK.Number, Value = 0 });
                    }
                    else
                    {
                        // unknown identifier = 0; unknown function-like call: skip its arguments too
                        if (i + 1 < input.Count && input[i + 1].Is('('))
                        {
                            int d = 0, j = i + 1;
                            for (; j < input.Count; j++) { if (input[j].Is('(')) d++; else if (input[j].Is(')')) { d--; if (d == 0) break; } }
                            i = j;
                        }
                        output.Add(new Token { Kind = TK.Number, Value = 0 });
                    }
                    continue;
                }
                if (!m.FunctionLike)
                {
                    var body = new List<Token>(m.Body);
                    Expand(body, output, depth + 1);
                    continue;
                }
                if (i + 1 >= input.Count || !input[i + 1].Is('(')) { output.Add(new Token { Kind = TK.Number, Value = 0 }); continue; }
                // collect arguments
                var args = new List<List<Token>> { new List<Token>() };
                int depthP = 0, k = i + 2;
                for (; k < input.Count; k++)
                {
                    var a = input[k];
                    if (a.Is('(')) depthP++;
                    else if (a.Is(')')) { if (depthP == 0) break; depthP--; }
                    else if (a.Is(',') && depthP == 0 && !(m.Variadic && args.Count >= m.Params.Length)) { args.Add(new List<Token>()); continue; }
                    args[args.Count - 1].Add(a);
                }
                i = k;
                var subst = new List<Token>();
                foreach (var b in m.Body)
                {
                    if (b.Kind == TK.Ident)
                    {
                        int pi = Array.IndexOf(m.Params, b.Value);
                        if (pi >= 0) { if (pi < args.Count) subst.AddRange(args[pi]); continue; }
                    }
                    if (b.Is('#') || b.Is(P.HashHash)) continue;
                    subst.Add(b);
                }
                Expand(subst, output, depth + 1);
            }
        }

        static readonly int HasFeature = Names.Intern("__has_feature"), HasBuiltin = Names.Intern("__has_builtin"),
            HasAttr = Names.Intern("__has_attribute"), HasCppAttr = Names.Intern("__has_cpp_attribute"), HasDeclspec = Names.Intern("__has_declspec_attribute"),
            HasExtension = Names.Intern("__has_extension"), HasWarning = Names.Intern("__has_warning");
        static bool IsHasFeature(int n) => n == HasFeature || n == HasBuiltin || n == HasAttr || n == HasCppAttr || n == HasDeclspec || n == HasExtension || n == HasWarning;

        static long EvalCond(List<Token> t, ref int p)
        {
            long c = EvalBin(t, ref p, 0);
            if (p < t.Count && t[p].Is('?'))
            {
                p++;
                long a = EvalCond(t, ref p);
                if (p < t.Count && t[p].Is(':')) p++;
                long b = EvalCond(t, ref p);
                return c != 0 ? a : b;
            }
            return c;
        }

        static int Prec(List<Token> t, int p, out int op, out int len)
        {
            op = 0; len = 1;
            if (p >= t.Count || t[p].Kind != TK.Punct) return -1;
            int v = t[p].Value;
            bool nextAdj(int ch) => p + 1 < t.Count && t[p + 1].Is(ch) && t[p + 1].Pos == t[p].Pos + 1;
            switch (v)
            {
                case P.OrOr: op = v; return 1;
                case P.AndAnd: op = v; return 2;
                case '|': op = v; return 3;
                case '^': op = v; return 4;
                case '&': op = v; return 5;
                case P.Eq: case P.Ne: op = v; return 6;
                case '<': case P.Le: op = v; return 7;
                case '>':
                    if (nextAdj('>')) { op = 1000; len = 2; return 8; }
                    if (nextAdj('=')) { op = 1001; len = 2; return 7; }
                    op = '>'; return 7;
                case P.Shl: op = v; return 8;
                case '+': case '-': op = v; return 9;
                case '*': case '/': case '%': op = v; return 10;
            }
            return -1;
        }

        static long EvalBin(List<Token> t, ref int p, int minPrec)
        {
            long l = EvalUnary(t, ref p);
            while (true)
            {
                int prec = Prec(t, p, out int op, out int len);
                if (prec < 0 || prec <= minPrec - 1 || prec < minPrec) break;
                p += len;
                long r = EvalBin(t, ref p, prec + 1);
                switch (op)
                {
                    case P.OrOr: l = (l != 0 || r != 0) ? 1 : 0; break;
                    case P.AndAnd: l = (l != 0 && r != 0) ? 1 : 0; break;
                    case '|': l |= r; break;
                    case '^': l ^= r; break;
                    case '&': l &= r; break;
                    case P.Eq: l = l == r ? 1 : 0; break;
                    case P.Ne: l = l != r ? 1 : 0; break;
                    case '<': l = l < r ? 1 : 0; break;
                    case P.Le: l = l <= r ? 1 : 0; break;
                    case '>': l = l > r ? 1 : 0; break;
                    case 1001: l = l >= r ? 1 : 0; break;
                    case 1000: l = r >= 0 && r < 63 ? l >> (int)r : 0; break;
                    case P.Shl: l = r >= 0 && r < 63 ? l << (int)r : 0; break;
                    case '+': l += r; break;
                    case '-': l -= r; break;
                    case '*': l *= r; break;
                    case '/': l = r == 0 ? 0 : l / r; break;
                    case '%': l = r == 0 ? 0 : l % r; break;
                }
            }
            return l;
        }

        static long EvalUnary(List<Token> t, ref int p)
        {
            if (p >= t.Count) return 0;
            var x = t[p];
            if (x.Is('!')) { p++; return EvalUnary(t, ref p) == 0 ? 1 : 0; }
            if (x.Is('-')) { p++; return -EvalUnary(t, ref p); }
            if (x.Is('+')) { p++; return EvalUnary(t, ref p); }
            if (x.Is('~')) { p++; return ~EvalUnary(t, ref p); }
            if (x.Is('('))
            {
                p++;
                long v = EvalCond(t, ref p);
                if (p < t.Count && t[p].Is(')')) p++;
                return v;
            }
            p++;
            if (x.Kind == TK.Number) return x.Value;
            if (x.Kind == TK.Char) return 1;
            return 0;
        }
    }
}
