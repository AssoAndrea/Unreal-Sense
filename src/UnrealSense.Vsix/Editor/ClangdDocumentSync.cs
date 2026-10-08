using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Clang;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Editor
{
    /// <summary>
    /// Opens C++ documents in clangd as soon as they are shown, and keeps it in sync after edits. clangd answers
    /// Find Usages and Go to Definition from the document's parsed form; building it for an Unreal file means
    /// parsing all of its headers (seconds to tens of seconds), so doing it when the document opens makes those
    /// commands answer at once instead of paying that cost when invoked.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ClangdDocumentSync : IWpfTextViewCreationListener
    {
        static readonly TimeSpan EditDelay = TimeSpan.FromSeconds(2);

        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public void TextViewCreated(IWpfTextView view)
        {
            if (!DocumentFactory.TryGetTextDocument(view.TextBuffer, out var document)) return;
            var path = document.FilePath;
            Timer timer = null;
            void Sync()
            {
                var client = ClangdService.Client;
                if (client == null || client.State == ClangdState.Failed || client.State == ClangdState.Stopped || client.State == ClangdState.Starting) return;
                var text = view.TextBuffer.CurrentSnapshot.GetText();
                Task.Run(() => client.SyncDocumentAsync(path, text)).FireAndForgetLogged("clangd document sync");
            }
            timer = new Timer(_ => Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!view.IsClosed) Sync();
            }).Task.FireAndForgetLogged("clangd document sync"));

            Sync();
            view.TextBuffer.Changed += (s, e) => timer.Change(EditDelay, Timeout.InfiniteTimeSpan);
            view.GotAggregateFocus += (s, e) => Sync();
            view.Closed += (s, e) =>
            {
                timer.Dispose();
                var client = ClangdService.Client;
                if (client != null) Task.Run(() => client.CloseDocumentAsync(path)).FireAndForgetLogged("clangd document close");
            };
        }
    }
}
