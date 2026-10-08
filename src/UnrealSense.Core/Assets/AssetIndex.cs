using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnrealSense.Project;

namespace UnrealSense.Assets
{
    public enum AssetUsageKind
    {
        /// <summary>Blueprint derives (directly or through other Blueprints) from the class.</summary>
        Subclass,
        /// <summary>Blueprint graph calls the function.</summary>
        FunctionCall,
        /// <summary>Blueprint implements/overrides the event (BlueprintImplementableEvent / BlueprintNativeEvent).</summary>
        EventImplementation,
        /// <summary>Asset references the type (variable, cast, spawn, placed actor...).</summary>
        TypeReference,
        /// <summary>Asset mentions the property name and is related to the owner class (heuristic).</summary>
        PropertyReference,
    }

    public sealed class AssetUsage
    {
        public AssetUsage(AssetRecord asset, AssetUsageKind kind, string detail = null)
        {
            Asset = asset;
            Kind = kind;
            Detail = detail;
        }

        public AssetRecord Asset { get; }
        public AssetUsageKind Kind { get; }
        public string Detail { get; }

        public bool IsHeuristic => Kind == AssetUsageKind.PropertyReference;
    }

    /// <summary>
    /// Offline index of project content (.uasset/.umap headers). Works with the editor closed, like Rider:
    /// we only read the name/import/export tables, which is enough to know which Blueprints derive from,
    /// call into or override which C++ symbols.
    /// </summary>
    public sealed class AssetIndex
    {
        const int CacheVersion = 3;
        static readonly string[] AssetExtensions = { "*.uasset", "*.umap" };

        readonly UnrealProject project;
        volatile HashSet<string> projectScriptPackages;
        ConcurrentDictionary<string, AssetRecord> records = new ConcurrentDictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        volatile Lookups lookups = Lookups.Empty;

        public AssetIndex(UnrealProject project)
        {
            this.project = project;
            projectScriptPackages = new HashSet<string>(
                project.AllModules.Select(m => "/Script/" + m.Name), StringComparer.OrdinalIgnoreCase);
        }

        public event EventHandler Updated;

        public int Count => records.Count;
        public IEnumerable<AssetRecord> Assets => records.Values;

        public static string DefaultCachePath(UnrealProject project)
        {
            // string.GetHashCode is randomized per process on .NET Core: use a stable FNV-1a hash instead.
            uint hash = 2166136261;
            foreach (var c in project.UProjectPath.ToUpperInvariant())
                hash = (hash ^ c) * 16777619;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnrealSense", "Cache", $"{project.Name}-{hash:x8}", "assets.json");
        }

        /// <summary>Scans all content roots, reusing cached records for unchanged files.</summary>
        public void Build(string cachePath = null, IProgress<(int Done, int Total)> progress = null, CancellationToken cancellationToken = default)
        {
            var cached = LoadCache(cachePath);
            var files = project.ContentRoots
                .Where(r => Directory.Exists(r.Directory))
                .SelectMany(r => AssetExtensions.SelectMany(ext => ModuleScanner.EnumerateFiles(r.Directory, ext)))
                .ToList();

            var fresh = new ConcurrentDictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            int done = 0, analyzed = 0;
            Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, file =>
            {
                var info = new FileInfo(file);
                if (cached.TryGetValue(file, out var record) && record.FileSize == info.Length && record.LastWriteUtc == info.LastWriteTimeUtc)
                    fresh[file] = record;
                else
                {
                    fresh[file] = Analyze(file);
                    Interlocked.Increment(ref analyzed);
                }

                var n = Interlocked.Increment(ref done);
                if (progress != null && (n % 200 == 0 || n == files.Count))
                    progress.Report((n, files.Count));
            });

