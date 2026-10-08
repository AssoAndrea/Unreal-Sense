using System.Collections.Generic;

namespace UnrealSense.Analysis
{
    public enum DiagnosticSeverity { Error, Warning, Suggestion }

    /// <summary>A text replacement; <see cref="FilePath"/> null means the analyzed file.</summary>
    public sealed class TextEdit
    {
        public TextEdit(int start, int length, string newText, string filePath = null)
        {
            Start = start;
            Length = length;
            NewText = newText;
            FilePath = filePath;
        }

        public int Start { get; }
        public int Length { get; }
        public string NewText { get; }
        public string FilePath { get; }

        /// <summary>Append at the end of <see cref="FilePath"/> (used to add stubs to .cpp files).</summary>
        public bool AppendToEnd => Start < 0;

        public static TextEdit Append(string filePath, string text) => new TextEdit(-1, 0, text, filePath);
    }

    public sealed class CodeFix
    {
        public CodeFix(string title, params TextEdit[] edits)
        {
            Title = title;
            Edits = new List<TextEdit>(edits);
        }

        public string Title { get; }
        public List<TextEdit> Edits { get; }
    }

    public sealed class Diagnostic
    {
        public Diagnostic(string id, DiagnosticSeverity severity, string message, int start, int length)
        {
            Id = id;
            Severity = severity;
            Message = message;
            Start = start;
            Length = length;
        }

        public string Id { get; }
        public DiagnosticSeverity Severity { get; }
        public string Message { get; }
        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public List<CodeFix> Fixes { get; } = new List<CodeFix>();

        public Diagnostic WithFix(CodeFix fix)
        {
            if (fix != null) Fixes.Add(fix);
            return this;
        }

        public override string ToString() => $"{Id} {Severity} @{Start}: {Message}";
    }
}
