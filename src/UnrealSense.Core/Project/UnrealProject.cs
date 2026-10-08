using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Project
{
    public enum ModuleOwnerKind { Project, ProjectPlugin, Engine, EnginePlugin }

    /// <summary>A UBT module, i.e. a folder with a *.Build.cs file.</summary>
    public sealed class UnrealModule
    {
        public string Name { get; set; }
        public string Directory { get; set; }
        public string BuildCsPath { get; set; }
        public string Type { get; set; }
        public ModuleOwnerKind Owner { get; set; }
        public string PluginName { get; set; }
        public IReadOnlyList<string> PublicDependencies { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> PrivateDependencies { get; set; } = Array.Empty<string>();

        public string ApiMacro => Name.ToUpperInvariant() + "_API";

        public override string ToString() => Name;
    }

    /// <summary>A .uplugin and its modules.</summary>
    public sealed class UnrealPlugin
    {
        public string Name { get; set; }
        public string FriendlyName { get; set; }
        public string UPluginPath { get; set; }
        public string Directory => Path.GetDirectoryName(UPluginPath);
        public bool CanContainContent { get; set; }
        /// <summary>The .uplugin's EnabledByDefault (true when absent, as for a project's own plugins).</summary>
        public bool EnabledByDefault { get; set; } = true;
        public bool IsEngine { get; set; }
        public List<UnrealModule> Modules { get; } = new List<UnrealModule>();
        public override string ToString() => Name;
    }

    /// <summary>A mount point mapping a long package root ("/Game", "/MyPlugin") to a Content folder.</summary>
    public sealed class ContentRoot
    {
        public ContentRoot(string mountPoint, string directory)
        {
            MountPoint = mountPoint;
            Directory = directory;
        }

        public string MountPoint { get; }
        public string Directory { get; }
    }

    /// <summary>In-memory model of a .uproject, its modules, plugins and content roots.</summary>
    public sealed class UnrealProject
    {
        public string UProjectPath { get; private set; }
        public string Name => Path.GetFileNameWithoutExtension(UProjectPath);
        public string ProjectDirectory => Path.GetDirectoryName(UProjectPath);
        public string SourceDirectory => Path.Combine(ProjectDirectory, "Source");
        public string ContentDirectory => Path.Combine(ProjectDirectory, "Content");
        public string ConfigDirectory => Path.Combine(ProjectDirectory, "Config");
        public string EngineAssociation { get; private set; }
        public EngineInstallation Engine { get; private set; }
        public List<UnrealModule> Modules { get; } = new List<UnrealModule>();
        public List<UnrealPlugin> Plugins { get; } = new List<UnrealPlugin>();
        public List<string> EnabledPluginNames { get; } = new List<string>();
        public CoreRedirects Redirects { get; private set; } = new CoreRedirects();

        public IEnumerable<UnrealModule> AllModules => Modules.Concat(Plugins.SelectMany(p => p.Modules));

        public IEnumerable<ContentRoot> ContentRoots
        {
            get
            {
                yield return new ContentRoot("/Game", ContentDirectory);
                foreach (var plugin in Plugins.Where(p => p.CanContainContent))
                    yield return new ContentRoot("/" + plugin.Name, Path.Combine(plugin.Directory, "Content"));
            }
        }

        /// <summary>Walks up from a file or folder looking for a .uproject.</summary>
        public static string FindUProject(string startPath)
        {
            if (string.IsNullOrEmpty(startPath)) return null;
            var dir = Directory.Exists(startPath) ? new DirectoryInfo(startPath) : new FileInfo(startPath).Directory;
            while (dir != null)
            {
                try
                {
                    var found = dir.GetFiles("*.uproject", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (found != null) return found.FullName;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// The .uproject files a solution belongs to. A solution generated next to a project finds it by walking up;
        /// a "native" solution at the engine root (projects in folders listed by *.uprojectdirs) finds them through
        /// the .uprojectdirs folders and through the -project="...uproject" arguments in its .vcxproj files.
        /// </summary>
        public static List<string> FindUProjectsForSolution(string solutionPath)
        {
            var result = new List<string>();
            void Add(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    path = Path.GetFullPath(path);
                    if (File.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                }
                catch (Exception) { }
            }

            Add(FindUProject(solutionPath));
            if (result.Count > 0) return result;
            var root = Directory.Exists(solutionPath) ? solutionPath : Path.GetDirectoryName(solutionPath);
            if (root == null) return result;

            // Engine-root layout: *.uprojectdirs lists the folders that contain projects (one level of subfolders).
            try
            {
                foreach (var dirsFile in Directory.GetFiles(root, "*.uprojectdirs"))
                    foreach (var line in File.ReadAllLines(dirsFile))
                    {
                        var entry = line.Trim();
                        if (entry.Length == 0 || entry.StartsWith(";")) continue;
                        var folder = Path.Combine(root, entry);
                        if (!Directory.Exists(folder)) continue;
                        foreach (var sub in Directory.GetDirectories(folder))
                            foreach (var uproject in Directory.GetFiles(sub, "*.uproject")) Add(uproject);
                    }
            }
            catch (Exception) { }

            // Projects referenced by the solution's .vcxproj build commands.
            if (File.Exists(solutionPath))
            {
                try
                {
                    var solution = File.ReadAllText(solutionPath);
                    foreach (Match m in Regex.Matches(solution, @"[""']([^""'<>|]+?\.vcxproj)[""']"))
                    {
                        var vcxproj = Path.Combine(root, m.Groups[1].Value.Replace('/', '\\'));
                        if (!File.Exists(vcxproj)) continue;
                        Add(FindUProject(vcxproj));
                        foreach (Match u in Regex.Matches(ReadPrefix(vcxproj, 1 << 20), @"([A-Za-z]:\\[^""';<>|]+?\.uproject|\$\(SolutionDir\)[^""';<>|]+?\.uproject)"))
                            Add(u.Value.Replace("$(SolutionDir)", root + "\\").Replace("&quot;", ""));
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        /// <summary>The beginning of a file: UBT writes the NMake build commands before the (huge) file lists.</summary>
        static string ReadPrefix(string path, int maxChars)
        {
            using (var reader = new StreamReader(path))
            {
                var buffer = new char[maxChars];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, read);
            }
        }

        public static UnrealProject Load(string uprojectPath)
        {
            var project = new UnrealProject { UProjectPath = Path.GetFullPath(uprojectPath) };
            var json = JObject.Parse(File.ReadAllText(uprojectPath));
            project.EngineAssociation = (string)json["EngineAssociation"] ?? string.Empty;
            project.Engine = EngineLocator.Resolve(project.EngineAssociation, project.ProjectDirectory);

            var declaredTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (json["Modules"] is JArray modules)
                foreach (var m in modules)
                    declaredTypes[(string)m["Name"] ?? ""] = (string)m["Type"];

            if (json["Plugins"] is JArray plugins)
                foreach (var p in plugins)
                    if ((bool?)p["Enabled"] ?? false)
                        project.EnabledPluginNames.Add((string)p["Name"]);

            foreach (var module in ModuleScanner.ScanModules(project.SourceDirectory, ModuleOwnerKind.Project, null))
            {
                module.Type = declaredTypes.TryGetValue(module.Name, out var type) ? type : null;
                project.Modules.Add(module);
            }

            var pluginsDir = Path.Combine(project.ProjectDirectory, "Plugins");
            foreach (var plugin in ModuleScanner.ScanPlugins(pluginsDir, isEngine: false))
                project.Plugins.Add(plugin);

            // Plugins the project loads from other folders ("AdditionalPluginDirectories": shared team plugins): part of
            // the project like those in Plugins/, except the ones the .uproject turns off or that are off by default
            // without being turned on (such folders often hold plugins of several games).
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (json["Plugins"] is JArray pluginList)
                foreach (var p in pluginList)
                    if ((bool?)p["Enabled"] == false) disabled.Add((string)p["Name"] ?? "");
            if (json["AdditionalPluginDirectories"] is JArray additional)
                foreach (var entry in additional)
                {
                    var relative = (string)entry;
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    string directory;
                    try { directory = Path.GetFullPath(Path.Combine(project.ProjectDirectory, relative)); }
                    catch (ArgumentException) { continue; }
                    foreach (var plugin in ModuleScanner.ScanPlugins(directory, isEngine: false))
                    {
                        if (disabled.Contains(plugin.Name) || project.Plugins.Any(p => string.Equals(p.Name, plugin.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        if (!plugin.EnabledByDefault && !project.EnabledPluginNames.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        project.Plugins.Add(plugin);
                    }
                }

            project.Redirects = CoreRedirects.Load(project);
            return project;
        }

        /// <summary>Maps a long package name ("/Game/Foo/BP_Bar") to a file path, or null.</summary>
        public string PackageNameToFile(string packageName)
        {
            foreach (var root in ContentRoots)
            {
                var prefix = root.MountPoint + "/";
                if (!packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = packageName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                foreach (var ext in new[] { ".uasset", ".umap" })
                {
                    var candidate = Path.Combine(root.Directory, relative + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>Maps an asset file to its long package name ("/Game/Foo/BP_Bar").</summary>
        public string FileToPackageName(string filePath)
        {
            var full = Path.GetFullPath(filePath);
            foreach (var root in ContentRoots)
            {
                var dir = root.Directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = full.Substring(dir.Length);
                relative = Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, '/');
                return root.MountPoint + "/" + relative;
            }
            return null;
        }

        /// <summary>Module that owns a source file, if any.</summary>
        public UnrealModule FindModuleForFile(string filePath)
        {
            var full = Path.GetFullPath(filePath);
            return AllModules
                .Where(m => full.StartsWith(m.Directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Directory.Length)
                .FirstOrDefault();
        }
    }
}
