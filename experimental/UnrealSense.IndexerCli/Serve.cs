using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Indexer;

namespace UnrealSense.IndexerCli
{
    /// <summary>
    /// <c>usindex serve &lt;uproject&gt; [--engine]</c>: the index of a project kept in memory for an editor. One JSON object per line
    /// on stdin, one answer per line on stdout (same "id"), log lines on stderr. Builds run in the background (incremental): queries
    /// keep using the previous index until the new one is ready.
    /// <list type="bullet">
    /// <item><c>{"id":1,"cmd":"build"}</c> → <c>{"id":1,"ok":true,"seconds":..,"files":..,"incremental":..,"changed":..}</c> once built</item>
    /// <item><c>{"id":2,"cmd":"refs","file":..,"line":..,"col":..,"altFile":..}</c> (1-based; altFile = same file by another path,
    /// e.g. a subst drive) → <c>{"id":2,"ok":true,"symbol":..,"kind":..,"usages":[{"file","line","col","kind"}],"uncertain":[...]}</c></item>
    /// <item><c>{"id":3,"cmd":"status"}</c>, <c>{"cmd":"exit"}</c></item>
    /// </list>
    /// </summary>
    static class Serve
    {
        static readonly object writeLock = new object();
        static volatile IndexData index;
        static readonly object buildLock = new object();
        static readonly List<Action<BuildStats>> waiting = new List<Action<BuildStats>>();
        static bool running;

        public static int Run(string uproject, bool engine, int threads, string indexPath)
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            void Send(JObject o) { lock (writeLock) stdout.WriteLine(o.ToString(Formatting.None)); }
            void Log(string m) { lock (writeLock) Console.Error.WriteLine(m); }

            Log($"usindex serve {uproject} engine={engine} index={indexPath}");
            // the previous index answers at once while the first (incremental) build runs
            if (File.Exists(indexPath))
                try { index = IndexData.Load(indexPath); Log($"previous index loaded: {index.Files.Length} files, {index.SymbolCount} symbols"); }
                catch (Exception ex) { Log("previous index unreadable: " + ex.Message); }

            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                JObject req;
                try { req = JObject.Parse(line); } catch (Exception ex) { Log("bad request: " + ex.Message); continue; }
                var id = req["id"];
                var cmd = (string)req["cmd"];
                try
                {
                    switch (cmd)
                    {
                        case "exit": return 0;
                        case "status":
                            Send(new JObject { ["id"] = id, ["ok"] = true, ["ready"] = index != null, ["building"] = running });
                            break;
                        case "build":
                            StartBuild(uproject, engine, threads, indexPath, Log, stats => Send(new JObject
                            {
                                ["id"] = id, ["ok"] = stats != null, ["seconds"] = stats?.Total ?? 0, ["files"] = stats?.Files ?? 0,
                                ["incremental"] = stats?.Incremental ?? false, ["changed"] = stats?.ChangedFiles ?? 0,
                            }));
                            break;
                        case "refs":
                            Send(Refs(id, req));
                            break;
                        default:
                            Send(new JObject { ["id"] = id, ["ok"] = false, ["error"] = "unknown command " + cmd });
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"{cmd} failed: {ex}");
                    Send(new JObject { ["id"] = id, ["ok"] = false, ["error"] = ex.Message });
                }
            }
            return 0;
        }

        /// <summary>
        /// One build at a time. Requests that arrive during a build are answered by the next one (their files may have changed
        /// after the running build listed them); any number of them make a single further build.
        /// </summary>
        static void StartBuild(string uproject, bool engine, int threads, string indexPath, Action<string> log, Action<BuildStats> done)
        {
            lock (buildLock)
            {
                waiting.Add(done);
                if (running) return;
                running = true;
            }
            Task.Run(() =>
            {
                while (true)
                {
                    List<Action<BuildStats>> callbacks;
                    lock (buildLock) { callbacks = waiting.ToList(); waiting.Clear(); }
                    var stats = RunBuild(uproject, engine, threads, indexPath, log);
                    foreach (var cb in callbacks) cb(stats);
                    lock (buildLock) if (waiting.Count == 0) { running = false; return; }
                }
            });
        }

        static BuildStats RunBuild(string uproject, bool engine, int threads, string indexPath, Action<string> log)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var data = new IndexBuilder().Build(new BuildOptions
                {
                    UProject = uproject, Engine = engine, Threads = threads, IndexPath = indexPath,
                    Log = m => log($"[{clock.Elapsed.TotalSeconds,6:F1}s] {m}"),
                }, out var stats);
                if (!stats.Unchanged) data.Save(indexPath);
                index = data;
                stats.Total = clock.Elapsed.TotalSeconds;
                log($"index ready: {data.Files.Length} files, {data.SymbolCount} symbols, {data.RefCount} references in {stats.Total:F1}s" + (stats.Incremental ? $" (incremental, {stats.ChangedFiles} files changed)" : " (full)"));
                GC.Collect(2, GCCollectionMode.Optimized, false);
                return stats;
            }
            catch (Exception ex) { log("build failed: " + ex); return null; }
        }

        static JObject Refs(JToken id, JObject req)
        {
            var data = index;
            if (data == null) return new JObject { ["id"] = id, ["ok"] = false, ["error"] = "index not ready" };
            var file = (string)req["file"];
            var alt = (string)req["altFile"];
            int line = (int)req["line"], col = (int)req["col"];
            int sym = data.SymbolAt(file, line, col);
            if (sym < 0 && !string.IsNullOrEmpty(alt)) sym = data.SymbolAt(alt, line, col);
            if (sym < 0) return new JObject { ["id"] = id, ["ok"] = true, ["symbol"] = null, ["usages"] = new JArray(), ["uncertain"] = new JArray() };
            var usages = new JArray();
            foreach (var r in data.Usages(sym).OrderBy(r => data.Files[r.File], StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line).ThenBy(r => r.Col))
                usages.Add(new JObject { ["file"] = data.Files[r.File], ["line"] = r.Line, ["col"] = r.Col, ["kind"] = ((RefKind)r.Kind).ToString() });
            var uncertain = new JArray();
            foreach (var u in data.UnresolvedOf(data.SymNames[sym]).Where(u => u.Kind != 3).Take(2000))
                uncertain.Add(new JObject { ["file"] = data.Files[u.File], ["line"] = u.Line, ["col"] = u.Col });
            return new JObject
            {
                ["id"] = id, ["ok"] = true, ["symbol"] = data.QualifiedName(sym), ["name"] = data.SymNames[sym],
                ["kind"] = ((SymKind)data.SymKinds[sym]).ToString(), ["usages"] = usages, ["uncertain"] = uncertain,
            };
        }
    }
}
