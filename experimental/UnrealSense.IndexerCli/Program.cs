using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnrealSense.Indexer;

namespace UnrealSense.IndexerCli
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();
            var opts = args.Where(a => a.StartsWith("--")).ToList();
            var pos = args.Where(a => !a.StartsWith("--")).ToList();
            int threads = 0;
            foreach (var o in opts) if (o.StartsWith("--threads=")) threads = int.Parse(o.Substring(10));
            string indexOverride = opts.FirstOrDefault(o => o.StartsWith("--index="))?.Substring(8);
            switch (pos[0])
            {
                case "oracle": return Oracle.Run(pos[1], pos[2], pos[3], pos.Count > 4 ? int.Parse(pos[4]) : 4);
                case "build": return Build(pos[1], opts.Contains("--engine"), threads, indexOverride);
                case "refs": return Refs(pos[1], pos[2], int.Parse(pos[3]), int.Parse(pos[4]), opts.Contains("--engine"), indexOverride);
                case "score": return Score(pos[1], pos.Count > 2 ? pos[2] : null, opts.Contains("--engine"), indexOverride, opts.FirstOrDefault(o => o.StartsWith("--details="))?.Substring(10), opts.FirstOrDefault(o => o.StartsWith("--scope="))?.Substring(8));
                case "heldout":
                {
                    // heldout <projectDir> <clangdDbDir> <out.json> <seed> <types> <funcs> <fields> <enumvalues> <maxAttempts> <root>...
                    return HeldOut.Run(pos[1], pos[2], pos[3], int.Parse(pos[4]), pos.Skip(10).ToArray(), int.Parse(pos[5]), int.Parse(pos[6]), int.Parse(pos[7]), int.Parse(pos[8]), int.Parse(pos[9]));
                }
                case "unresolved":
                {
                    // unresolved <uproject> <kind 1|2|3> [samples] [--engine]: most frequent unresolved names with sample locations
                    var data = IndexData.Load(IndexPath(pos[1], opts.Contains("--engine"), indexOverride));
                    int kind = int.Parse(pos[2]); int samples = pos.Count > 3 ? int.Parse(pos[3]) : 40;
                    var rnd = new Random(1);
                    var idx = Enumerable.Range(0, data.UnKind.Length).Where(i => data.UnKind[i] == kind).ToList();
                    Console.WriteLine($"{idx.Count} unresolved of kind {kind}");
                    foreach (var g in idx.GroupBy(i => data.UnName[i]).OrderByDescending(g => g.Count()).Take(25)) Console.WriteLine($"  {g.Count(),7} {data.UnresolvedNames[g.Key]}");
                    foreach (var i in idx.OrderBy(_ => rnd.Next()).Take(samples))
                    {
                        var f = data.Files[data.UnFile[i]];
                        string text = ""; try { text = File.ReadLines(f).Skip(data.UnLine[i] - 1).First().Trim(); } catch { }
                        Console.WriteLine($"{f}:{data.UnLine[i]}:{data.UnCol[i]} {data.UnresolvedNames[data.UnName[i]]}");
                        Console.WriteLine("      " + text);
                    }
                    return 0;
                }
                case "dump": return Dump(pos[1], pos[2], pos.Count > 3 ? int.Parse(pos[3]) : 0, pos.Count > 4 ? int.Parse(pos[4]) : int.MaxValue, opts.Contains("--engine"), indexOverride);
                default: return Usage();
            }
        }

        static int Dump(string uproject, string file, int fromLine, int toLine, bool engine, string indexOverride)
        {
            var data = IndexData.Load(IndexPath(uproject, engine, indexOverride));
            int f = data.FileId(file);
            if (f < 0) { Console.WriteLine("file not in index"); return 1; }
            var rows = new List<(int Line, int Col, string Text)>();
            for (int s = 0; s < data.SymbolCount; s++)
                foreach (var r in data.RefsOf(s))
                    if (r.File == f && r.Line >= fromLine && r.Line <= toLine) rows.Add((r.Line, r.Col, $"{(RefKind)r.Kind,-5} {(SymKind)data.SymKinds[s]} {data.QualifiedName(s)} #{s}"));
            for (int k = 0; k < data.UnFile.Length; k++)
                if (data.UnFile[k] == f && data.UnLine[k] >= fromLine && data.UnLine[k] <= toLine) rows.Add((data.UnLine[k], data.UnCol[k], $"??{data.UnKind[k]}   {data.UnresolvedNames[data.UnName[k]]}"));
            var lines = File.ReadAllLines(file);
            int last = -1;
            foreach (var r in rows.OrderBy(x => x.Line).ThenBy(x => x.Col))
            {
                if (r.Line != last) { Console.WriteLine($"{r.Line,5}: {(r.Line - 1 < lines.Length ? lines[r.Line - 1].Trim() : "")}"); last = r.Line; }
                Console.WriteLine($"         {r.Col,4}  {r.Text}");
            }
            return 0;
        }

        static int Usage()
        {
            Console.WriteLine("usindex build <uproject|dir> [--engine] [--threads=N] [--index=path]");
            Console.WriteLine("usindex refs <uproject|dir> <file> <line> <col> [--engine] [--index=path]");
            Console.WriteLine("usindex score <oracle.json> [uproject] [--engine] [--index=path] [--details=out.txt] [--scope=project|indexed]");
            Console.WriteLine("usindex oracle <queries.json> <clangd-db-dir> <out.json> [jobs]");
            return 1;
        }

        static string IndexPath(string uproject, bool engine, string overridePath)
        {
            if (overridePath != null) return overridePath;
            var name = Path.GetFileNameWithoutExtension(uproject.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ? uproject : Directory.GetFiles(uproject, "*.uproject").First());
            return Path.Combine(IndexData.DefaultDirectory(name), engine ? "index-engine.bin" : "index-project.bin");
        }

        static int Build(string uproject, bool engine, int threads, string indexOverride)
        {
            var builder = new IndexBuilder();
            var clock = Stopwatch.StartNew();
            var data = builder.Build(new BuildOptions { UProject = uproject, Engine = engine, Threads = threads, Log = m => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] {m}") }, out var stats);
            var sw = Stopwatch.StartNew();
            var path = IndexPath(uproject, engine, indexOverride);
            long size = data.Save(path);
            stats.Save = sw.Elapsed.TotalSeconds;
            var proc = Process.GetCurrentProcess();
            Console.WriteLine();
            Console.WriteLine($"index: {path} ({size / 1048576.0:F1} MB)");
            Console.WriteLine($"files: {stats.Files:N0} parsed ({stats.ResolvedFiles:N0} resolved), {stats.Bytes / 1048576.0:F0} MB, {stats.Lines:N0} lines");
            Console.WriteLine($"symbols: {stats.Symbols:N0}, references: {stats.Refs:N0}, uncertain (unresolved) usages: {stats.Unresolved:N0}");
            long accesses = stats.ResolvedMember + stats.UnresolvedMemberUnknownRecv + stats.UnresolvedMemberNotFound;
            Console.WriteLine($"member accesses (. -> ::): {accesses:N0}, resolved {100.0 * stats.ResolvedMember / Math.Max(1, accesses):F1}%, unknown receiver {100.0 * stats.UnresolvedMemberUnknownRecv / Math.Max(1, accesses):F1}%, member not found {100.0 * stats.UnresolvedMemberNotFound / Math.Max(1, accesses):F1}%");
            double total = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"time: total {total:F1}s = discover {stats.Discover:F1} + phase0 {stats.Phase0:F1} + pass1 {stats.Pass1:F1} + merge {stats.Merge:F1} + pass2 {stats.Pass2:F1} + save {stats.Save:F1}");
            Console.WriteLine($"throughput: {stats.Files / total:F0} files/s, {stats.Bytes / 1048576.0 / total:F1} MB/s; threads {stats.Threads}; CPU {proc.TotalProcessorTime.TotalSeconds:F0}s ({proc.TotalProcessorTime.TotalSeconds / total:F1} busy cores)");
            Console.WriteLine($"memory: peak working set {stats.PeakWorkingSet / 1073741824.0:F2} GB, managed heap {stats.ManagedHeap / 1073741824.0:F2} GB");
            return 0;
        }

        static int Refs(string uproject, string file, int line, int col, bool engine, string indexOverride)
        {
            var sw = Stopwatch.StartNew();
            var data = IndexData.Load(IndexPath(uproject, engine, indexOverride));
            double load = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            int sym = data.SymbolAt(file, line, col);
            if (sym < 0) { Console.WriteLine($"no symbol at {file}:{line}:{col} (load {load:F0} ms)"); return 1; }
            var related = data.RelatedSymbols(sym);
            var refs = data.Usages(sym).OrderBy(r => data.Files[r.File], StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line).ThenBy(r => r.Col).ToList();
            var uncertain = data.UnresolvedOf(data.SymNames[sym]).Where(u => u.Kind != 3).ToList();
            double query = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"symbol: {(SymKind)data.SymKinds[sym]} {data.QualifiedName(sym)}" + (related.Count > 1 ? $" (+ overridden: {string.Join(", ", related.Skip(1).Select(data.QualifiedName))})" : ""));
            Console.WriteLine($"{refs.Count} references (index load {load:F0} ms, query {query:F1} ms):");
            foreach (var r in refs) Console.WriteLine($"  {data.Files[r.File]}:{r.Line}:{r.Col}  [{(RefKind)r.Kind}]");
            if (uncertain.Count > 0)
            {
                Console.WriteLine($"{uncertain.Count} uncertain usages (member '{data.SymNames[sym]}' on a receiver whose type could not be inferred):");
                foreach (var u in uncertain.Take(200)) Console.WriteLine($"  ? {data.Files[u.File]}:{u.Line}:{u.Col}");
            }
            return 0;
        }

        static int Score(string oraclePath, string uproject, bool engine, string indexOverride, string detailsPath, string scope)
        {
            var oracle = JObject.Parse(File.ReadAllText(oraclePath));
            uproject ??= (string)oracle["project"];
            var data = IndexData.Load(IndexPath(uproject, engine, indexOverride));
            var projectDir = CompileDb.Norm(Path.GetFullPath((string)oracle["project"])).TrimEnd('/') + "/";
            // scope: files whose references both sides can see. "project" = files under the project directory;
            // "indexed" = files in our index that clangd also indexed (by name: oracle scopeFiles list)
            HashSet<string> scopeNames = null;
            if (scope == "indexed" && oracle["clangdFiles"] is JArray cf) scopeNames = new HashSet<string>(cf.Select(x => (string)x), StringComparer.OrdinalIgnoreCase);
            bool InScope(string f)
            {
                f = CompileDb.Norm(f);
                if (f.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)) return true;
                return scopeNames != null && scopeNames.Contains(Path.GetFileName(f));
            }
            var details = detailsPath != null ? new StreamWriter(detailsPath) : null;
            int totTp = 0, totO = 0, totU = 0;
            double sumP = 0, sumR = 0; int nq = 0;
            Console.WriteLine($"{"query",-70} {"clangd",6} {"ours",6} {"TP",5} {"prec",7} {"recall",7} {"uncert",6}");
            foreach (JObject r in oracle["results"])
            {
                var id = (string)r["id"];
                var expected = new HashSet<string>(r["refs"].Where(x => InScope((string)x["file"])).Select(x => Key((string)x["file"], (int)x["line"], (int)x["col"])), StringComparer.OrdinalIgnoreCase);
                int sym = data.SymbolAt((string)r["file"], (int)r["line"], (int)r["col"]);
                var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int uncertain = 0;
                if (sym >= 0)
                {
                    foreach (var x in data.Usages(sym))
                        if (InScope(data.Files[x.File])) ours.Add(Key(data.Files[x.File], x.Line, x.Col));
                    uncertain = data.UnresolvedOf(data.SymNames[sym]).Count(u => u.Kind != 3 && InScope(data.Files[u.File]));
                }
                int tp = ours.Count(expected.Contains);
                double prec = ours.Count == 0 ? (expected.Count == 0 ? 1 : 0) : (double)tp / ours.Count;
                double rec = expected.Count == 0 ? 1 : (double)tp / expected.Count;
                totTp += tp; totO += expected.Count; totU += ours.Count;
                sumP += prec; sumR += rec; nq++;
                Console.WriteLine($"{Trunc(id, 70),-70} {expected.Count,6} {ours.Count,6} {tp,5} {prec * 100,6:F1}% {rec * 100,6:F1}% {uncertain,6}" + (sym < 0 ? "  (symbol not found)" : ""));
                if (details != null)
                {
                    details.WriteLine($"=== {id} : {(sym >= 0 ? data.QualifiedName(sym) : "NOT FOUND")}  clangd {expected.Count} ours {ours.Count} tp {tp}");
                    foreach (var m in expected.Where(e => !ours.Contains(e)).OrderBy(x => x)) details.WriteLine("  MISSING " + m + "  " + LineText(m));
                    foreach (var m in ours.Where(e => !expected.Contains(e)).OrderBy(x => x)) details.WriteLine("  EXTRA   " + m + "  " + LineText(m));
                }
            }
            details?.Dispose();
            Console.WriteLine();
            Console.WriteLine($"overall (micro): precision {100.0 * totTp / Math.Max(1, totU):F2}%  recall {100.0 * totTp / Math.Max(1, totO):F2}%  ({totTp} TP, {totU - totTp} FP, {totO - totTp} FN over {nq} queries)");
            Console.WriteLine($"overall (macro): precision {100.0 * sumP / nq:F2}%  recall {100.0 * sumR / nq:F2}%");
            return 0;
        }

        static string Key(string f, int line, int col) => CompileDb.Norm(f) + ":" + line + ":" + col;
        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        static string LineText(string key)
        {
            try
            {
                int c2 = key.LastIndexOf(':'), c1 = key.LastIndexOf(':', c2 - 1);
                var file = key.Substring(0, c1);
                int line = int.Parse(key.Substring(c1 + 1, c2 - c1 - 1));
                return File.ReadLines(file).Skip(line - 1).First().Trim();
            }
            catch (Exception) { return ""; }
        }
    }
}
