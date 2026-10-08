using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UnrealSense.Assets;
using UnrealSense.Extension.Options;
using UnrealSense.Project;
using UnrealSense.Remote;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Opens assets in the Unreal Editor that has the project loaded, through the Python plugin's remote execution;
    /// falls back to showing the file in Explorer when no editor answers.
    /// </summary>
    internal static class UnrealEditorBridge
    {
        // Discovery takes ~1.5 s: remember the editor of a project for a while so the next double-clicks are instant.
        static readonly TimeSpan NodeCacheLifetime = TimeSpan.FromSeconds(30);
        static readonly ConcurrentDictionary<string, (RemoteNode Node, DateTime Found)> nodes = new ConcurrentDictionary<string, (RemoteNode, DateTime)>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loggedSetup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly RemoteExecutionClient client = new RemoteExecutionClient();

        /// <summary>The project can be reached by remote execution (as far as its config files tell).</summary>
        public static bool IsSetUp(UnrealProject project) => project != null && RemoteExecutionSetup.IsEnabled(project);

        public static async Task OpenAssetAsync(UnrealProject project, AssetRecord asset)
        {
            if (asset == null) return;
            try
            {
                if (project == null || asset.PackageName == null || !General.Instance.OpenAssetsInUnrealEditor)
                {
                    EditorNavigation.RevealInExplorer(asset.FilePath);
                    return;
                }

                var editors = await Task.Run(RemoteExecutionClient.FindEditorProcesses);
                try
                {
                    if (editors.Count == 0)
                    {
                        Log.Trace($"UnrealEditorBridge: no Unreal Editor running, showing {asset.FilePath} in Explorer");
                        EditorNavigation.RevealInExplorer(asset.FilePath);
                        return;
                    }

                    LogSetupOnce(project);
                    if (!IsSetUp(project))
                    {
                        // Asked once: a "No" turns the option off (the menu command asks again).
                        await AskToEnableAsync(project);
                        EditorNavigation.RevealInExplorer(asset.FilePath);
                        return;
                    }

                    var objectPath = $"{asset.PackageName}.{asset.AssetName}";
                    await VS.StatusBar.ShowMessageAsync($"UnrealSense: opening {asset.AssetName} in Unreal Editor…");
                    var watch = Stopwatch.StartNew();
                    var node = await FindNodeAsync(project);
                    if (node == null)
                    {
                        Log.Write($"UnrealEditorBridge: no Unreal Editor answered for {project.ProjectDirectory} ({editors.Count} editor process(es) running) - restart the editor after enabling Remote Execution; if it is already on, the local multicast 239.0.0.1:6766 may be blocked");
                        await VS.StatusBar.ShowMessageAsync("UnrealSense: the Unreal Editor did not answer (restart it after enabling Remote Execution). Showing the file in Explorer.");
                        EditorNavigation.RevealInExplorer(asset.FilePath);
                        return;
                    }

                    AllowEditorsToComeForward(editors);
                    RemoteCommandResult result;
                    try { result = await client.RunAsync(node, RemoteExecutionClient.OpenAssetScript(objectPath), TimeSpan.FromSeconds(10)); }
                    catch (Exception)
                    {
                        nodes.TryRemove(project.ProjectDirectory, out _); // the editor may have been closed or restarted
                        throw;
                    }
                    Log.Write($"UnrealEditorBridge: open {objectPath} -> {(result.Success ? "ok" : "failed: " + result.Result?.Replace("\n\n", "\n").Trim())} in {watch.ElapsedMilliseconds} ms{(string.IsNullOrEmpty(result.Output) ? "" : " | " + result.Output)}");
                    if (!result.Success)
                    {
                        // The result is a Python traceback: its last line is the error.
                        var reason = (result.Result ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                        await VS.StatusBar.ShowMessageAsync($"UnrealSense: the Unreal Editor could not open {asset.AssetName}: {reason}");
                        EditorNavigation.RevealInExplorer(asset.FilePath);
                        return;
                    }
                    BringEditorToFront(editors, project);
                    await VS.StatusBar.ShowMessageAsync($"UnrealSense: opened {asset.AssetName} in Unreal Editor.");
                }
                finally
                {
                    foreach (var process in editors) process.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("UnrealEditorBridge: opening the asset in Unreal Editor failed", ex);
                await VS.StatusBar.ShowMessageAsync("UnrealSense: could not reach the Unreal Editor, see Output › UnrealSense. Showing the file in Explorer.");
                EditorNavigation.RevealInExplorer(asset.FilePath);
            }
        }

        /// <summary>
        /// Offers to enable remote execution in the project (Python plugin in the .uproject, bRemoteExecution in
        /// Config/DefaultEngine.ini). Yes writes both files; No turns the option off.
        /// </summary>
        public static async Task AskToEnableAsync(UnrealProject project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var options = General.Instance;
            if (IsSetUp(project))
            {
                options.OpenAssetsInUnrealEditor = true;
                await options.SaveAsync();
                await VS.MessageBox.ShowAsync("UnrealSense", "Double-clicking a Blueprint usage now opens it in the running Unreal Editor.",
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK);
                return;
            }

            var answer = await VS.MessageBox.ShowAsync("UnrealSense",
                "Open Blueprints directly in the running Unreal Editor?\n\n"
                + $"UnrealSense needs Python Remote Execution in {project.Name}. It will:\n"
                + $"  • enable the \"{RemoteExecutionSetup.PluginName}\" plugin in {System.IO.Path.GetFileName(project.UProjectPath)}\n"
                + "  • set bRemoteExecution=True in Config/DefaultEngine.ini\n"
                + "Read-only files (not checked out in Perforce) are made writable: check them out afterwards.\n\n"
                + "The editor only accepts connections from this machine. Restart the Unreal Editor afterwards "
                + "(with a source-built engine, build the editor first so the plugin is compiled).\n\n"
                + "No: keep showing Blueprints in Explorer (Extensions › UnrealSense can ask again).",
                OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO);

            if (answer != VSConstants.MessageBoxResult.IDYES)
            {
                options.OpenAssetsInUnrealEditor = false;
                await options.SaveAsync();
                Log.Write($"UnrealEditorBridge: the user declined enabling Remote Execution in {project.Name}; Blueprint usages open in Explorer");
                return;
            }

            var madeWritable = new List<string>();
            var error = RemoteExecutionSetup.Enable(project.UProjectPath, madeWritable);
            Log.Write($"UnrealEditorBridge: enabling Remote Execution in {project.Name}: {error ?? "done"}{(madeWritable.Count == 0 ? "" : "; made writable: " + string.Join(", ", madeWritable))}");
            if (error != null)
            {
                await VS.MessageBox.ShowErrorAsync("UnrealSense", "Could not enable Python Remote Execution:\n" + error);
                return;
            }
            options.OpenAssetsInUnrealEditor = true;
            await options.SaveAsync();
            lock (loggedSetup) loggedSetup.Remove(project.ProjectDirectory);
            var checkout = madeWritable.Count == 0 ? ""
                : "\n\nThese files were read-only and have been made writable: check them out in Perforce (or revert them) so the change is not lost:\n"
                  + string.Join("\n", madeWritable.Select(f => "  • " + f));
            await VS.MessageBox.ShowAsync("UnrealSense", "Python Remote Execution is enabled. Restart the Unreal Editor to apply it." + checkout,
                madeWritable.Count == 0 ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK);
        }

        static async Task<RemoteNode> FindNodeAsync(UnrealProject project)
        {
            if (nodes.TryGetValue(project.ProjectDirectory, out var cached) && DateTime.UtcNow - cached.Found < NodeCacheLifetime)
                return cached.Node;
            var found = await client.DiscoverAsync(TimeSpan.FromSeconds(1.5));
            Log.Write($"UnrealEditorBridge: {found.Count} editor(s) answered: {(found.Count == 0 ? "none" : string.Join("; ", found))}");
            var node = RemoteExecutionClient.FindNodeForProject(found, project.ProjectDirectory);
            if (node != null) nodes[project.ProjectDirectory] = (node, DateTime.UtcNow);
            return node;
        }

        static void LogSetupOnce(UnrealProject project)
        {
            lock (loggedSetup)
                if (!loggedSetup.Add(project.ProjectDirectory)) return;
            Log.Write($"UnrealEditorBridge: {project.Name}: {RemoteExecutionSetup.PluginName} listed in .uproject = {RemoteExecutionSetup.IsPythonPluginListed(project.UProjectPath)}, bRemoteExecution = {RemoteExecutionSetup.IsRemoteExecutionEnabled(project.ProjectDirectory)}");
        }

        // The editor activates the asset editor's window itself; Windows only lets it take the foreground if the
        // foreground process (Visual Studio) allows it.
        static void AllowEditorsToComeForward(List<Process> editors)
        {
            foreach (var process in editors)
            {
                try { AllowSetForegroundWindow(process.Id); }
                catch (Exception) { }
            }
        }

        static void BringEditorToFront(List<Process> editors, UnrealProject project)
        {
            try
            {
                var foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out var foregroundPid);
                if (editors.Any(p => p.Id == foregroundPid)) return;
                // Several editors: the one whose main window title names the project ("Game - Unreal Editor").
                var editor = editors.FirstOrDefault(p => SafeTitle(p).IndexOf(project.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                             ?? (editors.Count == 1 ? editors[0] : null);
                var window = editor?.MainWindowHandle ?? IntPtr.Zero;
                if (window == IntPtr.Zero) return;
                if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
                SetForegroundWindow(window);
            }
            catch (Exception) { }
        }

        static string SafeTitle(Process process)
        {
            try { return process.MainWindowTitle ?? ""; }
            catch (Exception) { return ""; }
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    }
}
