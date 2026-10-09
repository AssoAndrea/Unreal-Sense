using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnrealSense.Indexer
{
    /// <summary>What phase 0 and pass 1 produced for one file at a given size and write time.</summary>
    public sealed class CachedFile
    {
        public string Path;
        public long Size, Ticks;
        public int Lines;
        public List<MacroDef> Defines;
        public HashSet<int> Undefs;
        public List<string> Includes;
        public List<(string Include, Dictionary<int, MacroDef> Macros)> IncludeContexts;
        public HashSet<int> UnknownInConditions;
        public ulong DeclHash;
        public Dictionary<string, List<Decl>> Units = new Dictionary<string, List<Decl>>(); // context key ("" = no context) → declarations
    }

    /// <summary>
    /// Per-file results of the previous build, so that the next one reads and parses only the files that changed (a cold build
    /// of the engine spends most of its time opening 100k files). References are not stored here: they come from the previous
    /// index (<see cref="IndexData"/>) and are remapped by stable symbol key. Names are stored as strings: <see cref="Names"/>
    /// ids are per process.
    /// </summary>
    public sealed class BuildCache
    {
        const int Version = 2;
        public string EnvKey;
        public string BuildId;   // same as the index written by that build: a cache and an index from different builds are not combined
        public Dictionary<string, CachedFile> Files = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ save

        const int Segments = 32;

        /// <summary>
        /// Written as independent Brotli segments (each with its own name table) so that saving and loading run on all cores:
        /// one stream took ~3 s to load and ~2 s to save for the engine (100k files, 2.3 M declarations).
        /// </summary>
        public long Save(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            var all = Files.Values.ToList();
            var blobs = new byte[Segments][];
            Parallel.For(0, Segments, seg =>
            {
                var names = new Dictionary<int, int>();
                var nameList = new List<int>();
                var body = new MemoryStream();
                using (var w = new BinaryWriter(body, Encoding.UTF8, true))
                {
                    int N(int id)
                    {
                        if (id <= 0) return id; // 0 = anonymous, negative = none
                        if (!names.TryGetValue(id, out int i)) { i = nameList.Count + 1; names[id] = i; nameList.Add(id); }
                        return i;
                    }
                    var cw = new Writer(w, N);
                    int count = 0;
                    for (int k = seg; k < all.Count; k += Segments) count++;
                    w.Write(count);
                    for (int k = seg; k < all.Count; k += Segments) WriteFile(w, cw, N, all[k]);
                }
                var outMs = new MemoryStream();
                using (var z = new System.IO.Compression.BrotliStream(outMs, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new BinaryWriter(z, Encoding.UTF8, true))
                {
                    w.Write(nameList.Count);
                    foreach (var id in nameList) w.Write(Names.Get(id));
                    body.Position = 0;
                    w.Flush();
                    body.CopyTo(z);
                }
                blobs[seg] = outMs.ToArray();
            });
            using (var fs = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                fs.Write(Version); fs.Write(EnvKey ?? ""); fs.Write(BuildId ?? "");
                fs.Write(Segments);
                foreach (var bl in blobs) fs.Write(bl.Length);
                foreach (var bl in blobs) fs.Write(bl);
            }
            File.Move(tmp, path, true);
            return new FileInfo(path).Length;
        }

        static void WriteFile(BinaryWriter w, Writer cw, Func<int, int> N, CachedFile f)
        {
            w.Write(f.Path); w.Write(f.Size); w.Write(f.Ticks); w.Write(f.Lines); w.Write(f.DeclHash);
            cw.Macros(f.Defines);
            cw.NameSet(f.Undefs);
            cw.NameSet(f.UnknownInConditions);
            w.Write(f.Includes?.Count ?? -1);
            if (f.Includes != null) foreach (var x in f.Includes) w.Write(x);
            w.Write(f.IncludeContexts?.Count ?? -1);
            if (f.IncludeContexts != null)
                foreach (var (inc, macros) in f.IncludeContexts)
                {
                    w.Write(inc);
                    w.Write(macros.Count);
                    foreach (var kv in macros) { w.Write(N(kv.Key)); cw.Macro(kv.Value); }
                }
            w.Write(f.Units.Count);
            foreach (var kv in f.Units) { w.Write(kv.Key); cw.Decls(kv.Value); }
        }

        // ------------------------------------------------------------------ meta (file list only)

        /// <summary>The file list of a build, small enough to read first: when nothing changed the previous index is reused as is.</summary>
        public sealed class Meta
        {
            public string EnvKey, BuildId;
            public Dictionary<string, (long Size, long Ticks)> Files = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        }

        public static void SaveMeta(string path, string envKey, string buildId, IEnumerable<(string Path, long Size, long Ticks)> files)
        {
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20), Encoding.UTF8))
            {
                w.Write(Version); w.Write(envKey); w.Write(buildId);
                var list = files.ToList();
                w.Write(list.Count);
                foreach (var f in list) { w.Write(f.Path); w.Write(f.Size); w.Write(f.Ticks); }
            }
            File.Move(tmp, path, true);
        }

        public static Meta LoadMeta(string path)
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20), Encoding.UTF8);
            if (r.ReadInt32() != Version) return null;
            var m = new Meta { EnvKey = r.ReadString(), BuildId = r.ReadString() };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) { var p = r.ReadString(); m.Files[p] = (r.ReadInt64(), r.ReadInt64()); }
            return m;
        }

        // ------------------------------------------------------------------ load

        public static BuildCache Load(string path)
        {
            byte[][] blobs;
            BuildCache c;
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (r.ReadInt32() != Version) return null;
                c = new BuildCache { EnvKey = r.ReadString(), BuildId = r.ReadString() };
                var lengths = new int[r.ReadInt32()];
                for (int i = 0; i < lengths.Length; i++) lengths[i] = r.ReadInt32();
                blobs = new byte[lengths.Length][];
                for (int i = 0; i < lengths.Length; i++) blobs[i] = r.ReadBytes(lengths[i]);
            }
            var parts = new List<CachedFile>[blobs.Length];
            Parallel.For(0, blobs.Length, seg =>
            {
                using var z = new System.IO.Compression.BrotliStream(new MemoryStream(blobs[seg]), System.IO.Compression.CompressionMode.Decompress);
                using var r = new BinaryReader(new BufferedStream(z, 1 << 20), Encoding.UTF8);
                var ids = new int[r.ReadInt32() + 1];
                for (int i = 1; i < ids.Length; i++) ids[i] = Names.Intern(r.ReadString());
                int N(int i) => i <= 0 ? i : ids[i];
                var cr = new Reader(r, N);
                int files = r.ReadInt32();
                var list = new List<CachedFile>(files);
                for (int k = 0; k < files; k++) list.Add(ReadFile(r, cr, N));
                parts[seg] = list;
            });
            foreach (var list in parts) foreach (var f in list) c.Files[f.Path] = f;
            return c;
        }

        static CachedFile ReadFile(BinaryReader r, Reader cr, Func<int, int> N)
        {
            var f = new CachedFile { Path = r.ReadString(), Size = r.ReadInt64(), Ticks = r.ReadInt64(), Lines = r.ReadInt32(), DeclHash = r.ReadUInt64() };
            f.Defines = cr.Macros();
            if (f.Defines != null) foreach (var d in f.Defines) d.Origin = f.Path;
            f.Undefs = cr.NameSet();
            f.UnknownInConditions = cr.NameSet();
            int n = r.ReadInt32();
            if (n >= 0) { f.Includes = new List<string>(n); for (int i = 0; i < n; i++) f.Includes.Add(r.ReadString()); }
            n = r.ReadInt32();
            if (n >= 0)
            {
                f.IncludeContexts = new List<(string, Dictionary<int, MacroDef>)>(n);
                for (int i = 0; i < n; i++)
                {
                    var inc = r.ReadString();
                    int m = r.ReadInt32();
                    var d = new Dictionary<int, MacroDef>(m);
                    for (int j = 0; j < m; j++) { int key = N(r.ReadInt32()); d[key] = cr.Macro(); }
                    f.IncludeContexts.Add((inc, d));
                }
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) { var key = r.ReadString(); f.Units[key] = cr.Decls(); }
            return f;
        }

        public static ulong HashBytes(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        // ------------------------------------------------------------------ declaration "shape" hash

        /// <summary>
        /// Hash of what other files can see of a file's declarations: kinds, names, types, parameters, nesting — not their
        /// positions. An edit inside function bodies keeps it, so the files that include it need no new pass 2.
        /// </summary>
        public static ulong HashDecls(IEnumerable<List<Decl>> units)
        {
            var h = new Fnv();
            foreach (var decls in units)
            {
                h.Add(decls.Count);
                foreach (var d in decls)
                {
                    h.Add((int)d.Kind); h.Name(d.Name); h.Add(d.Parent); h.Add((int)(d.Flags & ~DeclFlags.Definition)); h.Add(d.HasQualifiedAncestor ? 1 : 0);
                    h.Parts(d.Qualifier); h.Type(d.Type); h.Types(d.Bases);
                    h.Add(d.Params?.Length ?? -1);
                    if (d.Params != null) foreach (var p in d.Params) { h.Type(p.Type); h.Name(p.Name); h.Add(p.HasDefault ? 1 : 0); }
                    h.Add(d.MinArgs); h.Add(d.MaxArgs);
                    h.Add(d.TemplateParams?.Length ?? -1);
                    if (d.TemplateParams != null) foreach (var t in d.TemplateParams) h.Name(t);
                }
            }
            return h.Value;
        }

        sealed class Fnv
        {
            public ulong Value = 14695981039346656037UL;
            public void Add(int v) { for (int i = 0; i < 4; i++) { Value ^= (byte)(v >> (8 * i)); Value *= 1099511628211UL; } }
            public void Name(int id)
            {
                if (id <= 0) { Add(id); return; }
                foreach (var ch in Names.Get(id)) { Value ^= ch; Value *= 1099511628211UL; }
                Add(-7);
            }
            public void Parts(NamePart[] ps)
            {
                Add(ps?.Length ?? -1);
                if (ps != null) foreach (var p in ps) { Name(p.Name); Types(p.Args); }
            }
            public void Types(TypeExpr[] ts)
            {
                Add(ts?.Length ?? -1);
                if (ts != null) foreach (var t in ts) Type(t);
            }
            public void Type(TypeExpr t)
            {
                if (t == null) { Add(-2); return; }
                Parts(t.Parts); Add(t.Global ? 1 : 0); Add((int)t.Builtin); Add(t.Ptr); Add(t.Ref ? 1 : 0); Add(t.Const ? 1 : 0);
                Type(t.FuncReturn); Types(t.FuncParams);
            }
        }

        // ------------------------------------------------------------------ serialization

        sealed class Writer
        {
            readonly BinaryWriter w; readonly Func<int, int> n;
            public Writer(BinaryWriter w, Func<int, int> n) { this.w = w; this.n = n; }

            public void NameSet(HashSet<int> s)
            {
                w.Write(s?.Count ?? -1);
                if (s != null) foreach (var x in s) w.Write(n(x));
            }

            public void Macros(List<MacroDef> list)
            {
                w.Write(list?.Count ?? -1);
                if (list != null) foreach (var m in list) Macro(m);
            }

            public void Macro(MacroDef m)
            {
                w.Write(n(m.Name)); w.Write(m.FunctionLike); w.Write(m.Variadic); w.Write(m.IsDefault); w.Write(m.BodyKey ?? "");
                w.Write(m.Params.Length); foreach (var p in m.Params) w.Write(n(p));
                w.Write(m.Body.Length);
                foreach (var t in m.Body) Token(t);
            }

            void Token(Token t)
            {
                w.Write((byte)t.Kind); w.Write(t.Kind == TK.Ident ? n(t.Value) : t.Value);
                w.Write(t.Pos); w.Write(t.Line); w.Write(t.Col); w.Write(t.Len); w.Write(t.Virtual);
            }

            public void Decls(List<Decl> decls)
            {
                w.Write(decls.Count);
                foreach (var d in decls)
                {
                    w.Write((byte)d.Kind); w.Write(n(d.Name)); w.Write(d.Line); w.Write(d.Col); w.Write(d.Parent);
                    Parts(d.Qualifier); Type(d.Type); Types(d.Bases);
                    w.Write(d.Params?.Length ?? -1);
                    if (d.Params != null) foreach (var p in d.Params) { Type(p.Type); w.Write(n(p.Name)); w.Write(p.HasDefault); }
                    w.Write(d.MinArgs); w.Write(d.MaxArgs);
                    w.Write(d.TemplateParams?.Length ?? -1);
                    if (d.TemplateParams != null) foreach (var t in d.TemplateParams) w.Write(n(t));
                    w.Write((uint)d.Flags); w.Write(d.HasQualifiedAncestor);
                }
            }

            void Parts(NamePart[] ps)
            {
                w.Write(ps?.Length ?? -1);
                if (ps != null) foreach (var p in ps) { w.Write(n(p.Name)); w.Write(p.Tok); Types(p.Args); }
            }

            void Types(TypeExpr[] ts)
            {
                w.Write(ts?.Length ?? -1);
                if (ts != null) foreach (var t in ts) Type(t);
            }

            void Type(TypeExpr t)
            {
                if (t == null) { w.Write((byte)0); return; }
                w.Write((byte)1);
                Parts(t.Parts); w.Write(t.Global); w.Write((byte)t.Builtin); w.Write(t.Ptr); w.Write(t.Ref); w.Write(t.Const);
                Type(t.FuncReturn); Types(t.FuncParams);
            }
        }

        sealed class Reader
        {
            readonly BinaryReader r; readonly Func<int, int> n;
            public Reader(BinaryReader r, Func<int, int> n) { this.r = r; this.n = n; }

            public HashSet<int> NameSet()
            {
                int c = r.ReadInt32();
                if (c < 0) return null;
                var s = new HashSet<int>();
                for (int i = 0; i < c; i++) s.Add(n(r.ReadInt32()));
                return s;
            }

            public List<MacroDef> Macros()
            {
                int c = r.ReadInt32();
                if (c < 0) return null;
                var l = new List<MacroDef>(c);
                for (int i = 0; i < c; i++) l.Add(Macro());
                return l;
            }

            public MacroDef Macro()
            {
                var m = new MacroDef { Name = n(r.ReadInt32()), FunctionLike = r.ReadBoolean(), Variadic = r.ReadBoolean(), IsDefault = r.ReadBoolean(), BodyKey = r.ReadString() };
                var ps = new int[r.ReadInt32()];
                for (int i = 0; i < ps.Length; i++) ps[i] = n(r.ReadInt32());
                m.Params = ps;
                var body = new Token[r.ReadInt32()];
                for (int i = 0; i < body.Length; i++) body[i] = Token();
                m.Body = body;
                return m;
            }

            Token Token()
            {
                var k = (TK)r.ReadByte();
                int v = r.ReadInt32();
                return new Token { Kind = k, Value = k == TK.Ident ? n(v) : v, Pos = r.ReadInt32(), Line = r.ReadInt32(), Col = r.ReadInt32(), Len = r.ReadUInt16(), Virtual = r.ReadBoolean() };
            }

            public List<Decl> Decls()
            {
                int c = r.ReadInt32();
                var l = new List<Decl>(c);
                for (int i = 0; i < c; i++)
                {
                    var d = new Decl { Kind = (SymKind)r.ReadByte(), Name = n(r.ReadInt32()), Line = r.ReadInt32(), Col = r.ReadInt32(), Parent = r.ReadInt32() };
                    d.Qualifier = Parts(); d.Type = Type(); d.Bases = Types();
                    int pc = r.ReadInt32();
                    if (pc >= 0)
                    {
                        d.Params = new Param[pc];
                        for (int j = 0; j < pc; j++) d.Params[j] = new Param { Type = Type(), Name = n(r.ReadInt32()), HasDefault = r.ReadBoolean() };
                    }
                    d.MinArgs = r.ReadInt16(); d.MaxArgs = r.ReadInt16();
                    int tc = r.ReadInt32();
                    if (tc >= 0) { d.TemplateParams = new int[tc]; for (int j = 0; j < tc; j++) d.TemplateParams[j] = n(r.ReadInt32()); }
                    d.Flags = (DeclFlags)r.ReadUInt32(); d.HasQualifiedAncestor = r.ReadBoolean();
                    l.Add(d);
                }
                return l;
            }

            NamePart[] Parts()
            {
                int c = r.ReadInt32();
                if (c < 0) return null;
                var ps = new NamePart[c];
                for (int i = 0; i < c; i++) ps[i] = new NamePart { Name = n(r.ReadInt32()), Tok = r.ReadInt32(), Args = Types() };
                return ps;
            }

            TypeExpr[] Types()
            {
                int c = r.ReadInt32();
                if (c < 0) return null;
                var ts = new TypeExpr[c];
                for (int i = 0; i < c; i++) ts[i] = Type();
                return ts;
            }

            TypeExpr Type()
            {
                if (r.ReadByte() == 0) return null;
                return new TypeExpr { Parts = Parts(), Global = r.ReadBoolean(), Builtin = (Builtin)r.ReadByte(), Ptr = r.ReadByte(), Ref = r.ReadBoolean(), Const = r.ReadBoolean(), FuncReturn = Type(), FuncParams = Types() };
            }
        }
    }
}
