using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Commands
{
    /// <summary>
    /// Undoes what older versions did for the clangd index (Visual Studio's own C++ database turned off); visible only
    /// while those settings are still applied.
    /// </summary>
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.ToggleVsIndexing)]
    internal sealed class ToggleVsIndexingCommand : BaseCommand<ToggleVsIndexingCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Visible = VisualStudioTuning.IsApplied;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!VisualStudioTuning.IsApplied) return;
            var summary = string.Join("\n", VisualStudioTuning.Restore());
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
