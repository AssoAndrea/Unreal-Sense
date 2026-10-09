using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnrealSense.Indexer
{
    /// <summary>
    /// The persisted index: files, symbols (with parent, kind, location, overrides) and references grouped by symbol.
    /// Struct-of-arrays, written with BinaryWriter (+ Deflate).
    /// </summary>
    public sealed class IndexData
    {
        const int Version = 4;
        public string ProjectDir, EngineDir;
        public bool Engine;
        public string BuildId;                // matches the build cache written by the same build
        public string[] Files;
        // symbols
        public string[] SymNames;
        public byte[] SymKinds;
        public uint[] SymFlags;
        public int[] SymParent, SymFile, SymLine, SymCol;
        public int[][] SymOverrides;          // methods this one overrides
        public string[] SymSigs;              // functions: parameter signature (SymbolTable.SigKey), else null
        // references, sorted by symbol
        public int[] RefStart;                // per symbol: index into Ref* arrays (length = symbols + 1)
        public int[] RefFile, RefLine, RefCol;
        public byte[] RefKind;
        // unresolved (uncertain) usages by name
        public string[] UnresolvedNames;
        public int[] UnName, UnFile, UnLine, UnCol;
        public byte[] UnKind;

        public int SymbolCount => SymNames.Length;
        public int RefCount => RefFile.Length;

        public static IndexData Create(List<SourceFile> files, SymbolTable table, Dictionary<Symbol, List<Symbol>> overrides,
            List<RefRec>[] refsPerFile, List<UnresolvedRec>[] unresolvedPerFile, string projectDir, string engineDir, bool engine)
        {
            var d = CreateSymbols(files, table, overrides, projectDir, engineDir, engine);
            d.SetReferences(refsPerFile, unresolvedPerFile);
            return d;
        }

        /// <summary>Files and symbols only; <see cref="SetReferences"/> completes it (the incremental build needs the symbols' keys first).</summary>
        public static IndexData CreateSymbols(List<SourceFile> files, SymbolTable table, Dictionary<Symbol, List<Symbol>> overrides, string projectDir, string engineDir, bool engine)
        {
            var d = new IndexData { ProjectDir = projectDir, EngineDir = engineDir, Engine = engine };
            d.Files = files.Select(f => f.Path).ToArray();
            int n = table.All.Count;
            d.SymNames = new string[n]; d.SymKinds = new byte[n]; d.SymFlags = new uint[n];
            d.SymParent = new int[n]; d.SymFile = new int[n]; d.SymLine = new int[n]; d.SymCol = new int[n];
            d.SymOverrides = new int[n][];
            d.SymSigs = new string[n];
            for (int i = 0; i < n; i++)
            {
                var s = table.All[i];
                d.SymNames[i] = s.Name == 0 ? "" : Names.Get(s.Name);
                d.SymKinds[i] = (byte)s.Kind;
                d.SymFlags[i] = (uint)s.Flags;
                d.SymParent[i] = s.Parent?.Id ?? -1;
                d.SymFile[i] = s.File; d.SymLine[i] = s.Line; d.SymCol[i] = s.Col;
                if (overrides.TryGetValue(s, out var ov)) d.SymOverrides[i] = ov.Select(x => x.Id).ToArray();
                d.SymSigs[i] = s.Kind == SymKind.Function ? s.SigKey ?? "" : null;
            }
            return d;
        }

        public void SetReferences(List<RefRec>[] refsPerFile, List<UnresolvedRec>[] unresolvedPerFile) =>
            SetReferences(refsPerFile, unresolvedPerFile, null, null, null, out _, out _);

        /// <summary>
        /// References and unresolved usages: the fresh ones (per new file id) plus, for an incremental build, the previous index's
        /// ones in the files it keeps (<paramref name="fileMap"/>: old file id → new id or -1), their symbols mapped through
        /// <paramref name="oldToNew"/> (-1 = the symbol no longer exists).
        /// </summary>
        public void SetReferences(List<RefRec>[] refsPerFile, List<UnresolvedRec>[] unresolvedPerFile, IndexData prev, int[] fileMap, int[] oldToNew, out int kept, out int lost)
        {
            var d = this;
            int n = SymNames.Length;
            kept = 0; lost = 0;
            // refs: counting sort by symbol
            var count = new int[n + 1];
            for (int f = 0; f < refsPerFile.Length; f++)
                if (refsPerFile[f] != null) foreach (var r in refsPerFile[f]) count[r.Sym + 1]++;
            if (prev != null)
                for (int s = 0; s < oldToNew.Length; s++)
                {
                    int ns = oldToNew[s];
                    for (int k = prev.RefStart[s]; k < prev.RefStart[s + 1]; k++)
                        if (fileMap[prev.RefFile[k]] >= 0) { if (ns >= 0) { count[ns + 1]++; kept++; } else lost++; }
                }
            for (int i = 0; i < n; i++) count[i + 1] += count[i];
            d.RefStart = (int[])count.Clone();
            int total = count[n];
            d.RefFile = new int[total]; d.RefLine = new int[total]; d.RefCol = new int[total]; d.RefKind = new byte[total];
            var pos = (int[])count.Clone();
            for (int f = 0; f < refsPerFile.Length; f++)
            {
                if (refsPerFile[f] == null) continue;
                foreach (var r in refsPerFile[f])
                {
                    int k = pos[r.Sym]++;
                    d.RefFile[k] = f; d.RefLine[k] = r.Line; d.RefCol[k] = r.Col; d.RefKind[k] = (byte)r.Kind;
                }
            }
            if (prev != null)
                for (int s = 0; s < oldToNew.Length; s++)
                {
                    int ns = oldToNew[s];
                    if (ns < 0) continue;
                    for (int k = prev.RefStart[s]; k < prev.RefStart[s + 1]; k++)
                    {
                        int f = fileMap[prev.RefFile[k]];
                        if (f < 0) continue;
                        int q = pos[ns]++;
                        d.RefFile[q] = f; d.RefLine[q] = prev.RefLine[k]; d.RefCol[q] = prev.RefCol[k]; d.RefKind[q] = prev.RefKind[k];
                    }
                }
            // unresolved
            var nameIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new List<string>();
            int NameId(string name)
            {
                if (!nameIds.TryGetValue(name, out int id)) { id = names.Count; names.Add(name); nameIds[name] = id; }
                return id;
            }
            var un = new List<(int, int, int, int, byte)>();
            for (int f = 0; f < unresolvedPerFile.Length; f++)
            {
                if (unresolvedPerFile[f] == null) continue;
                foreach (var u in unresolvedPerFile[f]) un.Add((NameId(Names.Get(u.Name)), f, u.Line, u.Col, u.Kind));
            }
            if (prev != null)
                for (int k = 0; k < prev.UnName.Length; k++)
                {
                    int f = fileMap[prev.UnFile[k]];
                    if (f >= 0) un.Add((NameId(prev.UnresolvedNames[prev.UnName[k]]), f, prev.UnLine[k], prev.UnCol[k], prev.UnKind[k]));
                }
            un.Sort();
            d.UnresolvedNames = names.ToArray();
            d.UnName = un.Select(x => x.Item1).ToArray(); d.UnFile = un.Select(x => x.Item2).ToArray();
            d.UnLine = un.Select(x => x.Item3).ToArray(); d.UnCol = un.Select(x => x.Item4).ToArray(); d.UnKind = un.Select(x => x.Item5).ToArray();
        }

        /// <summary>The same symbols in the same order (the merge of unchanged declarations): ids can be reused as they are.</summary>
        public bool SameSymbols(IndexData other)
        {
            if (other.SymNames.Length != SymNames.Length) return false;
            for (int i = 0; i < SymNames.Length; i++)
                if (SymKinds[i] != other.SymKinds[i] || SymParent[i] != other.SymParent[i] || SymNames[i] != other.SymNames[i] || SymSigs[i] != other.SymSigs[i]) return false;
            return true;
        }

        // ------------------------------------------------------------------ persistence

        public static string DefaultDirectory(string projectName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "OwnIndex", projectName);

        public long Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = File.Create(path))
            using (var z = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            using (var w = new BinaryWriter(new BufferedStream(z, 1 << 20), Encoding.UTF8))
            {
                w.Write(Version); w.Write(ProjectDir); w.Write(EngineDir); w.Write(Engine); w.Write(BuildId ?? "");
                WriteStrings(w, Files);
                WriteStrings(w, SymNames);
                w.Write(SymKinds.Length); w.Write(SymKinds);
                WriteInts(w, Array.ConvertAll(SymFlags, x => (int)x)); WriteInts(w, SymParent); WriteInts(w, SymFile); WriteInts(w, SymLine); WriteInts(w, SymCol);
                int ov = SymOverrides.Count(x => x != null);
                w.Write(ov);
                for (int i = 0; i < SymOverrides.Length; i++) if (SymOverrides[i] != null) { w.Write(i); WriteInts(w, SymOverrides[i]); }
                for (int i = 0; i < SymSigs.Length; i++) { w.Write(SymSigs[i] != null); if (SymSigs[i] != null) w.Write(SymSigs[i]); }
                WriteInts(w, RefStart); WriteInts(w, RefFile); WriteInts(w, RefLine); WriteInts(w, RefCol); w.Write(RefKind.Length); w.Write(RefKind);
                WriteStrings(w, UnresolvedNames);
                WriteInts(w, UnName); WriteInts(w, UnFile); WriteInts(w, UnLine); WriteInts(w, UnCol); w.Write(UnKind.Length); w.Write(UnKind);
            }
            return new FileInfo(path).Length;
        }

        public static IndexData Load(string path)
        {
            using var fs = File.OpenRead(path);
            using var z = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(z, 1 << 20), Encoding.UTF8);
            if (r.ReadInt32() != Version) throw new InvalidDataException("index version mismatch: rebuild");
            var d = new IndexData { ProjectDir = r.ReadString(), EngineDir = r.ReadString(), Engine = r.ReadBoolean(), BuildId = r.ReadString() };
            d.Files = ReadStrings(r);
            d.SymNames = ReadStrings(r);
            d.SymKinds = r.ReadBytes(r.ReadInt32());
            d.SymFlags = Array.ConvertAll(ReadInts(r), x => (uint)x); d.SymParent = ReadInts(r); d.SymFile = ReadInts(r); d.SymLine = ReadInts(r); d.SymCol = ReadInts(r);
            d.SymOverrides = new int[d.SymNames.Length][];
            int ov = r.ReadInt32();
            for (int i = 0; i < ov; i++) { int s = r.ReadInt32(); d.SymOverrides[s] = ReadInts(r); }
            d.SymSigs = new string[d.SymNames.Length];
            for (int i = 0; i < d.SymSigs.Length; i++) if (r.ReadBoolean()) d.SymSigs[i] = r.ReadString();
            d.RefStart = ReadInts(r); d.RefFile = ReadInts(r); d.RefLine = ReadInts(r); d.RefCol = ReadInts(r); d.RefKind = r.ReadBytes(r.ReadInt32());
            d.UnresolvedNames = ReadStrings(r);
            d.UnName = ReadInts(r); d.UnFile = ReadInts(r); d.UnLine = ReadInts(r); d.UnCol = ReadInts(r); d.UnKind = r.ReadBytes(r.ReadInt32());
            return d;
        }

        static void WriteStrings(BinaryWriter w, string[] s) { w.Write(s.Length); foreach (var x in s) w.Write(x ?? ""); }
        static string[] ReadStrings(BinaryReader r) { var a = new string[r.ReadInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadString(); return a; }
        static void WriteInts(BinaryWriter w, int[] a)
        {
            w.Write(a.Length);
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());
            w.Write(bytes);
        }
        static int[] ReadInts(BinaryReader r)
        {
            var a = new int[r.ReadInt32()];
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());
            int read = 0;
            while (read < bytes.Length) { int n = r.Read(bytes.Slice(read)); if (n <= 0) break; read += n; }
            return a;
        }

        /// <summary>
        /// A key per symbol that survives a rebuild (ids do not): parent key / kind : name (signature). Anonymous symbols add their
        /// file (not the line, which moves with edits); remaining clashes are numbered in id order.
        /// </summary>
        public string[] StableKeys()
        {
            int n = SymNames.Length;
            var keys = new string[n];
            var seen = new Dictionary<string, int>();
            string Key(int i)
            {
                if (keys[i] != null) return keys[i];
                var sb = new StringBuilder();
                int p = SymParent[i];
                if (p >= 0 && p != i) sb.Append(Key(p)).Append('/');
                sb.Append(SymKinds[i]).Append(':').Append(SymNames[i]);
                if (SymSigs[i] != null) sb.Append('(').Append(SymSigs[i]).Append(')');
                if (SymNames[i].Length == 0 && SymFile[i] >= 0) sb.Append('@').Append(Files[SymFile[i]]);
                var k = sb.ToString();
                if (seen.TryGetValue(k, out int c)) { seen[k] = c + 1; k += "#" + c; } else seen[k] = 1;
                return keys[i] = k;
            }
            for (int i = 0; i < n; i++) Key(i);
            return keys;
        }

        // ------------------------------------------------------------------ queries

        Dictionary<string, int> fileIndex;

        public int FileId(string path)
        {
            fileIndex ??= Enumerable.Range(0, Files.Length).ToDictionary(i => Files[i], i => i, StringComparer.OrdinalIgnoreCase);
            return fileIndex.TryGetValue(CompileDb.Norm(Path.GetFullPath(path)), out int id) ? id : -1;
        }

        /// <summary>The symbol referenced/declared at a position (1-based line/column).</summary>
        public int SymbolAt(string path, int line, int col)
        {
            int f = FileId(path);
            if (f < 0) return -1;
            int best = -1, bestKind = 99;
            for (int s = 0; s < SymNames.Length; s++)
            {
                int a = RefStart[s], b = RefStart[s + 1];
                for (int k = a; k < b; k++)
                {
                    if (RefFile[k] != f || RefLine[k] != line) continue;
                    int c = RefCol[k];
                    if (col < c || col >= c + Math.Max(1, SymNames[s].Length)) continue;
                    // prefer non-class symbols for constructor names (class + constructor share the token)
                    int pref = RefKind[k] == (byte)Indexer.RefKind.Type ? 2 : 1;
                    if (pref < bestKind) { best = s; bestKind = pref; }
                }
            }
            return best;
        }

        /// <summary>clangd semantics: the symbol plus the methods it overrides (transitively).</summary>
        public List<int> RelatedSymbols(int sym)
        {
            var list = new List<int> { sym };
            for (int i = 0; i < list.Count; i++)
            {
                var ov = SymOverrides[list[i]];
                if (ov != null) foreach (var o in ov) if (!list.Contains(o)) list.Add(o);
            }
            return list;
        }

        int[][] overriddenBy;

        /// <summary>
        /// What Find Usages reports for a symbol, as clangd does: references of the symbol and of the methods it overrides,
        /// plus the declarations of the methods that directly override it (AController::SetPawn lists
        /// APlayerController::SetPawn ... override).
        /// </summary>
        public HashSet<(int File, int Line, int Col, byte Kind)> Usages(int sym)
        {
            var result = new HashSet<(int, int, int, byte)>();
            foreach (var s in RelatedSymbols(sym)) foreach (var r in RefsOf(s)) result.Add(r);
            if (overriddenBy == null)
            {
                var lists = new List<int>[SymbolCount];
                for (int i = 0; i < SymOverrides.Length; i++)
                    if (SymOverrides[i] != null) foreach (var b in SymOverrides[i]) (lists[b] ??= new List<int>()).Add(i);
                overriddenBy = Array.ConvertAll(lists, l => l?.ToArray());
            }
            var by = overriddenBy[sym];
            if (by != null) foreach (var o in by) if (SymFile[o] >= 0) result.Add((SymFile[o], SymLine[o], SymCol[o], (byte)UnrealSense.Indexer.RefKind.Decl));
            return result;
        }

        public IEnumerable<(int File, int Line, int Col, byte Kind)> RefsOf(int sym)
        {
            for (int k = RefStart[sym]; k < RefStart[sym + 1]; k++) yield return (RefFile[k], RefLine[k], RefCol[k], RefKind[k]);
        }

        public IEnumerable<(int File, int Line, int Col, byte Kind)> UnresolvedOf(string name)
        {
            int id = Array.IndexOf(UnresolvedNames, name);
            if (id < 0) yield break;
            int lo = Array.BinarySearch(UnName, id);
            if (lo < 0) yield break;
            while (lo > 0 && UnName[lo - 1] == id) lo--;
            for (int k = lo; k < UnName.Length && UnName[k] == id; k++) yield return (UnFile[k], UnLine[k], UnCol[k], UnKind[k]);
        }

        public string QualifiedName(int s)
        {
            var parts = new List<string>();
            for (int x = s; x > 0; x = SymParent[x]) parts.Add(SymNames[x].Length == 0 ? "(anonymous)" : SymNames[x]);
            parts.Reverse();
            return string.Join("::", parts);
        }
    }
}
