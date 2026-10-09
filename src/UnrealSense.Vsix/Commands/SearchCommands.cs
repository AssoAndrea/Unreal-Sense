using System;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.GoTo;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;

namespace UnrealSense.Extension.Commands
{
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.FindSymbol)]
    internal sealed class FindSymbolCommand : BaseCommand<FindSymbolCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e) =>
            GoToWindow.ShowPopup(GoToMode.Symbols, await SelectedWordAsync());

        /// <summary>Selected text (single line) to prefill the query, like VA/Rider.</summary>
        internal static async Task<string> SelectedWordAsync()
        {
            var view = await VS.Documents.GetActiveDocumentViewAsync();
            var selection = view?.TextView?.Selection;
            if (selection == null || selection.IsEmpty) return null;
            var text = selection.StreamSelectionSpan.GetText();
            return text.Length > 0 && text.Length < 120 && text.IndexOf('\n') < 0 ? text.Trim() : null;
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.FindFile)]
    internal sealed class FindFileCommand : BaseCommand<FindFileCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e) =>
            GoToWindow.ShowPopup(GoToMode.Files, await FindSymbolCommand.SelectedWordAsync());
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.FindUsages)]
    internal sealed class FindUsagesCommand : BaseCommand<FindUsagesCommand>
    {
        static CancellationTokenSource running;

        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Visible = General.Instance.UseOwnIndex && WorkspaceService.Current?.Project != null;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var view = await VS.Documents.GetActiveDocumentViewAsync();
            if (view?.TextView == null || view.FilePath == null) return;
            var caret = view.TextView.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var text = view.TextBuffer.CurrentSnapshot.GetText();

            running?.Cancel();
            var cts = running = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await VS.StatusBar.ShowMessageAsync("UnrealSense: finding usages…");
            await VS.StatusBar.StartAnimationAsync(StatusAnimation.Find);
            try
            {
                var results = await Task.Run(() => FindUsagesService.FindAsync(view.FilePath, text, line.LineNumber, caret.Position - line.Start.Position, cts.Token));
                if (results == null)
                {
                    await VS.StatusBar.ShowMessageAsync("UnrealSense: place the caret on a symbol.");
                    return;
                }
                await FindUsagesWindow.ShowResultsAsync(results);
                await VS.StatusBar.ShowMessageAsync($"UnrealSense: {results.Code.Count} usages of {results.Symbol} ({results.Milliseconds:F0} ms)");
            }
            catch (OperationCanceledException)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: Find Usages cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("Find Usages", ex);
                await VS.StatusBar.ShowMessageAsync("UnrealSense: Find Usages failed — see Output › UnrealSense.");
            }
            finally
            {
                await VS.StatusBar.EndAnimationAsync(StatusAnimation.Find);
            }
        }
    }
}
