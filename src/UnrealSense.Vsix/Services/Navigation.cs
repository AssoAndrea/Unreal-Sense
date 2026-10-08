using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace UnrealSense.Extension.Services
{
    internal static class EditorNavigation
    {
        public static async Task OpenAtOffsetAsync(string filePath, int offset)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = await VS.Documents.OpenAsync(filePath);
            if (view?.TextView == null) return;
            var snapshot = view.TextView.TextSnapshot;
            MoveCaret(view.TextView, new SnapshotPoint(snapshot, Math.Max(0, Math.Min(offset, snapshot.Length))));
        }

        public static async Task OpenAtLineAsync(string filePath, int line, int column)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = await VS.Documents.OpenAsync(filePath);
            if (view?.TextView == null) return;
            var snapshot = view.TextView.TextSnapshot;
            var textLine = snapshot.GetLineFromLineNumber(Math.Max(0, Math.Min(line, snapshot.LineCount - 1)));
            MoveCaret(view.TextView, textLine.Start + Math.Max(0, Math.Min(column, textLine.Length)));
        }

        static void MoveCaret(ITextView view, SnapshotPoint point)
        {
            view.Caret.MoveTo(point);
            view.Selection.Select(new SnapshotSpan(point, 0), false);
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
            (view as IWpfTextView)?.VisualElement.Focus();
        }

        /// <summary>Opens Explorer with the file selected.</summary>
        public static void RevealInExplorer(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
    }
}
