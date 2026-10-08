using System;
using System.Collections.Generic;
using UnrealSense.Cpp;

namespace UnrealSense.Navigation
{
    public enum SymbolKind : byte
    {
        Class, Struct, Union, Enum, EnumValue, Function, Field, Variable, Macro, Typedef, Namespace, Delegate,
    }

    public struct Declaration
    {
        public string Name;
        public string Container;
        public SymbolKind Kind;
        public int Line;
    }

    /// <summary>
    /// Extracts declarations (types, functions, fields, enums, macros, typedefs, delegates) from a C++ file for
    /// "Go to symbol". It works on statements at namespace/class scope and skips function bodies, so it is fast
    /// enough to scan the whole engine. It is structural, not semantic: Find Usages uses clangd for that.
    /// </summary>
    public static class DeclarationScanner
    {
        enum ScopeKind { Namespace, Class, Other }

        struct Scope
        {
            public ScopeKind Kind;
            public string Name;
        }

        static readonly HashSet<string> NotFunctionNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "return", "sizeof", "decltype", "static_assert", "alignas", "alignof",
            "noexcept", "requires", "throw", "new", "delete", "catch", "typeid", "co_return", "co_await",
        };

        static readonly HashSet<string> TypeKeywords = new HashSet<string>(StringComparer.Ordinal) { "class", "struct", "union", "enum" };

