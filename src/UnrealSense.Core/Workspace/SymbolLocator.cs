using System;
using System.Linq;
using UnrealSense.Cpp;

namespace UnrealSense.Workspace
{
    /// <summary>A reflected type or member resolved at a text position.</summary>
    public sealed class SymbolAtPosition
    {
        public ReflectedType Type { get; set; }
        public ReflectedMember Member { get; set; }
        /// <summary>Header that declares the symbol (may differ from the file the caret is in).</summary>
        public string DeclaringFile { get; set; }
        public string DisplayName => Member != null ? $"{Type?.Name}::{Member.Name}" : Type?.Name;
    }

    public static class SymbolLocator
    {
        /// <summary>
        /// Finds the reflected symbol at <paramref name="offset"/>: inside its macro/declaration in a header, or
        /// on a qualified name such as "AFoo::Bar" / "AFoo::Bar_Implementation" in a source file.
        /// </summary>
        public static SymbolAtPosition Find(ParsedFile file, int offset, SymbolIndex symbols)
        {
            foreach (var type in file.Types)
            {
                foreach (var member in type.Functions.Cast<ReflectedMember>().Concat(type.Properties))
                    if (offset >= member.Macro.Start && offset <= member.DeclarationEnd)
                        return new SymbolAtPosition { Type = type, Member = member, DeclaringFile = file.FilePath };

                int headEnd = type.BodyOpen >= 0 ? type.BodyOpen : type.NameEnd;
                if (offset >= type.Macro.Start && offset <= headEnd)
                    return new SymbolAtPosition { Type = type, DeclaringFile = file.FilePath };
            }

            return symbols == null ? null : FindQualified(file.Text, offset, symbols);
        }

        /// <summary>Symbol whose declared name (not the whole declaration) contains <paramref name="offset"/>.</summary>
        public static SymbolAtPosition FindDeclaredName(ParsedFile file, int offset)
        {
            foreach (var type in file.Types)
            {
                if (offset >= type.NameStart && offset <= type.NameEnd)
                    return new SymbolAtPosition { Type = type, DeclaringFile = file.FilePath };
                foreach (var member in type.Functions.Cast<ReflectedMember>().Concat(type.Properties))
                    if (offset >= member.NameStart && offset <= member.NameEnd)
                        return new SymbolAtPosition { Type = type, Member = member, DeclaringFile = file.FilePath };
            }
            return null;
        }

        static SymbolAtPosition FindQualified(string text, int offset, SymbolIndex symbols)
        {
            var (start, end) = WordAt(text, offset);
            if (start == end) return null;
            var word = text.Substring(start, end - start);

            // Walk back over "::" to the class name.
            int i = start - 1;
            while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
            string className = null;
            if (i >= 1 && text[i] == ':' && text[i - 1] == ':')
            {
                i -= 2;
                while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
                var (cs, ce) = WordAt(text, i);
                if (ce > cs) className = text.Substring(cs, ce - cs);
            }

            if (className == null)
            {
                // A bare type name (e.g. "AShooterCharacter" anywhere in code).
                var bare = symbols.FindType(word);
                return bare == null ? null : new SymbolAtPosition { Type = bare, DeclaringFile = symbols.FindTypeFile(word) };
            }

            var type = symbols.FindType(className);
            if (type == null) return null;
            var memberName = StripSuffix(word);
            var member = type.Functions.Cast<ReflectedMember>().Concat(type.Properties).FirstOrDefault(m => m.Name == memberName);
            return new SymbolAtPosition { Type = type, Member = member, DeclaringFile = symbols.FindTypeFile(className) };
        }

        /// <summary>"Fire_Implementation" / "Fire_Validate" → "Fire".</summary>
        public static string StripSuffix(string name)
        {
            foreach (var suffix in new[] { "_Implementation", "_Validate" })
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                    return name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        public static (int Start, int End) WordAt(string text, int offset)
        {
            if (offset < 0 || offset > text.Length) return (offset, offset);
            int s = offset, e = offset;
            while (s > 0 && IsWordChar(text[s - 1])) s--;
            while (e < text.Length && IsWordChar(text[e])) e++;
            return (s, e);
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
