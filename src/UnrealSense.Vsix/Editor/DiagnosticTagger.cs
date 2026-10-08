using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Analysis;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IErrorTag))]
    [ContentType("C/C++")]
    internal sealed class DiagnosticTaggerProvider : ITaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!DocumentFactory.TryGetTextDocument(buffer, out var document)) return null;
            var analysis = DocumentAnalysis.TryGet(buffer, document.FilePath);
            if (analysis == null) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(() => new DiagnosticTagger(buffer, analysis)) as ITagger<T>;
        }
    }

    /// <summary>Squiggles for UnrealSense inspections.</summary>
    internal sealed class DiagnosticTagger : ITagger<IErrorTag>
    {
        readonly ITextBuffer buffer;
        readonly DocumentAnalysis analysis;

        public DiagnosticTagger(ITextBuffer buffer, DocumentAnalysis analysis)
        {
            this.buffer = buffer;
            this.analysis = analysis;
            analysis.Updated += OnUpdated;
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        void OnUpdated(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var snapshot = buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            }).FireAndForget();
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var analyzed = analysis.Snapshot;
            var diagnostics = analysis.Diagnostics;
            if (analyzed == null || diagnostics.Count == 0 || spans.Count == 0) yield break;
            var target = spans[0].Snapshot;

            foreach (var d in diagnostics)
            {
                if (d.Start < 0 || d.End > analyzed.Length) continue;
                var span = new SnapshotSpan(analyzed, d.Start, Math.Max(1, Math.Min(d.Length, analyzed.Length - d.Start)))
                    .TranslateTo(target, SpanTrackingMode.EdgeExclusive);
                if (!spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) continue;
                yield return new TagSpan<IErrorTag>(span, new ErrorTag(ErrorType(d.Severity), $"{d.Message} ({d.Id}, UnrealSense)"));
            }
        }

        static string ErrorType(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return PredefinedErrorTypeNames.SyntaxError;
                case DiagnosticSeverity.Warning: return PredefinedErrorTypeNames.Warning;
                default: return PredefinedErrorTypeNames.HintedSuggestion;
            }
        }
    }
}
