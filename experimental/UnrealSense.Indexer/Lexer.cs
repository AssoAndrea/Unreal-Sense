using System;
using System.Collections.Generic;

namespace UnrealSense.Indexer
{
    public enum TK : byte { Ident, Number, String, Char, Punct, Eof }

    /// <summary>Punctuator codes: single characters use their ASCII code, multi-character ones the constants below.</summary>
    public static class P
    {
        public const int Scope = 256, Arrow = 257, Inc = 258, Dec = 259, Shl = 260, Le = 261, Eq = 262, Ne = 263,
            AndAnd = 264, OrOr = 265, PlusEq = 266, MinusEq = 267, MulEq = 268, DivEq = 269, ModEq = 270, AndEq = 271,
            OrEq = 272, XorEq = 273, ShlEq = 274, Ellipsis = 275, ArrowStar = 276, DotStar = 277, Spaceship = 278, HashHash = 279;
        // '>' is always lexed alone so that template argument lists close correctly; ">>", ">=" are recombined by the parser.
    }

    public struct Token
    {
        public int Pos;
        public int Value;   // identifier: name id; punct: code
        public int Line;    // 1-based
        public int Col;     // 1-based, UTF-16 units (LSP / clangd convention)
        public ushort Len;
        public TK Kind;
        public bool Virtual; // comes from a macro expansion: never reported as a reference
        public bool Is(int punct) => Kind == TK.Punct && Value == punct;
        public bool IsId(int name) => Kind == TK.Ident && Value == name;
    }

    /// <summary>Raw tokenizer over UTF-8 bytes. Directives are reported through <see cref="Directive"/>.</summary>
    public sealed class Lexer
    {
        public readonly byte[] S;
        public int Pos;
        public readonly int End;
        public int Line = 1;
        int lineStart;
        int lineAdjust; // UTF-8 bytes that do not count as UTF-16 units on the current line
        bool atLineStart = true;

        public Lexer(byte[] s, int start, int end)
        {
            S = s; Pos = start; End = end; lineStart = start;
        }

        static readonly bool[] identStart = new bool[256], identPart = new bool[256];
        static Lexer()
        {
            for (int c = 0; c < 256; c++)
            {
                identStart[c] = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '$' || c >= 0x80;
                identPart[c] = identStart[c] || (c >= '0' && c <= '9');
            }
        }

        public static bool IsIdentStart(byte c) => identStart[c];
        public static bool IsIdentPart(byte c) => identPart[c];

        int Col(int pos) => pos - lineStart - lineAdjust + 1;

        void NewLine(int posAfterNewline) { Line++; lineStart = posAfterNewline; lineAdjust = 0; atLineStart = true; }

        void CountUtf8(byte b)
        {
            if ((b & 0xC0) == 0x80) lineAdjust++;
            else if (b >= 0xF0) lineAdjust--; // 4-byte sequence = 2 UTF-16 units
        }

