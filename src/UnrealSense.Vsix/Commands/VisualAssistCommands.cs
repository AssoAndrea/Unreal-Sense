using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Commands
{
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.DiagnoseIndexErrors)]
    internal sealed class DiagnoseIndexErrorsCommand : BaseCommand<DiagnoseIndexErrorsCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var directory = ClangdService.CompileCommandsDirectory;
            if (directory == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: the C++ index has not started yet.");
                return;
            }
            await Log.GetPane().ActivateAsync();
            await Task.Run(() => IndexDiagnostics.RunAsync(directory));
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.ToggleVsIndexing)]
    internal sealed class ToggleVsIndexingCommand : BaseCommand<ToggleVsIndexingCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Only offered to undo it: turning Visual Studio's indexing off belongs to the (hidden) semantic index.
            Command.Visible = VisualStudioTuning.IsApplied;
            Command.Text = VisualStudioTuning.IsApplied
                ? "Restore Visual Studio Indexing"
                : "Use UnrealSense Instead of Visual Studio Indexing...";
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            System.Collections.Generic.List<string> result;
            if (VisualStudioTuning.IsApplied)
            {
                result = VisualStudioTuning.Restore();
            }
            else
            {
                var message =
                    "UnrealSense will turn off this Visual Studio indexing, which it replaces:\n\n" +
                    VisualStudioTuning.Describe() + "\n\n" +
                    "Replacements: Go to Symbol/File (Alt+Shift+S/O) for Go To All, Find Usages (Alt+Shift+F) for Find All " +
                    "References, Go to Definition (F12) through clangd, Blueprint usages from UnrealSense's own index.\n\n" +
                    "Still available: IntelliSense completion, Quick Info, squiggles, the Unreal Engine log, test adapter and code analysis.\n" +
                    "No longer available while this is on: Class View, Call Hierarchy and Peek Definition into other files.\n\n" +
                    "Your current settings are saved; run this command again to restore them. Continue?";
                if (!await VS.MessageBox.ShowConfirmAsync("UnrealSense", message)) return;
                result = VisualStudioTuning.Apply();
            }
            var summary = string.Join("\n", result);
            if (await VS.MessageBox.ShowConfirmAsync("UnrealSense", summary + "\n\nThe changes apply after Visual Studio restarts. Restart now?"))
                VisualAssistService.Restart();
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.ToggleVisualAssist)]
    internal sealed class ToggleVisualAssistCommand : BaseCommand<ToggleVisualAssistCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (VisualAssistService.GetState(out _))
            {
                case VisualAssistState.NotInstalled:
                    Command.Text = "Visual Assist (not installed)";
                    Command.Enabled = false;
                    break;
                case VisualAssistState.Enabled:
                    Command.Text = "Disable Visual Assist";
                    Command.Enabled = true;
                    break;
                case VisualAssistState.Disabled:
                    Command.Text = "Enable Visual Assist";
                    Command.Enabled = true;
                    break;
                default:
                    Command.Text = "Enable/Disable Visual Assist";
                    Command.Enabled = true;
                    break;
            }
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var result = VisualAssistService.Toggle();
            await VS.StatusBar.ShowMessageAsync("UnrealSense: " + result.Message);
            if (result.NeedsRestart
                && await VS.MessageBox.ShowConfirmAsync("UnrealSense", result.Message + "\n\nRestart Visual Studio now?"))
                VisualAssistService.Restart();
        }
    }
}