            records = fresh;
            LastBuildAnalyzed = analyzed;
            RebuildLookups();
            // Rewriting the cache is expensive on large projects: only do it when something changed.
            if (analyzed > 0 || fresh.Count != cached.Count) SaveCache(cachePath);
            Updated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// What the index found, for the log: unreadable assets (with the most common reasons) and, per project C++
        /// module, how many assets derive from / reference its classes. A module with no references at all while others
        /// have some points at the asset side (packages not read, references under another /Script name).
        /// </summary>
        public List<string> DescribeCoverage()
        {
            var all = records.Values.ToList();
            var lines = new List<string>();
            var failed = all.Where(r => r.Error != null).ToList();
            lines.Add($"{all.Count:N0} assets, {all.Count(r => r.IsBlueprint):N0} Blueprints, {failed.Count:N0} not readable");
            foreach (var g in failed.GroupBy(r => r.Error).OrderByDescending(g => g.Count()).Take(3))
                lines.Add($"    {g.Count():N0} × {g.Key} (e.g. {g.First().PackageName})");
            var perModule = new Dictionary<string, (HashSet<string> Assets, int Parents)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in all)
            {
                void Count(NativeSymbolRef s, bool parent)
                {
                    if (s?.Module == null || !IsProjectSymbol(s)) return;
                    var m = ModuleOf(s);
                    if (!perModule.TryGetValue(m, out var entry)) perModule[m] = entry = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                    entry.Assets.Add(r.FilePath);
                    if (parent) perModule[m] = (entry.Assets, entry.Parents + 1);
                }
                Count(r.NativeParent, true);
                foreach (var s in r.ClassReferences.Concat(r.FunctionCalls).Concat(r.ImplementedEvents)) Count(s, false);
            }
            foreach (var module in project.AllModules.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                perModule.TryGetValue(module, out var entry);
                lines.Add($"    /Script/{module}: {entry.Assets?.Count ?? 0:N0} assets use it, {entry.Parents:N0} Blueprints derive from its classes");
            }
            // Native parents outside the project's modules (engine ones, and renamed modules without a redirect).
            var foreign = all.Select(r => r.NativeParent)
                .Where(s => s?.Module != null && !IsProjectSymbol(s))
                .GroupBy(s => s.Module, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(10).ToList();
            if (foreign.Count > 0)
                lines.Add("    Blueprints deriving from other /Script packages: " + string.Join(", ", foreign.Select(g => $"{g.Key} ({g.Count():N0})")));
            return lines;
        }

        /// <summary>Assets parsed (not served from cache) by the last <see cref="Build"/>.</summary>
        public int LastBuildAnalyzed { get; private set; }

        /// <summary>Re-reads a single asset after a file system change.</summary>
        public void Refresh(string file)
        {
            if (File.Exists(file))
                records[file] = Analyze(file);
            else
                records.TryRemove(file, out _);
            RebuildLookups();
            Updated?.Invoke(this, EventArgs.Empty);
        }

        public AssetRecord Analyze(string file)
        {
            var info = new FileInfo(file);
            var record = new AssetRecord
            {
                FilePath = file,
                PackageName = project.FileToPackageName(file),
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            };

            try
            {
                var package = UAssetPackage.Read(file);
                Populate(record, package);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is EndOfStreamException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                record.Error = ex.Message;
            }
            return record;
        }

        void Populate(AssetRecord record, UAssetPackage package)
        {
            var assetName = Path.GetFileNameWithoutExtension(record.FilePath);
            // Function imports that only exist as the SuperStruct of an overriding event are not calls.
            var superOnlyImports = new HashSet<int>(package.Exports.Where(e => e.SuperIndex < 0).Select(e => e.SuperIndex));

            for (int i = 0; i < package.Exports.Count; i++)
            {
                var export = package.Exports[i];
                var className = package.GetObjectName(export.ClassIndex);
                if (export.OuterIndex == 0 && string.Equals(export.ObjectName, assetName, StringComparison.OrdinalIgnoreCase))
                    record.AssetClass = className;

                // The generated class carries the parent in SuperStruct.
                if (export.OuterIndex == 0 && className != null && className.EndsWith("GeneratedClass", StringComparison.Ordinal) && export.SuperIndex < 0)
                {
                    var parent = ResolveNative(package, export.SuperIndex);
                    if (parent != null)
                        record.NativeParent = parent;
                    else
                        record.BlueprintParent = package.GetImportPackage(export.SuperIndex);
                }

                // A function export whose SuperStruct is a native function = event implementation / override.
                if (className == "Function" && export.SuperIndex < 0)
                {
                    var overridden = ResolveNativeMember(package, export.SuperIndex);
                    if (overridden != null && !record.ImplementedEvents.Contains(overridden))
                        record.ImplementedEvents.Add(overridden);
                }
            }

            for (int i = 0; i < package.Imports.Count; i++)
            {
                var import = package.Imports[i];
                int index = -i - 1;
                switch (import.ClassName)
                {
                    case "Class":
                    case "ScriptStruct":
                    case "Enum":
                    case "Interface":
                        var type = ResolveNative(package, index);
                        if (type != null && !record.ClassReferences.Contains(type))
                            record.ClassReferences.Add(type);
                        break;
                    case "Function":
                        if (superOnlyImports.Contains(index)) break;
                        var function = ResolveNativeMember(package, index);
                        if (function != null && !record.FunctionCalls.Contains(function) && !function.Member.EndsWith("__DelegateSignature", StringComparison.Ordinal))
                            record.FunctionCalls.Add(function);
                        break;
                }
            }

            bool touchesProject = record.ClassReferences.Any(IsProjectSymbol)
                             || record.FunctionCalls.Any(IsProjectSymbol)
                             || (record.NativeParent != null && IsProjectSymbol(record.NativeParent))
                             || record.BlueprintParent != null;
            if (touchesProject)
                record.Names = package.Names.ToList();
        }

