using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using UnrealSense.Analysis;
using UnrealSense.Cpp;
using UnrealSense.Extension.Options;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Per-buffer parse + inspection results, recomputed in the background (debounced) when the buffer or the
    /// project indexes change. Shared by the tagger, light bulbs, Blueprint hints and the Error List.
    /// </summary>
    internal sealed class DocumentAnalysis : IDisposable
    {
        const int DebounceMilliseconds = 350;

        readonly ITextBuffer buffer;
        readonly Timer timer;
        int version;
        bool disposed;

        DocumentAnalysis(ITextBuffer buffer, string filePath)
        {
            this.buffer = buffer;
            FilePath = filePath;
            timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
            buffer.Changed += OnBufferChanged;
            WorkspaceService.Changed += OnWorkspaceChanged;
            Schedule(0);
        }

        public string FilePath { get; }
        public ITextSnapshot Snapshot { get; private set; }
        public ParsedFile File { get; private set; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = Array.Empty<Diagnostic>();
        public UnrealWorkspace Workspace { get; private set; }

        /// <summary>Raised on a background thread after each analysis.</summary>
        public event EventHandler Updated;

        public static DocumentAnalysis TryGet(ITextBuffer buffer, string filePath)
        {
            if (buffer == null || string.IsNullOrEmpty(filePath) || !WorkspaceService.IsUnrealFile(filePath)) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(typeof(DocumentAnalysis), () => new DocumentAnalysis(buffer, filePath));
        }

        /// <summary>Parses synchronously when no result exists for the current snapshot yet (used by commands).</summary>
        public ParsedFile GetCurrentFile()
        {
            var snapshot = buffer.CurrentSnapshot;
            if (Snapshot == snapshot && File != null) return File;
            return HeaderParser.Parse(snapshot.GetText(), FilePath);
        }

        void OnBufferChanged(object sender, TextContentChangedEventArgs e) => Schedule(DebounceMilliseconds);

        void OnWorkspaceChanged(object sender, EventArgs e) => Schedule(DebounceMilliseconds * 2);

        void Schedule(int delay)
        {
            if (!disposed) timer.Change(delay, Timeout.Infinite);
        }

        void Run()
        {
            if (disposed) return;
            int myVersion = Interlocked.Increment(ref version);
            try
            {
                var snapshot = buffer.CurrentSnapshot;
                var text = snapshot.GetText();
                var parsed = HeaderParser.Parse(text, FilePath);
                var workspace = WorkspaceService.GetForFile(FilePath);

                IReadOnlyList<Diagnostic> diagnostics = Array.Empty<Diagnostic>();
                if (workspace != null)
                {
                    // Keep the symbol index in sync with unsaved edits (affects other files' inspections too).
                    workspace.Symbols?.Update(FilePath, text, notify: false);
                    if (General.Instance.EnableInspections)
                        diagnostics = Filter(HeaderAnalyzer.Analyze(parsed, workspace.CreateAnalysisContext()));
                }

                if (myVersion != Volatile.Read(ref version)) return; // superseded
                Snapshot = snapshot;
                File = parsed;
                Workspace = workspace;
                Diagnostics = diagnostics;
                Log.Trace($"Analyzed {System.IO.Path.GetFileName(FilePath)}: {parsed.Types.Count} reflected types, {diagnostics.Count} diagnostics" +
                          (diagnostics.Count > 0 ? " [" + string.Join(", ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(diagnostics, d => d.Id))) + "]" : "") +
                          (workspace == null ? " (workspace not ready)" : ""));
                Updated?.Invoke(this, EventArgs.Empty);
                ErrorListService.Update(FilePath, snapshot, diagnostics);
            }
            catch (Exception ex)
            {
                Log.Error("Analysis of " + FilePath, ex);
            }
        }

        static IReadOnlyList<Diagnostic> Filter(List<Diagnostic> diagnostics)
        {
            var options = General.Instance;
            if (!options.SuggestObjectPtr)
                diagnostics.RemoveAll(d => d.Id == HeaderAnalyzer.RawObjectPointer);
            return diagnostics;
        }

        public void Dispose()
        {
            disposed = true;
            buffer.Changed -= OnBufferChanged;
            WorkspaceService.Changed -= OnWorkspaceChanged;
            timer.Dispose();
            ErrorListService.Clear(FilePath);
        }
    }
}
