using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnrealSense.Clang
{
    /// <summary>
    /// Replaces include directories with clang header maps (".hmap": a hash table from include spelling to file).
    /// For every #include clang tries each include directory in turn until the file exists there; Unreal units
    /// have ~500 directories, so a unit costs hundreds of thousands of failed file opens, each of which goes
    /// through every file system filter installed on the machine (measured: 18 µs each on a plain PC, Lyra's
    /// project units spend 20% of their CPU there; much more on machines with heavier filters). A header map
    /// answers the same question from memory. Each directory gets its own map, in place, so the search order
    /// is unchanged; the directories themselves stay at the end of the search (as /imsvc) for headers created
    /// after the maps were written, and <see cref="Refresh"/> brings the maps up to date at each index start.
    /// </summary>
    public sealed class HeaderMaps
    {
        public const string FolderName = "hmap";
        const string ManifestName = "directories.txt";
        /// <summary>Directories with more files than this stay real directories: clang's key hash (a sum of
        /// characters) cannot spread larger tables, and such directories (Engine/Source) cost one probe each.</summary>
        public const int MaxEntries = 8192;

        static readonly HashSet<string> NotIncludable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".uasset", ".umap", ".uplugin", ".uproject", ".cs", ".csproj", ".ini", ".txt", ".md", ".json", ".xml", ".png", ".jpg",
            ".bmp", ".tga", ".ico", ".dll", ".lib", ".pdb", ".exe", ".obj", ".o", ".a", ".so", ".dylib", ".zip", ".py", ".bat", ".sh",
            ".ush", ".usf", ".rsp", ".response", ".modulemanifest", ".target", ".version", ".natvis", ".pch", ".ipch", ".dep", ".ilk",
            ".exp", ".log", ".html", ".css", ".js", ".ttf", ".otf", ".wav", ".ogg", ".mp4", ".bin", ".dat", ".hmap",
        };

        readonly string folder;
        readonly Dictionary<string, string> maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HeaderMaps(string folder)
        {
            this.folder = folder;
            Directory.CreateDirectory(folder);
        }

        public int Count => maps.Values.Count(m => m != null);

        /// <summary>
        /// Rewrites a unit's flags: each /I directory becomes its header map (or stays when too large), and the
        /// replaced directories are appended as /imsvc, after the toolchain's own.
        /// </summary>
        public List<string> Rewrite(IEnumerable<string> flags, string workingDirectory)
        {
            var result = new List<string>();
            var fallback = new List<string>();
            foreach (var flag in flags)
            {
                if (!flag.StartsWith("/I", StringComparison.Ordinal) || flag.Length <= 2) { result.Add(flag); continue; }
                var directory = CompileDatabase.Normalize(Path.GetFullPath(Path.Combine(workingDirectory ?? "", flag.Substring(2).Trim('"')))).TrimEnd('/');
                var map = MapFor(directory);
                if (map == null) { result.Add(flag); continue; }
                if (map.Length > 0) result.Add("/I" + map);
                fallback.Add("/imsvc" + directory);
            }
            result.AddRange(fallback);
            return result;
        }

        /// <summary>The map of <paramref name="directory"/>: its path, "" when the directory does not exist (clang skips
        /// it anyway), null when it is too large.</summary>
        string MapFor(string directory)
        {
            if (maps.TryGetValue(directory, out var map)) return map;
            if (!Directory.Exists(directory)) map = "";
            else
            {
                map = CompileDatabase.Normalize(Path.Combine(folder, $"{Fnv(directory.ToLowerInvariant()):x8}-{Path.GetFileName(directory)}.hmap"));
                if (!Write(directory, map)) map = null;
            }
            maps[directory] = map;
            return map;
        }

        public void SaveManifest() =>
            File.WriteAllLines(Path.Combine(folder, ManifestName), maps.Where(m => !string.IsNullOrEmpty(m.Value)).Select(m => m.Key + "\t" + m.Value));

        /// <summary>Rewrites the maps whose directories changed (headers added, removed or renamed). Returns how many changed.</summary>
        public static int Refresh(string folder)
        {
            var manifest = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifest)) return 0;
            int changed = 0;
            foreach (var line in File.ReadAllLines(manifest))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && Write(parts[0], parts[1], onlyIfChanged: true)) changed++;
            }
            return changed;
        }

        /// <summary>Writes the header map of <paramref name="directory"/> to <paramref name="mapFile"/>. False when the directory is
        /// too large (or, with <paramref name="onlyIfChanged"/>, when the file already has this content).</summary>
        static bool Write(string directory, string mapFile, bool onlyIfChanged = false)
        {
            var files = ListFiles(directory);
            if (files == null) return false;
            var bytes = Build(directory, files);
            if (onlyIfChanged && File.Exists(mapFile) && File.ReadAllBytes(mapFile).SequenceEqual(bytes)) return false;
            File.WriteAllBytes(mapFile, bytes);
            return true;
        }

        /// <summary>Paths relative to <paramref name="directory"/> of the files an #include could name; null when too many.</summary>
        static List<string> ListFiles(string directory)
        {
            var files = new List<string>();
            var stack = new Stack<string>();
            stack.Push(directory);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (NotIncludable.Contains(Path.GetExtension(file))) continue;
                        var relative = file.Substring(directory.Length).TrimStart('\\', '/').Replace('\\', '/');
                        if (relative.All(c => c < 128)) files.Add(relative);
                        if (files.Count > MaxEntries) return null;
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if (!Path.GetFileName(sub).StartsWith(".", StringComparison.Ordinal)) stack.Push(sub);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>
        /// clang's header map format (clang/Lex/HeaderMapTypes.h): a header (magic "hmap", version 1, string table
        /// offset, entry and bucket counts, longest value), a power-of-two bucket array of (key, prefix, suffix)
        /// string offsets probed linearly from HashHMapKey(key) (0 = empty bucket), then the string table, which
        /// starts with a NUL so that offset 0 is never a string. Keys compare case-insensitively.
        /// </summary>
        public static byte[] Build(string directory, IReadOnlyList<string> relativeFiles)
        {
            int buckets = 2;
            while (buckets < relativeFiles.Count * 2) buckets *= 2;
            var strings = new MemoryStream();
            strings.WriteByte(0);
            var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint String(string s)
            {
                if (offsets.TryGetValue(s, out var offset)) return offset;
                offset = (uint)strings.Length;
                var bytes = Encoding.ASCII.GetBytes(s);
                strings.Write(bytes, 0, bytes.Length);
                strings.WriteByte(0);
                return offsets[s] = offset;
            }
            var prefix = CompileDatabase.Normalize(directory).TrimEnd('/') + "/";
            uint prefixOffset = String(prefix);
            var table = new (uint Key, uint Prefix, uint Suffix)[buckets];
            var used = new string[buckets];
            int entries = 0, longest = 0;
            foreach (var key in relativeFiles)
            {
                int bucket = (int)(Hash(key) & (uint)(buckets - 1));
                bool duplicate = false;
                while (used[bucket] != null)
                {
                    if (string.Equals(used[bucket], key, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                    bucket = (bucket + 1) & (buckets - 1);
                }
                if (duplicate) continue;
                used[bucket] = key;
                uint keyOffset = String(key);
                table[bucket] = (keyOffset, prefixOffset, keyOffset);
                entries++;
                longest = Math.Max(longest, prefix.Length + key.Length + 1);
            }

            var output = new MemoryStream();
            using (var writer = new BinaryWriter(output))
            {
                writer.Write((uint)0x686D6170);                // 'hmap'
                writer.Write((ushort)1);                       // version
                writer.Write((ushort)0);                       // reserved
                writer.Write((uint)(24 + 12 * buckets));       // strings offset
                writer.Write((uint)entries);
                writer.Write((uint)buckets);
                writer.Write((uint)longest);
                foreach (var b in table) { writer.Write(b.Key); writer.Write(b.Prefix); writer.Write(b.Suffix); }
                writer.Write(strings.ToArray());
                writer.Flush();
                return output.ToArray();
            }
        }

        /// <summary>clang's HashHMapKey: the sum of the lowercased characters times 13.</summary>
        public static uint Hash(string key)
        {
            uint result = 0;
            foreach (var c in key) result += (uint)(c >= 'A' && c <= 'Z' ? c + 32 : c) * 13;
            return result;
        }

        static uint Fnv(string s)
        {
            uint hash = 2166136261;
            foreach (var c in s) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