        bool IsProjectSymbol(NativeSymbolRef symbol) => projectScriptPackages.Contains("/Script/" + symbol.Module);

        /// <summary>Resolves a type import in a /Script package, applying config redirects.</summary>
        NativeSymbolRef ResolveNative(UAssetPackage package, int index)
        {
            var import = package.GetImport(index);
            if (import == null) return null;
            var outer = package.GetImport(import.OuterIndex);
            if (outer == null || outer.ClassName != "Package" || !outer.ObjectName.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
                return null;
            var scriptPackage = project.Redirects.RedirectPackage(outer.ObjectName);
            return new NativeSymbolRef(scriptPackage.Substring("/Script/".Length), project.Redirects.RedirectClass(import.ObjectName));
        }

        /// <summary>Resolves a function import whose outer is a native class.</summary>
        NativeSymbolRef ResolveNativeMember(UAssetPackage package, int index)
        {
            var import = package.GetImport(index);
            if (import == null || import.ClassName != "Function") return null;
            var owner = ResolveNative(package, import.OuterIndex);
            if (owner == null) return null;
            owner.Member = project.Redirects.RedirectFunction(owner.Type, import.ObjectName);
            return owner;
        }

        /// <summary>Inverted indexes so per-symbol queries don't scan every asset (projects can have 100k+ assets).</summary>
        sealed class Lookups
        {
            public static readonly Lookups Empty = Build(Enumerable.Empty<AssetRecord>());

            public ILookup<string, AssetRecord> ChildrenByBlueprint;
            public ILookup<string, AssetRecord> ByNativeParent;
            public ILookup<string, (AssetRecord Record, NativeSymbolRef Symbol)> TypeRefs;
            public ILookup<string, (AssetRecord Record, NativeSymbolRef Symbol, bool IsEvent)> MemberRefs;
            public ILookup<string, AssetRecord> ByName;

            public static Lookups Build(IEnumerable<AssetRecord> records)
            {
                var list = records.ToList();
                var ci = StringComparer.OrdinalIgnoreCase;
                return new Lookups
                {
                    ChildrenByBlueprint = list.Where(r => r.BlueprintParent != null).ToLookup(r => r.BlueprintParent, ci),
                    ByNativeParent = list.Where(r => r.NativeParent != null).ToLookup(r => r.NativeParent.Type, ci),
                    TypeRefs = list.SelectMany(r => r.ClassReferences.Select(c => (r, c))).ToLookup(x => x.c.Type, ci),
                    MemberRefs = list.SelectMany(r => r.FunctionCalls.Select(f => (r, f, false)).Concat(r.ImplementedEvents.Select(f => (r, f, true))))
                        .ToLookup(x => x.Item2.Type + ":" + x.Item2.Member, ci),
                    ByName = list.Where(r => r.Names != null).SelectMany(r => r.Names.Distinct().Select(n => (n, r))).ToLookup(x => x.n, x => x.r, StringComparer.Ordinal),
                };
            }
        }

        void RebuildLookups() => lookups = Lookups.Build(records.Values);

        // ---------------------------------------------------------------- queries

        /// <summary>Blueprints deriving from a native class, directly or through other Blueprints.</summary>
        public IEnumerable<AssetRecord> GetDerivedBlueprints(string module, string type)
        {
            var current = lookups;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<AssetRecord>(current.ByNativeParent[type].Where(r => Matches(r.NativeParent, module, type)));
            // Blueprints of C++ subclasses derive from this class too (A <- B (C++) <- BP_B).
            foreach (var (subModule, subType) in NativeSubclasses?.Invoke(module, type) ?? Enumerable.Empty<(string, string)>())
                foreach (var r in current.ByNativeParent[subType].Where(r => Matches(r.NativeParent, subModule, subType)))
                    queue.Enqueue(r);
            while (queue.Count > 0)
            {
                var record = queue.Dequeue();
                if (record.PackageName == null || !visited.Add(record.PackageName)) continue;
                yield return record;
                foreach (var kid in current.ChildrenByBlueprint[record.PackageName]) queue.Enqueue(kid);
            }
        }

        public List<AssetUsage> FindTypeUsages(string module, string type)
        {
            var result = new List<AssetUsage>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bp in GetDerivedBlueprints(module, type))
            {
                seen.Add(bp.FilePath);
                var detail = bp.NativeParent == null ? "via " + ShortPackage(bp.BlueprintParent)
                    : Matches(bp.NativeParent, module, type) ? null
                    : "via C++ " + bp.NativeParent.Type;
                result.Add(new AssetUsage(bp, AssetUsageKind.Subclass, detail));
            }
            foreach (var (record, symbol) in lookups.TypeRefs[type])
            {
                if (!Matches(symbol, module, type) || !seen.Add(record.FilePath)) continue;
                result.Add(new AssetUsage(record, AssetUsageKind.TypeReference, record.IsLevel ? "level" : null));
            }
            return Sorted(result);
        }