        static readonly HashSet<string> Qualifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "final", "const", "constexpr", "consteval", "static", "virtual", "inline", "explicit", "mutable", "volatile",
            "extern", "friend", "override", "typename", "unsigned", "signed", "FORCEINLINE", "FORCENOINLINE",
        };

        public static List<Declaration> Scan(string text)
        {
            var result = new List<Declaration>();
            List<Token> tokens;
            try { tokens = CppLexer.Tokenize(text); }
            catch (Exception) { return result; }

            var lines = new LineCounter(text);
            var scopes = new List<Scope> { new Scope { Kind = ScopeKind.Namespace, Name = null } };
            int i = 0;
            while (i < tokens.Count)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.Preprocessor)
                {
                    ScanDirective(t, lines, result);
                    i++;
                    continue;
                }
                if (t.Text == "}")
                {
                    if (scopes.Count > 1) scopes.RemoveAt(scopes.Count - 1);
                    i++;
                    continue;
                }
                if (t.Text == ";") { i++; continue; }

                var scope = scopes[scopes.Count - 1];
                if (scope.Kind == ScopeKind.Other)
                {
                    // Inside something we don't model (extern "C", brace-initializer...): just track braces.
                    if (t.Text == "{") scopes.Add(new Scope { Kind = ScopeKind.Other });
                    i++;
                    continue;
                }

                // Access labels.
                if ((t.Text == "public" || t.Text == "protected" || t.Text == "private") && i + 1 < tokens.Count && tokens[i + 1].Text == ":")
                {
                    i += 2;
                    continue;
                }

                // Gather one statement: up to ';', '{' or '}' at paren depth 0.
                int start = i, depth = 0, end = i;
                for (; end < tokens.Count; end++)
                {
                    var x = tokens[end];
                    if (x.Kind == TokenKind.Preprocessor) continue;
                    if (x.Text == "(" || x.Text == "[") depth++;
                    else if (x.Text == ")" || x.Text == "]") depth = Math.Max(0, depth - 1);
                    else if (depth == 0 && (x.Text == ";" || x.Text == "{" || x.Text == "}")) break;
                }
                if (end >= tokens.Count) break;

                var terminator = tokens[end].Text;
                var container = ContainerName(scopes);
                i = HandleStatement(tokens, start, end, terminator, scope, container, scopes, lines, result);
            }
            return result;
        }

        /// <summary>Handles tokens [start, end) terminated by tokens[end]; returns the next index.</summary>
        static int HandleStatement(List<Token> tokens, int start, int end, string terminator, Scope scope, string container,
            List<Scope> scopes, LineCounter lines, List<Declaration> result)
        {
            // Strip prefixes that don't end a statement: template<...>, UPROPERTY(...)/GENERATED_BODY(), "public:".
            int s = start;
            while (true)
            {
                int before = s;
                s = SkipTemplateHead(tokens, s, end);
                s = SkipLeadingMacroCalls(tokens, s, end, out var delegateName, out int delegateToken);
                if (delegateName != null)
                    Add(result, delegateName, container, SymbolKind.Delegate, lines.LineOf(tokens[delegateToken].Start));
                if (s + 1 < end && (tokens[s].Text == "public" || tokens[s].Text == "protected" || tokens[s].Text == "private") && tokens[s + 1].Text == ":")
                    s += 2;
                if (s == before) break;
            }
            if (s >= end)
                return terminator == "{" ? OpenOther(tokens, end, scopes) : end + 1;

            var first = tokens[s].Text;

            if (first == "friend" || first == "static_assert" || first == "return")
                return terminator == "{" ? SkipBraces(tokens, end) : end + 1;

            if (first == "namespace")
            {
                string name = null;
                for (int k = s + 1; k < end; k++)
                    if (tokens[k].IsIdentifier) name = name == null ? tokens[k].Text : name + "::" + tokens[k].Text;
                if (terminator == "{")
                {
                    if (name != null) Add(result, name, container, SymbolKind.Namespace, lines.LineOf(tokens[s].Start));
                    scopes.Add(new Scope { Kind = ScopeKind.Namespace, Name = name });
                }
                return end + 1;
            }

            if (first == "using" || first == "typedef")
            {
                if (terminator == ";") ScanAlias(tokens, s, end, container, lines, result);
                return terminator == "{" ? OpenOther(tokens, end, scopes) : end + 1;
            }

            int typeKeyword = FindTypeKeyword(tokens, s, end);
            if (typeKeyword >= 0 && terminator == "{")
            {
                var keyword = tokens[typeKeyword].Text;
                int nameIndex = TypeNameIndex(tokens, typeKeyword + 1, end, keyword == "enum");
                var name = nameIndex >= 0 ? tokens[nameIndex].Text : null;
                int line = lines.LineOf(tokens[nameIndex >= 0 ? nameIndex : typeKeyword].Start);

                if (keyword == "enum")
                {
                    if (name != null) Add(result, name, container, SymbolKind.Enum, line);
                    return ScanEnumBody(tokens, end, name ?? container, lines, result);
                }
                if (name != null)
                    Add(result, name, container, keyword == "class" ? SymbolKind.Class : keyword == "struct" ? SymbolKind.Struct : SymbolKind.Union, line);
                scopes.Add(new Scope { Kind = name != null ? ScopeKind.Class : ScopeKind.Other, Name = name });
                return end + 1;
            }

            // Function declaration/definition: identifier right before the first '(' at depth 0.
            int paren = -1, equals = -1;
            int angle = 0;
            for (int k = s; k < end; k++)
            {
                var x = tokens[k].Text;
                if (x == "<") angle++;
                else if (x == ">") angle = Math.Max(0, angle - 1);
                else if (x == "=" && angle == 0 && equals < 0 && !IsOperatorEquals(tokens, k, s, end)) equals = k;
                else if (x == "(" && paren < 0 && angle == 0) paren = k;
            }

            if (paren > s && (equals < 0 || paren < equals))
            {
                int nameIndex = paren - 1;
                string name = null;
                if (tokens[nameIndex].IsIdentifier)
                    name = tokens[nameIndex].Text;
                // operator overloads: "operator==(", "operator()(" ...
                for (int k = paren - 1; k >= s && k >= paren - 3; k--)
                    if (tokens[k].Text == "operator")
                    {
                        name = "operator" + string.Concat(Texts(tokens, k + 1, paren));
                        nameIndex = k;
                        break;
                    }

                if (name != null && !NotFunctionNames.Contains(name) && !IsAllCapsMacro(name))
                {
                    var owner = container;
                    // Out-of-class definition: AFoo::Bar( → container AFoo (strip ~ for destructors)
                    if (nameIndex >= 2 && tokens[nameIndex - 1].Text == "::" && tokens[nameIndex - 2].IsIdentifier)
                        owner = tokens[nameIndex - 2].Text;
                    else if (nameIndex >= 3 && tokens[nameIndex - 1].Text == "~" && tokens[nameIndex - 2].Text == "::")
                    {
                        owner = tokens[nameIndex - 3].Text;
                        name = "~" + name;
                    }
                    else if (nameIndex >= 1 && tokens[nameIndex - 1].Text == "~")
                    {
                        name = "~" + name;
                    }
                    Add(result, name, owner, SymbolKind.Function, lines.LineOf(tokens[nameIndex].Start));
                }
                return terminator == "{" ? SkipBraces(tokens, end) : end + 1;
            }

            if (terminator == ";")
            {
                // Field or variable: last identifier before '=', '[', ':' (bit field) or the end.
                int limit = equals >= 0 ? equals : end;
                int nameIndex = -1;
                angle = 0;
                for (int k = s; k < limit; k++)
                {
                    var x = tokens[k].Text;
                    if (x == "<") angle++;
                    else if (x == ">") angle = Math.Max(0, angle - 1);
                    else if (angle == 0 && (x == "[" || x == ":")) break;
                    else if (angle == 0 && tokens[k].IsIdentifier && !Qualifiers.Contains(x) && !TypeKeywords.Contains(x)) nameIndex = k;
                }
                // Need at least "Type Name": a lone identifier is a macro invocation or an expression.
                if (nameIndex > s && (typeKeyword < 0 || nameIndex > typeKeyword + 1))
                {
                    var name = tokens[nameIndex].Text;
                    if (!IsAllCapsMacro(name) && tokens[nameIndex - 1].Text != "::")
                        Add(result, name, container, scope.Kind == ScopeKind.Class ? SymbolKind.Field : SymbolKind.Variable, lines.LineOf(tokens[nameIndex].Start));
                }
                return end + 1;
            }

            return terminator == "{" ? OpenOther(tokens, end, scopes) : end + 1;
        }

        /// <summary>'=' that belongs to an operator name or a comparison (operator==, !=, <=, >=), not an initializer.</summary>
        static bool IsOperatorEquals(List<Token> tokens, int k, int s, int end)
        {
            var prev = k > s ? tokens[k - 1].Text : null;
            var next = k + 1 < end ? tokens[k + 1].Text : null;
            if (prev == "=" || prev == "!" || prev == "<" || prev == ">" || next == "=") return true;
            for (int i = k - 1; i >= s && i >= k - 3; i--)
                if (tokens[i].Text == "operator") return true;
            return false;
        }

        static IEnumerable<string> Texts(List<Token> tokens, int from, int to)
        {
            for (int k = from; k < to; k++) yield return tokens[k].Text;
        }

        static int OpenOther(List<Token> tokens, int brace, List<Scope> scopes)
        {
            // Brace initializers of fields ("int32 X{0};") and extern "C" blocks are skipped wholesale.
            return SkipBraces(tokens, brace);
        }

        /// <summary>Index after the brace block opened at <paramref name="open"/> (and a trailing ';').</summary>
        static int SkipBraces(List<Token> tokens, int open)
        {
            int depth = 0;
            for (int k = open; k < tokens.Count; k++)
            {
                var x = tokens[k].Text;
                if (x == "{") depth++;
                else if (x == "}" && --depth == 0)
                    return k + 1 < tokens.Count && tokens[k + 1].Text == ";" ? k + 2 : k + 1;
            }
            return tokens.Count;
        }

        static int SkipTemplateHead(List<Token> tokens, int s, int end)
        {
            while (s < end && tokens[s].Text == "template" && s + 1 < end && tokens[s + 1].Text == "<")
            {
                int depth = 0, k = s + 1;
                for (; k < end; k++)
                {
                    if (tokens[k].Text == "<") depth++;
                    else if (tokens[k].Text == ">" && --depth == 0) break;
                }
                s = k + 1;
            }
            return s;
        }

        /// <summary>Skips UCLASS(...), UPROPERTY(...), UE_DEPRECATED(...) etc.; reports DECLARE_*DELEGATE names.</summary>
        static int SkipLeadingMacroCalls(List<Token> tokens, int s, int end, out string delegateName, out int delegateToken)
        {
            delegateName = null;
            delegateToken = -1;
            while (s + 1 < end && tokens[s].IsIdentifier && (IsAllCapsMacro(tokens[s].Text) || tokens[s].Text.StartsWith("DECLARE_", StringComparison.Ordinal)) && tokens[s + 1].Text == "(")
            {
                var macro = tokens[s].Text;
                int depth = 0, k = s + 1, argIndex = 0;
                int wantedArg = macro.StartsWith("DECLARE_", StringComparison.Ordinal) && (macro.Contains("DELEGATE") || macro.Contains("EVENT"))
                    ? (macro.Contains("_RetVal") || macro.StartsWith("DECLARE_EVENT", StringComparison.Ordinal) || macro.Contains("SPARSE") ? 1 : 0)
                    : -1;
                string lastIdentInArg = null;
                int lastIdentIndex = -1;
                for (; k < end; k++)
                {
                    var x = tokens[k].Text;
                    if (x == "(") { depth++; continue; }
                    if (x == ")")
                    {
                        if (--depth == 0) break;
                        continue;
                    }
                    if (depth == 1 && x == ",")
                    {
                        if (argIndex == wantedArg && lastIdentInArg != null) { delegateName = lastIdentInArg; delegateToken = lastIdentIndex; }
                        argIndex++;
                        lastIdentInArg = null;
                        continue;
                    }
                    if (depth == 1 && tokens[k].IsIdentifier) { lastIdentInArg = x; lastIdentIndex = k; }
                }
                if (argIndex == wantedArg && lastIdentInArg != null && delegateName == null) { delegateName = lastIdentInArg; delegateToken = lastIdentIndex; }
                s = k + 1;
            }
            return s;
        }

        static bool IsAllCapsMacro(string name)
        {
            if (name.Length < 2) return false;
            bool hasLetter = false;
            foreach (var c in name)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) hasLetter = true;
            }
            return hasLetter;
        }

        static int FindTypeKeyword(List<Token> tokens, int s, int end)
        {
            int depth = 0;
            for (int k = s; k < end; k++)
            {
                var x = tokens[k].Text;
                if (x == "(" || x == "<") depth++;
                else if (x == ")" || x == ">") depth = Math.Max(0, depth - 1);
                else if (depth == 0 && TypeKeywords.Contains(x))
                    return k;
                else if (depth == 0 && (x == "=" || x == "::")) return -1; // "auto x = struct..." / "Foo::Bar" etc.
            }
            return -1;
        }

        static int TypeNameIndex(List<Token> tokens, int from, int end, bool isEnum)
        {
            int nameIndex = -1;
            for (int k = from; k < end; k++)
            {
                var t = tokens[k];
                if (t.Text == ":" ) break;
                if (t.Text == "(") { k = MatchParen(tokens, k, end); continue; }
                if (!t.IsIdentifier) continue;
                if (t.Text == "final" || t.Text == "class" || t.Text == "struct" || t.Text.EndsWith("_API", StringComparison.Ordinal)) continue;
                nameIndex = k;
            }
            return nameIndex;
        }

        static int MatchParen(List<Token> tokens, int open, int end)
        {
            int depth = 0;
            for (int k = open; k < end; k++)
            {
                if (tokens[k].Text == "(") depth++;
                else if (tokens[k].Text == ")" && --depth == 0) return k;
            }
            return end;
        }

        static int ScanEnumBody(List<Token> tokens, int open, string enumName, LineCounter lines, List<Declaration> result)
        {
            bool expectName = true;
            int depth = 0;
            for (int k = open; k < tokens.Count; k++)
            {
                var t = tokens[k];
                if (t.Text == "{" || t.Text == "(") { depth++; continue; }
                if (t.Text == "}" || t.Text == ")")
                {
                    depth--;
                    if (depth == 0)
                        return k + 1 < tokens.Count && tokens[k + 1].Text == ";" ? k + 2 : k + 1;
                    continue;
                }
                if (depth != 1) continue;
                if (t.Text == ",") { expectName = true; continue; }
                if (expectName && t.IsIdentifier)
                {
                    Add(result, t.Text, enumName, SymbolKind.EnumValue, lines.LineOf(t.Start));
                    expectName = false;
                }
            }
            return tokens.Count;
        }

        static void ScanAlias(List<Token> tokens, int s, int end, string container, LineCounter lines, List<Declaration> result)
        {
            if (tokens[s].Text == "using")
            {
                // using Name = Type;   (skip "using namespace X;" and "using Base::Member;")
                if (s + 2 < end && tokens[s + 1].IsIdentifier && tokens[s + 2].Text == "=")
                    Add(result, tokens[s + 1].Text, container, SymbolKind.Typedef, lines.LineOf(tokens[s + 1].Start));
                return;
            }
            // typedef ... Name;  or  typedef Ret (*Name)(Args);
            for (int k = s + 1; k + 1 < end; k++)
                if (tokens[k].Text == "*" && tokens[k + 1].IsIdentifier && k > 0 && tokens[k - 1].Text == "(")
                {
                    Add(result, tokens[k + 1].Text, container, SymbolKind.Typedef, lines.LineOf(tokens[k + 1].Start));
                    return;
                }
            for (int k = end - 1; k > s; k--)
                if (tokens[k].IsIdentifier)
                {
                    Add(result, tokens[k].Text, container, SymbolKind.Typedef, lines.LineOf(tokens[k].Start));
                    return;
                }
        }

        static void ScanDirective(Token t, LineCounter lines, List<Declaration> result)
        {
            var text = t.Text;
            int i = 1;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            if (string.CompareOrdinal(text, i, "define", 0, 6) != 0) return;
            i += 6;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            int s = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i > s)
                Add(result, text.Substring(s, i - s), null, SymbolKind.Macro, lines.LineOf(t.Start));
        }

        static string ContainerName(List<Scope> scopes)
        {
            string name = null;
            for (int k = 1; k < scopes.Count; k++)
                if (scopes[k].Name != null)
                    name = name == null ? scopes[k].Name : name + "::" + scopes[k].Name;
            return name;
        }

        static void Add(List<Declaration> result, string name, string container, SymbolKind kind, int line) =>
            result.Add(new Declaration { Name = name, Container = container, Kind = kind, Line = line });

        /// <summary>Offset → 0-based line, for increasing offsets (amortized O(1)).</summary>
        sealed class LineCounter
        {
            readonly string text;
            int position;
            int line;

            public LineCounter(string text) => this.text = text;

            public int LineOf(int offset)
            {
                if (offset < position) { position = 0; line = 0; }
                for (; position < offset && position < text.Length; position++)
                    if (text[position] == '\n') line++;
                return line;
            }
        }
    }
}
