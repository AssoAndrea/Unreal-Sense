using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnrealSense.Navigation
{
    /// <summary>A folder to index. Symbols are extracted only when <see cref="SymbolExtensions"/> matches.</summary>
    public sealed class IndexRoot
    {
        public string Directory { get; set; }
        public bool IsEngine { get; set; }
        public string[] FileExtensions { get; set; }
        public string[] SymbolExtensions { get; set; } = Array.Empty<string>();
        /// <summary>Folder names whose files are listed but not scanned for symbols (e.g. ThirdParty).</summary>
        public string[] NoSymbolFolders { get; set; } = Array.Empty<string>();
    }

    public sealed class SymbolResult
    {
        public string Name;
        public string Container;
        public SymbolKind Kind;
        public string File;
        public int Line;
        public bool IsEngine;
        public int Score;
        public int[] Highlights;
        public override string ToString() => $"{(Container != null ? Container + "::" : "")}{Name} [{Kind}] {Path.GetFileName(File)}:{Line + 1}";
    }

    public sealed class FileResult
    {
        public string Path;
        public string RelativePath;
        public bool IsEngine;
        public int Score;
        public int Line = -1;
        public int[] Highlights;
        public override string ToString() => RelativePath;
    }

    /// <summary>
    /// In-memory index for "Go to symbol / file" over the project and the engine. Data is kept in flat arrays
    /// (struct of arrays) so a query scans hundreds of thousands of entries in a few milliseconds; a 64-bit
    /// character mask rejects most candidates before fuzzy scoring. Per-file results are cached on disk and
    /// rebuilt incrementally from timestamps.
    /// </summary>
    public sealed class GoToIndex
    {
        const int CacheVersion = 2;

        sealed class FileData
        {
            public string Path;
            public long Ticks;
            public long Length;
            public bool IsEngine;
            public Declaration[] Declarations = Array.Empty<Declaration>();
        }

        sealed class Snapshot
        {
            // Files
            public string[] FilePaths = Array.Empty<string>();
            public string[] FileNames = Array.Empty<string>();
            public string[] FileRelative = Array.Empty<string>();
            public ulong[] FileMasks = Array.Empty<ulong>();
            public bool[] FileIsEngine = Array.Empty<bool>();
            // Symbols
            public string[] Names = Array.Empty<string>();
            public string[] Containers = Array.Empty<string>();
            public ulong[] Masks = Array.Empty<ulong>();
            public int[] Files = Array.Empty<int>();
            public int[] Lines = Array.Empty<int>();
            public SymbolKind[] Kinds = Array.Empty<SymbolKind>();
        }

        readonly ConcurrentDictionary<string, FileData> files = new ConcurrentDictionary<string, FileData>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> rootByPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        volatile Snapshot snapshot = new Snapshot();

        public int FileCount => snapshot.FilePaths.Length;
        public int SymbolCount => snapshot.Names.Length;
        public bool IsReady { get; private set; }
        public event EventHandler Updated;

        // ------------------------------------------------------------ building

        public void Build(IReadOnlyList<IndexRoot> roots, string cachePath, IProgress<string> progress = null, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var cached = LoadCache(cachePath);
            if (cached.Count > 0 && files.IsEmpty)
            {
                // Serve the cached state immediately; refresh below.
                foreach (var f in cached.Values) files[f.Path] = f;
                Publish(roots);
                progress?.Report($"Loaded {SymbolCount:N0} symbols / {FileCount:N0} files from cache");
            }

            var found = new ConcurrentBag<(string Path, IndexRoot Root)>();
            Parallel.ForEach(roots.Where(r => Directory.Exists(r.Directory)), new ParallelOptions { CancellationToken = cancellationToken }, root =>
            {
                foreach (var path in EnumerateFiles(root.Directory, root.FileExtensions))
                    found.Add((path, root));
            });
            cancellationToken.ThrowIfCancellationRequested();

            var live = new HashSet<string>(found.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var stale in files.Keys.Where(k => !live.Contains(k)).ToList())
                if (files.TryRemove(stale, out _)) removed++;

            int scanned = 0, total = found.Count;
            Parallel.ForEach(found, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, item =>
            {
                FileInfo info;
                try { info = new FileInfo(item.Path); }
                catch (Exception) { return; }
                if (files.TryGetValue(item.Path, out var existing) && existing.Ticks == info.LastWriteTimeUtc.Ticks && existing.Length == info.Length)
                    return;
                files[item.Path] = ScanFile(item.Path, info, item.Root);
                var n = Interlocked.Increment(ref scanned);
                if (progress != null && n % 2000 == 0) progress.Report($"Indexing symbols: {n:N0} files…");
            });

            // Warm start with nothing changed: the cached snapshot is already published and the cache is current.
            bool changed = scanned > 0 || removed > 0 || cached.Count == 0;
            if (changed || SymbolCount + FileCount == 0) Publish(roots);
            IsReady = true;
            if (changed) SaveCache(cachePath);
            progress?.Report($"Go-to index: {SymbolCount:N0} symbols in {FileCount:N0} files ({scanned:N0} rescanned, {sw.Elapsed.TotalSeconds:F1}s)");
        }

        /// <summary>Re-indexes one file (saved document or file system change).</summary>
        public void Refresh(string path, IReadOnlyList<IndexRoot> roots, string text = null, bool publish = true)
        {
            var root = roots.FirstOrDefault(r => path.StartsWith(r.Directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (root == null) return;
            if (!File.Exists(path)) files.TryRemove(path, out _);
            else files[path] = ScanFile(path, new FileInfo(path), root, text);
            if (publish) Publish(roots);
        }

        public void Commit(IReadOnlyList<IndexRoot> roots) => Publish(roots);

        static FileData ScanFile(string path, FileInfo info, IndexRoot root, string text = null)
        {
            var data = new FileData { Path = path, Ticks = info.LastWriteTimeUtc.Ticks, Length = info.Length, IsEngine = root.IsEngine };
            var ext = Path.GetExtension(path);
            if (!root.SymbolExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) return data;
            if (root.NoSymbolFolders.Any(f => path.IndexOf("\\" + f + "\\", StringComparison.OrdinalIgnoreCase) >= 0)) return data;
            if (info.Length > 4 * 1024 * 1024) return data; // generated monsters
            try
            {
                data.Declarations = DeclarationScanner.Scan(text ?? File.ReadAllText(path)).ToArray();
            }
            catch (Exception) { }
            return data;
        }

        static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Saved", "DerivedDataCache", ".git", ".vs", ".idea", "node_modules", "Content",
        };

        static IEnumerable<string> EnumerateFiles(string root, string[] extensions)
        {
            var exts = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                List<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos().ToList(); }
                catch (Exception) { continue; }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (!SkippedFolders.Contains(e.Name) && (e.Attributes & FileAttributes.ReparsePoint) == 0)
                            stack.Push((DirectoryInfo)e);
                    }
                    else if (exts.Contains(e.Extension))
                    {
                        yield return e.FullName;
                    }
                }
            }
        }

        void Publish(IReadOnlyList<IndexRoot> roots)
        {
            var all = files.Values.OrderBy(f => f.IsEngine).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            int symbolCount = all.Sum(f => f.Declarations.Length);
            var s = new Snapshot
            {
                FilePaths = new string[all.Length],
                FileNames = new string[all.Length],
                FileRelative = new string[all.Length],
                FileMasks = new ulong[all.Length],
                FileIsEngine = new bool[all.Length],
                Names = new string[symbolCount],
                Containers = new string[symbolCount],
                Masks = new ulong[symbolCount],
                Files = new int[symbolCount],
                Lines = new int[symbolCount],
                Kinds = new SymbolKind[symbolCount],
            };
            // Names repeat a lot (overrides, constructors, containers): intern them to keep memory down.
            var pool = new Dictionary<string, string>(StringComparer.Ordinal);
            string Intern(string v)
            {
                if (v == null) return null;
                if (pool.TryGetValue(v, out var existing)) return existing;
                pool[v] = v;
                return v;
            }
            int k = 0;
            for (int f = 0; f < all.Length; f++)
            {
                var file = all[f];
                s.FilePaths[f] = file.Path;
                s.FileNames[f] = Path.GetFileName(file.Path);
                s.FileRelative[f] = Relative(file.Path, roots);
                s.FileMasks[f] = FuzzyMatcher.CharMask(s.FileNames[f]);
                s.FileIsEngine[f] = file.IsEngine;
                foreach (var d in file.Declarations)
                {
                    s.Names[k] = Intern(d.Name);
                    s.Containers[k] = Intern(d.Container);
                    s.Masks[k] = FuzzyMatcher.CharMask(d.Name);
                    s.Files[k] = f;
                    s.Lines[k] = d.Line;
                    s.Kinds[k] = d.Kind;
                    k++;
                }
            }
            snapshot = s;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        static string Relative(string path, IReadOnlyList<IndexRoot> roots)
        {
            foreach (var r in roots)
            {
                var parent = Path.GetDirectoryName(r.Directory.TrimEnd('\\'));
                if (parent != null && path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase))
                    return path.Substring(parent.Length + 1);
            }
            return path;
        }

        // ------------------------------------------------------------ queries

        /// <summary>
        /// Fuzzy symbol search. "Container::Name" filters by container; kinds null = all kinds.
        /// Project symbols rank above engine symbols with the same score.
        /// </summary>
        public List<SymbolResult> SearchSymbols(string query, ISet<SymbolKind> kinds, int maxResults, CancellationToken cancellationToken = default, bool includeEngine = true)
        {
            var s = snapshot;
            query = (query ?? string.Empty).Trim().Replace(" ", "");
            if (query.Length == 0) return new List<SymbolResult>();

            string containerQuery = null;
            int sep = query.LastIndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                containerQuery = query.Substring(0, sep);
                query = query.Substring(sep + 2);
            }
            if (query.Length == 0 && containerQuery == null) return new List<SymbolResult>();

            var heaps = new ConcurrentBag<TopK>();
            var ranges = System.Collections.Concurrent.Partitioner.Create(0, s.Names.Length, Math.Max(4096, s.Names.Length / (Environment.ProcessorCount * 4) + 1));
            Parallel.ForEach(ranges, new ParallelOptions { CancellationToken = cancellationToken }, () => new TopK(maxResults), (range, state, top) =>
            {
                var matcher = new FuzzyMatcher(query);
                var containerMatcher = containerQuery != null ? new FuzzyMatcher(containerQuery) : null;
                ulong mask = matcher.Mask;
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if ((s.Masks[i] & mask) != mask) continue;
                    if (kinds != null && !kinds.Contains(s.Kinds[i])) continue;
                    if (!includeEngine && s.FileIsEngine[s.Files[i]]) continue;
                    int score = query.Length == 0 ? 0 : matcher.Score(s.Names[i]);
                    if (score == int.MinValue) continue;
                    if (containerMatcher != null)
                    {
                        var c = s.Containers[i];
                        int cs = c == null ? int.MinValue : containerMatcher.Score(c);
                        if (cs == int.MinValue) continue;
                        score += cs / 2;
                    }
                    if (!s.FileIsEngine[s.Files[i]]) score += 15;
                    score += KindBonus(s.Kinds[i]);
                    if (top.WouldAccept(score)) top.Add(score, i);
                }
                return top;
            }, top => heaps.Add(top));

            var merged = heaps.SelectMany(h => h.Items).OrderByDescending(x => x.Score).ThenBy(x => s.Names[x.Index].Length).Take(maxResults);
            var highlighter = new FuzzyMatcher(query);
            return merged.Select(x => new SymbolResult
            {
                Name = s.Names[x.Index],
                Container = s.Containers[x.Index],
                Kind = s.Kinds[x.Index],
                File = s.FilePaths[s.Files[x.Index]],
                Line = s.Lines[x.Index],
                IsEngine = s.FileIsEngine[s.Files[x.Index]],
                Score = x.Score,
                Highlights = highlighter.GetMatchPositions(s.Names[x.Index]),
            }).ToList();
        }

        static int KindBonus(SymbolKind kind)
        {
            switch (kind)
            {
                case SymbolKind.Class:
                case SymbolKind.Struct:
                case SymbolKind.Enum:
                case SymbolKind.Delegate: return 6;
                case SymbolKind.Function: return 3;
                case SymbolKind.Macro:
                case SymbolKind.Variable: return -3;
                default: return 0;
            }
        }

        /// <summary>Fuzzy file search on file names ("shchar" → ShooterCharacter.h); a '/' or '\' matches paths; ":123" jumps to a line.</summary>
        public List<FileResult> SearchFiles(string query, int maxResults, CancellationToken cancellationToken = default, bool includeEngine = true)
        {
            var s = snapshot;
            query = (query ?? string.Empty).Trim();
            int line = -1;
            int colon = query.LastIndexOf(':');
            if (colon > 1 && int.TryParse(query.Substring(colon + 1), out var l))
            {
                line = Math.Max(0, l - 1);
                query = query.Substring(0, colon);
            }
            if (query.Length == 0) return new List<FileResult>();
            bool byPath = query.IndexOf('/') >= 0 || query.IndexOf('\\') >= 0;
            var pattern = query.Replace('/', '\\');

            var heaps = new ConcurrentBag<TopK>();
            var ranges = System.Collections.Concurrent.Partitioner.Create(0, s.FilePaths.Length, Math.Max(2048, s.FilePaths.Length / (Environment.ProcessorCount * 4) + 1));
            Parallel.ForEach(ranges, new ParallelOptions { CancellationToken = cancellationToken }, () => new TopK(maxResults), (range, state, top) =>
            {
                var matcher = new FuzzyMatcher(pattern);
                ulong mask = byPath ? 0 : matcher.Mask;
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (!byPath && (s.FileMasks[i] & mask) != mask) continue;
                    if (!includeEngine && s.FileIsEngine[i]) continue;
                    int score = matcher.Score(byPath ? s.FileRelative[i] : s.FileNames[i]);
                    if (score == int.MinValue) continue;
                    if (!s.FileIsEngine[i]) score += 20;
                    else if (s.FileRelative[i].IndexOf("\\ThirdParty\\", StringComparison.OrdinalIgnoreCase) >= 0) score -= 15;
                    if (top.WouldAccept(score)) top.Add(score, i);
                }
                return top;
            }, top => heaps.Add(top));

            var highlighter = new FuzzyMatcher(pattern);
            return heaps.SelectMany(h => h.Items).OrderByDescending(x => x.Score).ThenBy(x => s.FileNames[x.Index].Length).Take(maxResults)
                .Select(x => new FileResult
                {
                    Path = s.FilePaths[x.Index],
                    RelativePath = s.FileRelative[x.Index],
                    IsEngine = s.FileIsEngine[x.Index],
                    Score = x.Score,
                    Line = line,
                    Highlights = byPath ? Array.Empty<int>() : highlighter.GetMatchPositions(s.FileNames[x.Index]),
                }).ToList();
        }

        /// <summary>Bounded min-heap of (score, index).</summary>
        sealed class TopK
        {
            readonly int capacity;
            readonly List<(int Score, int Index)> heap;

            public TopK(int capacity)
            {
                this.capacity = Math.Max(1, capacity);
                heap = new List<(int, int)>(this.capacity + 1);
            }

            public IEnumerable<(int Score, int Index)> Items => heap;

            public bool WouldAccept(int score) => heap.Count < capacity || score > heap[0].Score;

            public void Add(int score, int index)
            {
                if (heap.Count < capacity)
                {
                    heap.Add((score, index));
                    SiftUp(heap.Count - 1);
                }
                else
                {
                    heap[0] = (score, index);
                    SiftDown(0);
                }
            }

            void SiftUp(int i)
            {
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (heap[p].Score <= heap[i].Score) break;
                    (heap[p], heap[i]) = (heap[i], heap[p]);
                    i = p;
                }
            }

            void SiftDown(int i)
            {
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < heap.Count && heap[l].Score < heap[m].Score) m = l;
                    if (r < heap.Count && heap[r].Score < heap[m].Score) m = r;
                    if (m == i) break;
                    (heap[m], heap[i]) = (heap[i], heap[m]);
                    i = m;
                }
            }
        }

        // ------------------------------------------------------------ cache

        static Dictionary<string, FileData> LoadCache(string cachePath)
        {
            var result = new Dictionary<string, FileData>(StringComparer.OrdinalIgnoreCase);
            if (cachePath == null || !File.Exists(cachePath)) return result;
            try
            {
                using (var stream = new BufferedStream(File.OpenRead(cachePath), 1 << 20))
                using (var r = new BinaryReader(stream))
                {
                    if (r.ReadInt32() != CacheVersion) return result;
                    int stringCount = r.ReadInt32();
                    var strings = new string[stringCount];
                    for (int i = 0; i < stringCount; i++) strings[i] = r.ReadString();
                    int fileCount = r.ReadInt32();
                    for (int f = 0; f < fileCount; f++)
                    {
                        var data = new FileData { Path = r.ReadString(), Ticks = r.ReadInt64(), Length = r.ReadInt64(), IsEngine = r.ReadBoolean() };
                        int n = r.ReadInt32();
                        data.Declarations = new Declaration[n];
                        for (int d = 0; d < n; d++)
                        {
                            int name = r.ReadInt32(), container = r.ReadInt32();
                            data.Declarations[d] = new Declaration
                            {
                                Name = strings[name],
                                Container = container < 0 ? null : strings[container],
                                Kind = (SymbolKind)r.ReadByte(),
                                Line = r.ReadInt32(),
                            };
                        }
                        result[data.Path] = data;
                    }
                }
            }
            catch (Exception)
            {
                result.Clear();
            }
            return result;
        }

        void SaveCache(string cachePath)
        {
            if (cachePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                var all = files.Values.ToList();
                var ids = new Dictionary<string, int>(StringComparer.Ordinal);
                var strings = new List<string>();
                int Id(string v)
                {
                    if (v == null) return -1;
                    if (!ids.TryGetValue(v, out var id)) { id = strings.Count; ids[v] = id; strings.Add(v); }
                    return id;
                }
                foreach (var f in all)
                    foreach (var d in f.Declarations) { Id(d.Name); Id(d.Container); }

                var temp = cachePath + ".tmp";
                using (var stream = new BufferedStream(File.Create(temp), 1 << 20))
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(CacheVersion);
                    w.Write(strings.Count);
                    foreach (var str in strings) w.Write(str);
                    w.Write(all.Count);
                    foreach (var f in all)
                    {
                        w.Write(f.Path); w.Write(f.Ticks); w.Write(f.Length); w.Write(f.IsEngine);
                        w.Write(f.Declarations.Length);
                        foreach (var d in f.Declarations)
                        {
                            w.Write(Id(d.Name)); w.Write(Id(d.Container)); w.Write((byte)d.Kind); w.Write(d.Line);
                        }
                    }
                }
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(temp, cachePath);
            }
            catch (Exception) { }
        }
    }
}
