using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnrealSense.IndexerCli
{
    sealed class OracleResult
    {
        public string Hover;
        public List<(string File, int Line, int Col)> Refs = new List<(string, int, int)>();
    }

    /// <summary>One clangd process over an already indexed database directory; many reference queries.</summary>
    sealed class ClangdSession : IDisposable
    {
        readonly UnrealSense.Clang.ClangdClient clangd = new UnrealSense.Clang.ClangdClient();

        public ClangdSession(string dbDir, string root, int jobs = 8)
        {
            var clock = Stopwatch.StartNew();
            clangd.Commands = UnrealSense.Clang.CompileCommandIndex.Load(Path.Combine(dbDir, "compile_commands.files.json"));
            clangd.StartAsync(UnrealSense.Clang.ClangdClient.FindClangd(), dbDir, root, jobs: jobs, lowPriority: false).GetAwaiter().GetResult();
            var first = (string)JArray.Parse(File.ReadAllText(Path.Combine(dbDir, "compile_commands.json")))[0]["file"];
            clangd.SyncDocumentAsync(first, File.ReadAllText(first), pushCommand: false).GetAwaiter().GetResult();
            clangd.CloseDocumentAsync(first).GetAwaiter().GetResult();
            var started = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            while (quiet.Elapsed < TimeSpan.FromSeconds(30) && started.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (clangd.State == UnrealSense.Clang.ClangdState.Indexing) quiet.Restart();
                System.Threading.Thread.Sleep(250);
            }
            Console.WriteLine($"clangd index ready after {clock.Elapsed.TotalSeconds:F1}s");
        }

        public OracleResult Query(string file, int line, int col)
        {
            try
            {
                clangd.SyncDocumentAsync(file, File.ReadAllText(file)).GetAwaiter().GetResult();
                var hover = clangd.HoverAsync(file, line - 1, col - 1).GetAwaiter().GetResult();
                var refs = clangd.FindReferencesAsync(file, line - 1, col - 1, includeDeclaration: true).GetAwaiter().GetResult();
                clangd.CloseDocumentAsync(file).GetAwaiter().GetResult();
                var r = new OracleResult { Hover = hover?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) };
                foreach (var x in refs.OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Line).ThenBy(x => x.Column))
                    r.Refs.Add((x.FilePath.Replace('\\', '/'), x.Line + 1, x.Column + 1));
                return r;
            }
            catch (Exception ex) { Console.WriteLine("query failed: " + ex.Message); return null; }
        }

        public void Dispose() => clangd.Dispose();
    }

    /// <summary>Ground truth from clangd for a list of hand-written queries (the TUNING set).</summary>
    static class Oracle
    {
        public static int Run(string queriesPath, string dbDir, string outPath, int jobs)
        {
            var q = JObject.Parse(File.ReadAllText(queriesPath));
            string project = (string)q["project"], engine = (string)q["engine"];
            using var session = new ClangdSession(dbDir, project, jobs);
            var results = new JArray();
            if (File.Exists(outPath))
                foreach (var r in (JArray)JObject.Parse(File.ReadAllText(outPath))["results"]) results.Add(r);
            var done = new HashSet<string>(results.Select(r => (string)r["id"]));
            foreach (JObject query in q["queries"])
            {
                var id = (string)query["id"];
                if (done.Contains(id)) continue;
                var root = (bool?)query["engine"] == true ? engine : project;
                var file = Path.GetFullPath(Path.Combine(root, (string)query["file"]));
                int line = (int)query["line"];
                var lineText = File.ReadLines(file).Skip(line - 1).First();
                int col = IndexOfWord(lineText, (string)query["text"]) + 1;
                if (col <= 0) { Console.WriteLine($"!! {id}: text not found on line {line}"); continue; }
                var sw = Stopwatch.StartNew();
                var res = session.Query(file, line, col);
                if (res == null) continue;
                results.Add(ToJson(id, file, line, col, res));
                Console.WriteLine($"{id,-70} {res.Refs.Count,5} refs  ({sw.ElapsedMilliseconds} ms)  {res.Hover}");
                File.WriteAllText(outPath, new JObject { ["project"] = project, ["engine"] = engine, ["results"] = results }.ToString());
            }
            return 0;
        }

        /// <summary>
        /// The same queries (same positions) answered again by another clangd index: keeps a closed set closed while the oracle
        /// improves. Prints only totals. "clangdFiles" is recomputed from the new index when the source has it.
        /// </summary>
        public static int Requery(string inPath, string dbDir, string outPath, int jobs)
        {
            var src = JObject.Parse(File.ReadAllText(inPath));
            var project = (string)src["project"];
            using var session = new ClangdSession(dbDir, project, jobs);
            var results = new JArray();
            int before = 0, after = 0, failed = 0;
            foreach (JObject r in src["results"])
            {
                var file = (string)r["file"];
                int line = (int)r["line"], col = (int)r["col"];
                before += ((JArray)r["refs"]).Count;
                var res = session.Query(file, line, col);
                if (res == null) { failed++; results.Add(r); continue; }
                after += res.Refs.Count;
                results.Add(ToJson((string)r["id"], file, line, col, res));
            }
            var output = new JObject();
            foreach (var p in src.Properties()) if (p.Name != "results" && p.Name != "clangdFiles") output[p.Name] = p.Value;
            if (src["clangdFiles"] != null)
            {
                var idxDir = Path.Combine(dbDir, ".cache", "clangd", "index");
                var names = Directory.GetFiles(idxDir, "*.idx").Select(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    int dot = n.LastIndexOf('.');
                    return dot > 0 ? n.Substring(0, dot) : n;
                }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                output["clangdFiles"] = new JArray(names);
            }
            output["oracleIndex"] = dbDir;
            output["results"] = results;
            File.WriteAllText(outPath, output.ToString());
            Console.WriteLine($"{results.Count} queries re-asked: {before} references before, {after} now ({failed} failed)");
            return failed == 0 ? 0 : 1;
        }

        public static JObject ToJson(string id, string file, int line, int col, OracleResult r)
        {
            var arr = new JArray();
            foreach (var x in r.Refs) arr.Add(new JObject { ["file"] = x.File, ["line"] = x.Line, ["col"] = x.Col });
            return new JObject { ["id"] = id, ["file"] = file.Replace('\\', '/'), ["line"] = line, ["col"] = col, ["hover"] = r.Hover, ["refs"] = arr };
        }

        public static int IndexOfWord(string line, string word)
        {
            for (int i = line.IndexOf(word, StringComparison.Ordinal); i >= 0; i = line.IndexOf(word, i + 1, StringComparison.Ordinal))
            {
                bool before = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_');
                int e = i + word.Length;
                bool after = e >= line.Length || !(char.IsLetterOrDigit(line[e]) || line[e] == '_');
                if (before && after) return i;
            }
            return -1;
        }
    }
}
