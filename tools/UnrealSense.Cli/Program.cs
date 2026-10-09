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
                case "compiledb":
                {
                    // compiledb <dir|uproject>: the compile database the own indexer reads (UBT runs only when it is out of date)
                    var project = UnrealProject.Load(UnrealProject.FindUProject(args[1]));
                    Console.WriteLine(CompileDatabase.Ensure(project, Console.WriteLine));
                    return 0;
                }
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
                case "resanitize":
                {
                    var project = UnrealProject.Load(UnrealProject.FindUProject(args[1]));
                    return CompileDatabase.Resanitize(project, Console.WriteLine) ? 0 : 1;
                }
                case "editor-enable":
                {
                    var error = Remote.RemoteExecutionSetup.Enable(UnrealProject.FindUProject(args[1]));
                    Console.WriteLine(error ?? "remote execution enabled");
                    return error == null ? 0 : 1;
                }
                case "editor-open": return EditorOpen(args[1], args.Length > 2 ? args[2] : null);
                case "classes": return Classes(args[1], args.Length > 2 ? args[2] : null);
                case "new-class": return NewClass(args[1], args.Skip(2).ToArray());
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
