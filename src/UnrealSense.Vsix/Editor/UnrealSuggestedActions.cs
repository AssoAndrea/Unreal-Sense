using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Analysis;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Name("UnrealSense quick fixes")]
    [ContentType("C/C++")]
    internal sealed class UnrealSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            if (textBuffer != textView.TextBuffer || !DocumentFactory.TryGetTextDocument(textBuffer, out var document)) return null;
            var analysis = DocumentAnalysis.TryGet(textBuffer, document.FilePath);
            return analysis == null ? null : new UnrealSuggestedActionsSource(textView, analysis);
        }
    }

    internal sealed class UnrealSuggestedActionsSource : ISuggestedActionsSource
    {
        readonly ITextView view;
        readonly DocumentAnalysis analysis;

        public UnrealSuggestedActionsSource(ITextView view, DocumentAnalysis analysis)
        {
            this.view = view;
            this.analysis = analysis;
            analysis.Updated += OnUpdated;
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        void OnUpdated(object sender, EventArgs e) => SuggestedActionsChanged?.Invoke(this, EventArgs.Empty);

        IEnumerable<Diagnostic> DiagnosticsAt(SnapshotSpan range)
        {
            var analyzed = analysis.Snapshot;
            if (analyzed == null) yield break;
            var mapped = range.TranslateTo(analyzed, SpanTrackingMode.EdgeInclusive);
            foreach (var d in analysis.Diagnostics)
                if (d.Fixes.Count > 0 && d.Start <= mapped.End && d.End >= mapped.Start)
                    yield return d;
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken) =>
            Task.FromResult(DiagnosticsAt(range).Any() || SymbolAt(range.Start) != null);

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            var fixes = DiagnosticsAt(range)
                .SelectMany(d => d.Fixes.Select(f => (ISuggestedAction)new CodeFixAction(view, analysis, d, f)))
                .ToList();
            if (fixes.Count > 0)
                yield return new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.CodeFix, fixes, "UnrealSense", SuggestedActionSetPriority.High);

            var symbol = SymbolAt(range.Start);
            if (symbol != null)
                yield return new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring,
                    new ISuggestedAction[] { new FindBlueprintUsagesAction(symbol, analysis.FilePath) }, "UnrealSense", SuggestedActionSetPriority.Low);
        }

        SymbolAtPosition SymbolAt(SnapshotPoint point)
        {
            var file = analysis.File;
            var analyzed = analysis.Snapshot;
            if (file == null || analyzed == null || analysis.Workspace?.Assets == null) return null;
            // Only on the declared name itself, so the light bulb does not show everywhere.
            return SymbolLocator.FindDeclaredName(file, point.TranslateTo(analyzed, PointTrackingMode.Positive).Position);
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose() => analysis.Updated -= OnUpdated;
    }

    internal sealed class CodeFixAction : ISuggestedAction
    {
        readonly ITextView view;
        readonly DocumentAnalysis analysis;
        readonly Diagnostic diagnostic;
        readonly CodeFix fix;

        public CodeFixAction(ITextView view, DocumentAnalysis analysis, Diagnostic diagnostic, CodeFix fix)
        {
            this.view = view;
            this.analysis = analysis;
            this.diagnostic = diagnostic;
            this.fix = fix;
        }

        public string DisplayText => fix.Title;
        public string IconAutomationText => null;
        public ImageMoniker IconMoniker => diagnostic.Severity == DiagnosticSeverity.Error ? KnownMonikers.StatusError : KnownMonikers.IntellisenseLightBulb;
        public string InputGestureText => null;
        public bool HasActionSets => false;
        public bool HasPreview => false;

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);
        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);

        public void Invoke(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var analyzed = analysis.Snapshot;
            var buffer = view.TextBuffer;
            var current = buffer.CurrentSnapshot;

            var local = fix.Edits.Where(e => e.FilePath == null).ToList();
            if (local.Count > 0)
            {
                using (var edit = buffer.CreateEdit())
                {
                    foreach (var e in local)
                    {
                        var span = new SnapshotSpan(analyzed, e.Start, e.Length).TranslateTo(current, SpanTrackingMode.EdgeInclusive);
                        edit.Replace(span, e.NewText);
                    }
                    edit.Apply();
                }
            }

            foreach (var external in fix.Edits.Where(e => e.FilePath != null))
                ApplyExternalAsync(external).FireAndForget();
        }

        static async Task ApplyExternalAsync(TextEdit edit)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var docView = await VS.Documents.OpenAsync(edit.FilePath);
            var buffer = docView?.TextBuffer;
            if (buffer == null) return;
            var snapshot = buffer.CurrentSnapshot;
            int position = edit.AppendToEnd ? snapshot.Length : edit.Start;
            var text = edit.NewText;
            if (edit.AppendToEnd && snapshot.Length > 0 && !snapshot.GetText(snapshot.Length - 1, 1).EndsWith("\n"))
                text = Environment.NewLine + text;
            buffer.Insert(position, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            await EditorNavigation.OpenAtOffsetAsync(edit.FilePath, position + text.Length - 2);
        }

        public void Dispose() { }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }
    }

    internal sealed class FindBlueprintUsagesAction : ISuggestedAction
    {
        readonly SymbolAtPosition symbol;
        readonly string filePath;

        public FindBlueprintUsagesAction(SymbolAtPosition symbol, string filePath)
        {
            this.symbol = symbol;
            this.filePath = filePath;
        }

        public string DisplayText => $"Find Blueprint usages of {symbol.DisplayName}";
        public string IconAutomationText => null;
        public ImageMoniker IconMoniker => KnownMonikers.FindSymbol;
        public string InputGestureText => "Alt+Shift+B";
        public bool HasActionSets => false;
        public bool HasPreview => false;

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>>(null);
        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);
        public void Invoke(CancellationToken cancellationToken) => BlueprintUsagesWindow.ShowForSymbolAsync(symbol, filePath).FireAndForget();
        public void Dispose() { }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }
    }
}
