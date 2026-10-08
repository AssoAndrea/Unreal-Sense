using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Assets;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Commands
{
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.GenerateProjectFiles)]
    internal sealed class GenerateProjectFilesCommand : BaseCommand<GenerateProjectFilesCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => RunAsync();

        /// <summary>Runs UnrealBuildTool -projectfiles for the current project, streaming output to the UnrealSense pane.</summary>
        public static async Task RunAsync()
        {
            var project = WorkspaceService.Current?.Project;
            var engine = project?.Engine;
            if (engine == null || !File.Exists(engine.UnrealBuildTool))
            {
                await VS.MessageBox.ShowErrorAsync("UnrealSense", "No Unreal project/engine loaded, or UnrealBuildTool was not found.");
                return;
            }

            var pane = Log.GetPane();
            await pane.ActivateAsync();
            var args = $"-projectfiles -project=\"{project.UProjectPath}\" -game -progress";
            await pane.WriteLineAsync($"> {engine.UnrealBuildTool} {args}");

            int exitCode = await Task.Run(() => ProcessRunner.Run(engine.UnrealBuildTool, args, project.ProjectDirectory, line => pane.WriteLineAsync(line).FireAndForget()));
            await pane.WriteLineAsync(exitCode == 0 ? "Project files generated." : $"UnrealBuildTool exited with code {exitCode}.");
            await VS.StatusBar.ShowMessageAsync(exitCode == 0 ? "UnrealSense: project files generated (reload the solution if prompted)." : "UnrealSense: project file generation failed, see Output › UnrealSense.");
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.OpenInUnrealEditor)]
    internal sealed class OpenInUnrealEditorCommand : BaseCommand<OpenInUnrealEditorCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            Run();
            return Task.CompletedTask;
        }

        public static void Run()
        {
            var project = WorkspaceService.Current?.Project;
            var editor = project?.Engine?.EditorExecutable;
            if (editor == null || !File.Exists(editor))
            {
                VS.MessageBox.ShowError("UnrealSense", "UnrealEditor.exe was not found for this project's engine.");
                return;
            }
            Process.Start(new ProcessStartInfo(editor, $"\"{project.UProjectPath}\"") { UseShellExecute = false, WorkingDirectory = project.ProjectDirectory });
            Log.Write($"Started {editor} \"{project.UProjectPath}\"");
        }
    }

    /// <summary>Asks again to open Blueprint usages in the Unreal Editor, after the user said no or before the project is set up.</summary>
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.EnableOpenAssetsInEditor)]
    internal sealed class EnableOpenAssetsInEditorCommand : BaseCommand<EnableOpenAssetsInEditorCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = WorkspaceService.Current?.Project;
            Command.Visible = project != null && (!General.Instance.OpenAssetsInUnrealEditor || !UnrealEditorBridge.IsSetUp(project));
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var project = WorkspaceService.Current?.Project;
            if (project == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: no Unreal project loaded.");
                return;
            }
            await UnrealEditorBridge.AskToEnableAsync(project);
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.RefreshIndex)]
    internal sealed class RefreshIndexCommand : BaseCommand<RefreshIndexCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var project = WorkspaceService.Current?.Project;
            if (project == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: no Unreal project loaded.");
                return;
            }
            try { File.Delete(AssetIndex.DefaultCachePath(project)); }
            catch (IOException) { }
            WorkspaceService.Reload();
        }
    }

    internal static class ProcessRunner
    {
        public static int Run(string fileName, string arguments, string workingDirectory, Action<string> onLine)
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
