using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace UnrealSense.Indexer
{
    /// <summary>
    /// What the indexer needs from UnrealSense's per-file compile database (compile_commands.files.json):
    /// include directories (union, in order), /D defines and the forced-include Definitions.h files.
    /// Only read, never written.
    /// </summary>
    public sealed class CompileDb
    {
        public readonly List<string> IncludeDirs = new List<string>();
        public readonly List<(string Name, string Value)> Defines = new List<(string, string)>();
        public readonly List<string> ForcedIncludes = new List<string>();
        public readonly List<string> Files = new List<string>();
        public string EngineSourceDir;

        public static string FindFor(string projectName)
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "Cache");
            if (!Directory.Exists(cache)) return null;
            return Directory.GetDirectories(cache, projectName + "-*")
                // "compiledb" since 0.3.19; "clangd" = versions with the clangd index (and the oracle databases)
                .SelectMany(d => new[] { Path.Combine(d, "compiledb", "compile_commands.files.json"), Path.Combine(d, "clangd", "compile_commands.files.json") })
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        public static CompileDb Load(string path)
        {
            var db = new CompileDb();
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDefines = new HashSet<string>(StringComparer.Ordinal);
            var seenForced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rspCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var dir = e.GetProperty("directory").GetString();
                db.EngineSourceDir ??= dir;
                var file = e.GetProperty("file").GetString();
                db.Files.Add(Norm(Path.GetFullPath(Path.Combine(dir, file))));
                var args = new List<string>();
                if (e.TryGetProperty("arguments", out var a)) foreach (var x in a.EnumerateArray()) args.Add(x.GetString());
                else if (e.TryGetProperty("command", out var c)) args.AddRange(Split(c.GetString()));
                Collect(db, args, dir, rspCache, seenDirs, seenDefines, seenForced, 0);
            }
            return db;
        }

        static void Collect(CompileDb db, List<string> args, string dir, Dictionary<string, List<string>> rspCache, HashSet<string> seenDirs, HashSet<string> seenDefines, HashSet<string> seenForced, int depth)
        {
            for (int i = 0; i < args.Count; i++)
            {
                var a = args[i];
                string Next() => i + 1 < args.Count ? args[++i] : "";
                if (a.StartsWith("@"))
                {
                    if (depth > 4) continue;
                    var p = a.Substring(1).Trim('"');
                    if (!Path.IsPathRooted(p)) p = Path.Combine(dir, p);
                    if (!rspCache.TryGetValue(p, out var toks))
                    {
                        try { toks = Split(File.ReadAllText(p)); } catch (Exception) { toks = new List<string>(); }
                        rspCache[p] = toks;
                        Collect(db, toks, dir, rspCache, seenDirs, seenDefines, seenForced, depth + 1);
                    }
                    continue;
                }
                string inc = null;
                if (a == "/I" || a == "-I" || a == "/imsvc" || a == "-imsvc" || a == "/external:I") inc = Next();
                else if (a.StartsWith("/external:I")) inc = a.Substring(11);
                else if (a.StartsWith("/imsvc") || a.StartsWith("-imsvc")) inc = a.Substring(6);
                else if (a.StartsWith("/I") || a.StartsWith("-I")) inc = a.Substring(2);
                if (inc != null)
                {
                    inc = inc.Trim('"');
                    if (inc.Length == 0) continue;
                    var full = Norm(Path.GetFullPath(Path.Combine(dir, inc)));
                    if (seenDirs.Add(full)) db.IncludeDirs.Add(full);
                    continue;
                }
                string def = null;
                if (a == "/D" || a == "-D") def = Next();
                else if (a.StartsWith("/D") || a.StartsWith("-D")) def = a.Substring(2);
                if (def != null)
                {
                    def = def.Trim('"');
                    if (!seenDefines.Add(def)) continue;
                    int eq = def.IndexOf('=');
                    db.Defines.Add(eq < 0 ? (def, "1") : (def.Substring(0, eq), def.Substring(eq + 1)));
                    continue;
                }
                string fi = null;
                if (a == "/FI" || a == "-include") fi = Next();
                else if (a.StartsWith("/FI")) fi = a.Substring(3);
                if (fi != null)
                {
                    fi = fi.Trim('"');
                    var full = Norm(Path.GetFullPath(Path.Combine(dir, fi)));
                    if (seenForced.Add(full)) db.ForcedIncludes.Add(full);
                }
            }
        }

        public static string Norm(string p) => p.Replace('\\', '/');

        public static List<string> Split(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool q = false, any = false;
            foreach (var ch in s)
            {
                if (ch == '"') { q = !q; any = true; continue; }
                if (!q && char.IsWhiteSpace(ch))
                {
                    if (any || sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); any = false; }
                    continue;
                }
                sb.Append(ch);
            }
            if (any || sb.Length > 0) list.Add(sb.ToString());
            return list;
        }
    }
}
