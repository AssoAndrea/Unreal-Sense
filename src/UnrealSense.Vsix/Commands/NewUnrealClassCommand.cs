using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Dialogs;
using UnrealSense.Extension.Services;
using UnrealSense.Project;
using UnrealSense.Templates;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Commands
{
    /// <summary>
    /// "Unreal Class...": creates the .h/.cpp of a new class from a parent, adds them to the game's .vcxproj (UBT
    /// lists files one by one, so a new file is invisible until project files are regenerated) and opens them.
    /// </summary>
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.NewUnrealClass)]
    internal sealed class NewUnrealClassCommand : BaseCommand<NewUnrealClassCommand>
    {
        const string VirtualFolderKind = "{6BB5F8F0-4483-11D3-8BCF-00C04F8EC28C}";

        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Command.Visible = WorkspaceService.Current?.Project != null;
        }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => RunAsync(null);

        /// <summary>Opens the dialog; <paramref name="startDirectory"/> null means the Solution Explorer selection or the active document's folder.</summary>
        public static async Task RunAsync(string startDirectory)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var workspace = WorkspaceService.Current;
            if (workspace?.Project == null || workspace.State != WorkspaceState.Ready)
            {
                await VS.StatusBar.ShowMessageAsync(workspace?.Project == null ? "UnrealSense: no Unreal project loaded." : "UnrealSense: the Unreal project is still loading, try again in a moment.");
                return;
            }

            startDirectory = startDirectory ?? GetSelectedDirectory(workspace.Project) ?? await GetActiveDocumentDirectoryAsync();
            var dialog = new NewUnrealClassDialog(workspace, startDirectory);
            if (dialog.ShowModal() != true || dialog.Result == null) return;
            await CreateAsync(workspace, dialog.Result, dialog.Request, dialog.Module, dialog.AddDependency);
        }

        static async Task CreateAsync(UnrealWorkspace workspace, NewClassResult result, NewClassRequest request, UnrealModule module, bool addDependency)
        {
            Log.Write($"NewUnrealClass: {result.ClassName} : {request.Parent?.Name ?? request.Parent?.DisplayName} ({result.TemplateName}) in {module.Name}, " +
                $"header {result.HeaderPath}, source {result.SourcePath ?? "none"}");
            try
            {
                NewClassGenerator.Write(result);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                Log.Error("NewUnrealClass: write", e);
                await VS.MessageBox.ShowErrorAsync("UnrealSense", "The class files could not be written:\n" + e.Message);
                return;
            }

            var madeWritable = new List<string>();
            if (addDependency && result.MissingDependency != null)
            {
                var error = BuildCsEditor.AddPublicDependencyToFile(module.BuildCsPath, result.MissingDependency, madeWritable);
                Log.Write(error == null
                    ? $"NewUnrealClass: added {result.MissingDependency} to {module.BuildCsPath}"
                    : $"NewUnrealClass: could not add {result.MissingDependency} to {module.BuildCsPath}: {error}");
                if (error != null)
                    await VS.MessageBox.ShowWarningAsync("UnrealSense", $"Add \"{result.MissingDependency}\" to the dependencies of {module.Name} by hand:\n{error}");
            }

            var files = new[] { result.HeaderPath, result.SourcePath }.Where(f => f != null).ToList();
            var added = AddToProject(workspace.Project, files);

            if (result.SourcePath != null) await VS.Documents.OpenAsync(result.SourcePath);
            await VS.Documents.OpenAsync(result.HeaderPath);

            // The project system of UBT's Makefile projects cannot give a new .cpp its module's include paths without
            // reloading the project: IntelliSense knows them after the next Generate Project Files (the user's choice).
            var status = $"UnrealSense: created {result.ClassName}" + (added
                ? ". IntelliSense resolves its includes after Generate Project Files."
                : " (not added to a Visual Studio project: Generate Project Files adds it).");
            if (madeWritable.Count > 0)
            {
                status += " Check out " + string.Join(", ", madeWritable.Select(Path.GetFileName)) + " in source control.";
                Log.Write("NewUnrealClass: made writable (check out in source control): " + string.Join(", ", madeWritable));
            }
            await VS.StatusBar.ShowMessageAsync(status);
        }

        /// <summary>
        /// Adds the files to the Unreal project's C++ project, under the filter mirroring their folder ("Source\Gym\Public\X").
        /// </summary>
        static bool AddToProject(UnrealProject project, List<string> files)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = (DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
                var projects = AllProjects(dte?.Solution).ToList();
                // UBT names the game's .vcxproj after the .uproject (Intermediate\ProjectFiles\<Name>.vcxproj).
                var target = projects.FirstOrDefault(p => string.Equals(SafeName(p), project.Name, StringComparison.OrdinalIgnoreCase)
                        && SafeFullName(p).EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
                    ?? projects.FirstOrDefault(p => string.Equals(SafeName(p), project.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    Log.Write($"NewUnrealClass: no project named {project.Name} in the solution ({string.Join(", ", projects.Select(SafeName).Take(20))}); files not added");
                    return false;
                }

                foreach (var file in files)
                {
                    var relative = Path.GetDirectoryName(file);
                    var root = project.ProjectDirectory.TrimEnd('\\') + "\\";
                    var segments = relative.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        ? relative.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                        : Array.Empty<string>();
                    var (items, depth) = FindFilter(target.ProjectItems, segments);
                    items.AddFromFile(file);
                    Log.Write($"NewUnrealClass: added {Path.GetFileName(file)} to {SafeFullName(target)} under filter \"{string.Join("\\", segments.Take(depth))}\"");
                }
                target.Save();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("NewUnrealClass: add to project", e);
                return false;
            }
        }

        /// <summary>The filter for a folder path, created where missing; the project root when filters cannot be made.</summary>
        static (ProjectItems Items, int Depth) FindFilter(ProjectItems items, string[] segments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int depth = 0;
            foreach (var segment in segments)
            {
                ProjectItem match = null;
                foreach (ProjectItem item in items)
                    if (string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase) && item.Kind == VirtualFolderKind)
                    {
                        match = item;
                        break;
                    }
                if (match == null)
                {
                    try { match = items.AddFolder(segment, VirtualFolderKind); }
                    catch (Exception e)
                    {
                        Log.Write($"NewUnrealClass: could not create filter {segment}: {e.Message}");
                        break;
                    }
                }
                items = match.ProjectItems;
                depth++;
            }
            return (items, depth);
        }

        static IEnumerable<EnvDTE.Project> AllProjects(EnvDTE.Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (solution?.Projects == null) yield break;
            var stack = new Stack<EnvDTE.Project>();
            foreach (EnvDTE.Project p in solution.Projects) stack.Push(p);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (p == null) continue;
                if (p.Kind == EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder)
                {
                    foreach (ProjectItem item in p.ProjectItems)
                        if (item.SubProject != null) stack.Push(item.SubProject);
                    continue;
                }
                yield return p;
            }
        }

        static string SafeName(EnvDTE.Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.Name ?? ""; } catch (Exception) { return ""; }
        }

        static string SafeFullName(EnvDTE.Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.FullName ?? ""; } catch (Exception) { return ""; }
        }

        /// <summary>The folder selected in Solution Explorer, when it is the active window (context menu).</summary>
        static string GetSelectedDirectory(UnrealProject project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = (DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
                if (dte?.ActiveWindow?.Type != vsWindowType.vsWindowTypeSolutionExplorer || dte.SelectedItems.Count == 0) return null;
                var item = dte.SelectedItems.Item(1)?.ProjectItem;
                if (item == null) return null;
                if (item.Kind != VirtualFolderKind)
                {
                    string file = null;
                    try { file = item.FileNames[1]; } catch (Exception) { }
                    if (file != null && File.Exists(file)) return Path.GetDirectoryName(file);
                    if (file != null && Directory.Exists(file)) return file;
                }
                // UE filters mirror the folders under the project directory: rebuild the path from their names.
                var names = new List<string>();
                for (var current = item; current != null; current = current.Collection?.Parent as ProjectItem)
                    names.Insert(0, current.Name);
                var directory = Path.Combine(new[] { project.ProjectDirectory }.Concat(names).ToArray());
                return Directory.Exists(directory) ? directory : null;
            }
            catch (Exception e)
            {
                Log.Write("NewUnrealClass: selected folder: " + e.Message);
                return null;
            }
        }

        static async Task<string> GetActiveDocumentDirectoryAsync()
        {
            var view = await VS.Documents.GetActiveDocumentViewAsync();
            var path = view?.FilePath;
            return path != null && WorkspaceService.IsUnrealFile(path) ? Path.GetDirectoryName(path) : null;
        }
    }
}
