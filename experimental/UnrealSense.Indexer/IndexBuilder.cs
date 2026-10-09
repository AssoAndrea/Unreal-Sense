using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnrealSense.Indexer
{
    public sealed class BuildOptions
    {
        public string UProject;
        public bool Engine;            // index the whole engine (Source + Plugins), not only the headers the project includes
        public int Threads;            // 0 = all logical processors
        public Action<string> Log = Console.WriteLine;
        public string CompileDbPath;   // default: UnrealSense cache of the project
        public bool KeepSymbolTable;   // for debugging
        public string IndexPath;       // previous index (incremental build) and where the caller saves the new one; the build cache sits next to it
        public bool Full;              // ignore the build cache
    }

    public sealed class SourceFile
    {
        public int Id;
        public string Path;
        public byte[] Bytes;
        public bool IsProject;
        public bool Resolve;           // pass 2 (references) runs on this file
        public List<MacroDef> Defines;
        public HashSet<int> Undefs;
        public List<string> Includes;
        public List<(string Include, Dictionary<int, MacroDef> Macros)> IncludeContexts;
        public HashSet<int> UnknownInConditions;
        public Dictionary<int, MacroDef> OwnUndefined; // its own macros that start undefined (include guard, own defaults)
        public int Lines;
        public long Size, Ticks;       // from the directory enumeration
        public bool Changed;           // read again (new, or size/time differ from the build cache)
        public bool Reparsed;          // pass 1 ran on it in this build
        public CachedFile Cached;
        public ulong DeclHash;

        public void FromCache(CachedFile c)
        {
            Cached = c; Defines = c.Defines; Undefs = c.Undefs; Includes = c.Includes; IncludeContexts = c.IncludeContexts;
            UnknownInConditions = c.UnknownInConditions; Lines = c.Lines;
            if (Defines != null) foreach (var d in Defines) d.Origin = Path;
        }

        public void SetPhase0(PreprocessedFile pp)
        {
            Defines = pp.Defines; Undefs = pp.Undefs; Includes = pp.Includes; Lines = pp.Lines; IncludeContexts = pp.IncludeContexts;
            UnknownInConditions = pp.UnknownInConditions;
            if (Defines != null) foreach (var d in Defines) d.Origin = Path;
        }

        public CachedFile ToCache() => new CachedFile
        {
            Path = Path, Size = Size, Ticks = Ticks, Lines = Lines, Defines = Defines, Undefs = Undefs, Includes = Includes,
            IncludeContexts = IncludeContexts, UnknownInConditions = UnknownInConditions, DeclHash = DeclHash,
        };
    }

    public sealed class BuildStats
    {
        public int Files, ResolvedFiles;
        public long Bytes, Lines;
        public int Decls, Symbols, Refs, Unresolved;
        public int ResolvedMember, UnresolvedMemberUnknownRecv, UnresolvedMemberNotFound, UnresolvedNames;
        public double Discover, Phase0, Pass1, Merge, Pass2, Save, Total;
        public long PeakWorkingSet, ManagedHeap;
        public double CacheSave;
        public bool Incremental;
        public int ChangedFiles;
        public bool Unchanged;         // nothing changed: the previous index was returned as it is
        public int Threads;
        public long IndexBytes;
        public string IndexPath;
    }

    public sealed class IndexBuilder
    {
        static readonly HashSet<string> platformDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Android", "IOS", "Mac", "Linux", "LinuxArm64", "Unix", "TVOS", "VisionOS", "Apple", "HoloLens", "Lumin", "PS4", "PS5", "XboxOne",
            "XSX", "Switch", "WinGDK", "GDK", "Win32", "ThirdParty", "Hololens", "Quail", "VulkanAndroid",
        };
        static readonly string[] sourceExt = { ".h", ".cpp", ".inl", ".hpp", ".c", ".cc" };

        static string Mem() => $"[heap {GC.GetTotalMemory(false) / 1073741824.0:F2} GB, ws {Process.GetCurrentProcess().WorkingSet64 / 1073741824.0:F2} GB]";

        /// <summary>Testing: files treated as changed even if their size and time match the build cache.</summary>
        public static HashSet<string> ForceChanged;

        public SymbolTable Table;
        public List<SourceFile> FilesOut;

        /// <summary>Bumped when the cached phase 0 / pass 1 results or the index format change meaning.</summary>
        const string CacheFormat = "2026-10-09.1";

        public IndexData Build(BuildOptions o, out BuildStats stats)
        {
            var data = BuildCore(o, false, out stats);
            if (data != null) return data;
            // the incremental build found a change it cannot apply file by file (a macro of the global table changed)
            return BuildCore(o, true, out stats);
        }

        IndexData BuildCore(BuildOptions o, bool forceFull, out BuildStats stats)
        {
            stats = new BuildStats();
            tokenizeTicks = 0;
            var total = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            int threads = o.Threads > 0 ? o.Threads : Environment.ProcessorCount;
            stats.Threads = threads;
            var po = new ParallelOptions { MaxDegreeOfParallelism = threads };

            var uproject = o.UProject.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ? o.UProject : Directory.GetFiles(o.UProject, "*.uproject").First();
            var projectDir = CompileDb.Norm(Path.GetDirectoryName(Path.GetFullPath(uproject)));
            var projectName = Path.GetFileNameWithoutExtension(uproject);
            var dbPath = o.CompileDbPath ?? CompileDb.FindFor(projectName) ?? throw new InvalidOperationException("compile_commands.files.json not found for " + projectName + " (open the project once with UnrealSense)");
            var db = CompileDb.Load(dbPath);
            var engineDir = CompileDb.Norm(Path.GetFullPath(Path.Combine(db.EngineSourceDir, "..")));
            o.Log($"project {projectDir}, engine {engineDir}, compile db: {db.Files.Count} entries, {db.IncludeDirs.Count} include dirs, {db.Defines.Count} /D, {db.ForcedIncludes.Count} forced includes");

            // ---------------------------------------------------------------- macro environment (predefined + compile db)
            var env = MacroEnv.CreateDefault();
            // the module version id changes whenever the indexer's code changes (deterministic build): its caches are then discarded
            var envKey = new StringBuilder(CacheFormat).Append('|').Append(typeof(IndexBuilder).Assembly.ManifestModule.ModuleVersionId).Append('|').Append(o.Engine).Append('|').Append(projectDir).Append('|');
            foreach (var (n, v) in db.Defines) { env.DefineBase(n, v); envKey.Append(n).Append('=').Append(v).Append(';'); }
            foreach (var fi in db.ForcedIncludes)
            {
                if (!fi.EndsWith("Definitions.h", StringComparison.OrdinalIgnoreCase) || !File.Exists(fi)) continue;
                var bytes = File.ReadAllBytes(fi);
                envKey.Append(fi).Append('#').Append(BuildCache.HashBytes(bytes)).Append(';');
                var pp = new Preprocessor(env, true).Run(bytes, false, false);
                foreach (var d in pp.Defines) if (!env.Base.ContainsKey(d.Name) || !IsApiDefine(d)) env.Base[d.Name] = d;
            }
            foreach (var dir in db.IncludeDirs) envKey.Append(dir).Append(';');

            var cachePath = o.IndexPath != null ? o.IndexPath + ".cache" : null;
            var metaPath = o.IndexPath != null ? o.IndexPath + ".meta" : null;

            // ---------------------------------------------------------------- files
            var projectRoots = new[] { projectDir + "/Source", projectDir + "/Plugins", projectDir + "/Intermediate/Build/Win64/UnrealEditor/Inc" };
            var engineRoots = new[] { engineDir + "/Source/Runtime", engineDir + "/Source/Developer", engineDir + "/Source/Editor", engineDir + "/Plugins", engineDir + "/Intermediate/Build/Win64/UnrealEditor/Inc" };
            var projectFiles = Enumerate(projectRoots, po, p => !p.Contains("/Intermediate/") || p.Contains("/Intermediate/Build/Win64/UnrealEditor/Inc/"));
            var engineFiles = Enumerate(engineRoots, po, p => !p.Contains("/Intermediate/") || p.Contains("/Intermediate/Build/Win64/UnrealEditor/Inc/"));
            stats.Discover = sw.Elapsed.TotalSeconds;
            o.Log($"discovered {projectFiles.Count} project files, {engineFiles.Count} engine files in {sw.Elapsed.TotalSeconds:F1}s");

            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var info = new Dictionary<string, (long Size, long Ticks)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in engineFiles.Concat(projectFiles))
            {
                info[f.Path] = (f.Size, f.Ticks);
                var n = Path.GetFileName(f.Path);
                if (!byName.TryGetValue(n, out var l)) byName[n] = l = new List<string>(1);
                l.Add(f.Path);
            }
            var incDirIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < db.IncludeDirs.Count; i++) incDirIndex.TryAdd(db.IncludeDirs[i].TrimEnd('/'), i);

            // ---------------------------------------------------------------- nothing changed: the previous index as it is
            if (!forceFull && !o.Full && metaPath != null && File.Exists(metaPath) && File.Exists(o.IndexPath))
            {
                BuildCache.Meta meta = null;
                try { meta = BuildCache.LoadMeta(metaPath); } catch (Exception ex) { o.Log("build meta unreadable: " + ex.Message); }
                if (meta != null && meta.EnvKey == envKey.ToString() && Unchanged(meta, info, o.Engine ? engineFiles.Concat(projectFiles) : projectFiles))
                {
                    IndexData same = null;
                    try { same = IndexData.Load(o.IndexPath); } catch (Exception ex) { o.Log("previous index unreadable: " + ex.Message); }
                    if (same != null && same.BuildId == meta.BuildId && same.Engine == o.Engine)
                    {
                        stats.Incremental = true; stats.Unchanged = true;
                        stats.Files = stats.ResolvedFiles = meta.Files.Count;
                        stats.Symbols = same.SymbolCount; stats.Refs = same.RefCount; stats.Unresolved = same.UnName.Length;
                        stats.Total = total.Elapsed.TotalSeconds;
                        o.Log($"no file changed since the last build: previous index reused ({meta.Files.Count:N0} files checked) in {total.Elapsed.TotalSeconds:F1}s");
                        return same;
                    }
                }
            }

            // ---------------------------------------------------------------- previous build (incremental)
            BuildCache cache = null;
            IndexData previous = null;
            string fullReason = null;
            if (forceFull) fullReason = "a macro of the global table changed";
            else if (o.Full) fullReason = "--full";
            else if (cachePath == null) fullReason = "no index path";
            else
            {
                try { cache = File.Exists(cachePath) ? BuildCache.Load(cachePath) : null; } catch (Exception ex) { o.Log("build cache unreadable: " + ex.Message); }
                if (cache == null) fullReason = "no build cache";
                else if (cache.EnvKey != envKey.ToString()) { fullReason = "compile environment or indexer changed"; cache = null; }
                else
                {
                    try { previous = File.Exists(o.IndexPath) ? IndexData.Load(o.IndexPath) : null; } catch (Exception ex) { o.Log("previous index unreadable: " + ex.Message); }
                    if (previous == null || previous.Engine != o.Engine) { fullReason = "no previous index"; cache = null; previous = null; }
                    else if (previous.BuildId != cache.BuildId) { fullReason = "build cache and index are from different builds"; cache = null; previous = null; }
                }
            }
            if (cache != null) o.Log($"build cache: {cache.Files.Count:N0} files loaded in {sw.Elapsed.TotalSeconds:F1}s " + Mem());
            else o.Log("full build: " + fullReason);


            // ---------------------------------------------------------------- phase 0: directives (defines + includes), closure
            sw.Restart();
            var files = new ConcurrentDictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
            SourceFile NewFile(string path, bool isProject)
            {
                var sf = new SourceFile { Path = path, IsProject = isProject, Resolve = true };
                if (info.TryGetValue(path, out var fi)) { sf.Size = fi.Size; sf.Ticks = fi.Ticks; }
                return sf;
            }
            var wave = new List<SourceFile>();
            foreach (var f in projectFiles) { var sf = NewFile(f.Path, true); files[f.Path] = sf; wave.Add(sf); }
            if (o.Engine)
                foreach (var f in engineFiles) { var sf = NewFile(f.Path, false); if (files.TryAdd(f.Path, sf)) wave.Add(sf); }
            var resolveCache = new ConcurrentDictionary<(string, string), string>();
            string Resolve(string inc, string dir) => resolveCache.GetOrAdd((inc.StartsWith(".") ? dir + "|" + inc : inc, dir), k => ResolveInclude(inc, dir, byName, incDirIndex));
            int waves = 0;
            void Closure(List<SourceFile> wave)
            {
            while (wave.Count > 0)
            {
                waves++;
                var next = new ConcurrentBag<SourceFile>();
                Parallel.ForEach(wave, po, sf =>
                {
                    if (cache != null && cache.Files.TryGetValue(sf.Path, out var cf) && cf.Size == sf.Size && cf.Ticks == sf.Ticks && ForceChanged?.Contains(sf.Path) != true) sf.FromCache(cf);
                    else
                    {
                        try { sf.Bytes = ReadSource(sf.Path); } catch (Exception) { sf.Bytes = Array.Empty<byte>(); }
                        var pp = new Preprocessor(env, true).Run(sf.Bytes, false, true);
                        sf.SetPhase0(pp);
                        sf.Changed = true;
                        if (cache != null && cache.Files.TryGetValue(sf.Path, out var before)) sf.Cached = before; // to compare its declarations
                    }
                    if (o.Engine) return;
                    var dir = sf.Path.Substring(0, sf.Path.LastIndexOf('/'));
                    foreach (var inc in sf.Includes)
                    {
                        var r = Resolve(inc, dir);
                        if (r == null) continue;
                        var nf = NewFile(r, false);
                        if (files.TryAdd(r, nf)) next.Add(nf);
                    }
                });
                wave = next.ToList();
            }
            }
            Closure(wave);
            var all = files.Values.OrderBy(f => f.IsProject ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < all.Count; i++) all[i].Id = i;
            var removed = cache == null ? new List<CachedFile>() : cache.Files.Values.Where(c => !files.ContainsKey(c.Path)).ToList();
            int changedFiles = all.Count(f => f.Changed);
            stats.Files = all.Count;
            stats.ResolvedFiles = all.Count(f => f.Resolve);
            stats.Bytes = all.Sum(f => f.Bytes?.Length ?? f.Size);
            stats.Lines = all.Sum(f => (long)f.Lines);

            // global macro table: header definitions; helper macros #undef'd in the same file stay local
            Dictionary<int, MacroDef> global, previousGlobal = null;
            if (cache != null)
            {
                // the cached files carry their final (re-evaluated) directives: their table is the previous build's
                previousGlobal = BuildGlobalMacros(cache.Files.Values
                    .OrderBy(c => c.Path.StartsWith(projectDir + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(c => (c.Path, c.Defines, c.Undefs)));
                global = previousGlobal;
            }
            else global = BuildGlobalMacros(all.Select(f => (f.Path, f.Defines, f.Undefs)));
            env.Global = global;
            // Phase 0 evaluated each file's #if before the global table existed: a define computed from another header's
            // macro got its fallback value (CoreMiscDefines.h: WITH_EDITORONLY_DATA = 0 because PLATFORM_CAN_SUPPORT_EDITORONLY_DATA,
            // from WindowsPlatform.h, was unknown; then every "#if WITH_EDITORONLY_DATA" member was skipped). Re-run the
            // directives of the files whose conditions named a macro defined elsewhere, until the table is stable.
            int rerun = 0, rounds = 0;
            HashSet<int> changed = null;
            for (; rounds < 4; rounds++)
            {
                var again = all.Where(f => f.Changed && IsHeader(f.Path) && f.UnknownInConditions != null && f.UnknownInConditions.Any(n => changed?.Contains(n) ?? global.ContainsKey(n))).ToList();
                if (again.Count == 0) break;
                rerun += again.Count;
                foreach (var f in again) f.OwnUndefined = OwnUndefined(f, global);
                Parallel.ForEach(again, po, f =>
                {
                    var pp = new Preprocessor(env, true).Run(f.Bytes, false, true, f.OwnUndefined);
                    var includes = f.Includes;
                    f.SetPhase0(pp);
                    f.Includes = UnionIncludes(includes, pp.Includes);
                });
                var next = BuildGlobalMacros(all.Select(f => (f.Path, f.Defines, f.Undefs)));
                changed = new HashSet<int>();
                foreach (var kv in next) if (!global.TryGetValue(kv.Key, out var old) || old.BodyKey != kv.Value.BodyKey) changed.Add(kv.Key);
                foreach (var k in global.Keys) if (!next.ContainsKey(k)) changed.Add(k);
                env.Global = global = next;
                if (changed.Count == 0) break;
            }
            if (!o.Engine)
            {
                // re-evaluated conditions can enable includes the closure has not seen yet: add those files and iterate
                for (int extra = 0; extra < 4; extra++)
                {
                    var fresh = new List<SourceFile>();
                    foreach (var f in all.Where(f => f.Changed && f.Includes != null))
                    {
                        var dir = f.Path.Substring(0, f.Path.LastIndexOf('/'));
                        foreach (var inc in f.Includes)
                        {
                            var r = Resolve(inc, dir);
                            if (r == null) continue;
                            var nf = NewFile(r, false);
                            if (files.TryAdd(r, nf)) fresh.Add(nf);
                        }
                    }
                    if (fresh.Count == 0) break;
                    Closure(fresh);
                    all = files.Values.OrderBy(f => f.IsProject ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                    for (int i = 0; i < all.Count; i++) all[i].Id = i;
                    // the new files and their dependencies run the directive re-evaluation like the others
                    global = BuildGlobalMacros(all.Select(f => (f.Path, f.Defines, f.Undefs)));
                    env.Global = global;
                    for (int round = 0; round < 4; round++)
                    {
                        var again = all.Where(f => f.Changed && IsHeader(f.Path) && f.UnknownInConditions != null && f.UnknownInConditions.Any(n => global.ContainsKey(n) && !string.Equals(global[n].Origin, f.Path, StringComparison.OrdinalIgnoreCase))).ToList();
                        var before = global;
                        foreach (var f in again) f.OwnUndefined = OwnUndefined(f, global);
                        Parallel.ForEach(again, po, f =>
                        {
                            var pp = new Preprocessor(env, true).Run(f.Bytes, false, true, f.OwnUndefined);
                            var includes = f.Includes;
                            f.SetPhase0(pp);
                            f.Includes = UnionIncludes(includes, pp.Includes);
                        });
                        rerun += again.Count;
                        global = BuildGlobalMacros(all.Select(f => (f.Path, f.Defines, f.Undefs)));
                        env.Global = global;
                        if (MacroTableDifference(before, global) == null) break;
                    }
                }
                stats.Files = all.Count;
                stats.ResolvedFiles = all.Count(f => f.Resolve);
                stats.Bytes = all.Sum(f => f.Bytes?.Length ?? f.Size);
                stats.Lines = all.Sum(f => (long)f.Lines);
                changedFiles = all.Count(f => f.Changed);
                removed = cache == null ? new List<CachedFile>() : cache.Files.Values.Where(c => !files.ContainsKey(c.Path)).ToList();
            }
            if (cache != null)
            {
                global = BuildGlobalMacros(all.Select(f => (f.Path, f.Defines, f.Undefs)));
                env.Global = global;
                var diff = MacroTableDifference(previousGlobal, global);
                if (diff != null && Environment.GetEnvironmentVariable("US_DEBUG") != null)
                {
                    var dn = Names.Intern(diff.Substring(0, diff.IndexOf(' ')));
                    foreach (var f in all.Where(f => f.Defines != null && f.Defines.Any(d => d.Name == dn)))
                    {
                        cache.Files.TryGetValue(f.Path, out var c);
                        o.Log($"DEBUG {f.Path}: changed={f.Changed} cachedSame={ReferenceEquals(c?.Defines, f.Defines)} undef={f.Undefs?.Contains(dn)} cacheUndef={c?.Undefs?.Contains(dn)} cacheHas={c?.Defines?.Any(d => d.Name == dn)} header={IsHeader(f.Path)}");
                    }
                    o.Log($"DEBUG cache files {cache.Files.Count}, all {all.Count}");
                }
                if (diff != null)
                {
                    o.Log($"incremental build abandoned: macro {diff} changed in the global table");
                    return null;
                }
            }
            foreach (var f in all) f.OwnUndefined = OwnUndefined(f, global);
            stats.Phase0 = sw.Elapsed.TotalSeconds;
            o.Log($"phase 0 (read + directives{(o.Engine ? "" : ", include closure in " + waves + " waves")}): {all.Count} files ({stats.ResolvedFiles} to resolve), {changedFiles:N0} read{(cache != null ? $" (changed or new), {removed.Count} removed" : "")}, {stats.Bytes / 1048576.0:F0} MB, {stats.Lines:N0} lines, {global.Count:N0} macros ({rerun} files re-evaluated in {rounds} rounds) in {sw.Elapsed.TotalSeconds:F1}s " + Mem());

            // ---------------------------------------------------------------- parse units
            // A file included "with parameters" (the includer #defines macros, #includes it, then #undefs them, e.g.
            // Map.h.inl for TMap/TSparseMap/TCompactMap, UnrealString.h.inl for FString/FUtf8String) is parsed once per context.
            var contexts = new Dictionary<SourceFile, List<(Dictionary<int, MacroDef> Macros, string Key)>>();
            var contextKeys = new HashSet<string>();
            foreach (var f in all)
            {
                if (f.IncludeContexts == null || f.Undefs == null) continue;
                var dir = f.Path.Substring(0, f.Path.LastIndexOf('/'));
                foreach (var (inc, macros) in f.IncludeContexts)
                {
                    var ctx = macros.Where(kv => f.Undefs.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                    if (ctx.Count == 0) continue;
                    var target = ResolveInclude(inc, dir, byName, incDirIndex);
                    if (target == null || !files.TryGetValue(target, out var tf)) continue;
                    var ckey = string.Join(";", ctx.OrderBy(kv => Names.Get(kv.Key), StringComparer.Ordinal).Select(kv => Names.Get(kv.Key) + "=" + kv.Value.BodyKey));
                    if (!contextKeys.Add(target + "|" + ckey)) continue;
                    if (!contexts.TryGetValue(tf, out var list)) contexts[tf] = list = new List<(Dictionary<int, MacroDef>, string)>();
                    list.Add((ctx, ckey));
                }
            }
            var units = new List<ParseUnit>();
            foreach (var f in all)
            {
                if (contexts.TryGetValue(f, out var list)) foreach (var c in list) units.Add(new ParseUnit { File = f, Context = c.Macros, ContextKey = c.Key, Index = units.Count });
                else units.Add(new ParseUnit { File = f, ContextKey = "", Index = units.Count });
            }
            if (contexts.Count > 0) o.Log($"parameterized includes: {contexts.Count} files parsed in {contexts.Sum(c => c.Value.Count)} contexts");

            // ---------------------------------------------------------------- pass 1: declarations
            sw.Restart();
            var fileDecls = new FileDecls[units.Count];
            var toParse = new List<ParseUnit>();
            foreach (var u in units)
            {
                if (!u.File.Changed && u.File.Cached != null && u.File.Cached.Units.TryGetValue(u.ContextKey, out var decls))
                    fileDecls[u.Index] = new FileDecls { FileId = u.File.Id, Decls = decls };
                else toParse.Add(u);
            }
            foreach (var f in toParse.Select(u => u.File).Distinct()) f.Reparsed = true;
            EnsureBytes(toParse.Select(u => u.File), po);
            var order = toParse.OrderByDescending(u => u.File.Size).ToList();
            Parallel.ForEach(order, po, u =>
            {
                var (toks, count, fm) = Tokenize(u, env);
                var parser = new DeclParser(toks, count, fm, null);
                try { parser.ParseFile(); } catch (Exception ex) { o.Log($"pass 1 failed on {u.File.Path}: {ex.Message}"); }
                fileDecls[u.Index] = new FileDecls { FileId = u.File.Id, Decls = parser.Decls };
            });
            var unitsOf = units.GroupBy(u => u.File).ToDictionary(g => g.Key, g => g.ToList());
            var declChanged = new List<SourceFile>();
            foreach (var f in all)
            {
                if (!f.Reparsed) { f.DeclHash = f.Cached.DeclHash; continue; }
                f.DeclHash = BuildCache.HashDecls(unitsOf[f].Select(u => fileDecls[u.Index].Decls));
                if (cache != null && (f.Cached == null || f.Cached.DeclHash != f.DeclHash)) declChanged.Add(f);
            }
            double tokCpu1 = tokenizeTicks / (double)Stopwatch.Frequency;
            stats.Decls = fileDecls.Sum(f => f.Decls.Count);
            stats.Pass1 = sw.Elapsed.TotalSeconds;
            o.Log($"pass 1 (declarations): {stats.Decls:N0} declarations, {toParse.Count:N0}/{units.Count:N0} units parsed{(cache != null ? $", {declChanged.Count} files with changed declarations" : "")} in {sw.Elapsed.TotalSeconds:F1}s (tokenize {tokCpu1:F1}s CPU) " + Mem());

            // ---------------------------------------------------------------- merge
            sw.Restart();
            var table = new SymbolTable();
            table.Merge(fileDecls);
            table.PrecomputeTypes();
            var overrides = ComputeOverrides(table, po);
            stats.Symbols = table.All.Count;
            stats.Merge = sw.Elapsed.TotalSeconds;
            o.Log($"merge: {table.All.Count:N0} symbols, {overrides.Count:N0} overrides in {sw.Elapsed.TotalSeconds:F1}s " + Mem());

            // ---------------------------------------------------------------- pass 2: references
            sw.Restart();
            // Incremental: the files whose references may differ from the previous build are pass-2'd again; the others keep
            // theirs (remapped to the new symbol ids). That is: the files read or parsed again, the files that include (directly
            // or not) a file whose declarations changed or that was removed, and the files whose unresolved names are declared
            // by such a file (a new declaration elsewhere can resolve them).
            var redo = new HashSet<SourceFile>(all.Where(f => f.Changed || f.Reparsed || cache == null));
            if (cache != null && (declChanged.Count > 0 || removed.Count > 0))
            {
                var includers = new Dictionary<string, List<SourceFile>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in all)
                {
                    if (f.Includes == null) continue;
                    var dir = f.Path.Substring(0, f.Path.LastIndexOf('/'));
                    foreach (var inc in f.Includes)
                    {
                        var t = Resolve(inc, dir);
                        if (t == null) continue;
                        if (!includers.TryGetValue(t, out var l)) includers[t] = l = new List<SourceFile>();
                        l.Add(f);
                    }
                }
                var queue = new Queue<string>(declChanged.Select(f => f.Path).Concat(removed.Select(c => c.Path)));
                var visited = new HashSet<string>(queue, StringComparer.OrdinalIgnoreCase);
                while (queue.Count > 0)
                    if (includers.TryGetValue(queue.Dequeue(), out var l))
                        foreach (var f in l) { redo.Add(f); if (visited.Add(f.Path)) queue.Enqueue(f.Path); }
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in declChanged)
                {
                    foreach (var u in unitsOf[f]) foreach (var d in fileDecls[u.Index].Decls) if (d.Name > 0) declared.Add(Names.Get(d.Name));
                    if (f.Cached != null) foreach (var dl in f.Cached.Units.Values) foreach (var d in dl) if (d.Name > 0) declared.Add(Names.Get(d.Name));
                }
                foreach (var c in removed) foreach (var dl in c.Units.Values) foreach (var d in dl) if (d.Name > 0) declared.Add(Names.Get(d.Name));
                for (int k = 0; k < previous.UnName.Length; k++)
                    if (declared.Contains(previous.UnresolvedNames[previous.UnName[k]]) && files.TryGetValue(previous.Files[previous.UnFile[k]], out var f)) redo.Add(f);
            }
            var pass2Units = units.Where(u => u.File.Resolve && redo.Contains(u.File)).ToList();
            EnsureBytes(pass2Units.Select(u => u.File), po);
            var refsPerUnit = new List<RefRec>[units.Count];
            var unresolvedPerUnit = new List<UnresolvedRec>[units.Count];
            long rm = 0, ru = 0, rn = 0, un = 0;
            Parallel.ForEach(pass2Units.OrderByDescending(u => u.File.Size), po, u =>
            {
                var sf = u.File;
                var p2 = new Pass2(table, fileDecls[u.Index]);
                p2.GeneratedBodyIdents = GeneratedBodyResolver(sf, files, byName, env);
                var (toks, count, fm) = Tokenize(u, env);
                try { p2.Run(toks, count, fm); }
                catch (Exception ex) { o.Log($"pass 2 failed on {sf.Path}: {ex.GetType().Name} {ex.Message}"); }
                refsPerUnit[u.Index] = p2.Refs;
                unresolvedPerUnit[u.Index] = p2.Unresolved;
                Interlocked.Add(ref rm, p2.ResolvedMember); Interlocked.Add(ref ru, p2.UnresolvedMemberUnknownRecv);
                Interlocked.Add(ref rn, p2.UnresolvedMemberNotFound); Interlocked.Add(ref un, p2.UnresolvedNames);
            });
            stats.ResolvedMember = (int)rm; stats.UnresolvedMemberUnknownRecv = (int)ru; stats.UnresolvedMemberNotFound = (int)rn; stats.UnresolvedNames = (int)un;
            double tokCpu2 = tokenizeTicks / (double)Stopwatch.Frequency - tokCpu1;
            var refsPerFile = new List<RefRec>[all.Count];
            var unresolvedPerFile = new List<UnresolvedRec>[all.Count];
            foreach (var u in units)
            {
                int id = u.File.Id;
                if (refsPerUnit[u.Index] != null) { if (refsPerFile[id] == null) refsPerFile[id] = refsPerUnit[u.Index]; else refsPerFile[id].AddRange(refsPerUnit[u.Index]); }
                if (unresolvedPerUnit[u.Index] != null) { if (unresolvedPerFile[id] == null) unresolvedPerFile[id] = unresolvedPerUnit[u.Index]; else unresolvedPerFile[id].AddRange(unresolvedPerUnit[u.Index]); }
            }
            var data = IndexData.CreateSymbols(all, table, overrides, projectDir, engineDir, o.Engine);
            data.BuildId = cache?.BuildId ?? Guid.NewGuid().ToString("N");
            int kept = 0, lost = 0;
            if (cache != null)
            {
                // the other files keep the previous build's references: same symbol ids when the merge produced the same symbols
                // (only bodies changed), else old id → new id by stable key
                int[] oldToNew;
                bool same = data.SameSymbols(previous);
                if (same) oldToNew = Enumerable.Range(0, previous.SymbolCount).ToArray();
                else
                {
                    var newIds = new Dictionary<string, int>(data.SymbolCount, StringComparer.Ordinal);
                    var newKeys = data.StableKeys();
                    for (int i = 0; i < newKeys.Length; i++) newIds[newKeys[i]] = i;
                    var oldKeys = previous.StableKeys();
                    oldToNew = new int[oldKeys.Length];
                    for (int i = 0; i < oldKeys.Length; i++) oldToNew[i] = newIds.TryGetValue(oldKeys[i], out int x) ? x : -1;
                }
                var fileMap = new int[previous.Files.Length];
                for (int i = 0; i < fileMap.Length; i++) fileMap[i] = files.TryGetValue(previous.Files[i], out var f) && !redo.Contains(f) && f.Resolve ? f.Id : -1;
                data.SetReferences(refsPerFile, unresolvedPerFile, previous, fileMap, oldToNew, out kept, out lost);
                if (!same) o.Log("symbols changed: references remapped by key");
            }
            else data.SetReferences(refsPerFile, unresolvedPerFile);
            stats.Refs = data.RefCount;
            stats.Unresolved = data.UnName.Length;
            stats.Pass2 = sw.Elapsed.TotalSeconds;
            o.Log($"pass 2 (references): {stats.Refs:N0} references, {stats.Unresolved:N0} unresolved; {pass2Units.Count:N0}/{units.Count:N0} units resolved{(cache != null ? $", {kept:N0} references kept from the previous build ({lost:N0} to symbols that no longer exist)" : "")} in {sw.Elapsed.TotalSeconds:F1}s; member accesses: {rm:N0} resolved, {ru:N0} unknown receiver, {rn:N0} member not found; {un:N0} unresolved names; tokenize {tokCpu2:F1}s CPU " + Mem());

            // ---------------------------------------------------------------- build cache for the next run
            if (cachePath != null && (cache == null || changedFiles > 0 || removed.Count > 0 || toParse.Count > 0))
            {
                sw.Restart();
                data.BuildId = Guid.NewGuid().ToString("N");
                var next = new BuildCache { EnvKey = envKey.ToString(), BuildId = data.BuildId };
                foreach (var f in all)
                {
                    if (!f.Reparsed && f.Cached != null) { next.Files[f.Path] = f.Cached; continue; }
                    var cf = f.ToCache();
                    foreach (var u in unitsOf[f]) cf.Units[u.ContextKey] = fileDecls[u.Index].Decls;
                    next.Files[f.Path] = cf;
                }
                long size = next.Save(cachePath);
                stats.CacheSave = sw.Elapsed.TotalSeconds;
                o.Log($"build cache: {next.Files.Count:N0} files, {size / 1048576.0:F1} MB written in {sw.Elapsed.TotalSeconds:F1}s");
            }
            if (metaPath != null) BuildCache.SaveMeta(metaPath, envKey.ToString(), data.BuildId, all.Select(f => (f.Path, f.Size, f.Ticks)));
            stats.Incremental = cache != null;
            stats.ChangedFiles = changedFiles;
            stats.PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
            stats.ManagedHeap = GC.GetTotalMemory(false);
            if (o.KeepSymbolTable) { Table = table; FilesOut = all; }
            foreach (var f in all) f.Bytes = null;
            stats.Total = total.Elapsed.TotalSeconds;
            return data;
        }

        /// <summary>
        /// Includes seen by either evaluation of a file's directives: the include closure already followed the first ones, and an
        /// incremental build (which starts from the cached list) must reach the same files.
        /// </summary>
        static List<string> UnionIncludes(List<string> a, List<string> b)
        {
            if (a == null || a.Count == 0) return b;
            if (b == null || b.Count == 0) return a;
            var r = new List<string>(a);
            var seen = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            foreach (var x in b) if (seen.Add(x)) r.Add(x);
            return r;
        }

        /// <summary>Every file of the previous build still has its size and time, and no file to index is new.</summary>
        static bool Unchanged(BuildCache.Meta meta, Dictionary<string, (long Size, long Ticks)> info, IEnumerable<(string Path, long Size, long Ticks)> toIndex)
        {
            foreach (var kv in meta.Files)
                if (!info.TryGetValue(kv.Key, out var fi) || fi != kv.Value || ForceChanged?.Contains(kv.Key) == true) return false;
            foreach (var f in toIndex) if (!meta.Files.ContainsKey(f.Path)) return false;
            return true;
        }

        static void EnsureBytes(IEnumerable<SourceFile> list, ParallelOptions po)
        {
            Parallel.ForEach(list.Where(f => f.Bytes == null).Distinct().ToList(), po, f =>
            {
                try { f.Bytes = ReadSource(f.Path); } catch (Exception) { f.Bytes = Array.Empty<byte>(); }
            });
        }

        /// <summary>First macro whose global definition differs between two tables (null when they agree).</summary>
        static string MacroTableDifference(Dictionary<int, MacroDef> a, Dictionary<int, MacroDef> b)
        {
            foreach (var kv in b)
                if (!a.TryGetValue(kv.Key, out var x) || x.BodyKey != kv.Value.BodyKey) return $"{Names.Get(kv.Key)} ({x?.Origin ?? "absent"} -> {kv.Value.Origin})";
            foreach (var k in a.Keys) if (!b.ContainsKey(k)) return $"{Names.Get(k)} ({a[k].Origin} -> absent)";
            return null;
        }

        static bool IsHeader(string path) => !path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".c", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One definition per macro name. A definition guarded by "#ifndef NAME" is a fallback: a definition elsewhere wins
        /// (HAL/Platform.h: PLATFORM_CAN_SUPPORT_EDITORONLY_DATA 0 only if WindowsPlatform.h did not set it to 1).
        /// </summary>
        static Dictionary<int, MacroDef> BuildGlobalMacros(IEnumerable<(string Path, List<MacroDef> Defines, HashSet<int> Undefs)> all)
        {
            var global = new Dictionary<int, MacroDef>();
            foreach (var f in all) // callers pass project files first, then by path (the order of the file list)
            {
                if (f.Defines == null || !IsHeader(f.Path)) continue;
                foreach (var d in f.Defines)
                {
                    if (f.Undefs != null && f.Undefs.Contains(d.Name)) continue;
                    d.Ambiguous = false;
                    if (!global.TryGetValue(d.Name, out var ex)) global[d.Name] = d;
                    else if (ex.IsDefault && !d.IsDefault) global[d.Name] = d;
                    else if (ex.IsDefault == d.IsDefault && ex.BodyKey != d.BodyKey) ex.Ambiguous = true;
                }
            }
            return global;
        }

        /// <summary>
        /// Its own macros whose global definition is its own (include guard, its own fallback with nobody overriding it):
        /// they start undefined when the file is read, as when the compiler first includes it.
        /// </summary>
        static Dictionary<int, MacroDef> OwnUndefined(SourceFile f, Dictionary<int, MacroDef> global)
        {
            if (f.Defines == null || f.Defines.Count == 0) return null;
            Dictionary<int, MacroDef> r = null;
            foreach (var d in f.Defines)
                if (global.TryGetValue(d.Name, out var g) && string.Equals(g.Origin, f.Path, StringComparison.OrdinalIgnoreCase)) (r ??= new Dictionary<int, MacroDef>())[d.Name] = MacroDef.Undefined;
            return r;
        }

        static readonly int CurrentFileId = Names.Intern("CURRENT_FILE_ID");

        /// <summary>For a header with UHT code: maps the line of a GENERATED_BODY() to the identifiers its expansion names.</summary>
        static Func<int, List<int>> GeneratedBodyResolver(SourceFile sf, ConcurrentDictionary<string, SourceFile> files, Dictionary<string, List<string>> byName, MacroEnv env)
        {
            var inc = sf.Includes?.FirstOrDefault(i => i.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase));
            if (inc == null) return null;
            var name = Path.GetFileName(inc);
            if (!byName.TryGetValue(name, out var cands)) return null;
            SourceFile gen = null;
            foreach (var c in cands) if (files.TryGetValue(c, out var g) && g.Defines != null) { gen = g; break; }
            if (gen == null) return null;
            var defs = new Dictionary<int, MacroDef>();
            foreach (var d in gen.Defines) defs[d.Name] = d;
            if (!defs.TryGetValue(CurrentFileId, out var fid) || fid.Body.Length != 1) return null;
            string prefix = Names.Get(fid.Body[0].Value) + "_";
            return line =>
            {
                MacroDef m = null;
                foreach (var suffix in new[] { "_GENERATED_BODY", "_GENERATED_BODY_LEGACY" })
                    if (defs.TryGetValue(Names.Intern(prefix + line + suffix), out m)) break;
                if (m == null) return null;
                var result = new List<int>();
                var seen = new HashSet<int>();
                void Walk(MacroDef md, int depth)
                {
                    if (depth > 8) return;
                    foreach (var t in md.Body)
                    {
                        if (t.Kind != TK.Ident || !seen.Add(t.Value)) continue;
                        var sub = defs.TryGetValue(t.Value, out var x) ? x : env.Find(t.Value);
                        if (sub != null) { Walk(sub, depth + 1); continue; }
                        result.Add(t.Value);
                    }
                }
                Walk(m, 0);
                return result;
            };
        }

        sealed class ParseUnit
        {
            public SourceFile File;
            public Dictionary<int, MacroDef> Context;
            public string ContextKey;      // "" for the plain file; stable across runs (names, not ids)
            public int Index;
        }

        /// <summary>Preprocess + macro rewrites; identical in both passes.</summary>
        static long tokenizeTicks;

        static (Token[], int, FileMacros) Tokenize(ParseUnit u, MacroEnv env)
        {
            long t0 = Stopwatch.GetTimestamp();
            try { return TokenizeCore(u, env); }
            finally { Interlocked.Add(ref tokenizeTicks, Stopwatch.GetTimestamp() - t0); }
        }

        // One token array per thread, reused file after file: allocating ~4x the source size per file (large object heap,
        // zeroed) twice per build cost more CPU than lexing itself. The arrays never outlive the unit being parsed.
        [ThreadStatic] static Token[] tokenBuffer;

        static (Token[], int, FileMacros) TokenizeCore(ParseUnit u, MacroEnv env)
        {
            var start = u.File.OwnUndefined;
            if (u.Context != null) { start = start == null ? u.Context : new Dictionary<int, MacroDef>(start); if (start != u.Context) foreach (var kv in u.Context) start[kv.Key] = kv.Value; }
            var pp = new Preprocessor(env, false).Run(u.File.Bytes, true, false, start, tokenBuffer);
            if (pp.Tokens.Length > (tokenBuffer?.Length ?? 0)) tokenBuffer = pp.Tokens;
            int count = pp.Count;
            Token[] toks = pp.Tokens;
            FileMacros fm;
            if (u.Context != null)
            {
                var defs = new List<MacroDef>(u.Context.Values);
                defs.AddRange(pp.Defines);
                fm = new FileMacros(env, defs);
                var names = new HashSet<int>(u.Context.Keys);
                foreach (var d in pp.Defines) names.Add(d.Name);
                toks = MacroExpander.RewriteContextMacros(toks, ref count, names, fm);
            }
            else fm = new FileMacros(env, u.File.Defines);
            toks = MacroExpander.RewriteTypeMacros(toks, ref count, fm);
            return (toks, count, fm);
        }

        static bool IsApiDefine(MacroDef d) => FileMacros.IsApiName(d.Name);

        public static byte[] ReadSource(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                return System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.Unicode.GetString(b, 2, b.Length - 2));
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2));
            return b;
        }

        static string ResolveInclude(string inc, string dir, Dictionary<string, List<string>> byName, Dictionary<string, int> incDirIndex)
        {
            inc = inc.Replace('\\', '/');
            var name = inc.Substring(inc.LastIndexOf('/') + 1);
            if (!byName.TryGetValue(name, out var cands)) return null;
            string suffix = "/" + inc.TrimStart('.', '/');
            // relative to the including file
            var rel = CompileDb.Norm(Path.GetFullPath(Path.Combine(dir, inc)));
            foreach (var c in cands) if (string.Equals(c, rel, StringComparison.OrdinalIgnoreCase)) return c;
            string best = null; int bestIdx = int.MaxValue;
            foreach (var c in cands)
            {
                if (!c.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var prefix = c.Substring(0, c.Length - suffix.Length);
                if (incDirIndex.TryGetValue(prefix, out int idx) && idx < bestIdx) { best = c; bestIdx = idx; }
            }
            if (best != null) return best;
            // fallback: the candidate closest to the including file
            int bestCommon = -1;
            foreach (var c in cands)
            {
                if (!c.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                int common = 0;
                while (common < c.Length && common < dir.Length && char.ToLowerInvariant(c[common]) == char.ToLowerInvariant(dir[common])) common++;
                if (common > bestCommon) { bestCommon = common; best = c; }
            }
            return best;
        }

        static List<(string Path, long Size, long Ticks)> Enumerate(string[] roots, ParallelOptions po, Func<string, bool> filter)
        {
            var result = new ConcurrentBag<(string, long, long)>();
            var pending = new BlockingCollection<string>();
            int outstanding = 0;
            foreach (var r in roots) if (Directory.Exists(r)) { Interlocked.Increment(ref outstanding); pending.Add(r); }
            if (outstanding == 0) return new List<(string, long, long)>();
            var workers = new Task[Math.Max(1, po.MaxDegreeOfParallelism)];
            for (int w = 0; w < workers.Length; w++)
            {
                workers[w] = Task.Run(() =>
                {
                    foreach (var dir in pending.GetConsumingEnumerable())
                    {
                        try
                        {
                            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                            {
                                if ((e.Attributes & FileAttributes.Directory) != 0)
                                {
                                    if (platformDirs.Contains(e.Name) || e.Name.StartsWith(".")) continue;
                                    var sub = CompileDb.Norm(e.FullName);
                                    if (sub.EndsWith("/Intermediate", StringComparison.OrdinalIgnoreCase) && !filter(sub + "/Build/Win64/UnrealEditor/Inc/")) continue;
                                    if (sub.EndsWith("/Binaries", StringComparison.OrdinalIgnoreCase) || sub.EndsWith("/Content", StringComparison.OrdinalIgnoreCase) || sub.EndsWith("/Saved", StringComparison.OrdinalIgnoreCase)) continue;
                                    Interlocked.Increment(ref outstanding);
                                    pending.Add(sub);
                                }
                                else
                                {
                                    var ext = Path.GetExtension(e.Name);
                                    if (Array.IndexOf(sourceExt, ext.ToLowerInvariant()) < 0) continue;
                                    var path = CompileDb.Norm(e.FullName);
                                    if (filter(path)) result.Add((path, ((FileInfo)e).Length, e.LastWriteTimeUtc.Ticks));
                                }
                            }
                        }
                        catch (Exception) { }
                        if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                    }
                });
            }
            Task.WaitAll(workers);
            // sorted: the workers' order is random, and include resolution picks among same-name candidates in list order
            return result.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Virtual overrides: method → the base-class methods it overrides (same name and signature).</summary>
        static Dictionary<Symbol, List<Symbol>> ComputeOverrides(SymbolTable t, ParallelOptions po)
        {
            var result = new ConcurrentDictionary<Symbol, List<Symbol>>();
            var classes = t.All.Where(s => s.IsClassLike && s.Members != null && s.BaseExprs != null).ToList();
            Parallel.ForEach(classes, po, cls =>
            {
                var bases = t.GetBases(cls);
                if (bases == null || bases.Length == 0) return;
                foreach (var kv in cls.Members)
                {
                    IEnumerable<Symbol> ms = kv.Value is Symbol s ? new[] { s } : (List<Symbol>)kv.Value;
                    foreach (var m in ms)
                    {
                        if (m.Kind != SymKind.Function || m.Parent != cls || (m.Flags & (DeclFlags.Static | DeclFlags.Ctor)) != 0) continue;
                        var found = new List<Symbol>();
                        foreach (var b in bases) FindOverridden(t, b?.Sym, m, found, 0, new HashSet<Symbol>());
                        if (found.Count > 0) result[m] = found;
                    }
                }
            });
            return new Dictionary<Symbol, List<Symbol>>(result);
        }

        static void FindOverridden(SymbolTable t, Symbol cls, Symbol m, List<Symbol> found, int depth, HashSet<Symbol> seen)
        {
            if (cls == null || depth > 24 || !seen.Add(cls)) return;
            var mem = cls.GetMember(m.Name);
            Symbol hit = null;
            if (mem != null)
            {
                IEnumerable<Symbol> cands = mem is Symbol s ? new[] { s } : (List<Symbol>)mem;
                foreach (var c in cands)
                {
                    if (c.Kind != SymKind.Function || (c.Flags & (DeclFlags.Virtual | DeclFlags.Override)) == 0) continue;
                    if (c.SigKey == m.SigKey) { hit = c; break; }
                    if ((c.Params?.Length ?? 0) == (m.Params?.Length ?? 0) && ((c.Flags & DeclFlags.Const) == (m.Flags & DeclFlags.Const)) && hit == null) hit = c;
                }
            }
            if (hit != null) { found.Add(hit); return; } // its own overrides are found transitively at query time
            if (!cls.IsClassLike) return;
            var bases = t.GetBases(cls);
            if (bases != null) foreach (var b in bases) FindOverridden(t, b?.Sym, m, found, depth + 1, seen);
        }
    }
}