        public List<AssetUsage> FindFunctionUsages(string module, string type, string function)
        {
            var result = new List<AssetUsage>();
            var seen = new HashSet<(string, bool)>();
            foreach (var (record, symbol, isEvent) in lookups.MemberRefs[type + ":" + function])
            {
                if (!Matches(symbol, module, type) || !seen.Add((record.FilePath.ToLowerInvariant(), isEvent))) continue;
                result.Add(new AssetUsage(record, isEvent ? AssetUsageKind.EventImplementation : AssetUsageKind.FunctionCall));
            }
            return Sorted(result);
        }

        /// <summary>
        /// Properties are FProperty (not UObjects), so they never appear as imports. We report assets that
        /// both relate to the owner class (derive from / reference it) and carry the property name in their
        /// name map: this catches variable get/set nodes and overridden defaults.
        /// </summary>
        public List<AssetUsage> FindPropertyUsages(string module, string type, string property)
        {
            var current = lookups;
            var candidates = current.ByName[property].ToList();
            if (candidates.Count == 0) return new List<AssetUsage>();

            var related = new HashSet<string>(GetDerivedBlueprints(module, type).Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var (record, symbol) in current.TypeRefs[type])
                if (Matches(symbol, module, type)) related.Add(record.FilePath);

            var result = candidates
                .Where(r => related.Contains(r.FilePath))
                .Distinct()
                .Select(r => new AssetUsage(r, AssetUsageKind.PropertyReference))
                .ToList();
            return Sorted(result);
        }

