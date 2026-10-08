using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public int Lines;
    }

    public sealed class BuildStats
    {
        public int Files, ResolvedFiles;
        public long Bytes, Lines;
        public int Decls, Symbols, Refs, Unresolved;
        public int ResolvedMember, UnresolvedMemberUnknownRecv, UnresolvedMemberNotFound, UnresolvedNames;
        public double Discover, Phase0, Pass1, Merge, Pass2, Save, Total;
        public long PeakWorkingSet, ManagedHeap;
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

        public SymbolTable Table;
        public List<SourceFile> FilesOut;

        public IndexData Build(BuildOptions o, out BuildStats stats)
        {
            stats = new BuildStats();
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
            foreach (var (n, v) in db.Defines) env.DefineBase(n, v);
            foreach (var fi in db.ForcedIncludes)
            {
                if (!fi.EndsWith("Definitions.h", StringComparison.OrdinalIgnoreCase) || !File.Exists(fi)) continue;
                var pp = new Preprocessor(env, true).Run(File.ReadAllBytes(fi), false, false);
                foreach (var d in pp.Defines) if (!env.Base.ContainsKey(d.Name) || !IsApiDefine(d)) env.Base[d.Name] = d;
            }

            // ---------------------------------------------------------------- files
            var projectRoots = new[] { projectDir + "/Source", projectDir + "/Plugins", projectDir + "/Intermediate/Build/Win64/UnrealEditor/Inc" };
            var engineRoots = new[] { engineDir + "/Source/Runtime", engineDir + "/Source/Developer", engineDir + "/Source/Editor", engineDir + "/Plugins", engineDir + "/Intermediate/Build/Win64/UnrealEditor/Inc" };
            var projectFiles = Enumerate(projectRoots, po, p => !p.Contains("/Intermediate/") || p.Contains("/Intermediate/Build/Win64/UnrealEditor/Inc/"));
            var engineFiles = Enumerate(engineRoots, po, p => !p.Contains("/Intermediate/") || p.Contains("/Intermediate/Build/Win64/UnrealEditor/Inc/"));
            stats.Discover = sw.Elapsed.TotalSeconds;
            o.Log($"discovered {projectFiles.Count} project files, {engineFiles.Count} engine files in {sw.Elapsed.TotalSeconds:F1}s");

            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in engineFiles.Concat(projectFiles))
            {
                var n = Path.GetFileName(f);
                if (!byName.TryGetValue(n, out var l)) byName[n] = l = new List<string>(1);
                l.Add(f);
            }
            var incDirIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < db.IncludeDirs.Count; i++) incDirIndex.TryAdd(db.IncludeDirs[i].TrimEnd('/'), i);

            // ---------------------------------------------------------------- phase 0: directives (defines + includes), closure
            sw.Restart();
            var files = new ConcurrentDictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
            var wave = new List<SourceFile>();
            foreach (var f in projectFiles) { var sf = new SourceFile { Path = f, IsProject = true, Resolve = true }; files[f] = sf; wave.Add(sf); }
            if (o.Engine)
                foreach (var f in engineFiles) { var sf = new SourceFile { Path = f, Resolve = true }; if (files.TryAdd(f, sf)) wave.Add(sf); }
            var resolveCache = new ConcurrentDictionary<(string, string), string>();
            int waves = 0;
            while (wave.Count > 0)
            {
                waves++;
                var next = new ConcurrentBag<SourceFile>();
                Parallel.ForEach(wave, po, sf =>
                {
                    try { sf.Bytes = ReadSource(sf.Path); } catch (Exception) { sf.Bytes = Array.Empty<byte>(); }
                    var pp = new Preprocessor(env, true).Run(sf.Bytes, false, true);
                    sf.Defines = pp.Defines; sf.Undefs = pp.Undefs; sf.Includes = pp.Includes; sf.Lines = pp.Lines; sf.IncludeContexts = pp.IncludeContexts;
                    if (o.Engine) return;
                    var dir = sf.Path.Substring(0, sf.Path.LastIndexOf('/'));
                    foreach (var inc in pp.Includes)
                    {
                        var r = resolveCache.GetOrAdd((inc.StartsWith(".") ? dir + "|" + inc : inc, dir), k => ResolveInclude(inc, dir, byName, incDirIndex));
                        if (r == null) continue;
                        var nf = new SourceFile { Path = r, Resolve = true };
                        if (files.TryAdd(r, nf)) next.Add(nf);
                    }
                });
                wave = next.ToList();
            }
            var all = files.Values.OrderBy(f => f.IsProject ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < all.Count; i++) all[i].Id = i;
            stats.Files = all.Count;
            stats.ResolvedFiles = all.Count(f => f.Resolve);
            stats.Bytes = all.Sum(f => (long)f.Bytes.Length);
            stats.Lines = all.Sum(f => (long)f.Lines);

            // global macro table: header definitions; helper macros #undef'd in the same file stay local
            var global = new Dictionary<int, MacroDef>();
            foreach (var f in all)
            {
                if (f.Defines == null) continue;
                bool header = !f.Path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) && !f.Path.EndsWith(".c", StringComparison.OrdinalIgnoreCase);
                if (!header) continue;
                foreach (var d in f.Defines)
                {
                    if (f.Undefs != null && f.Undefs.Contains(d.Name)) continue;
                    if (global.TryGetValue(d.Name, out var ex)) { if (ex.BodyKey != d.BodyKey) ex.Ambiguous = true; }
                    else global[d.Name] = d;
                }
            }
            env.Global = global;
            stats.Phase0 = sw.Elapsed.TotalSeconds;
            o.Log($"phase 0 (read + directives{(o.Engine ? "" : ", include closure in " + waves + " waves")}): {all.Count} files ({stats.ResolvedFiles} to resolve), {stats.Bytes / 1048576.0:F0} MB, {stats.Lines:N0} lines, {global.Count:N0} macros in {sw.Elapsed.TotalSeconds:F1}s");

            // ---------------------------------------------------------------- parse units
            // A file included "with parameters" (the includer #defines macros, #includes it, then #undefs them, e.g.
            // Map.h.inl for TMap/TSparseMap/TCompactMap, UnrealString.h.inl for FString/FUtf8String) is parsed once per context.
            var contexts = new Dictionary<SourceFile, List<Dictionary<int, MacroDef>>>();
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
                    var key = target + "|" + string.Join(";", ctx.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.BodyKey));
                    if (!contextKeys.Add(key)) continue;
                    if (!contexts.TryGetValue(tf, out var list)) contexts[tf] = list = new List<Dictionary<int, MacroDef>>();
                    list.Add(ctx);
                }
            }
            var units = new List<ParseUnit>();
            foreach (var f in all)
            {
                if (contexts.TryGetValue(f, out var list)) foreach (var c in list) units.Add(new ParseUnit { File = f, Context = c, Index = units.Count });
                else units.Add(new ParseUnit { File = f, Index = units.Count });
            }
            if (contexts.Count > 0) o.Log($"parameterized includes: {contexts.Count} files parsed in {contexts.Sum(c => c.Value.Count)} contexts");

            // ---------------------------------------------------------------- pass 1: declarations
            sw.Restart();
            var fileDecls = new FileDecls[units.Count];
            var order = units.OrderByDescending(u => u.File.Bytes.Length).ToList();
            Parallel.ForEach(order, po, u =>
            {
                var (toks, count, fm) = Tokenize(u, env);
                var parser = new DeclParser(toks, count, fm, null);
                try { parser.ParseFile(); } catch (Exception ex) { o.Log($"pass 1 failed on {u.File.Path}: {ex.Message}"); }
                fileDecls[u.Index] = new FileDecls { FileId = u.File.Id, Decls = parser.Decls };
            });
            stats.Decls = fileDecls.Sum(f => f.Decls.Count);
            stats.Pass1 = sw.Elapsed.TotalSeconds;
            o.Log($"pass 1 (declarations): {stats.Decls:N0} declarations in {sw.Elapsed.TotalSeconds:F1}s ({stats.Bytes / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} MB/s)");

            // ---------------------------------------------------------------- merge
            sw.Restart();
            var table = new SymbolTable();
            table.Merge(fileDecls);
            var overrides = ComputeOverrides(table, po);
            stats.Symbols = table.All.Count;
            stats.Merge = sw.Elapsed.TotalSeconds;
            o.Log($"merge: {table.All.Count:N0} symbols, {overrides.Count:N0} overrides in {sw.Elapsed.TotalSeconds:F1}s");

            // ---------------------------------------------------------------- pass 2: references
            sw.Restart();
            var refsPerUnit = new List<RefRec>[units.Count];
            var unresolvedPerUnit = new List<UnresolvedRec>[units.Count];
            long rm = 0, ru = 0, rn = 0, un = 0;
            Parallel.ForEach(order.Where(u => u.File.Resolve), po, u =>
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
            stats.Pass2 = sw.Elapsed.TotalSeconds;
            var refsPerFile = new List<RefRec>[all.Count];
            var unresolvedPerFile = new List<UnresolvedRec>[all.Count];
            foreach (var u in units)
            {
                int id = u.File.Id;
                if (refsPerUnit[u.Index] != null) { if (refsPerFile[id] == null) refsPerFile[id] = refsPerUnit[u.Index]; else refsPerFile[id].AddRange(refsPerUnit[u.Index]); }
                if (unresolvedPerUnit[u.Index] != null) { if (unresolvedPerFile[id] == null) unresolvedPerFile[id] = unresolvedPerUnit[u.Index]; else unresolvedPerFile[id].AddRange(unresolvedPerUnit[u.Index]); }
            }
            stats.Refs = refsPerFile.Sum(r => r?.Count ?? 0);
            stats.Unresolved = unresolvedPerFile.Sum(r => r?.Count ?? 0);
            o.Log($"pass 2 (references): {stats.Refs:N0} references, {stats.Unresolved:N0} unresolved in {sw.Elapsed.TotalSeconds:F1}s; member accesses: {rm:N0} resolved, {ru:N0} unknown receiver, {rn:N0} member not found; {un:N0} unresolved names");

            var data = IndexData.Create(all, table, overrides, refsPerFile, unresolvedPerFile, projectDir, engineDir, o.Engine);
            stats.PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
            stats.ManagedHeap = GC.GetTotalMemory(false);
            if (o.KeepSymbolTable) { Table = table; FilesOut = all; }
            foreach (var f in all) f.Bytes = null;
            stats.Total = total.Elapsed.TotalSeconds;
            return data;
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
            public int Index;
        }

        /// <summary>Preprocess + macro rewrites; identical in both passes.</summary>
        static (Token[], int, FileMacros) Tokenize(ParseUnit u, MacroEnv env)
        {
            var pp = new Preprocessor(env, false).Run(u.File.Bytes, true, false, u.Context);
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

        static List<string> Enumerate(string[] roots, ParallelOptions po, Func<string, bool> filter)
        {
            var result = new ConcurrentBag<string>();
            var pending = new BlockingCollection<string>();
            int outstanding = 0;
            foreach (var r in roots) if (Directory.Exists(r)) { Interlocked.Increment(ref outstanding); pending.Add(r); }
            if (outstanding == 0) return new List<string>();
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
                                    if (filter(path)) result.Add(path);
                                }
                            }
                        }
                        catch (Exception) { }
                        if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                    }
                });
            }
            Task.WaitAll(workers);
            return result.ToList();
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
