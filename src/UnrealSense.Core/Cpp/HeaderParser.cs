using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnrealSense.Cpp
{
    /// <summary>Result of parsing one header/source file for Unreal reflection.</summary>
    public sealed class ParsedFile
    {
        int[] lineStarts;

        public string FilePath { get; set; }
        public string Text { get; set; }
        public List<IncludeDirective> Includes { get; } = new List<IncludeDirective>();
        public List<ReflectedType> Types { get; } = new List<ReflectedType>();
        public List<DelegateDeclaration> Delegates { get; } = new List<DelegateDeclaration>();

        public IncludeDirective GeneratedInclude => Includes.FirstOrDefault(i => i.Path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase));

        public bool HasReflectedTypes => Types.Count > 0 || Delegates.Any(d => d.IsDynamic);

        public IEnumerable<ReflectedMember> AllMembers =>
            Types.SelectMany(t => t.Functions.Cast<ReflectedMember>().Concat(t.Properties));

        public int GetLine(int offset)
        {
            if (lineStarts == null)
            {
                var starts = new List<int> { 0 };
                for (int i = 0; i < Text.Length; i++)
                    if (Text[i] == '\n') starts.Add(i + 1);
                lineStarts = starts.ToArray();
            }
            int index = Array.BinarySearch(lineStarts, offset);
            return index >= 0 ? index : ~index - 1;
        }
    }

    /// <summary>
    /// Structural parser for Unreal headers. It is not a C++ compiler front-end: it understands exactly the
    /// subset UHT understands (reflection macros, class heads, member declarations) and tolerates the rest.
    /// </summary>
    public static class HeaderParser
    {
        static readonly HashSet<string> TypeMacros = new HashSet<string> { "UCLASS", "USTRUCT", "UENUM", "UINTERFACE" };
        static readonly HashSet<string> GeneratedBodyMacros = new HashSet<string>
        {
            "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY", "GENERATED_IINTERFACE_BODY", "GENERATED_UINTERFACE_BODY",
        };
        static readonly Regex IncludeRegex = new Regex(@"^#\s*include\s*(?<q>[""<])(?<path>[^"">]+)["">]", RegexOptions.Compiled);
        static readonly Regex DelegateMacroRegex = new Regex(@"^DECLARE_(DYNAMIC_)?(MULTICAST_)?(SPARSE_)?(DELEGATE|EVENT)", RegexOptions.Compiled);

        public static ParsedFile Parse(string text, string filePath = null)
        {
            var file = new ParsedFile { FilePath = filePath, Text = text };
            var tokens = CppLexer.Tokenize(text);
            ReflectedType pendingInterface = null;

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];

                // UINTERFACE() class UFoo {...}; class IFoo {...}: the I-class carries the UFUNCTIONs.
                if (pendingInterface != null && t.Text == "class")
                {
                    var native = ParseTypeHead(tokens, i, pendingInterface.Macro, out int next);
                    if (native == null) continue; // forward declaration in between
                    if (native.Name == "I" + pendingInterface.Name.Substring(1))
                    {
                        native.Kind = ReflectedKind.Interface;
                        native.IsNativeInterfaceClass = true;
                        file.Types.Add(native);
                        if (native.BodyOpen >= 0) ParseBody(text, tokens, native);
                        i = Math.Max(i, next - 1);
                    }
                    pendingInterface = null;
                    continue;
                }

                if (t.Kind == TokenKind.Preprocessor)
                {
                    var m = IncludeRegex.Match(t.Text);
                    if (m.Success)
                        file.Includes.Add(new IncludeDirective { Path = m.Groups["path"].Value, IsAngled = m.Groups["q"].Value == "<", Start = t.Start, Length = t.Length });
                    continue;
                }
                if (!t.IsIdentifier) continue;

                if (TypeMacros.Contains(t.Text) && Next(tokens, i, "("))
                {
                    var macro = ParseMacro(text, tokens, i, out int after);
                    var type = ParseTypeHead(tokens, after, macro, out int resume);
                    if (type != null)
                    {
                        file.Types.Add(type);
                        if (type.BodyOpen >= 0)
                            ParseBody(text, tokens, type);
                        if (type.Kind == ReflectedKind.Interface && type.Name?.Length > 1)
                            pendingInterface = type;
                    }
                    i = Math.Max(i, resume - 1);
                    continue;
                }

                if (t.Text.StartsWith("DECLARE_", StringComparison.Ordinal) && DelegateMacroRegex.IsMatch(t.Text) && Next(tokens, i, "("))
                {
                    var args = SplitArgs(tokens, i + 1, out _);
                    // DECLARE_*_RetVal*(RetType, Name, ...) puts the name second; DECLARE_EVENT(Owner, Name).
                    int nameArg = t.Text.Contains("_RetVal") || t.Text.StartsWith("DECLARE_EVENT", StringComparison.Ordinal) || t.Text.Contains("SPARSE") ? 1 : 0;
                    if (args.Count > nameArg && args[nameArg].Count > 0)
                        file.Delegates.Add(new DelegateDeclaration { Macro = t.Text, Name = args[nameArg].Last().Text, Start = t.Start });
                }
            }
            return file;
        }

        static bool Next(List<Token> tokens, int i, string text) => i + 1 < tokens.Count && tokens[i + 1].Text == text;

        /// <summary>Index of the token closing the bracket opened at <paramref name="open"/>, or tokens.Count.</summary>
        static int MatchClose(List<Token> tokens, int open)
        {
            string o = tokens[open].Text, c = o == "(" ? ")" : o == "{" ? "}" : o == "[" ? "]" : ">";
            int depth = 0;
            for (int i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.Punctuation) continue;
                if (tokens[i].Text == o) depth++;
                else if (tokens[i].Text == c && --depth == 0) return i;
            }
            return tokens.Count;
        }

        /// <summary>Splits "( a, b(c, d), e )" at top-level commas. <paramref name="close"/> receives the ')' index.</summary>
        static List<List<Token>> SplitArgs(List<Token> tokens, int open, out int close)
        {
            close = MatchClose(tokens, open);
            var result = new List<List<Token>>();
            var current = new List<Token>();
            int depth = 0;
            for (int i = open + 1; i < close && i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.Punctuation)
                {
                    if (t.Text == "(" || t.Text == "{" || t.Text == "[" || t.Text == "<") depth++;
                    else if (t.Text == ")" || t.Text == "}" || t.Text == "]" || t.Text == ">") depth--;
                    else if (t.Text == "," && depth == 0)
                    {
                        result.Add(current);
                        current = new List<Token>();
                        continue;
                    }
                }
                current.Add(t);
            }
            if (current.Count > 0 || result.Count > 0) result.Add(current);
            return result;
        }

        public static ReflectionMacro ParseMacro(string text, List<Token> tokens, int nameIndex, out int afterIndex)
        {
            var macro = new ReflectionMacro { Name = tokens[nameIndex].Text, Start = tokens[nameIndex].Start, OpenParen = tokens[nameIndex + 1].Start };
            var args = SplitArgs(tokens, nameIndex + 1, out int close);
            macro.CloseParen = close < tokens.Count ? tokens[close].Start : text.Length;
            afterIndex = close + 1;
            foreach (var arg in args)
                AddSpecifier(text, macro, arg, isMeta: false);
            return macro;
        }

        static void AddSpecifier(string text, ReflectionMacro macro, List<Token> arg, bool isMeta)
        {
            if (arg.Count == 0) return;
            var spec = new Specifier
            {
                Key = arg[0].Text,
                KeyStart = arg[0].Start,
                Start = arg[0].Start,
                End = arg[arg.Count - 1].End,
                IsMeta = isMeta,
            };
            int eq = arg.FindIndex(t => t.Text == "=");
            if (eq >= 0 && eq + 1 < arg.Count)
            {
                spec.ValueStart = arg[eq + 1].Start;
                spec.ValueEnd = arg[arg.Count - 1].End;
                var raw = text.Substring(spec.ValueStart, spec.ValueEnd - spec.ValueStart);

                if (!isMeta && string.Equals(spec.Key, "meta", StringComparison.OrdinalIgnoreCase) && arg[eq + 1].Text == "(")
                {
                    macro.MetaOpenParen = arg[eq + 1].Start;
                    var inner = new List<Token>();
                    int depth = 0;
                    for (int i = eq + 1; i < arg.Count; i++)
                    {
                        var t = arg[i];
                        if (t.Text == "(" && depth++ == 0) continue;
                        if (t.Text == ")" && --depth == 0) { macro.MetaCloseParen = t.Start; break; }
                        if (t.Text == "," && depth == 1)
                        {
                            AddSpecifier(text, macro, inner, isMeta: true);
                            inner = new List<Token>();
                            continue;
                        }
                        inner.Add(t);
                    }
                    AddSpecifier(text, macro, inner, isMeta: true);
                    spec.Value = raw;
                }
                else
                {
                    spec.Value = Unquote(raw);
                }
            }
            else if (eq >= 0)
            {
                spec.Value = string.Empty;
            }

            if (isMeta) macro.Meta.Add(spec);
            else macro.Specifiers.Add(spec);
        }

        static string Unquote(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("TEXT(", StringComparison.Ordinal) && raw.EndsWith(")"))
                raw = raw.Substring(5, raw.Length - 6).Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        static ReflectedType ParseTypeHead(List<Token> tokens, int index, ReflectionMacro macro, out int resume)
        {
            resume = index;
            if (index >= tokens.Count) return null;

            // Skip to the class/struct/enum keyword (there can be template<>, attributes, etc. in between).
            int k = index;
            while (k < tokens.Count && k < index + 8 && !(tokens[k].Text == "class" || tokens[k].Text == "struct" || tokens[k].Text == "enum"))
                k++;
            if (k >= tokens.Count || k >= index + 8) return null;

            var type = new ReflectedType { Macro = macro, Keyword = tokens[k].Text };
            switch (macro.Name)
            {
                case "UCLASS": type.Kind = ReflectedKind.Class; break;
                case "USTRUCT": type.Kind = ReflectedKind.Struct; break;
                case "UENUM": type.Kind = ReflectedKind.Enum; break;
                default: type.Kind = ReflectedKind.Interface; break;
            }

            int i = k + 1;
            if (type.Keyword == "enum" && i < tokens.Count && (tokens[i].Text == "class" || tokens[i].Text == "struct")) i++;

            // Find '{', ':' or ';' that ends the head; the name is the last plain identifier before it.
            int nameIndex = -1;
            for (; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Text == "{" || t.Text == ";" || t.Text == ":") break;
                if (t.Text == "(") { i = MatchClose(tokens, i); continue; } // alignas(..), UE_DEPRECATED(..)
                if (t.IsIdentifier && t.Text != "final")
                {
                    if (t.Text.EndsWith("_API", StringComparison.Ordinal)) type.ApiMacro = t.Text;
                    else nameIndex = i;
                }
            }
            if (nameIndex < 0 || i >= tokens.Count) { resume = i; return null; }
            type.Name = tokens[nameIndex].Text;
            type.NameStart = tokens[nameIndex].Start;

            if (tokens[i].Text == ";") { resume = i + 1; return null; } // forward declaration

            if (tokens[i].Text == ":")
            {
                var current = new StringBuilder();
                for (i++; i < tokens.Count && tokens[i].Text != "{" && tokens[i].Text != ";"; i++)
                {
                    var t = tokens[i];
                    if (t.Text == ",")
                    {
                        if (current.Length > 0) type.BaseTypes.Add(current.ToString());
                        current.Clear();
                    }
                    else if (t.Text == "public" || t.Text == "protected" || t.Text == "private" || t.Text == "virtual") { }
                    else current.Append(t.Text);
                }
                if (current.Length > 0) type.BaseTypes.Add(current.ToString());
            }

            if (i >= tokens.Count || tokens[i].Text != "{") { resume = i; return type; }

            int close = MatchClose(tokens, i);
            type.BodyOpen = i;     // token indexes for now, converted by ParseBody
            type.BodyClose = close;
            resume = close + 1;
            return type;
        }

        static void ParseBody(string text, List<Token> tokens, ReflectedType type)
        {
            int open = type.BodyOpen, close = Math.Min(type.BodyClose, tokens.Count);
            type.BodyOpen = tokens[open].Start;
            type.BodyClose = close < tokens.Count ? tokens[close].Start : text.Length;

            if (type.Kind == ReflectedKind.Enum)
            {
                foreach (var arg in SplitArgsRange(tokens, open + 1, close))
                    if (arg.Count > 0 && arg[0].IsIdentifier)
                        type.EnumValues.Add(arg[0].Text);
                return;
            }

            var access = type.Keyword == "struct" ? AccessLevel.Public : AccessLevel.Private;
            for (int i = open + 1; i < close; i++)
            {
                var t = tokens[i];

                if (t.Text == "{") { i = MatchClose(tokens, i); continue; } // nested type / inline function body
                if (t.Text == "(") { i = MatchClose(tokens, i); continue; }
                if (!t.IsIdentifier) continue;

                if ((t.Text == "public" || t.Text == "protected" || t.Text == "private") && i + 1 < close && tokens[i + 1].Text == ":")
                {
                    access = t.Text == "public" ? AccessLevel.Public : t.Text == "protected" ? AccessLevel.Protected : AccessLevel.Private;
                    i++;
                    continue;
                }

                if (GeneratedBodyMacros.Contains(t.Text))
                {
                    type.GeneratedBodyMacro = t.Text;
                    type.GeneratedBodyStart = t.Start;
                    // GENERATED_UCLASS_BODY etc. switch the default access to public.
                    if (t.Text != "GENERATED_BODY") access = AccessLevel.Public;
                    if (Next(tokens, i, "(")) i = MatchClose(tokens, i + 1);
                    continue;
                }

                if ((t.Text == "UPROPERTY" || t.Text == "UFUNCTION") && Next(tokens, i, "("))
                {
                    var macro = ParseMacro(text, tokens, i, out int after);
                    int end = FindDeclarationEnd(tokens, after, close, out bool hasBody);
                    if (t.Text == "UFUNCTION")
                    {
                        var fn = ParseFunction(text, tokens, after, end, hasBody);
                        if (fn != null)
                        {
                            fn.Macro = macro; fn.Access = access; fn.Owner = type;
                            type.Functions.Add(fn);
                        }
                    }
                    else
                    {
                        var prop = ParseProperty(text, tokens, after, end);
                        if (prop != null)
                        {
                            prop.Macro = macro; prop.Access = access; prop.Owner = type;
                            type.Properties.Add(prop);
                        }
                    }
                    i = end;
                }
            }
        }

        static IEnumerable<List<Token>> SplitArgsRange(List<Token> tokens, int start, int end)
        {
            var current = new List<Token>();
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                var t = tokens[i];
                if (t.Text == "(" || t.Text == "{") depth++;
                else if (t.Text == ")" || t.Text == "}") depth--;
                else if (t.Text == "," && depth == 0) { yield return current; current = new List<Token>(); continue; }
                current.Add(t);
            }
            if (current.Count > 0) yield return current;
        }

        /// <summary>Token index of the ';' (or closing '}' of an inline body) ending a member declaration.</summary>
        static int FindDeclarationEnd(List<Token> tokens, int start, int limit, out bool hasBody)
        {
            hasBody = false;
            bool sawParams = false;
            for (int i = start; i < limit; i++)
            {
                var t = tokens[i];
                if (t.Text == ";") return i;
                if (t.Text == "(") { i = MatchClose(tokens, i); sawParams = true; continue; }
                if (t.Text == "{")
                {
                    int close = MatchClose(tokens, i);
                    // A '{' after the parameter list is a function body (unless it is "= {...}" / brace-init).
                    if (sawParams && (i == 0 || tokens[i - 1].Text != "="))
                    {
                        hasBody = true;
                        int next = close + 1;
                        return next < limit && tokens[next].Text == ";" ? next : close;
                    }
                    i = close;
                    continue;
                }
                // Next reflection macro / access label without ';' means the buffer is mid-edit.
                if (t.IsIdentifier && (t.Text == "UPROPERTY" || t.Text == "UFUNCTION") && i > start) return i - 1;
            }
            return limit - 1;
        }

        static readonly HashSet<string> FunctionPrefixKeywords = new HashSet<string>
        {
            "virtual", "static", "inline", "FORCEINLINE", "FORCENOINLINE", "explicit", "constexpr", "UE_NODISCARD", "UE_DEPRECATED",
        };

        static ReflectedFunction ParseFunction(string text, List<Token> tokens, int start, int end, bool hasBody)
        {
            int paren = -1;
            for (int i = start; i <= end && i < tokens.Count; i++)
                if (tokens[i].Text == "(") { paren = i; break; }
            if (paren <= start || !tokens[paren - 1].IsIdentifier) return null;

            var fn = new ReflectedFunction
            {
                Name = tokens[paren - 1].Text,
                NameStart = tokens[paren - 1].Start,
                DeclarationStart = tokens[start].Start,
                DeclarationEnd = tokens[Math.Min(end, tokens.Count - 1)].End,
                HasInlineBody = hasBody,
            };

            int typeStart = start;
            while (typeStart < paren - 1 && FunctionPrefixKeywords.Contains(tokens[typeStart].Text))
            {
                if (tokens[typeStart].Text == "virtual") fn.IsVirtual = true;
                if (tokens[typeStart].Text == "static") fn.IsStatic = true;
                if (Next(tokens, typeStart, "(")) typeStart = MatchClose(tokens, typeStart + 1);
                typeStart++;
            }
            if (typeStart < paren - 1)
            {
                fn.ReturnTypeStart = tokens[typeStart].Start;
                fn.ReturnType = Slice(text, tokens[typeStart].Start, tokens[paren - 2].End);
            }

            var args = SplitArgs(tokens, paren, out int close);
            foreach (var arg in args)
            {
                if (arg.Count == 0 || (arg.Count == 1 && arg[0].Text == "void")) continue;
                var p = new FunctionParameter();
                int eq = arg.FindIndex(x => x.Text == "=");
                var decl = eq >= 0 ? arg.Take(eq).ToList() : arg;
                if (eq >= 0) p.DefaultValue = Slice(text, arg[eq + 1 < arg.Count ? eq + 1 : eq].Start, arg[arg.Count - 1].End);
                // UPARAM(ref) prefix is a type annotation.
                int from = 0;
                if (decl.Count > 1 && decl[0].Text == "UPARAM" && decl[1].Text == "(")
                {
                    int depth = 0;
                    for (from = 1; from < decl.Count; from++)
                    {
                        if (decl[from].Text == "(") depth++;
                        else if (decl[from].Text == ")" && --depth == 0) { from++; break; }
                    }
                }
                if (from >= decl.Count) continue;
                var last = decl[decl.Count - 1];
                if (last.IsIdentifier && decl.Count - from > 1)
                {
                    p.Name = last.Text;
                    p.Type = Slice(text, decl[from].Start, decl[decl.Count - 2].End);
                }
                else
                {
                    p.Type = Slice(text, decl[from].Start, last.End);
                }
                fn.Parameters.Add(p);
            }

            for (int i = close + 1; i <= end && i < tokens.Count; i++)
            {
                var t = tokens[i].Text;
                if (t == "{" || t == ";") break;
                if (t == "const") fn.IsConst = true;
                if (t == "override" || t == "final") fn.IsOverride = true;
            }
            return fn;
        }

        static ReflectedProperty ParseProperty(string text, List<Token> tokens, int start, int end)
        {
            if (start >= tokens.Count) return null;
            int nameIndex = -1;
            int depthAngle = 0;
            for (int i = start; i <= end && i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Text == "<") depthAngle++;
                else if (t.Text == ">") depthAngle--;
                else if (depthAngle <= 0 && (t.Text == ";" || t.Text == "=" || t.Text == "{" || t.Text == "[" || t.Text == ":")) break;
                else if (t.Text == "(") { i = MatchClose(tokens, i); continue; }
                if (t.IsIdentifier && depthAngle <= 0) nameIndex = i;
            }
            if (nameIndex <= start) return null;

            int typeFrom = start;
            while (typeFrom < nameIndex && (tokens[typeFrom].Text == "mutable" || tokens[typeFrom].Text == "static")) typeFrom++;
            if (typeFrom >= nameIndex) return null;

            return new ReflectedProperty
            {
                Name = tokens[nameIndex].Text,
                NameStart = tokens[nameIndex].Start,
                Type = Slice(text, tokens[typeFrom].Start, tokens[nameIndex - 1].End),
                TypeStart = tokens[typeFrom].Start,
                TypeEnd = tokens[nameIndex - 1].End,
                DeclarationStart = tokens[start].Start,
                DeclarationEnd = tokens[Math.Min(end, tokens.Count - 1)].End,
            };
        }

        static string Slice(string text, int start, int end) =>
            end > start ? Regex.Replace(text.Substring(start, end - start), @"\s+", " ").Trim() : string.Empty;
    }
}
