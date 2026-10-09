using System;
using System.Collections.Generic;

namespace UnrealSense.Cpp
{
    /// <summary>Syntax colour of a piece of a code line, mapped to the editor's classifications by the VSIX.</summary>
    public enum HighlightKind { Plain, Keyword, Comment, String, Number, Preprocessor, Macro, Type, Namespace, Function, Member }

    public readonly struct HighlightSpan
    {
        public HighlightSpan(int start, int length, HighlightKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        public int Start { get; }
        public int Length { get; }
        public HighlightKind Kind { get; }

        public override string ToString() => $"{Kind}@{Start}+{Length}";
    }

    /// <summary>
    /// Colours a single line of C++ for result previews (Find Usages) where the file is usually not open, so the
    /// editor's classifier cannot be asked. Lexical, plus the Unreal naming convention (U/A/F/E/T/I/S + capital
    /// letter is a type, ALL_CAPS is a macro) and the token after an identifier ('(' call, '::' scope, '.'/'->'
    /// member): close to what VS shows for these lines without semantic analysis.
    /// </summary>
    public static class LineHighlighter
    {
        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t",
            "char32_t", "class", "co_await", "co_return", "co_yield", "concept", "const", "consteval", "constexpr",
            "constinit", "const_cast", "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast",
            "else", "enum", "explicit", "export", "extern", "false", "final", "float", "for", "friend", "goto", "if",
            "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not", "nullptr", "operator", "or",
            "override", "private", "protected", "public", "register", "reinterpret_cast", "requires", "return",
            "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template",
            "this", "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned",
            "using", "virtual", "void", "volatile", "wchar_t", "while", "__forceinline", "__declspec", "__int64",
        };

        // Unreal's fixed-size typedefs read as types, not as identifiers or (TCHAR) macros.
        static readonly HashSet<string> UnrealTypedefs = new HashSet<string>(StringComparer.Ordinal)
        {
            "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "TCHAR", "ANSICHAR", "WIDECHAR",
            "UTF8CHAR", "UCS2CHAR", "SIZE_T", "SSIZE_T", "PTRINT", "UPTRINT", "size_t", "ptrdiff_t", "nullptr_t",
        };

        /// <summary>
        /// Spans of <paramref name="line"/> that are not plain text, ordered and non-overlapping.
        /// <paramref name="isKnownType"/> (optional) recognises project types that do not follow the prefix convention.
        /// </summary>
        public static List<HighlightSpan> Highlight(string line, Func<string, bool> isKnownType = null)
        {
            var spans = new List<HighlightSpan>();
            if (string.IsNullOrEmpty(line)) return spans;
            HighlightRange(line, 0, line.Length, isKnownType, spans);
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            return spans;
        }

        static void HighlightRange(string line, int from, int to, Func<string, bool> isKnownType, List<HighlightSpan> spans)
        {
            var tokens = CppLexer.Tokenize(line, from, to);

            // The lexer skips comments: any non-blank gap between tokens is one. A line inside a block comment that
            // does not start with "/*" (" * text") cannot be told apart from code here.
            int previousEnd = from;
            for (int t = 0; t <= tokens.Count; t++)
            {
                int gapEnd = t < tokens.Count ? tokens[t].Start : to;
                AddComment(line, previousEnd, gapEnd, spans);
                if (t < tokens.Count) previousEnd = tokens[t].End;
            }

            for (int t = 0; t < tokens.Count; t++)
            {
                var token = tokens[t];
                switch (token.Kind)
                {
                    case TokenKind.String:
                    case TokenKind.Char:
                        spans.Add(new HighlightSpan(token.Start, token.Length, HighlightKind.String));
                        break;
                    case TokenKind.Number:
                        spans.Add(new HighlightSpan(token.Start, token.Length, HighlightKind.Number));
                        break;
                    case TokenKind.Preprocessor:
                        HighlightDirective(line, token, isKnownType, spans);
                        break;
                    case TokenKind.Identifier:
                        var kind = ClassifyIdentifier(line, token.Text, t > 0 ? tokens[t - 1] : (Token?)null,
                            t + 1 < tokens.Count ? tokens[t + 1] : (Token?)null, t + 2 < tokens.Count ? tokens[t + 2] : (Token?)null, isKnownType);
                        if (kind != HighlightKind.Plain) spans.Add(new HighlightSpan(token.Start, token.Length, kind));
                        break;
                }
            }
        }

        static void AddComment(string line, int from, int to, List<HighlightSpan> spans)
        {
            while (from < to && char.IsWhiteSpace(line[from])) from++;
            while (to > from && char.IsWhiteSpace(line[to - 1])) to--;
            if (to > from) spans.Add(new HighlightSpan(from, to - from, HighlightKind.Comment));
        }

        // "#include "Foo.h"": the directive name is a preprocessor keyword, the rest is ordinary code.
        static void HighlightDirective(string line, Token token, Func<string, bool> isKnownType, List<HighlightSpan> spans)
        {
            int i = token.Start + 1;
            while (i < token.End && char.IsWhiteSpace(line[i])) i++;
            while (i < token.End && (char.IsLetter(line[i]) || line[i] == '_')) i++;
            spans.Add(new HighlightSpan(token.Start, i - token.Start, HighlightKind.Preprocessor));
            if (i >= token.End) return;

            int rest = spans.Count;
            HighlightRange(line, i, token.End, isKnownType, spans);
            // <Path/File.h> is a string to the eye; the lexer sees punctuation and identifiers.
            int open = line.IndexOf('<', i, token.End - i);
            if (open >= 0 && line.Substring(token.Start, i - token.Start).TrimEnd().EndsWith("include", StringComparison.Ordinal))
            {
                int close = line.IndexOf('>', open, token.End - open);
                int end = close < 0 ? token.End : close + 1;
                for (int s = spans.Count - 1; s >= rest; s--)
                    if (spans[s].Start >= open && spans[s].Start < end) spans.RemoveAt(s);
                spans.Add(new HighlightSpan(open, end - open, HighlightKind.String));
            }
        }

        static HighlightKind ClassifyIdentifier(string line, string text, Token? previous, Token? next, Token? afterNext, Func<string, bool> isKnownType)
        {
            if (Keywords.Contains(text)) return HighlightKind.Keyword;
            if (UnrealTypedefs.Contains(text)) return HighlightKind.Type;

            bool isType = LooksLikeUnrealType(text) || (isKnownType != null && isKnownType(text));
            bool member = previous.HasValue && (previous.Value.Is(".") || (previous.Value.Is(">") && IsArrow(line, previous.Value)));
            bool call = next.HasValue && next.Value.Is("(");

            if (next.HasValue && next.Value.Is("::")) return isType ? HighlightKind.Type : HighlightKind.Namespace;
            if (isType && !member) return HighlightKind.Type;
            if (IsAllCaps(text) && !member) return HighlightKind.Macro;
            if (call) return HighlightKind.Function;
            // Template call "Cast<AActor>(x)" / "GetDefault<U>()": a lowercase-led name before '<' is a template, not a comparison.
            if (next.HasValue && next.Value.Is("<") && afterNext.HasValue && afterNext.Value.IsIdentifier && LooksLikeUnrealType(afterNext.Value.Text))
                return HighlightKind.Function;
            if (member) return HighlightKind.Member;
            return HighlightKind.Plain;
        }

        // The lexer emits "->" as '-' then '>', so the '>' is an arrow when the character before it is '-'.
        static bool IsArrow(string line, Token gt) => gt.Start >= 1 && line[gt.Start - 1] == '-';

        /// <summary>The Unreal naming convention: a type prefix letter followed by an upper-case letter (UObject, FName, TArray, EKind).</summary>
        public static bool LooksLikeUnrealType(string text)
        {
            if (text.Length < 2 || !char.IsUpper(text[1])) return false;
            switch (text[0])
            {
                case 'U': case 'A': case 'F': case 'E': case 'T': case 'I': case 'S':
                    // "FOO_BAR" and "TEXT" are macros, not types.
                    return !IsAllCaps(text);
                default:
                    return false;
            }
        }

        static bool IsAllCaps(string text)
        {
            if (text.Length < 2) return false;
            bool letter = false;
            foreach (var c in text)
            {
                if (char.IsLower(c)) return false;
                if (char.IsLetter(c)) letter = true;
            }
            return letter;
        }
    }
}