        /// <summary>Returns the next token. When a '#' starts a line, returns a Punct '#' token with <paramref name="directive"/> = true.</summary>
        public bool Next(out Token t, out bool directive)
        {
            directive = false;
            var s = S;
            while (Pos < End)
            {
                byte c = s[Pos];
                if (c == '\n') { Pos++; NewLine(Pos); continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { Pos++; continue; }
                if (c == '\\' && Pos + 1 < End && (s[Pos + 1] == '\n' || s[Pos + 1] == '\r'))
                {
                    // line continuation outside directives: skip, keep line counting
                    Pos++; if (s[Pos] == '\r') Pos++; if (Pos < End && s[Pos] == '\n') Pos++;
                    Line++; lineStart = Pos; lineAdjust = 0; continue;
                }
                if (c == '/' && Pos + 1 < End)
                {
                    byte d = s[Pos + 1];
                    if (d == '/')
                    {
                        Pos += 2;
                        while (Pos < End && s[Pos] != '\n')
                        {
                            byte b = s[Pos];
                            if (b >= 0x80) CountUtf8(b);
                            else if (b == '\\' && Pos + 1 < End && (s[Pos + 1] == '\n' || (s[Pos + 1] == '\r' && Pos + 2 < End && s[Pos + 2] == '\n')))
                            {
                                Pos += s[Pos + 1] == '\r' ? 3 : 2; Line++; lineStart = Pos; lineAdjust = 0; continue;
                            }
                            Pos++;
                        }
                        continue;
                    }
                    if (d == '*')
                    {
                        Pos += 2;
                        while (Pos < End)
                        {
                            byte b = s[Pos];
                            if (b == '*' && Pos + 1 < End && s[Pos + 1] == '/') { Pos += 2; break; }
                            if (b == '\n') { Pos++; Line++; lineStart = Pos; lineAdjust = 0; continue; }
                            if (b >= 0x80) CountUtf8(b);
                            Pos++;
                        }
                        continue;
                    }
                }
                break;
            }
            t = default;
            if (Pos >= End) { t.Kind = TK.Eof; t.Pos = End; t.Line = Line; return false; }

            int start = Pos;
            byte ch = s[Pos];
            t.Pos = start; t.Line = Line; t.Col = Col(start);
            bool wasLineStart = atLineStart;
            atLineStart = false;

            if (identStart[ch])
            {
                // string literal prefixes
                if (ch == 'R' || ch == 'L' || ch == 'u' || ch == 'U')
                {
                    int q = Pos;
                    if (ch == 'u' && q + 1 < End && s[q + 1] == '8') q += 2; else q += 1;
                    bool raw = false;
                    if (ch != 'R' && q < End && s[q] == 'R') { raw = true; q++; }
                    else if (ch == 'R' && q == Pos + 1) raw = true;
                    if (q < End && s[q] == '"')
                    {
                        if (raw) { Pos = q; LexRawString(); }
                        else { Pos = q; LexQuoted((byte)'"'); }
                        t.Kind = TK.String; t.Len = (ushort)Math.Min(Pos - start, ushort.MaxValue); return true;
                    }
                    if (!raw && q < End && s[q] == '\'' )
                    {
                        Pos = q; LexQuoted((byte)'\''); t.Kind = TK.Char; t.Len = (ushort)Math.Min(Pos - start, ushort.MaxValue); return true;
                    }
                }
                int p = Pos + 1;
                uint h = (2166136261 ^ ch) * 16777619;
                while (p < End && identPart[s[p]]) { h = (h ^ s[p]) * 16777619; if (s[p] >= 0x80) CountUtf8(s[p]); p++; }
                Pos = p;
                t.Kind = TK.Ident;
                t.Len = (ushort)(p - start);
                t.Value = Names.Intern(new ReadOnlySpan<byte>(s, start, p - start), (int)(h & 0x7fffffff));
                return true;
            }
            if (ch >= '0' && ch <= '9' || (ch == '.' && Pos + 1 < End && s[Pos + 1] >= '0' && s[Pos + 1] <= '9'))
            {
                int p = Pos + 1;
                while (p < End)
                {
                    byte b = s[p];
                    if (identPart[b] || b == '.') { p++; continue; }
                    if (b == '\'' && p + 1 < End && identPart[s[p + 1]]) { p++; continue; }
                    if ((b == '+' || b == '-') && (s[p - 1] == 'e' || s[p - 1] == 'E' || s[p - 1] == 'p' || s[p - 1] == 'P')) { p++; continue; }
                    break;
                }
                Pos = p; t.Kind = TK.Number; t.Len = (ushort)(p - start); return true;
            }
            if (ch == '"') { LexQuoted((byte)'"'); t.Kind = TK.String; t.Len = (ushort)Math.Min(Pos - start, ushort.MaxValue); return true; }
            if (ch == '\'') { LexQuoted((byte)'\''); t.Kind = TK.Char; t.Len = (ushort)Math.Min(Pos - start, ushort.MaxValue); return true; }

            t.Kind = TK.Punct;
            byte n1 = Pos + 1 < End ? s[Pos + 1] : (byte)0;
            byte n2 = Pos + 2 < End ? s[Pos + 2] : (byte)0;
            int code = ch, len = 1;
            switch (ch)
            {
                case (byte)':': if (n1 == ':') { code = P.Scope; len = 2; } break;
                case (byte)'-':
                    if (n1 == '>') { if (n2 == '*') { code = P.ArrowStar; len = 3; } else { code = P.Arrow; len = 2; } }
                    else if (n1 == '-') { code = P.Dec; len = 2; }
                    else if (n1 == '=') { code = P.MinusEq; len = 2; }
                    break;
                case (byte)'+': if (n1 == '+') { code = P.Inc; len = 2; } else if (n1 == '=') { code = P.PlusEq; len = 2; } break;
                case (byte)'<':
                    if (n1 == '<') { if (n2 == '=') { code = P.ShlEq; len = 3; } else { code = P.Shl; len = 2; } }
                    else if (n1 == '=') { if (n2 == '>') { code = P.Spaceship; len = 3; } else { code = P.Le; len = 2; } }
                    break;
                case (byte)'=': if (n1 == '=') { code = P.Eq; len = 2; } break;
                case (byte)'!': if (n1 == '=') { code = P.Ne; len = 2; } break;
                case (byte)'&': if (n1 == '&') { code = P.AndAnd; len = 2; } else if (n1 == '=') { code = P.AndEq; len = 2; } break;
                case (byte)'|': if (n1 == '|') { code = P.OrOr; len = 2; } else if (n1 == '=') { code = P.OrEq; len = 2; } break;
                case (byte)'*': if (n1 == '=') { code = P.MulEq; len = 2; } break;
                case (byte)'/': if (n1 == '=') { code = P.DivEq; len = 2; } break;
                case (byte)'%': if (n1 == '=') { code = P.ModEq; len = 2; } break;
                case (byte)'^': if (n1 == '=') { code = P.XorEq; len = 2; } break;
                case (byte)'.': if (n1 == '.' && n2 == '.') { code = P.Ellipsis; len = 3; } else if (n1 == '*') { code = P.DotStar; len = 2; } break;
                case (byte)'#':
                    if (n1 == '#') { code = P.HashHash; len = 2; }
                    else if (wasLineStart) directive = true;
                    break;
            }
            if (ch >= 0x80) { CountUtf8(ch); }
            Pos += len; t.Value = code; t.Len = (ushort)len;
            return true;
        }

        void LexQuoted(byte q)
        {
            var s = S;
            Pos++;
            while (Pos < End)
            {
                byte b = s[Pos];
                if (b == q) { Pos++; return; }
                if (b == '\\') { Pos += 2; if (Pos <= End && s[Pos - 1] == '\n') { Line++; lineStart = Pos; lineAdjust = 0; } continue; }
                if (b == '\n') return; // unterminated
                if (b >= 0x80) CountUtf8(b);
                Pos++;
            }
        }

        void LexRawString()
        {
            var s = S;
            int p = Pos + 1;
            int dStart = p;
            while (p < End && s[p] != '(' && p - dStart < 17) p++;
            int dLen = p - dStart;
            p++;
            while (p < End)
            {
                if (s[p] == ')' && p + dLen + 1 < End + 1)
                {
                    bool ok = p + dLen + 1 <= End - 0 && p + dLen < End;
                    for (int i = 0; ok && i < dLen; i++) if (s[p + 1 + i] != s[dStart + i]) ok = false;
                    if (ok && p + 1 + dLen < End && s[p + 1 + dLen] == '"') { Pos = p + dLen + 2; return; }
                }
                if (s[p] == '\n') { Line++; lineStart = p + 1; lineAdjust = 0; }
                else if (s[p] >= 0x80) CountUtf8(s[p]);
                p++;
            }
            Pos = End;
        }

        /// <summary>Reads the rest of a directive line (handles continuations and comments); returns [start,end) of raw text.</summary>
        public void ReadDirectiveTokens(List<Token> into)
        {
            // Tokens until end of line (continuations are already treated as whitespace by Next()).
            int line = Line;
            while (true)
            {
                int save = Pos, saveLine = Line, saveLs = lineStart, saveAdj = lineAdjust;
                bool saveAt = atLineStart;
                // peek: stop at newline
                var s = S;
                int p = Pos;
                while (p < End && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r')) p++;
                if (p >= End || s[p] == '\n') { Pos = p; return; }
                if (!Next(out var t, out _)) return;
                if (t.Line != line)
                {
                    // the token belongs to a following line: unless the line was continued, stop
                    if (!ContinuedBetween(save, t.Pos)) { Pos = save; Line = saveLine; lineStart = saveLs; lineAdjust = saveAdj; atLineStart = saveAt; return; }
                    line = t.Line;
                }
                into.Add(t);
            }
        }

        bool ContinuedBetween(int from, int to)
        {
            // true if every newline between from and to is escaped by a backslash or inside a block comment
            bool inBlock = false;
            for (int i = from; i < to; i++)
            {
                byte b = S[i];
                if (!inBlock && b == '/' && i + 1 < to && S[i + 1] == '*') { inBlock = true; i++; continue; }
                if (inBlock && b == '*' && i + 1 < to && S[i + 1] == '/') { inBlock = false; i++; continue; }
                if (b == '\n' && !inBlock)
                {
                    int k = i - 1; if (k >= from && S[k] == '\r') k--;
                    if (k < from || S[k] != '\\') return false;
                }
            }
            return true;
        }

        /// <summary>Skips to the beginning of the next line (honouring continuations and block comments).</summary>
        public void SkipLine()
        {
            var s = S;
            while (Pos < End)
            {
                byte b = s[Pos];
                if (b == '\n')
                {
                    int k = Pos - 1; if (k >= 0 && s[k] == '\r') k--;
                    Pos++;
                    if (k >= 0 && s[k] == '\\') { Line++; lineStart = Pos; lineAdjust = 0; continue; }
                    NewLine(Pos); return;
                }
                if (b == '/' && Pos + 1 < End && s[Pos + 1] == '*')
                {
                    Pos += 2;
                    while (Pos < End && !(s[Pos] == '*' && Pos + 1 < End && s[Pos + 1] == '/')) { if (s[Pos] == '\n') { Line++; lineStart = Pos + 1; lineAdjust = 0; } Pos++; }
                    Pos += 2; continue;
                }
                if (b == '/' && Pos + 1 < End && s[Pos + 1] == '/')
                {
                    while (Pos < End && s[Pos] != '\n') Pos++;
                    continue;
                }
                if (b == '"' || b == '\'') { LexQuoted(b); continue; }
                Pos++;
            }
        }

        /// <summary>Fast skip of an inactive line: only looks for '#' as first non-blank character.</summary>
        public bool SkipToNextDirectiveCandidate()
        {
            // We are at the start of a line (inactive region). Skip whole lines until one begins with '#'.
            var s = S;
            while (Pos < End)
            {
                int p = Pos;
                while (p < End && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\f')) p++;
                if (p < End && s[p] == '#') { Pos = p; atLineStart = true; return true; }
                // skip line, handling block comments that may hide a '#' line or span lines
                Pos = p;
                SkipLineInactive();
            }
            return false;
        }

        void SkipLineInactive()
        {
            var s = S;
            while (Pos < End)
            {
                byte b = s[Pos];
                if (b == '\n') { Pos++; NewLine(Pos); return; }
                if (b == '/' && Pos + 1 < End)
                {
                    if (s[Pos + 1] == '*')
                    {
                        Pos += 2;
                        while (Pos < End && !(s[Pos] == '*' && Pos + 1 < End && s[Pos + 1] == '/')) { if (s[Pos] == '\n') { Line++; lineStart = Pos + 1; lineAdjust = 0; } Pos++; }
                        Pos = Math.Min(End, Pos + 2); continue;
                    }
                    if (s[Pos + 1] == '/') { while (Pos < End && s[Pos] != '\n') Pos++; continue; }
                }
                Pos++;
            }
        }

        public void MarkLineStart() { atLineStart = true; }
    }
}
