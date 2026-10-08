using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Clang
{
    /// <summary>
    /// Per-file compile commands (compile_commands.files.json). clangd's database only lists unity translation
    /// units, so a file opened in the editor gets its exact command pushed to clangd from here; a header gets the
    /// command of the nearest source file of its module, with the header as the main file.
    /// </summary>
    public sealed class CompileCommandIndex
    {
        public sealed class Command
        {
            public string Directory { get; set; }
            public List<string> Arguments { get; set; }
        }

        readonly Dictionary<string, (string Directory, List<string> Args, int SourceIndex)> byFile;
        readonly Dictionary<string, string> sourceByDirectory;

        CompileCommandIndex(Dictionary<string, (string, List<string>, int)> byFile, Dictionary<string, string> sourceByDirectory)
        {
            this.byFile = byFile;
            this.sourceByDirectory = sourceByDirectory;
        }

        public int Count => byFile.Count;

        public static CompileCommandIndex Load(string filesDatabase)
        {
            var byFile = new Dictionary<string, (string, List<string>, int)>(StringComparer.OrdinalIgnoreCase);
            var byDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var moduleRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(filesDatabase))
            {
                foreach (var entry in JArray.Parse(File.ReadAllText(filesDatabase)).OfType<JObject>())
                {
                    var directory = (string)entry["directory"];
                    var file = Key((string)entry["file"], directory);
                    var args = ((JArray)entry["arguments"]).Select(x => (string)x).ToList();
                    // Engine sources are written relative to Engine/Source: resolve against the entry's directory.
                    int source = file == null ? -1 : args.FindIndex(1, x => !x.StartsWith("/") && !x.StartsWith("-") && !x.StartsWith("@") && Key(x, directory) == file);
                    if (source < 0) continue;
                    byFile[file] = (directory, args, source);
                    // Register the folders up to the module root (where *.Build.cs is), so a header in Public/ finds a
                    // source of its module in Private/. Never higher: borrowing flags across modules, or for files
                    // outside the code (such as the unity files), would keep clangd from loading its own database.
                    var root = ModuleRoot(file, moduleRoots);
                    for (var dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    {
                        if (!byDir.ContainsKey(dir)) byDir[dir] = file;
                        if (root == null || string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
            }
            return new CompileCommandIndex(byFile, byDir);
        }

        /// <summary>The command for <paramref name="path"/>, or null when nothing in its module is known.</summary>
        public Command Find(string path)
        {
            var key = Key(path);
            if (key == null) return null;
            if (byFile.TryGetValue(key, out var exact))
                return new Command { Directory = exact.Directory, Arguments = exact.Args };

            // Header (or a source missing from the database): borrow the flags of the closest source file.
            for (var dir = Path.GetDirectoryName(key); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (!sourceByDirectory.TryGetValue(dir, out var source)) continue;
                var donor = byFile[source];
                var args = donor.Args.ToList();
                args[donor.SourceIndex] = path.Replace('\\', '/');
                return new Command { Directory = donor.Directory, Arguments = args };
            }
            return null;
        }

        /// <summary>Nearest ancestor folder (at most 6 levels up) containing a *.Build.cs, or null.</summary>
        static string ModuleRoot(string file, Dictionary<string, string> cache)
        {
            var visited = new List<string>();
            string result = null;
            var dir = Path.GetDirectoryName(file);
            for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(dir); depth++, dir = Path.GetDirectoryName(dir))
            {
                if (cache.TryGetValue(dir, out var known)) { result = known; break; }
                visited.Add(dir);
                bool hasRules;
                try { hasRules = Directory.EnumerateFiles(dir, "*.Build.cs").Any(); }
                catch (Exception) { hasRules = false; }
                if (hasRules) { result = dir; break; }
            }
            foreach (var v in visited) cache[v] = result;
            return result;
        }

        static string Key(string path, string directory = null)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                path = path.Trim('"').Replace('/', '\\');
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(directory)) path = Path.Combine(directory.Replace('/', '\\'), path);
                return Path.GetFullPath(path).ToLowerInvariant();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
