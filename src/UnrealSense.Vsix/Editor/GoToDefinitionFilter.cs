using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Clang;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Editor
{
    /// <summary>
    /// Go to Definition (F12 / Ctrl+click) through clangd. Used when Visual Studio's C++ database is off (its
    /// cross-file Go To Definition depends on it) or when the option says so; falls back to Visual Studio's own
    /// command when clangd has no answer. On a definition it jumps to the declaration and vice versa.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class GoToDefinitionFilterProvider : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var view = Adapters.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new GoToDefinitionFilter(view);
            if (textViewAdapter.AddCommandFilter(filter, out var next) == VSConstants.S_OK)
                filter.Next = next;
        }
    }

    internal sealed class GoToDefinitionFilter : IOleCommandTarget
    {
        static bool passThrough;
        static CancellationTokenSource running;
        readonly IWpfTextView view;

        public GoToDefinitionFilter(IWpfTextView view) => this.view = view;

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!passThrough && pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97
                && nCmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn && ShouldHandle())
            {
                GoToDefinitionAsync().FireAndForgetLogged("Go to Definition");
                return VSConstants.S_OK;
            }
            return Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        static bool ShouldHandle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var client = ClangdService.Client;
            if (client == null || client.State == ClangdState.Failed || client.State == ClangdState.Stopped) return false;
            switch (General.Instance.ClangdGoToDefinition)
            {
                case ClangdGoToDefinitionMode.Always: return true;
                case ClangdGoToDefinitionMode.Never: return false;
                default: return VisualStudioTuning.IsCppDatabaseDisabled;
            }
        }

        async Task GoToDefinitionAsync()
        {
            if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)) return;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            int lineNumber = line.LineNumber, column = caret.Position - line.Start.Position;
            var text = caret.Snapshot.GetText();
            var path = document.FilePath;

            running?.Cancel();
            var cts = running = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await VS.StatusBar.ShowMessageAsync("UnrealSense: finding definition…");
            SourceLocation target = null;
            try
            {
                target = await Task.Run(() => ResolveAsync(path, text, lineNumber, column, cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Go to Definition (clangd)", ex);
            }
            if (cts.IsCancellationRequested && running != cts) return; // superseded by a newer request

            if (target != null)
            {
                await VS.StatusBar.ClearAsync();
                await EditorNavigation.OpenAtLineAsync(target.FilePath, target.Line, target.Column);
                return;
            }

            // clangd had nothing (macro, file outside the database, still parsing...): let Visual Studio try.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await VS.StatusBar.ShowMessageAsync("UnrealSense: clangd found no definition, using Visual Studio's.");
            passThrough = true;
            try { ((DTE)Package.GetGlobalService(typeof(DTE))).ExecuteCommand("Edit.GoToDefinition"); }
            catch (Exception) { }
            finally { passThrough = false; }
        }

        static async Task<SourceLocation> ResolveAsync(string path, string text, int line, int column, CancellationToken token)
        {
            var client = ClangdService.Client;
            if (client == null) return null;
            await client.SyncDocumentAsync(path, text).ConfigureAwait(false);
            var definitions = await client.FindDefinitionAsync(path, line, column, token).ConfigureAwait(false);
            var declarations = await client.FindDeclarationAsync(path, line, column, token).ConfigureAwait(false);

            bool IsHere(SourceLocation l) => string.Equals(l.FilePath, path, StringComparison.OrdinalIgnoreCase)
                                              && l.Line == line && column >= l.Column && column <= Math.Max(l.EndColumn, l.Column + 1);

            // Already on the definition: go to the declaration (header), like Visual Studio and Visual Assist.
            var definition = definitions.FirstOrDefault(d => !IsHere(d));
            if (definitions.Any(IsHere) || definition == null)
                return declarations.FirstOrDefault(d => !IsHere(d)) ?? definition;
            return definition;
        }
    }
}
