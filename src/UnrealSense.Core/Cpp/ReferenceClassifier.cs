using System;
using System.IO;

namespace UnrealSense.Cpp
{
    public enum ReferenceKind { Declaration, Call, Write, Read, Generated }

    /// <summary>Labels a reference from the text around it (the index tells where, not how).</summary>
    public static class ReferenceClassifier
    {
        public static bool IsGeneratedFile(string path)
        {
            var name = Path.GetFileName(path);
            return name.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".init.gen.cpp", StringComparison.OrdinalIgnoreCase);
        }

        public static ReferenceKind Classify(string filePath, string lineText, int column, int endColumn, bool isDeclaration)
        {
            if (IsGeneratedFile(filePath)) return ReferenceKind.Generated;
            if (isDeclaration) return ReferenceKind.Declaration;
            if (lineText == null) return ReferenceKind.Read;

            int i = Math.Min(Math.Max(endColumn, column), lineText.Length);
            // Skip template arguments: Foo<int>(...)
            while (i < lineText.Length && char.IsWhiteSpace(lineText[i])) i++;
            if (i < lineText.Length && lineText[i] == '<')
            {
                int depth = 0;
                for (; i < lineText.Length; i++)
                {
                    if (lineText[i] == '<') depth++;
                    else if (lineText[i] == '>' && --depth == 0) { i++; break; }
                }
                while (i < lineText.Length && char.IsWhiteSpace(lineText[i])) i++;
            }
            if (i < lineText.Length && lineText[i] == '(') return ReferenceKind.Call;

            var rest = i < lineText.Length ? lineText.Substring(i) : string.Empty;
            if (rest.StartsWith("++") || rest.StartsWith("--")) return ReferenceKind.Write;
            if (rest.Length > 1 && "+-*/%&|^".IndexOf(rest[0]) >= 0 && rest[1] == '=') return ReferenceKind.Write;
            if (rest.StartsWith("<<=") || rest.StartsWith(">>=")) return ReferenceKind.Write;
            if (rest.StartsWith("=") && !rest.StartsWith("==")) return ReferenceKind.Write;

            var before = lineText.Substring(0, Math.Min(column, lineText.Length)).TrimEnd();
            if (before.EndsWith("++") || before.EndsWith("--")) return ReferenceKind.Write;
            return ReferenceKind.Read;
        }
    }
}
