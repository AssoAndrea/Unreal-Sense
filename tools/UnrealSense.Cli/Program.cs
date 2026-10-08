using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnrealSense.Analysis;
using UnrealSense.Assets;
using UnrealSense.Cpp;
using UnrealSense.Project;
using UnrealSense.Workspace;

namespace UnrealSense.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: unrealsense dump <file.uasset>");
                return 1;
            }

            switch (args[0])
            {
                case "dump": return Dump(args[1]);
                case "assets": return Assets(args[1], args.Skip(2).ToArray());
                case "parse": return Parse(args[1]);
                case "analyze": return Analyze(args[1]);
                case "goto": return GoTo(args[1], args.Skip(2).ToArray());
                case "decls": return Decls(args[1]);
                case "clangdb":
                {
                    var project = UnrealProject.Load(UnrealProject.FindUProject(args[1]));
                    Console.WriteLine(Clang.CompileDatabase.Generate(project, Console.WriteLine));
                    return 0;
                }
                case "unity":
                {
                    // unity <compile_commands.files.json> <outDir> <maxFiles> [maxKB]: unity database with a chosen group size
                    Directory.CreateDirectory(args[2]);
                    var stats = Clang.CompileDatabase.BuildUnity(args[1], Path.Combine(args[2], "compile_commands.json"), Path.Combine(args[2], "unity"), null,
                        maxFiles: int.Parse(args[3]), maxBytes: (args.Length > 4 ? long.Parse(args[4]) : 1024) * 1024);
                    Console.WriteLine(stats.ToString().Split('\n')[0]);
                    return 0;
                }
                case "simengine": return SimEngine(args[1], args[2], args[3], args.Skip(4).ToArray());
                case "grep":
                {
                    // grep <word> <root>...: parallel whole-word search over .h/.cpp/.inl (no index), timed twice (cold/warm cache).
                    var word = args[1];
                    var roots = args.Skip(2).ToArray();
                    for (int run = 0; run < 2; run++)
                    {
                        var sw = Stopwatch.StartNew();
                        var files = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.*", SearchOption.AllDirectories))
                            .Where(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".inl", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var listTime = sw.Elapsed.TotalSeconds;
                        var needle = System.Text.Encoding.ASCII.GetBytes(word);
                        long bytes = 0;
                        int hits = 0;
                        System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
                        {
                            byte[] data;
                            try { data = File.ReadAllBytes(f); } catch (IOException) { return; }
                            System.Threading.Interlocked.Add(ref bytes, data.Length);
                            var span = new ReadOnlySpan<byte>(data);
                            int at = span.IndexOf(needle);
                            while (at >= 0)
                            {
                                bool start = at == 0 || !(char.IsLetterOrDigit((char)data[at - 1]) || data[at - 1] == '_');
                                int end = at + needle.Length;
                                bool stop = end >= data.Length || !(char.IsLetterOrDigit((char)data[end]) || data[end] == '_');
                                if (start && stop) { System.Threading.Interlocked.Increment(ref hits); return; }
                                int next = span.Slice(at + 1).IndexOf(needle);
                                at = next < 0 ? -1 : at + 1 + next;
                            }
                        });
                        Console.WriteLine($"run {run + 1}: {files.Count:N0} files, {bytes / (1024 * 1024):N0} MB, '{word}' in {hits:N0} files, " +
                                          $"listing {listTime:F1}s, total {sw.Elapsed.TotalSeconds:F1}s");
                    }
                    return 0;
                }
                case "sampledb": return SampleDb(args[1], args[2], int.Parse(args[3]), args.Length > 4 ? int.Parse(args[4]) : 0);
                case "indexbench": return IndexBench(args[1], args.Length > 2 ? int.Parse(args[2]) : 1, args.Length > 3 && args[3] == "keep");
                case "resanitize":
                {
                    var project = UnrealProject.Load(UnrealProject.FindUProject(args[1]));
                    Clang.CompileDatabase.Resanitize(project, args.Length > 2 ? args[2] : null, Console.WriteLine);
                    return 0;
                }
                case "opentest": return OpenTest(args[1], args.Skip(2).ToArray());
                case "editor-enable":
                {
                    var error = Remote.RemoteExecutionSetup.Enable(UnrealProject.FindUProject(args[1]));
                    Console.WriteLine(error ?? "remote execution enabled");
                    return error == null ? 0 : 1;
                }
                case "editor-open": return EditorOpen(args[1], args.Length > 2 ? args[2] : null);
                case "classes": return Classes(args[1], args.Length > 2 ? args[2] : null);
                case "new-class": return NewClass(args[1], args.Skip(2).ToArray());
                case "refs": return Refs(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args.Length > 5 ? args[5] : null, args.Length > 6 ? int.Parse(args[6]) : 0);
                default:
                    Console.Error.WriteLine($"unknown command '{args[0]}'");
                    return 1;
            }
        }

        /// <summary>editor-open &lt;dir|uproject&gt; [/Game/Path/Asset.Asset]: lists the editors that answer remote execution, opens the asset in the project's one.</summary>
        static int EditorOpen(string path, string objectPath)
        {
            var uproject = UnrealProject.FindUProject(path);
            var directory = Path.GetDirectoryName(uproject);
            Console.WriteLine($"{uproject}: plugin listed={Remote.RemoteExecutionSetup.IsPythonPluginListed(uproject)}, bRemoteExecution={Remote.RemoteExecutionSetup.IsRemoteExecutionEnabled(directory)}");
            Console.WriteLine($"editor processes: {string.Join(", ", Remote.RemoteExecutionClient.FindEditorProcesses().Select(p => $"{p.ProcessName}({p.Id})"))}");
            var client = new Remote.RemoteExecutionClient();
            var nodes = client.DiscoverAsync(TimeSpan.FromSeconds(1.5)).GetAwaiter().GetResult();
            foreach (var n in nodes) Console.WriteLine("node: " + n);
            var node = Remote.RemoteExecutionClient.FindNodeForProject(nodes, directory);
            if (node == null) { Console.WriteLine("no editor has this project open"); return 2; }
            if (objectPath == null) return 0;
            var sw = Stopwatch.StartNew();
            var result = client.RunAsync(node, Remote.RemoteExecutionClient.OpenAssetScript(objectPath), TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Console.WriteLine($"success={result.Success} result={result.Result} output={result.Output} ({sw.ElapsedMilliseconds} ms)");
            return result.Success ? 0 : 3;
        }

        static Templates.ParentClassCatalog LoadCatalog(UnrealProject project, out SymbolIndex symbols)
        {
            symbols = new SymbolIndex(project);
            var sw = Stopwatch.StartNew();
            symbols.Build();
            Console.WriteLine($"project headers: {symbols.Headers.Count()} in {sw.ElapsedMilliseconds} ms");
            var engine = Templates.EngineClassScanner.Scan(project.Engine, Templates.EngineClassScanner.DefaultCachePath(project.Engine), Console.WriteLine);
            return Templates.ParentClassCatalog.Create(project, symbols, engine);
        }

        /// <summary>classes &lt;dir|uproject&gt; [filter]: the parents the New Unreal Class dialog offers.</summary>
        static int Classes(string path, string filter)
        {
            var project = UnrealProject.Load(UnrealProject.FindUProject(path));
            var catalog = LoadCatalog(project, out _);
            Console.WriteLine($"catalog: {catalog.All.Count} parents");
            foreach (var info in catalog.All.Where(i => filter == null || i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(200))
                Console.WriteLine($"  {info.Kind,-9} {info.Name,-40} : {info.BaseName,-30} {info.ModuleName,-20} {info.PluginName} \"{info.IncludePath}\"{(info.IsPrivateHeader ? " (private)" : "")}");
            return 0;
        }

        /// <summary>new-class &lt;dir|uproject&gt; &lt;Parent|Common label&gt; &lt;Name&gt; [Module] [Public|Private|Root] [SubFolder] [--write]</summary>
        static int NewClass(string path, string[] args)
        {
            var project = UnrealProject.Load(UnrealProject.FindUProject(path));
            var catalog = LoadCatalog(project, out var symbols);
            var parent = Templates.ParentClassCatalog.CommonParents.FirstOrDefault(c => string.Equals(c.DisplayName, args[0], StringComparison.OrdinalIgnoreCase))
                ?? catalog.Find(args[0]);
            if (parent == null) { Console.WriteLine("unknown parent " + args[0]); return 1; }
            var module = args.Length > 2 ? project.AllModules.First(m => m.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase)) : project.Modules[0];
            var request = new Templates.NewClassRequest
            {
                Name = args[1],
                Parent = parent,
                Module = module,
                Location = args.Length > 3 ? (Templates.ClassLocation)Enum.Parse(typeof(Templates.ClassLocation), args[3], true) : Templates.ClassLocation.Public,
                SubFolder = args.Length > 4 && !args[4].StartsWith("--") ? args[4] : null,
            };
            var generator = new Templates.NewClassGenerator(project, catalog, symbols);
            var result = generator.Generate(request);
            Console.WriteLine($"class {result.ClassName}, template {result.TemplateName} ({(generator.EngineTemplatesDirectory != null ? "engine" : "built-in")}), missing dependency: {result.MissingDependency ?? "none"}");
            foreach (var e in result.Errors) Console.WriteLine("ERROR: " + e);
            foreach (var w in result.Warnings) Console.WriteLine("warning: " + w);
            Console.WriteLine("==== " + result.HeaderPath);
            Console.Write(result.HeaderText);
            if (result.SourcePath != null)
            {
                Console.WriteLine("==== " + result.SourcePath);
                Console.Write(result.SourceText);
            }
            if (args.Contains("--write") && result.IsValid) Templates.NewClassGenerator.Write(result);
            return result.IsValid ? 0 : 2;
        }

        /// <summary>assets &lt;dir|uproject&gt; [Module.Type[:Member]]...</summary>
        static int Assets(string path, string[] queries)
        {
            var project = UnrealProject.Load(UnrealProject.FindUProject(path));
            Console.WriteLine($"{project.Name}: engine={project.Engine}, modules={string.Join(",", project.AllModules)}, redirects={project.Redirects.Count}");
            var index = new AssetIndex(project);
            var sw = Stopwatch.StartNew();
            index.Build(cachePath: null);
            var failed = index.Assets.Where(a => a.Error != null).ToList();
            Console.WriteLine($"indexed {index.Count} assets in {sw.ElapsedMilliseconds} ms, {failed.Count} failed");
            var symbols = new SymbolIndex(project);
            symbols.Build();
            foreach (var line in index.LearnModuleAliases(symbols.Headers.SelectMany(h => h.Types.Select(t => (project.FindModuleForFile(h.FilePath)?.Name, t.ReflectedName)))))
                Console.WriteLine("alias: " + line);
            foreach (var line in index.DescribeCoverage()) Console.WriteLine(line);
            string ModuleOfType(string cppName) => project.FindModuleForFile(symbols.FindTypeFile(cppName) ?? "")?.Name;
            // Cost of the counters for every reflected type and property, without and with C++ inheritance.
            var allTypes = symbols.Headers.SelectMany(h => h.Types.Select(t => (t, Module: project.FindModuleForFile(h.FilePath)?.Name))).ToList();
            void Measure(string label)
            {
                var clock = Stopwatch.StartNew();
                int found = 0;
                foreach (var (t, m) in allTypes)
                {
                    found += index.FindTypeUsages(m, t.ReflectedName).Count;
                    foreach (var p in t.Properties) found += index.FindPropertyUsages(m, t.ReflectedName, p.Name).Count;
                }
                Console.WriteLine($"{label}: {allTypes.Count} types and their properties in {clock.ElapsedMilliseconds} ms ({found} usages)");
            }
            Measure("counters without C++ inheritance");
            index.NativeSubclasses = (module, type) =>
            {
                var own = symbols.FindTypesByReflectedName(type)
                    .Where(t => module == null || string.Equals(ModuleOfType(t.Name), module, StringComparison.OrdinalIgnoreCase)).ToList();
                var derived = own.Count > 0 ? own.SelectMany(t => symbols.GetDerivedTypes(t)) : symbols.GetDerivedTypesOfExternal(type);
                return derived.Select(d => (ModuleOfType(d.Name), d.ReflectedName)).ToList();
            };
            Measure("counters with C++ inheritance");
            Measure("counters with C++ inheritance (again)");
            var ext = index.FindTypeUsages(null, "Character");
            Console.WriteLine($"engine ACharacter: {ext.Count(u => u.Kind == AssetUsageKind.Subclass)} derived Blueprints, {ext.Count(u => u.Detail?.StartsWith("via C++") == true)} via project C++ classes");
            foreach (var f in failed.Take(10))
                Console.WriteLine($"  ! {f.PackageName}: {f.Error}");

            foreach (var bp in index.Assets.Where(a => a.NativeParent != null && a.NativeParent.Module == project.Name).OrderBy(a => a.PackageName))
                Console.WriteLine($"  {bp.PackageName,-70} : {bp.NativeParent.Type}  calls={bp.FunctionCalls.Count(f => f.Module == project.Name)} events={bp.ImplementedEvents.Count}");

            foreach (var query in queries)
            {
                var parts = query.Split('.', ':');
                List<AssetUsage> usages = parts.Length == 2
                    ? index.FindTypeUsages(parts[0], parts[1])
                    : query.StartsWith("prop:")
                        ? index.FindPropertyUsages(parts[1], parts[2], parts[3])
                        : index.FindFunctionUsages(parts[0], parts[1], parts[2]);
                Console.WriteLine($"-- {query}: {usages.Count}");
                foreach (var u in usages)
                    Console.WriteLine($"   {u.Kind,-20} {u.Asset.PackageName} {u.Detail}");
            }
            return 0;
        }

        static int Parse(string path)
        {
            var file = HeaderParser.Parse(File.ReadAllText(path), path);
            foreach (var inc in file.Includes) Console.WriteLine($"#include {inc.Path}");
            foreach (var d in file.Delegates) Console.WriteLine($"delegate {d.Name} ({d.Macro})");
            foreach (var type in file.Types)
            {
                Console.WriteLine($"{type.Macro.Name}({string.Join(", ", type.Macro.Specifiers)}) {type.Keyword} {type.Name} : {string.Join(", ", type.BaseTypes)}  [{type.GeneratedBodyMacro}] api={type.ApiMacro}");
                foreach (var p in type.Properties)
                    Console.WriteLine($"   {p.Access,-9} UPROPERTY({string.Join(", ", p.Macro.Specifiers)}{(p.Macro.Meta.Count > 0 ? " | meta: " + string.Join(", ", p.Macro.Meta) : "")}) {p}");
                foreach (var f in type.Functions)
                    Console.WriteLine($"   {f.Access,-9} UFUNCTION({string.Join(", ", f.Macro.Specifiers)}) {f}{(f.IsOverride ? " override" : "")}{(f.HasInlineBody ? " {..}" : "")}");
                if (type.EnumValues.Count > 0) Console.WriteLine("   = " + string.Join(", ", type.EnumValues));
            }
            return 0;
        }

        static int Analyze(string path)
        {
            using var workspace = new UnrealWorkspace();
            workspace.LoadAsync(UnrealProject.FindUProject(path), watch: false).GetAwaiter().GetResult();
            Console.WriteLine(workspace.StatusText + $" (catalog: {workspace.Catalog.Count} entries)");
            var context = workspace.CreateAnalysisContext();
            int count = 0;
            foreach (var header in workspace.Symbols.Headers.OrderBy(h => h.FilePath))
            {
                foreach (var d in HeaderAnalyzer.Analyze(header, context))
                {
                    count++;
                    Console.WriteLine($"{Path.GetFileName(header.FilePath)}({header.GetLine(d.Start) + 1}): {d.Id} {d.Severity}: {d.Message}{(d.Fixes.Count > 0 ? "  [fix: " + d.Fixes[0].Title + "]" : "")}");
                }
            }
            Console.WriteLine($"{count} diagnostics");
            return 0;
        }

        static int Decls(string path)
        {
            foreach (var d in Navigation.DeclarationScanner.Scan(File.ReadAllText(path)))
                Console.WriteLine($"{d.Line + 1,5} {d.Kind,-10} {(d.Container != null ? d.Container + "::" : "")}{d.Name}");
            return 0;
        }

        /// <summary>goto &lt;project&gt; query... ("f:" prefix = file search)</summary>
        static int GoTo(string path, string[] queries)
        {
            var project = UnrealProject.Load(UnrealProject.FindUProject(path));
            var roots = Navigation.GoToRoots.For(project);
            var index = new Navigation.GoToIndex();
            var sw = Stopwatch.StartNew();
            index.Build(roots, Navigation.GoToRoots.CachePath(project), new Progress<string>(Console.WriteLine));
            Console.WriteLine($"build: {sw.ElapsedMilliseconds} ms, {index.SymbolCount:N0} symbols, {index.FileCount:N0} files, {GC.GetTotalMemory(true) / (1024 * 1024)} MB managed");
            foreach (var q in queries)
            {
                for (int warm = 0; warm < 2; warm++)
                {
                    sw.Restart();
                    if (q.StartsWith("f:"))
                    {
                        var results = index.SearchFiles(q.Substring(2), 50);
                        if (warm == 1)
                        {
                            Console.WriteLine($"-- files '{q.Substring(2)}' {sw.Elapsed.TotalMilliseconds:F1} ms");
                            foreach (var r in results.Take(8)) Console.WriteLine($"   {r.Score,4} {r.RelativePath}");
                        }
                    }
                    else
                    {
                        var results = index.SearchSymbols(q, null, 50);
                        if (warm == 1)
                        {
                            Console.WriteLine($"-- symbols '{q}' {sw.Elapsed.TotalMilliseconds:F1} ms");
                            foreach (var r in results.Take(8)) Console.WriteLine($"   {r.Score,4} {r}");
                        }
                    }
                }
            }
            return 0;
        }

        /// <summary>refs &lt;project&gt; &lt;file&gt; &lt;line 1-based&gt; &lt;column 1-based&gt;: semantic references via clangd.</summary>
        /// <summary>
        /// simengine &lt;project files.json&gt; &lt;Engine dir&gt; &lt;out files.json&gt; &lt;module dir&gt;...: builds an
        /// engine-scale benchmark database on an installed (precompiled) engine, whose own sources UBT never lists.
        /// Each engine source gets the command of a project source with its module's Definitions.h and include folders
        /// (Public/Private/Classes/Internal + UHT output). Not every file compiles cleanly, but the amount of work is
        /// representative of indexing a source-built engine.
        /// </summary>
        static int SimEngine(string projectFiles, string engineDir, string output, string[] moduleDirs)
        {
            var template = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(projectFiles)).OfType<Newtonsoft.Json.Linq.JObject>()
                .First(e => ((string)e["file"]).EndsWith(".cpp") && !((string)e["file"]).Contains(".gen."));
            var args = template["arguments"].Select(a => (string)a).ToList();
            int source = args.FindIndex(a => !a.StartsWith("/") && !a.StartsWith("-") && !a.StartsWith("@") && a.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase));
            int definitions = args.FindIndex(a => a.StartsWith("/FI") && a.EndsWith("Definitions.h", StringComparison.OrdinalIgnoreCase));
            var result = new Newtonsoft.Json.Linq.JArray();
            foreach (var moduleDir in moduleDirs)
            {
                var full = Path.GetFullPath(moduleDir);
                var name = Path.GetFileName(full.TrimEnd('\\'));
                var defs = Path.Combine(engineDir, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", name, "Definitions.h");
                if (!File.Exists(defs)) { Console.WriteLine($"skip {name}: no Definitions.h"); continue; }
                var extra = new[] { "Public", "Private", "Classes", "Internal" }.Select(d => Path.Combine(full, d)).Where(Directory.Exists)
                    .Concat(new[] { Path.Combine(engineDir, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", name, "UHT"), Path.Combine(engineDir, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", name) })
                    .Where(Directory.Exists).Select(d => "/I" + d.Replace('\\', '/')).ToList();
                int count = 0;
                foreach (var cpp in Directory.EnumerateFiles(full, "*.cpp", SearchOption.AllDirectories))
                {
                    if (cpp.IndexOf("\\Tests\\", StringComparison.OrdinalIgnoreCase) >= 0 || cpp.IndexOf("\\Mac\\", StringComparison.OrdinalIgnoreCase) >= 0
                        || cpp.IndexOf("\\Linux\\", StringComparison.OrdinalIgnoreCase) >= 0 || cpp.IndexOf("\\Android\\", StringComparison.OrdinalIgnoreCase) >= 0
                        || cpp.IndexOf("\\IOS\\", StringComparison.OrdinalIgnoreCase) >= 0 || cpp.IndexOf("\\Apple\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var a = args.ToList();
                    a[source] = cpp.Replace('\\', '/');
                    if (definitions >= 0) a[definitions] = "/FI" + defs.Replace('\\', '/');
                    a.AddRange(extra);
                    result.Add(new Newtonsoft.Json.Linq.JObject { ["directory"] = template["directory"], ["file"] = cpp.Replace('\\', '/'), ["arguments"] = new Newtonsoft.Json.Linq.JArray(a) });
                    count++;
                }
                Console.WriteLine($"{name}: {count} sources");
            }
            File.WriteAllText(output, result.ToString(Newtonsoft.Json.Formatting.None));
            Console.WriteLine($"{result.Count} engine sources -> {output}");
            return 0;
        }

        /// <summary>
        /// sampledb &lt;compile_commands.json&gt; &lt;outDir&gt; &lt;count&gt; [seed]: writes a database with
        /// <c>count</c> entries picked evenly (or randomly with a seed) from a full one, for <c>indexbench</c>.
        /// </summary>
        static int SampleDb(string source, string outDir, int count, int seed)
        {
            var entries = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(source));
            var rng = new Random(seed);
            var picked = seed != 0
                ? entries.OrderBy(_ => rng.Next()).Take(count).ToList()
                : Enumerable.Range(0, Math.Min(count, entries.Count)).Select(i => entries[(int)((long)i * entries.Count / Math.Min(count, entries.Count))]).ToList();
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "compile_commands.json"), new Newtonsoft.Json.Linq.JArray(picked).ToString());
            Console.WriteLine($"{picked.Count} of {entries.Count} entries -> {outDir}");
            return 0;
        }

        /// <summary>
        /// indexbench &lt;compile-commands-dir&gt; [jobs]: indexes the database from scratch (delete its .cache first)
        /// and prints when clangd's background index went quiet, with the peak clangd memory.
        /// </summary>
        static int IndexBench(string dbDir, int jobs, bool keepCache = false)
        {
            var cache = Path.Combine(dbDir, ".cache");
            if (!keepCache && Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            using var clangd = new Clang.ClangdClient();
            var clock = Stopwatch.StartNew();
            double lastEnd = 0;
            long peak = 0;
            int units = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dbDir, "compile_commands.json"))).Count;
            clangd.Log += m =>
            {
                if (m.StartsWith("progress end")) lastEnd = clock.Elapsed.TotalSeconds;
                if (m.StartsWith("progress report")) Console.WriteLine($"[{clock.Elapsed.TotalSeconds,7:F1}s] {m.Substring(16)}");
            };
            clangd.StartAsync(Clang.ClangdClient.FindClangd(), dbDir, dbDir, jobs: jobs, lowPriority: false).GetAwaiter().GetResult();
            var first = (string)Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dbDir, "compile_commands.json")))[0]["file"];
            clangd.SyncDocumentAsync(first, File.ReadAllText(first)).GetAwaiter().GetResult();
            clangd.CloseDocumentAsync(first).GetAwaiter().GetResult();

            // clangd reports several begin/end cycles: done after 30 s without indexing.
            var quiet = Stopwatch.StartNew();
            var cpuAtEnd = TimeSpan.Zero;
            while (quiet.Elapsed < TimeSpan.FromSeconds(30) && clock.Elapsed < TimeSpan.FromHours(4))
            {
                if (clangd.State == Clang.ClangdState.Indexing) { quiet.Restart(); cpuAtEnd = clangd.CpuTime; }
                peak = Math.Max(peak, clangd.MemoryBytes);
                System.Threading.Thread.Sleep(250);
            }
            // CPU use: how many cores clangd kept busy on average while indexing (ideal = threads).
            double busyCores = lastEnd > 0 ? cpuAtEnd.TotalSeconds / lastEnd : 0;
            Console.WriteLine($"indexed {units} translation units with {clangd.Jobs} threads in {lastEnd:F1}s " +
                              $"({(lastEnd > 0 ? units * 60 / lastEnd : 0):F1} units/min), CPU {cpuAtEnd.TotalSeconds:F0}s = {busyCores:F1} busy cores on average, " +
                              $"peak clangd memory {peak / (1024.0 * 1024 * 1024):F1} GB, {clangd.UnitsWithErrors} unit(s) with compile errors");
            return 0;
        }

        /// <summary>refs &lt;project&gt; &lt;file&gt; &lt;line&gt; &lt;column&gt; [compile-commands-dir] [jobs]</summary>
        static int Refs(string projectPath, string file, int line, int column, string dbDirOverride = null, int jobs = 0)
        {
            var project = UnrealProject.Load(UnrealProject.FindUProject(projectPath));
            var dbDir = dbDirOverride ?? Clang.CompileDatabase.GetOutputDirectory(project);
            if (!File.Exists(Path.Combine(dbDir, "compile_commands.json")))
                Clang.CompileDatabase.Generate(project, Console.WriteLine);

            using var clangd = new Clang.ClangdClient();
            var clock = Stopwatch.StartNew();
            clangd.Log += m => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] {m}");
            clangd.Commands = Clang.CompileCommandIndex.Load(Path.Combine(dbDir, Clang.CompileDatabase.FilesVariant));
            var sw = Stopwatch.StartNew();
            clangd.StatusChanged += (s, e) =>
            {
                if (clangd.State == Clang.ClangdState.Indexing && clangd.IndexPercentage % 25 == 0)
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:F0}s] indexing {clangd.IndexPercentage}% {clangd.IndexMessage}");
            };
            clangd.StartAsync(Clang.ClangdClient.FindClangd(), dbDir, project.ProjectDirectory, jobs: jobs, lowPriority: false).GetAwaiter().GetResult();
            // Like the extension: open a database entry first so clangd loads the database and starts the background index.
            var first = (string)Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dbDir, "compile_commands.json")))[0]["file"];
            clangd.SyncDocumentAsync(first, File.ReadAllText(first), pushCommand: false).GetAwaiter().GetResult();
            clangd.CloseDocumentAsync(first).GetAwaiter().GetResult();
            clangd.SyncDocumentAsync(file, File.ReadAllText(file)).GetAwaiter().GetResult();

            // Wait for the background index to start and finish (persisted: fast on later runs).
            // clangd reports several begin/end cycles (loading, opened files, the real indexing): wait for 30 s of quiet.
            var started = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            while (quiet.Elapsed < TimeSpan.FromSeconds(30) && started.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (clangd.State == Clang.ClangdState.Indexing) quiet.Restart();
                System.Threading.Thread.Sleep(250);
            }
            Console.WriteLine($"index ready after {sw.Elapsed.TotalSeconds:F1}s (state {clangd.State})");

            sw.Restart();
            var hover = clangd.HoverAsync(file, line - 1, column - 1).GetAwaiter().GetResult();
            Console.WriteLine("symbol: " + hover?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0));
            var refs = clangd.FindReferencesAsync(file, line - 1, column - 1, includeDeclaration: true).GetAwaiter().GetResult();
            Console.WriteLine($"{refs.Count} references in {sw.ElapsedMilliseconds} ms:");
            foreach (var r in refs.OrderBy(r => r.FilePath).ThenBy(r => r.Line))
            {
                var text = File.ReadLines(r.FilePath).Skip(r.Line).FirstOrDefault()?.Trim();
                Console.WriteLine($"  {Path.GetFileName(r.FilePath)}:{r.Line + 1}:{r.Column + 1}  {text}");
            }
            return 0;
        }

        /// <summary>
        /// Does opening documents with their own command (as the extension does) make clangd's background index work?
        /// Starts clangd on an already indexed database, waits for quiet, opens the files and logs what follows.
        /// </summary>
        static int OpenTest(string dbDir, string[] files)
        {
            using var clangd = new Clang.ClangdClient();
            var clock = Stopwatch.StartNew();
            clangd.Log += m => { if (m.StartsWith("progress")) Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] {m}"); };
            clangd.Commands = Clang.CompileCommandIndex.Load(Path.Combine(dbDir, Clang.CompileDatabase.FilesVariant));
            clangd.StartAsync(Clang.ClangdClient.FindClangd(), dbDir, dbDir, jobs: 8, lowPriority: false).GetAwaiter().GetResult();
            var first = (string)Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dbDir, "compile_commands.json")))[0]["file"];
            clangd.SyncDocumentAsync(first, File.ReadAllText(first), pushCommand: false).GetAwaiter().GetResult();
            clangd.CloseDocumentAsync(first).GetAwaiter().GetResult();
            void WaitQuiet(int seconds)
            {
                var quiet = Stopwatch.StartNew();
                var started = Stopwatch.StartNew();
                while (quiet.Elapsed < TimeSpan.FromSeconds(seconds) && started.Elapsed < TimeSpan.FromMinutes(20))
                {
                    if (clangd.State == Clang.ClangdState.Indexing) quiet.Restart();
                    System.Threading.Thread.Sleep(250);
                }
            }
            WaitQuiet(20);
            Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] quiet; opening {files.Length} file(s) with their own command");
            foreach (var f in files)
                clangd.SyncDocumentAsync(f, File.ReadAllText(f)).GetAwaiter().GetResult();
            foreach (var f in files)
            {
                var sw = Stopwatch.StartNew();
                clangd.HoverAsync(f, 0, 0).GetAwaiter().GetResult();
                Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] {Path.GetFileName(f)} parsed ({sw.Elapsed.TotalSeconds:F1}s)");
            }
            WaitQuiet(30);
            Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] done; cpu {clangd.CpuTime.TotalSeconds:F0}s");
            return 0;
        }

        static int Dump(string path)
        {
            var package = UAssetPackage.Read(path);
            Console.WriteLine($"UE4={package.FileVersionUE4} UE5={package.FileVersionUE5} flags=0x{package.PackageFlags:X8}");
            Console.WriteLine($"-- {package.Imports.Count} imports");
            for (int i = 0; i < package.Imports.Count; i++)
            {
                var imp = package.Imports[i];
                Console.WriteLine($"  [{-i - 1}] {imp.ClassName,-28} {package.GetImportPath(-i - 1)}");
            }
            Console.WriteLine($"-- {package.Exports.Count} exports");
            for (int i = 0; i < package.Exports.Count; i++)
            {
                var exp = package.Exports[i];
                Console.WriteLine($"  [{i + 1}] {exp.ObjectName,-40} class={package.GetObjectName(exp.ClassIndex)} super={package.GetObjectName(exp.SuperIndex)} outer={package.GetObjectName(exp.OuterIndex)}");
            }
            return 0;
        }
    }
}
