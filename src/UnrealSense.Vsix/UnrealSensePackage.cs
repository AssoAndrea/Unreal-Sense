using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;
using Task = System.Threading.Tasks.Task;

[assembly: ProvideCodeBase(AssemblyName = "UnrealSense.Core")]

namespace UnrealSense.Extension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("UnrealSense", "Unreal Engine productivity for Visual Studio", Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(UnrealExplorerWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.SolutionExplorer)]
    [ProvideToolWindow(typeof(BlueprintUsagesWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.ErrorList)]
    [ProvideToolWindow(typeof(FindUsagesWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.ErrorList)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralPage), "UnrealSense", "General", 0, 0, true, SupportsProfiles = true)]
    [Guid(PackageGuids.UnrealSensePackageString)]
    public sealed class UnrealSensePackage : ToolkitPackage
    {
        public const string Version = "0.3.19";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // Several Visual Studio versions write to the same log: say which extension version runs in which one.
            string vsVersion = null;
            try { vsVersion = ((EnvDTE.DTE)GetGlobalService(typeof(EnvDTE.DTE)))?.Version; }
            catch (Exception) { }
            Log.Write($"UnrealSense {Version} loaded in Visual Studio {vsVersion ?? "?"} ({System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName})");
            ErrorListService.Initialize(this);
            // An older version turned Visual Studio's own C++ database off for the clangd index (removed): say how to get it back.
            if (VisualStudioTuning.IsApplied)
                Log.Write("Visual Studio's own C++ indexing is still turned off by an older UnrealSense: " +
                          "run Extensions › UnrealSense › Restore Visual Studio Indexing to get Find All References and Go To back.");

            VS.Events.SolutionEvents.OnAfterOpenSolution += solution => OpenFromSolutionAsync().FireAndForget();
            VS.Events.SolutionEvents.OnAfterOpenFolder += folder => WorkspaceService.EnsureForPath(folder);
            VS.Events.SolutionEvents.OnAfterCloseSolution += () => WorkspaceService.Close();

            await OpenFromSolutionAsync();
        }

        static async Task OpenFromSolutionAsync()
        {
            var solution = await VS.Solutions.GetCurrentSolutionAsync();
            if (string.IsNullOrEmpty(solution?.FullPath)) return;

            // Engine-root solutions hold several games: the startup project tells which one is being worked on.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string startup = null;
            try
            {
                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte?.Solution?.SolutionBuild?.StartupProjects is object[] projects && projects.Length > 0)
                    startup = projects[0] as string;
            }
            catch (Exception) { }
            var path = solution.FullPath;
            await Task.Run(() => WorkspaceService.EnsureForSolution(path, startup));
        }
    }
}
