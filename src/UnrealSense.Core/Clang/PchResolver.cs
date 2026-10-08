using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnrealSense.Clang
{
    /// <summary>
    /// The precompiled header UnrealBuildTool force-includes for a source file in a real build. UBT generates the
    /// clang database with -NoPCH, so code that relies on its PCH for some includes (GEngine, DrawDebugSphere,
    /// TSubclassOf... without the matching #include) fails to compile in clangd. Force-including the same header
    /// reproduces the real build environment for every module at once:
    /// <list type="bullet">
    /// <item>PCHUsage = NoPCHs: none;</item>
    /// <item>a PrivatePCHHeaderFile (unless PCHUsage = UseSharedPCHs): that header;</item>
    /// <item>otherwise the shared PCH of the largest module among its (transitive) dependencies that provides one
    /// (UnrealEd, Engine, Slate, CoreUObject, Core), like UBT picks the shared PCH with the most dependencies.</item>
    /// </list>
    /// </summary>
    public sealed class PchResolver
    {
        static readonly Regex QuotedName = new Regex("\"([A-Za-z_][A-Za-z0-9_]*)\"", RegexOptions.Compiled);
        static readonly Regex PchUsage = new Regex(@"PCHUsage\s*=\s*(?:ModuleRules\s*\.\s*)?PCHUsageMode\s*\.\s*(\w+)", RegexOptions.Compiled);
        static readonly Regex PrivatePch = new Regex("PrivatePCHHeaderFile\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex SharedPch = new Regex("SharedPCHHeaderFile\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        sealed class Module
        {
            public string Name;
            public string Directory;
            public string Usage;
            public string PrivateHeader;
            public string SharedHeader;
            public List<string> Dependencies;
            public List<string> PublicDependencies;
        }

        readonly Dictionary<string, Module> byName = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Module> byDirectory = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> sharedForModule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Module> moduleOfFolder = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        readonly List<Module> sharedProviders;
        static readonly string[] KnownSharedPchOrder = { "UnrealEd", "Engine", "Slate", "CoreUObject", "Core" };

        /// <param name="roots">Folders to scan for *.Build.cs (engine Source and Plugins, project Source and Plugins).</param>
        public PchResolver(IEnumerable<string> roots)
        {
            foreach (var root in roots.Where(Directory.Exists))
                foreach (var rules in EnumerateBuildFiles(root))
                {
                    var module = Parse(rules);
                    if (module == null) continue;
                    if (!byName.ContainsKey(module.Name)) byName[module.Name] = module;
                    byDirectory[module.Directory] = module;
                }
            // Largest shared PCH first, like UBT (the one whose module has the most dependencies). Dependency counts
            // read from rules files are unreliable (cycles, quoted strings that are not modules), so the engine's
            // shared PCHs use their known order; other providers (plugins) come after them.
            sharedProviders = byName.Values.Where(m => m.SharedHeader != null)
                .OrderBy(m => { int i = Array.FindIndex(KnownSharedPchOrder, n => n.Equals(m.Name, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; })
                .ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        }

        public int ModuleCount => byName.Count;

        /// <summary>The header to force-include for <paramref name="sourceFile"/>, or null (no PCH / unknown module).</summary>
        public string Find(string sourceFile)
        {
            var module = ModuleOf(sourceFile);
            if (module == null) return null;
            var usage = module.Usage ?? "UseExplicitOrSharedPCHs";
            if (usage.Equals("NoPCHs", StringComparison.OrdinalIgnoreCase)) return null;
            if (module.PrivateHeader != null && !usage.Equals("UseSharedPCHs", StringComparison.OrdinalIgnoreCase)) return module.PrivateHeader;
            if (usage.Equals("NoSharedPCHs", StringComparison.OrdinalIgnoreCase)) return null;
            return SharedFor(module);
        }

        string SharedFor(Module module)
        {
            if (sharedForModule.TryGetValue(module.Name, out var cached)) return cached;
            var closure = Closure(module);
            string result = null;
            foreach (var provider in sharedProviders)
                if (provider != module && closure.Contains(provider.Name)) { result = provider.SharedHeader; break; }
            sharedForModule[module.Name] = result;
            return result;
        }

        /// <summary>Direct dependencies plus what they expose publicly, transitively (Unreal's include visibility).</summary>
        HashSet<string> Closure(Module module)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(module.Dependencies);
            while (stack.Count > 0)
            {
                var name = stack.Pop();
                if (!seen.Add(name) || !byName.TryGetValue(name, out var dep)) continue;
                foreach (var d in dep.PublicDependencies) if (!seen.Contains(d)) stack.Push(d);
            }
            return seen;
        }

        Module ModuleOf(string sourceFile)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFile.Replace('/', '\\')));
            var visited = new List<string>();
            Module found = null;
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++, dir = Path.GetDirectoryName(dir))
            {
                if (moduleOfFolder.TryGetValue(dir, out found)) break;
                visited.Add(dir);
                if (byDirectory.TryGetValue(dir, out found)) break;
            }
            // Generated sources live under Intermediate: ".../Development/<Module>/Module.X.gen.cpp" or ".../Inc/<Module>/UHT/".
            if (found == null)
            {
                var parts = Path.GetFullPath(sourceFile.Replace('/', '\\')).Split('\\');
                for (int i = parts.Length - 2; i > 0 && found == null; i--)
                    if ((parts[i - 1].Equals("Development", StringComparison.OrdinalIgnoreCase) || parts[i - 1].Equals("DebugGame", StringComparison.OrdinalIgnoreCase)
                         || parts[i - 1].Equals("Shipping", StringComparison.OrdinalIgnoreCase) || parts[i - 1].Equals("Inc", StringComparison.OrdinalIgnoreCase))
                        && byName.TryGetValue(parts[i], out var generatedFor))
                        found = generatedFor;
            }
            foreach (var v in visited) moduleOfFolder[v] = found;
            return found;
        }

        static Module Parse(string rulesFile)
        {
            string text;
            try { text = File.ReadAllText(rulesFile); }
            catch (Exception) { return null; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(rulesFile));
            var name = Path.GetFileName(rulesFile);
            name = name.Substring(0, name.Length - ".Build.cs".Length);
            string Header(Regex r)
            {
                var m = r.Match(text);
                if (!m.Success) return null;
                var path = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('/', '\\')));
                return File.Exists(path) ? path.Replace('\\', '/') : null;
            }
            var usage = PchUsage.Match(text);
            return new Module
            {
                Name = name,
                Directory = dir,
                Usage = usage.Success ? usage.Groups[1].Value : null,
                PrivateHeader = Header(PrivatePch),
                SharedHeader = Header(SharedPch),
                PublicDependencies = DependencyNames(text, "Public", name),
                Dependencies = DependencyNames(text, "Public", name).Concat(DependencyNames(text, "Private", name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        /// <summary>Quoted names inside PublicDependencyModuleNames / PrivateDependencyModuleNames .Add(...) / .AddRange(...).</summary>
        static List<string> DependencyNames(string text, string visibility, string self)
        {
            var names = new List<string>();
            foreach (Match call in Regex.Matches(text, visibility + @"DependencyModuleNames\s*\.\s*Add(?:Range)?\s*\("))
            {
                int depth = 1, i = call.Index + call.Length, start = i;
                for (; i < text.Length && depth > 0; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                }
                foreach (Match m in QuotedName.Matches(text.Substring(start, Math.Max(0, i - start))))
                    if (!m.Groups[1].Value.Equals(self, StringComparison.OrdinalIgnoreCase)) names.Add(m.Groups[1].Value);
            }
            return names;
        }

        static IEnumerable<string> EnumerateBuildFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir, "*.Build.cs");
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var f in files) yield return f;
                foreach (var d in dirs)
                {
                    var leaf = Path.GetFileName(d);
                    if (leaf.Equals("Intermediate", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("Content", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Saved", StringComparison.OrdinalIgnoreCase)
                        || leaf.StartsWith(".", StringComparison.Ordinal)) continue;
                    stack.Push(d);
                }
            }
        }
    }
}