        bool Matches(NativeSymbolRef symbol, string module, string type) =>
            (module == null || string.Equals(ModuleOf(symbol), module, StringComparison.OrdinalIgnoreCase))
            && string.Equals(symbol.Type, type, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Project C++ subclasses (module, name without prefix) of a native class, at any depth; set by the workspace from
        /// the parsed headers. Assets only record a Blueprint's direct native parent.
        /// </summary>
        public Func<string, string, IEnumerable<(string Module, string Type)>> NativeSubclasses { get; set; }

        // /Script package -> project module, for packages that are another name of a project module (see LearnModuleAliases).
        volatile Dictionary<string, string> moduleAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string ModuleOf(NativeSymbolRef symbol) =>
            symbol.Module != null && moduleAliases.TryGetValue(symbol.Module, out var module) ? module : symbol.Module;

        /// <summary>The project module a native symbol belongs to (renamed packages included), or null for engine symbols.</summary>
        public string ProjectModuleOf(NativeSymbolRef symbol) =>
            symbol?.Module != null && IsProjectSymbol(symbol) ? ModuleOf(symbol) : null;

        /// <summary>
        /// Finds /Script packages that are another name of a project module: the package is not a module of the
        /// project, yet the classes assets look for in it are (mostly) classes of one project module. That is what a
        /// renamed module looks like when the redirect that keeps old assets working is not in a config file we read
        /// (registered from C++ at startup, or in a plugin's own config). Such packages are then treated as that module.
        /// </summary>
        /// <param name="projectTypes">Reflected type names (without the U/A/F prefix) of each project module.</param>
        /// <returns>One line per alias, for the log.</returns>
        public List<string> LearnModuleAliases(IEnumerable<(string Module, string Type)> projectTypes)
        {
            var modulesOfType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (module, type) in projectTypes)
            {
                if (module == null || type == null) continue;
                if (!modulesOfType.TryGetValue(type, out var set)) modulesOfType[type] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(module);
            }
            // Distinct type names looked for in each package that is not a project module.
            var typesByPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // Real module names only: aliases learned earlier are learned again (calling this twice must not lose them).
            var modules = new HashSet<string>(project.AllModules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var r in records.Values)
                foreach (var symbol in r.ClassReferences.Concat(r.FunctionCalls).Concat(r.ImplementedEvents).Concat(new[] { r.NativeParent }))
                {
                    if (symbol?.Module == null || symbol.Type == null || modules.Contains(symbol.Module)) continue;
                    if (!typesByPackage.TryGetValue(symbol.Module, out var set)) typesByPackage[symbol.Module] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(symbol.Type);
                }
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var package in typesByPackage)
            {
                var best = package.Value.SelectMany(t => modulesOfType.TryGetValue(t, out var m) ? m : Enumerable.Empty<string>())
                    .GroupBy(m => m, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (best == null) continue;
                int matched = best.Count(), total = package.Value.Count;
                // Most of what is looked for there exists in that module (engine packages share a name or two at most).
                if (matched >= 2 && matched * 2 >= total)
                {
                    aliases[package.Key] = best.Key;
                    lines.Add($"/Script/{package.Key} treated as /Script/{best.Key}: {matched} of the {total} classes assets look for there are classes of {best.Key} (renamed module?)");
                }
            }
            moduleAliases = aliases;
            projectScriptPackages = new HashSet<string>(project.AllModules.Select(m => "/Script/" + m.Name).Concat(aliases.Keys.Select(a => "/Script/" + a)), StringComparer.OrdinalIgnoreCase);
            if (aliases.Count > 0) Updated?.Invoke(this, EventArgs.Empty);
            return lines;
        }

        static List<AssetUsage> Sorted(List<AssetUsage> usages) =>
            usages.OrderBy(u => u.Kind).ThenBy(u => u.Asset.PackageName, StringComparer.OrdinalIgnoreCase).ToList();

        static string ShortPackage(string package)
        {
            var slash = package?.LastIndexOf('/') ?? -1;
            return slash >= 0 ? package.Substring(slash + 1) : package;
        }

        // ---------------------------------------------------------------- cache

        sealed class CacheFile
        {
            public int Version { get; set; }
            public List<AssetRecord> Records { get; set; }
        }

        static Dictionary<string, AssetRecord> LoadCache(string cachePath)
        {
            try
            {
                if (cachePath != null && File.Exists(cachePath))
                {
                    CacheFile cache;
                    using (var reader = new JsonTextReader(new StreamReader(cachePath)))
                        cache = new JsonSerializer().Deserialize<CacheFile>(reader);
                    if (cache?.Version == CacheVersion && cache.Records != null)
                        return cache.Records.Where(r => r.FilePath != null)
                            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { }
            return new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        }

        void SaveCache(string cachePath)
        {
            if (cachePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                var cache = new CacheFile { Version = CacheVersion, Records = records.Values.ToList() };
                var temp = cachePath + ".tmp";
                using (var writer = new StreamWriter(temp))
                    new JsonSerializer { NullValueHandling = NullValueHandling.Ignore }.Serialize(writer, cache);
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(temp, cachePath);
            }
            catch (Exception) { }
        }
    }
}
