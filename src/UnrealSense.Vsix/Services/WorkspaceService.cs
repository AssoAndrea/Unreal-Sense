using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using UnrealSense.Project;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Owns the <see cref="UnrealWorkspace"/> of the project currently being edited. Editor components ask for
    /// the workspace of a file; the first request for a new .uproject starts loading it in the background.
    /// </summary>
    internal static class WorkspaceService
    {
        static readonly object gate = new object();
        static readonly ConcurrentDictionary<string, string> uprojectByDirectory = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static UnrealWorkspace current;
        static string currentUProject;

        /// <summary>Raised on a background thread when the workspace is replaced or its indexes change.</summary>
        public static event EventHandler Changed;

        public static UnrealWorkspace Current => current;

        /// <summary>Workspace of a source file, or null if the file is not in an Unreal project (or still loading).</summary>
        public static UnrealWorkspace GetForFile(string filePath)
        {
            var uproject = FindUProjectCached(filePath);
            if (uproject == null) return null;
            var workspace = EnsureLoaded(uproject);
            return workspace.State == WorkspaceState.Ready ? workspace : null;
        }

        /// <summary>True when the file belongs to an Unreal project (cheap, cached per folder).</summary>
        public static bool IsUnrealFile(string filePath) => FindUProjectCached(filePath) != null;

        /// <summary>
        /// Loads the Unreal project of an opened solution. Engine-root ("native") solutions contain several games:
        /// the one in Options › UnrealSense › Unreal project wins, then the startup project, then the one named
        /// like the solution.
        /// </summary>
        public static void EnsureForSolution(string solutionPath, string startupProject)
        {
            var candidates = UnrealProject.FindUProjectsForSolution(solutionPath);
            var configured = Options.General.Instance.UProjectPath;
            string chosen = null;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim('"')))
                chosen = Path.GetFullPath(configured.Trim('"'));
            else if (candidates.Count == 1)
                chosen = candidates[0];
            else if (candidates.Count > 1)
            {
                string Stem(string p) => Path.GetFileNameWithoutExtension(p ?? "") ?? "";
                var startup = Stem(startupProject);
                if (startup.EndsWith("Editor", StringComparison.OrdinalIgnoreCase)) startup = startup.Substring(0, startup.Length - 6);
                chosen = candidates.FirstOrDefault(c => string.Equals(Stem(c), startup, StringComparison.OrdinalIgnoreCase))
                         ?? candidates.FirstOrDefault(c => string.Equals(Stem(c), Stem(solutionPath), StringComparison.OrdinalIgnoreCase))
                         ?? candidates[0];
                Log.Write($"Solution {Path.GetFileName(solutionPath)} contains {candidates.Count} Unreal projects: " +
                          string.Join(", ", candidates.Select(Path.GetFileNameWithoutExtension)) +
                          $". Using {Path.GetFileNameWithoutExtension(chosen)} (startup project: {startupProject ?? "none"}); " +
                          "set Tools › Options › UnrealSense › Unreal project to choose another.");
            }
            if (chosen == null)
            {
                Log.Write($"No .uproject found for {solutionPath} (looked above it, in *.uprojectdirs folders and in its .vcxproj files). " +
                          "Set Tools › Options › UnrealSense › Unreal project.");
                return;
            }
            EnsureLoaded(chosen);
        }

        public static void EnsureForPath(string path)
        {
            var uproject = UnrealProject.FindUProject(path);
            // A generated solution sits next to the .uproject; a folder may contain it directly.
            if (uproject == null && Directory.Exists(path))
            {
                foreach (var candidate in Directory.GetFiles(path, "*.uproject"))
                {
                    uproject = candidate;
                    break;
                }
            }
            if (uproject != null) EnsureLoaded(uproject);
        }

        public static UnrealWorkspace EnsureLoaded(string uprojectPath)
        {
            lock (gate)
            {
                if (current != null && SameFile(currentUProject, uprojectPath))
                    return current;

                var previous = current;
                previous?.Dispose();
                // Go to Symbol/File is hidden: its index is not built.
                var workspace = new UnrealWorkspace { BuildGoToIndex = false };
                workspace.Changed += (s, e) => Changed?.Invoke(s, EventArgs.Empty);
                workspace.Timing += t => Log.Write("Timing: " + t);
                current = workspace;
                currentUProject = uprojectPath;

                Log.Write($"Loading Unreal project {uprojectPath}");
                var progress = new Progress<string>(Log.Status);
                workspace.LoadAsync(uprojectPath, progress).ContinueWith(t =>
                {
                    if (workspace.State == WorkspaceState.Failed)
                        Log.Error("Failed to load project", workspace.LastError);
                    else
                        Log.Write($"{workspace.StatusText}. Engine: {workspace.Project?.Engine?.ToString() ?? "not found"}. " +
                                  $"Specifiers: {workspace.Catalog.Count}{(workspace.Catalog.IsAuthoritative ? " (from UHT sources)" : " (built-in)")}.");
                    if (workspace.AssetState == WorkspaceState.Failed)
                        Log.Error("Blueprint index failed", workspace.LastError);
                    if (workspace.State == WorkspaceState.Ready && current == workspace)
                    {
                        OwnIndexService.OnWorkspaceReady(workspace);
                    }
                }, System.Threading.Tasks.TaskScheduler.Default);

                Changed?.Invoke(workspace, EventArgs.Empty);
                return workspace;
            }
        }

        public static void Reload()
        {
            string path;
            lock (gate)
            {
                path = currentUProject;
                current?.Dispose();
                current = null;
                currentUProject = null;
            }
            if (path != null) EnsureLoaded(path);
        }

        public static void Close()
        {
            lock (gate)
            {
                current?.Dispose();
                current = null;
                currentUProject = null;
            }
            System.Threading.Tasks.Task.Run(() => OwnIndexService.Stop());
            Changed?.Invoke(null, EventArgs.Empty);
        }

        static string FindUProjectCached(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            var dir = Path.GetDirectoryName(filePath);
            if (dir == null) return null;
            var uproject = uprojectByDirectory.GetOrAdd(dir, d => UnrealProject.FindUProject(d));
            if (uproject != null) return uproject;

            // Engine and engine-plugin files belong to the loaded project's workspace (they have no .uproject above).
            var ws = current;
            var engineDir = ws?.Project?.Engine?.EngineDirectory;
            if (engineDir != null && IsUnder(filePath, engineDir))
                return currentUProject;
            return null;
        }

        /// <summary>
        /// Same file, even when reached through a substituted drive, a junction or a link (S:\X is D:\workspaces\X):
        /// otherwise a path coming back in that other form would load the same project a second time.
        /// </summary>
        static bool SameFile(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || (a != null && b != null && string.Equals(RealPaths.CanonicalPath(a), RealPaths.CanonicalPath(b), StringComparison.OrdinalIgnoreCase));

        static bool IsUnder(string file, string directory)
        {
            bool Under(string f, string d) => f.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            return Under(Path.GetFullPath(file), Path.GetFullPath(directory))
                   || Under(RealPaths.CanonicalPath(file), RealPaths.CanonicalPath(directory));
        }
    }
}
