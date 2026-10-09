using System;
using System.Collections.Generic;

namespace UnrealSense.Indexer
{
    /// <summary>Function-like / object-like macro substitution on token streams (simple recursion, no full rescanning rules).</summary>
    public static class MacroExpander
    {
        /// <summary>Substitutes <paramref name="args"/> (token ranges of <paramref name="src"/>) into the body of <paramref name="m"/>.
        /// Body tokens are virtual; argument tokens keep their real positions unless <paramref name="virtualArgs"/>.</summary>
        public static List<Token> Expand(MacroDef m, Token[] src, List<(int, int)> args, bool virtualArgs)
        {
            var output = new List<Token>(m.Body.Length + 16);
            var body = m.Body;
            for (int i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (b.Is('#') && i + 1 < body.Length && body[i + 1].Kind == TK.Ident) { output.Add(new Token { Kind = TK.String, Virtual = true }); i++; continue; }
                if (b.Is(P.HashHash))
                {
                    if (i + 1 < body.Length && output.Count > 0)
                    {
                        var next = new List<Token>();
                        Append(m, body[i + 1], src, args, next, true);
                        var prev = output[output.Count - 1];
                        if (prev.Kind == TK.Ident && next.Count > 0 && (next[0].Kind == TK.Ident || next[0].Kind == TK.Number))
                        {
                            var text = Names.Get(prev.Value) + (next[0].Kind == TK.Ident ? Names.Get(next[0].Value) : next[0].Value.ToString());
                            prev.Value = Names.Intern(text);
                            prev.Virtual = true;
                            output[output.Count - 1] = prev;
                            for (int k = 1; k < next.Count; k++) output.Add(next[k]);
                        }
                        else output.AddRange(next);
                        i++;
                    }
                    continue;
                }
                // a parameter followed by ## is pasted: never a real reference
                bool pasted = i + 1 < body.Length && body[i + 1].Is(P.HashHash);
                Append(m, b, src, args, output, virtualArgs || pasted);
            }
            return output;
        }

