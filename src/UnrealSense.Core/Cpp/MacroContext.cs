using System;
using System.Collections.Generic;
using UnrealSense.Reflection;

namespace UnrealSense.Cpp
{
    /// <summary>Where the caret is relative to a reflection macro, e.g. UPROPERTY(Edit|) or meta=(Clamp|).</summary>
    public sealed class MacroContext
    {
        public string MacroName { get; set; }
        public SpecifierTarget Target => SpecifierCatalog.TargetForMacro(MacroName);
        public int MacroStart { get; set; }

        /// <summary>Caret is inside meta=( ... ).</summary>
        public bool InMeta { get; set; }

        /// <summary>Caret is after '=' (typing a value); <see cref="Key"/> is the specifier being assigned.</summary>
        public bool IsValue { get; set; }
        public string Key { get; set; }

        /// <summary>Caret is inside a string literal value.</summary>
        public bool InString { get; set; }

        /// <summary>Span of the identifier (or string content) being typed; length 0 when empty.</summary>
        public int PrefixStart { get; set; }
        public int PrefixLength { get; set; }

        /// <summary>Keys already written at the current level, so completion can skip them.</summary>
        public HashSet<string> UsedKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const int MaxLookBehind = 4000;

        public static MacroContext Find(string text, int caret)
        {
            if (caret < 0 || caret > text.Length) return null;
            int windowStart = Math.Max(0, caret - MaxLookBehind);
            // Start at a line boundary so preprocessor/comment state is sane.
            if (windowStart > 0)
            {
                int nl = text.IndexOf('\n', windowStart);
                windowStart = nl < 0 || nl >= caret ? windowStart : nl + 1;
            }

            var tokens = CppLexer.Tokenize(text, windowStart, caret);

            // Stack of open parentheses: label is the macro name, "meta", or null for any other '('.
            var stack = new List<(string Label, int TokenIndex)>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind != TokenKind.Punctuation) continue;
                if (t.Text == "(")
                {
                    string label = null;
                    if (i > 0 && tokens[i - 1].IsIdentifier && Array.IndexOf(SpecifierCatalog.MacroNames, tokens[i - 1].Text) >= 0)
                        label = tokens[i - 1].Text;
                    else if (i > 1 && tokens[i - 1].Text == "=" && string.Equals(tokens[i - 2].Text, "meta", StringComparison.OrdinalIgnoreCase)
                             && stack.Count > 0 && stack[stack.Count - 1].Label != null && stack[stack.Count - 1].Label != "meta")
                        label = "meta";
                    stack.Add((label, i));
                }
                else if (t.Text == ")" && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (t.Text == ";" || t.Text == "{" || t.Text == "}")
                {
                    stack.Clear(); // statement boundary: no macro can still be open
                }
            }

            if (stack.Count == 0) return null;
            var top = stack[stack.Count - 1];
            if (top.Label == null) return null;

            var context = new MacroContext { InMeta = top.Label == "meta" };
            if (context.InMeta)
            {
                if (stack.Count < 2 || stack[stack.Count - 2].Label == null) return null;
                context.MacroName = stack[stack.Count - 2].Label;
            }
            else
            {
                context.MacroName = top.Label;
            }
            var macroToken = tokens[(context.InMeta ? stack[stack.Count - 2].TokenIndex : top.TokenIndex) - 1];
            context.MacroStart = macroToken.Start;

            // Walk the segments at the current level.
            int depth = 0;
            int segmentStart = top.TokenIndex + 1;
            for (int i = top.TokenIndex + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Text == "(") depth++;
                else if (t.Text == ")") depth--;
                else if (t.Text == "," && depth == 0)
                {
                    if (segmentStart < i && tokens[segmentStart].IsIdentifier)
                        context.UsedKeys.Add(tokens[segmentStart].Text);
                    segmentStart = i + 1;
                }
            }
            if (depth != 0) return null; // inside a nested value like ClampMin=(...)

            context.PrefixStart = caret;
            int eq = -1;
            for (int i = segmentStart; i < tokens.Count; i++)
                if (tokens[i].Text == "=") { eq = i; break; }

            if (eq >= 0)
            {
                context.IsValue = true;
                context.Key = tokens[segmentStart].Text;
                if (eq + 1 < tokens.Count)
                {
                    var last = tokens[tokens.Count - 1];
                    if (last.Kind == TokenKind.String && last.End == caret && !IsClosedString(last.Text))
                    {
                        context.InString = true;
                        context.PrefixStart = last.Start + 1;
                    }
                    else if (last.Kind == TokenKind.String && last.End == caret)
                    {
                        return null; // caret right after a closing quote
                    }
                    else if (last.IsIdentifier && last.End == caret)
                    {
                        context.PrefixStart = last.Start;
                    }
                }
            }
            else if (segmentStart < tokens.Count)
            {
                var last = tokens[tokens.Count - 1];
                if (last.IsIdentifier && last.End == caret && tokens.Count - 1 == segmentStart)
                    context.PrefixStart = last.Start;
                else if (tokens.Count - 1 >= segmentStart && !(last.IsIdentifier && last.End == caret))
                    return null; // e.g. "EditAnywhere |" — a key is complete, waiting for ',' or ')'
            }
            context.PrefixLength = caret - context.PrefixStart;
            return context;
        }

        static bool IsClosedString(string literal)
        {
            int quote = literal.IndexOf('"');
            return quote >= 0 && literal.Length - quote >= 2 && literal[literal.Length - 1] == '"';
        }
    }
}
