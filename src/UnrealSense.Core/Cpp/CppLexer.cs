using System.Collections.Generic;

namespace UnrealSense.Cpp
{
    public enum TokenKind { Identifier, Number, String, Char, Punctuation, Preprocessor }

    public readonly struct Token
    {
        public Token(TokenKind kind, int start, int length, string text)
        {
            Kind = kind;
            Start = start;
            Length = length;
            Text = text;
        }

        public TokenKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public string Text { get; }

        public bool Is(string text) => Text == text;
        public bool IsIdentifier => Kind == TokenKind.Identifier;

        public override string ToString() => $"{Kind}:{Text}@{Start}";
    }

    /// <summary>
    /// A forgiving C++ tokenizer: skips whitespace and comments, keeps preprocessor directives as single
    /// tokens and never throws on malformed/incomplete input (the editor buffer is often mid-edit).
    /// </summary>
    public static class CppLexer
    {
        public static List<Token> Tokenize(string text, int start = 0, int end = -1)
        {
            if (end < 0 || end > text.Length) end = text.Length;
            var tokens = new List<Token>();
            int i = start;
            bool atLineStart = true;

            while (i < end)
            {
                char c = text[i];

                if (c == '\n') { atLineStart = true; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '/' && i + 1 < end && text[i + 1] == '/')
                {
                    while (i < end && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < end && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, end - i - 2 < 0 ? 0 : end - i - 2, System.StringComparison.Ordinal);
                    i = close < 0 ? end : close + 2;
                    continue;
                }

                if (c == '#' && atLineStart)
                {
                    int s = i;
                    while (i < end && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < end && (text[i + 1] == '\n' || text[i + 1] == '\r')) i += 2;
                        else if (text[i] == '/' && i + 1 < end && text[i + 1] == '/') break;
                        else i++;
                    }
                    int e = i;
                    while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
                    tokens.Add(new Token(TokenKind.Preprocessor, s, e - s, text.Substring(s, e - s)));
                    continue;
                }
                atLineStart = false;

                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    // Raw/prefixed string literals: TEXT is a macro, but u8"..", L"..", R"(..)" are literal prefixes.
                    if (i < end && text[i] == '"' && IsStringPrefix(text, s, i))
                    {
                        i = SkipString(text, i, end, text[i - 1] == 'R');
                        tokens.Add(new Token(TokenKind.String, s, i - s, text.Substring(s, i - s)));
                        continue;
                    }
                    tokens.Add(new Token(TokenKind.Identifier, s, i - s, text.Substring(s, i - s)));
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < end && char.IsDigit(text[i + 1])))
                {
                    int s = i;
                    while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '\''
                           || ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E')))) i++;
                    tokens.Add(new Token(TokenKind.Number, s, i - s, text.Substring(s, i - s)));
                    continue;
                }

                if (c == '"')
                {
                    int s = i;
                    i = SkipString(text, i, end, raw: false);
                    tokens.Add(new Token(TokenKind.String, s, i - s, text.Substring(s, i - s)));
                    continue;
                }

                if (c == '\'')
                {
                    int s = i++;
                    while (i < end && text[i] != '\'' && text[i] != '\n')
                    {
                        if (text[i] == '\\') i++;
                        i++;
                    }
                    if (i < end && text[i] == '\'') i++;
                    tokens.Add(new Token(TokenKind.Char, s, i - s, text.Substring(s, i - s)));
                    continue;
                }

                if (c == ':' && i + 1 < end && text[i + 1] == ':')
                {
                    tokens.Add(new Token(TokenKind.Punctuation, i, 2, "::"));
                    i += 2;
                    continue;
                }

                tokens.Add(new Token(TokenKind.Punctuation, i, 1, c.ToString()));
                i++;
            }
            return tokens;
        }

        static bool IsStringPrefix(string text, int start, int end)
        {
            var prefix = text.Substring(start, end - start);
            return prefix == "L" || prefix == "u" || prefix == "U" || prefix == "u8" || prefix == "R"
                   || prefix == "LR" || prefix == "uR" || prefix == "UR" || prefix == "u8R";
        }

        static int SkipString(string text, int i, int end, bool raw)
        {
            if (raw)
            {
                int open = text.IndexOf('(', i);
                if (open < 0 || open >= end) return end;
                var delimiter = ")" + text.Substring(i + 1, open - i - 1) + "\"";
                int close = text.IndexOf(delimiter, open, System.StringComparison.Ordinal);
                return close < 0 || close >= end ? end : close + delimiter.Length;
            }
            i++; // opening quote
            while (i < end && text[i] != '"' && text[i] != '\n')
            {
                if (text[i] == '\\') i++;
                i++;
            }
            if (i < end && text[i] == '"') i++;
            return i > end ? end : i;
        }
    }
}