        static void Append(MacroDef m, Token b, Token[] src, List<(int, int)> args, List<Token> output, bool virtualArgs)
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
                            if (a > pi) output.Add(new Token { Kind = TK.Punct, Value = ',', Virtual = true });
                            for (int k = args[a].Item1; k < args[a].Item2; k++) { var t = src[k]; if (virtualArgs) t.Virtual = true; output.Add(t); }
                        }
                    }
                    else if (pi < args.Count) for (int k = args[pi].Item1; k < args[pi].Item2; k++) { var t = src[k]; if (virtualArgs) t.Virtual = true; output.Add(t); }
                    return;
                }
            }
            b.Virtual = true;
            output.Add(b);
        }

        public static List<(int, int)> SplitArgs(Token[] t, int open, int close)
        {
            var list = new List<(int, int)>();
            int s = open + 1, depth = 0;
            for (int p = open + 1; p < close; p++)
            {
                var x = t[p];
                if (x.Kind != TK.Punct) continue;
                if (x.Value == '(' || x.Value == '[' || x.Value == '{') depth++;
                else if (x.Value == ')' || x.Value == ']' || x.Value == '}') depth--;
                else if (x.Value == ',' && depth == 0) { list.Add((s, p)); s = p + 1; }
            }
            if (close > open + 1) list.Add((s, close));
            return list;
        }

        static int MatchParen(Token[] t, int open, int n)
        {
            int depth = 0;
            for (int p = open; p < n; p++)
            {
                if (t[p].Kind != TK.Punct) continue;
                if (t[p].Value == '(') depth++;
                else if (t[p].Value == ')') { depth--; if (depth == 0) return p; }
                else if (t[p].Value == ';' || t[p].Value == '{' || t[p].Value == '}') return -1;
            }
            return -1;
        }

        static readonly int Class = K.Class, Struct = K.Struct;

        /// <summary>
        /// Expands, in the token stream, macro invocations that open a class whose body follows in the source
        /// (e.g. automation test / spec macros: NAME(args) { ... }). Both passes apply the same rewrite.
        /// </summary>
        public static Token[] RewriteTypeMacros(Token[] t, ref int n, FileMacros macros)
        {
            List<Token> output = null;
            int copied = 0;
            for (int p = 0; p < n; p++)
            {
                var x = t[p];
                if (x.Kind != TK.Ident || p + 1 >= n || !t[p + 1].Is('(')) continue;
                var m = macros.Find(x.Value);
                if (m == null || !m.FunctionLike || m.Body.Length == 0) continue;
                int close = MatchParen(t, p + 1, n);
                if (close < 0 || close + 1 >= n || !t[close + 1].Is('{')) continue;
                var exp = Expand(m, t, SplitArgs(t, p + 1, close), false);
                exp = ExpandNested(exp, macros, 0);
                if (!DefinesType(exp)) continue;
                output ??= new List<Token>(n + 256);
                for (int k = copied; k < p; k++) output.Add(t[k]);
                foreach (var e in exp)
                {
                    var v = e;
                    if (v.Virtual) { v.Line = x.Line; v.Col = x.Col; v.Pos = x.Pos; }
                    output.Add(v);
                }
                copied = close + 1;
                p = close;
            }
            if (output == null) return t;
            for (int k = copied; k < n; k++) output.Add(t[k]);
            n = output.Count;
            var arr = new Token[n + 1];
            output.CopyTo(arr);
            return arr;
        }

        static List<Token> ExpandNested(List<Token> toks, FileMacros macros, int depth)
        {
            if (depth > 6) return toks;
            bool changed = false;
            var result = new List<Token>(toks.Count);
            var arr = toks.ToArray();
            for (int p = 0; p < arr.Length; p++)
            {
                var x = arr[p];
                if (x.Kind == TK.Ident && x.Virtual)
                {
                    var m = macros.Find(x.Value);
                    if (m != null && m.Body.Length > 0)
                    {
                        var kind = macros.Classify(x.Value);
                        if (m.FunctionLike && kind == MacroKind.FuncOther && p + 1 < arr.Length && arr[p + 1].Is('('))
                        {
                            int close = MatchParen(arr, p + 1, arr.Length);
                            if (close > 0)
                            {
                                result.AddRange(Expand(m, arr, SplitArgs(arr, p + 1, close), false));
                                p = close; changed = true; continue;
                            }
                        }
                        else if (!m.FunctionLike && kind == MacroKind.ObjectOther)
                        {
                            result.AddRange(Expand(m, arr, null, true));
                            changed = true; continue;
                        }
                    }
                }
                result.Add(x);
            }
            return changed ? ExpandNested(result, macros, depth + 1) : result;
        }

        static bool DefinesType(List<Token> toks)
        {
            // the expansion must end with a class head: ... class|struct Name [: bases]  (the '{' follows in the source)
            int depth = 0;
            bool head = false;
            foreach (var t in toks)
            {
                if (t.Kind == TK.Punct)
                {
                    if (t.Value == '(' || t.Value == '[' || t.Value == '{') depth++;
                    else if (t.Value == ')' || t.Value == ']' || t.Value == '}') depth--;
                    else if (t.Value == ';' && depth == 0) head = false;
                }
                else if (t.Kind == TK.Ident && depth == 0 && (t.Value == Class || t.Value == Struct)) head = true;
            }
            return head && depth == 0;
        }

        // ------------------------------------------------------------------ parameterized includes (Map.h.inl, UnrealString.h.inl...)

        /// <summary>
        /// For a file parsed in the context of its includer's macros: expands object-like macros of the context (and of
        /// the file itself), and function-like macros whose arguments name them (UE_JOIN(UE_TMAP_PREFIX, Map) gives TMap).
        /// A single-identifier result keeps the position of the macro name (it is reported there, like clangd does).
        /// </summary>
        public static Token[] RewriteContextMacros(Token[] t, ref int n, HashSet<int> contextNames, FileMacros macros)
        {
            var output = new List<Token>(n + 64);
            bool changed = false;
            for (int p = 0; p < n; p++)
            {
                var x = t[p];
                if (x.Kind == TK.Ident)
                {
                    var m = macros.Find(x.Value);
                    if (m != null && m.Body.Length > 0)
                    {
                        int end = p;
                        bool expand = false;
                        if (!m.FunctionLike && contextNames.Contains(x.Value)) expand = true;
                        else if (m.FunctionLike && p + 1 < n && t[p + 1].Is('('))
                        {
                            int close = MatchParen(t, p + 1, n);
                            if (close > 0)
                            {
                                for (int k = p + 2; k < close && !expand; k++) if (t[k].Kind == TK.Ident && contextNames.Contains(t[k].Value)) expand = true;
                                end = close;
                            }
                        }
                        if (expand)
                        {
                            var input = new List<Token>();
                            for (int k = p; k <= end; k++) input.Add(t[k]);
                            var res = ExpandFull(input, macros, 0, new HashSet<int>());
                            if (res.Count == 1 && res[0].Kind == TK.Ident)
                            {
                                var r = x; r.Value = res[0].Value; r.Virtual = false; output.Add(r);
                            }
                            else foreach (var r0 in res) { var r = r0; r.Virtual = true; r.Line = x.Line; r.Col = x.Col; r.Pos = x.Pos; output.Add(r); }
                            p = end;
                            changed = true;
                            continue;
                        }
                    }
                }
                output.Add(x);
            }
            if (!changed) return t;
            n = output.Count;
            var arr = new Token[n + 1];
            output.CopyTo(arr);
            return arr;
        }

        /// <summary>Standard-ish full expansion (arguments pre-expanded, ## pasting, rescanning with a hide set).</summary>
        static List<Token> ExpandFull(List<Token> input, FileMacros macros, int depth, HashSet<int> hide)
        {
            if (depth > 16) return input;
            var output = new List<Token>(input.Count);
            int spliceAt = -1; // output.Count right after the last expansion (only its last token may take following arguments)
            var arr = input.ToArray();
            for (int p = 0; p < arr.Length; p++)
            {
                if (output.Count > 0 && arr[p].Is('(') && output[output.Count - 1].Kind == TK.Ident && !hide.Contains(output[output.Count - 1].Value)
                    && macros.Find(output[output.Count - 1].Value) is MacroDef fm && fm.FunctionLike && fm.Body.Length > 0 && output.Count == spliceAt)
                {
                    // rescanning: a function-like macro name produced by the previous expansion takes the arguments that
                    // follow it in the source (UE_JOIN: UE_APPEND_VA_ARG_COUNT(UE_PRIVATE_JOIN) gives UE_PRIVATE_JOIN0, then "(A, B)")
                    var rest = new List<Token>(arr.Length - p + 1) { output[output.Count - 1] };
                    for (int k = p; k < arr.Length; k++) rest.Add(arr[k]);
                    output.RemoveAt(output.Count - 1);
                    output.AddRange(ExpandFull(rest, macros, depth + 1, hide));
                    return output;
                }
                var x = arr[p];
                if (x.Kind == TK.Ident && !hide.Contains(x.Value))
                {
                    var m = macros.Find(x.Value);
                    if (m != null)
                    {
                        List<Token> body = null;
                        int end = p;
                        if (!m.FunctionLike) body = Substitute(m, null, macros, depth, hide);
                        else if (p + 1 < arr.Length && arr[p + 1].Is('('))
                        {
                            int close = MatchParen(arr, p + 1, arr.Length);
                            if (close > 0)
                            {
                                var ranges = SplitArgs(arr, p + 1, close);
                                var args = new List<List<Token>>();
                                foreach (var (a, b) in ranges) { var l = new List<Token>(); for (int k = a; k < b; k++) l.Add(arr[k]); args.Add(l); }
                                body = Substitute(m, args, macros, depth, hide);
                                end = close;
                            }
                        }
                        if (body != null)
                        {
                            hide.Add(x.Value);
                            var rescanned = ExpandFull(body, macros, depth + 1, hide);
                            hide.Remove(x.Value);
                            output.AddRange(rescanned);
                            spliceAt = output.Count;
                            p = end;
                            continue;
                        }
                    }
                }
                output.Add(x);
                spliceAt = -1;
            }
            return output;
        }

        static List<Token> Substitute(MacroDef m, List<List<Token>> args, FileMacros macros, int depth, HashSet<int> hide)
        {
            var body = m.Body;
            var output = new List<Token>(body.Length + 8);
            List<Token> Arg(int pi, bool raw)
            {
                if (args == null) return new List<Token>();
                if (Array.IndexOf(m.Params, Preprocessor.VaArgs) == pi || m.Variadic && pi == m.Params.Length - 1)
                {
                    var all = new List<Token>();
                    for (int a = pi; a < args.Count; a++) { if (a > pi) all.Add(new Token { Kind = TK.Punct, Value = ',' }); all.AddRange(args[a]); }
                    return raw ? all : ExpandFull(all, macros, depth + 1, hide);
                }
                if (pi >= args.Count) return new List<Token>();
                return raw ? args[pi] : ExpandFull(args[pi], macros, depth + 1, hide);
            }
            for (int i = 0; i < body.Length; i++)
            {
                var b = body[i];
                int pi = b.Kind == TK.Ident ? Array.IndexOf(m.Params, b.Value) : -1;
                bool nextPaste = i + 1 < body.Length && body[i + 1].Is(P.HashHash);
                if (b.Is('#') && i + 1 < body.Length && body[i + 1].Kind == TK.Ident) { output.Add(new Token { Kind = TK.String }); i++; continue; }
                if (b.Is(P.HashHash) && i + 1 < body.Length)
                {
                    var nb = body[i + 1];
                    int npi = nb.Kind == TK.Ident ? Array.IndexOf(m.Params, nb.Value) : -1;
                    var right = npi >= 0 ? Arg(npi, true) : new List<Token> { nb };
                    if (output.Count > 0 && right.Count > 0)
                    {
                        var left = output[output.Count - 1];
                        var r0 = right[0];
                        string lt = left.Kind == TK.Ident ? Names.Get(left.Value) : left.Kind == TK.Number ? left.Value.ToString() : null;
                        string rt = r0.Kind == TK.Ident ? Names.Get(r0.Value) : r0.Kind == TK.Number ? r0.Value.ToString() : null;
                        if (lt != null && rt != null)
                        {
                            left.Kind = TK.Ident; left.Value = Names.Intern(lt + rt);
                            output[output.Count - 1] = left;
                            for (int k = 1; k < right.Count; k++) output.Add(right[k]);
                        }
                        else output.AddRange(right);
                    }
                    else if (right.Count == 0 && npi >= 0 && output.Count > 0 && output[output.Count - 1].Is(',')) output.RemoveAt(output.Count - 1); // GNU ", ##__VA_ARGS__"
                    else output.AddRange(right);
                    i++;
                    continue;
                }
                if (pi >= 0) { output.AddRange(Arg(pi, nextPaste)); continue; }
                output.Add(b);
            }
            return output;
        }
    }
}
