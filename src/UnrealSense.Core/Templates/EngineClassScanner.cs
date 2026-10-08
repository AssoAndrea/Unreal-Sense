using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnrealSense.Project;

namespace UnrealSense.Templates
{
    /// <summary>A UCLASS/UINTERFACE/USTRUCT declaration found by the cheap header scan.</summary>
    public sealed class ScannedType
    {
        public NewClassKind Kind { get; set; }
        public string Name { get; set; }
        public string BaseName { get; set; }
        public bool IsExported { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsMinimalApi { get; set; }
        public bool IsFinal { get; set; }
        public bool IsDeprecated { get; set; }
    }

    /// <summary>
    /// Finds the classes a project class can derive from in the engine and its plugins. Reading every public header
    /// with the full <see cref="Cpp.HeaderParser"/> would take too long on an engine (tens of thousands of headers), so
    /// this only looks at the declaration line after each UCLASS/UINTERFACE/USTRUCT macro, and caches the result per
    /// file (time stamp + size) so an unchanged engine is not read again.
    /// </summary>
    public static class EngineClassScanner
    {
        const int CacheVersion = 1;

        // The macro must start a line: ObjectMacros.h and doc comments mention "UCLASS(" in the middle of text.
        static readonly Regex MacroStart = new Regex(@"^[ \t]*(?<m>UCLASS|UINTERFACE|USTRUCT)\s*\(", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex ParenMacro = new Regex(@"\b\w+\s*\((?:[^()]|\([^()]*\))*\)", RegexOptions.Compiled);

        sealed class CacheFile
        {
            public int Version { get; set; }
            public Dictionary<string, CacheEntry> Files { get; set; } = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        sealed class CacheEntry
        {
            public long Ticks { get; set; }
            public long Size { get; set; }
            public List<ScannedType> Types { get; set; }
        }

        sealed class HeaderRoot
        {
            public UnrealModule Module;
            public UnrealPlugin Plugin;
            public string Directory;
        }

        /// <summary>Cache file of an engine installation, next to the other UnrealSense caches.</summary>
        public static string DefaultCachePath(EngineInstallation engine)
        {
            uint hash = 2166136261;
            foreach (var c in engine.RootDirectory.ToUpperInvariant())
                hash = (hash ^ c) * 16777619;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnrealSense", "Cache", $"Engine-{engine.Version}-{hash:x8}", "classes.json");
        }

        /// <summary>
        /// Exported classes, interfaces and structs of the engine's Runtime/Developer/Editor modules and of all
        /// engine plugins (the caller filters plugins the project does not enable).
        /// </summary>
        public static List<ParentClassInfo> Scan(EngineInstallation engine, string cachePath = null, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            var result = new List<ParentClassInfo>();
            if (engine == null || !Directory.Exists(engine.EngineDirectory)) return result;
            var watch = Stopwatch.StartNew();

            var roots = new List<HeaderRoot>();
            foreach (var group in new[] { "Runtime", "Developer", "Editor" })
                foreach (var module in ModuleScanner.ScanModules(Path.Combine(engine.EngineDirectory, "Source", group), ModuleOwnerKind.Engine, null))
                    AddRoots(roots, module, null);
            foreach (var plugin in ModuleScanner.ScanPlugins(Path.Combine(engine.EngineDirectory, "Plugins"), isEngine: true))
                foreach (var module in plugin.Modules)
                    AddRoots(roots, module, plugin);

            var files = roots.SelectMany(r => ModuleScanner.EnumerateFiles(r.Directory, "*.h").Select(f => (Root: r, File: f))).ToList();
            var enumerated = watch.ElapsedMilliseconds;

            var cache = LoadCache(cachePath);
            var fresh = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            int read = 0;
            Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, item =>
            {
                FileInfo info;
                try { info = new FileInfo(item.File); if (!info.Exists) return; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }

                if (cache.Files.TryGetValue(item.File, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Size == info.Length)
                {
                    fresh[item.File] = cached;
                    return;
                }
                List<ScannedType> types;
                try { types = ScanHeaderText(File.ReadAllText(item.File)).ToList(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
                Interlocked.Increment(ref read);
                fresh[item.File] = new CacheEntry { Ticks = info.LastWriteTimeUtc.Ticks, Size = info.Length, Types = types.Count > 0 ? types : null };
            });

            foreach (var item in files)
            {
                if (!fresh.TryGetValue(item.File, out var entry) || entry.Types == null) continue;
                foreach (var type in entry.Types)
                {
                    // Deriving from another module needs the class exported (XXX_API or MinimalAPI); structs are
                    // usable without it as long as they have no out-of-line virtuals, which UHT structs rarely have.
                    if (type.IsFinal || type.IsDeprecated) continue;
                    if (type.Kind == NewClassKind.Class && !type.IsExported && !type.IsMinimalApi) continue;
                    result.Add(new ParentClassInfo
                    {
                        Name = type.Name,
                        Kind = type.Kind,
                        BaseName = type.BaseName,
                        HeaderPath = item.File,
                        IncludePath = ParentClassInfo.ComputeIncludePath(item.File, item.Root.Module.Directory, out _),
                        ModuleName = item.Root.Module.Name,
                        PluginName = item.Root.Plugin?.Name,
                        PluginEnabledByDefault = item.Root.Plugin?.EnabledByDefault ?? true,
                        IsEngine = true,
                        IsAbstract = type.IsAbstract,
                    });
                }
            }

            if (read > 0 || fresh.Count != cache.Files.Count) SaveCache(cachePath, fresh);
            log?.Invoke($"engine class scan: {result.Count} types in {files.Count} headers of {roots.Count} folders, " +
                $"{read} read ({files.Count - read} from cache), {watch.ElapsedMilliseconds} ms (enumeration {enumerated} ms)");
            return result;
        }

        static void AddRoots(List<HeaderRoot> roots, UnrealModule module, UnrealPlugin plugin)
        {
            // Only Public and Classes are on other modules' include paths.
            foreach (var folder in new[] { "Public", "Classes" })
            {
                var dir = Path.Combine(module.Directory, folder);
                if (Directory.Exists(dir)) roots.Add(new HeaderRoot { Module = module, Plugin = plugin, Directory = dir });
            }
        }

        /// <summary>The reflected declarations of a header: the class/struct line that follows each macro.</summary>
        public static IEnumerable<ScannedType> ScanHeaderText(string text)
        {
            foreach (Match m in MacroStart.Matches(text))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchingParen(text, open);
                if (close < 0) continue;
                var specifiers = text.Substring(open + 1, close - open - 1);

                int pos = SkipTrivia(text, close + 1);
                var keyword = ReadWord(text, pos);
                if (keyword != "class" && keyword != "struct") continue;
                pos += keyword.Length;
                int end = text.IndexOfAny(new[] { '{', ';' }, pos);
                if (end < 0) continue;
                var declaration = StripComments(text.Substring(pos, end - pos));
                var type = ParseDeclaration(declaration);
                if (type == null) continue;

                type.Kind = m.Groups["m"].Value == "USTRUCT" ? NewClassKind.Struct
                    : m.Groups["m"].Value == "UINTERFACE" ? NewClassKind.Interface : NewClassKind.Class;
                type.IsAbstract = Regex.IsMatch(specifiers, @"\bAbstract\b", RegexOptions.IgnoreCase);
                type.IsMinimalApi = Regex.IsMatch(specifiers, @"\bMinimalAPI\b", RegexOptions.IgnoreCase);
                type.IsDeprecated |= Regex.IsMatch(specifiers, @"\bDeprecated\b", RegexOptions.IgnoreCase) || type.Name.Contains("DEPRECATED");
                yield return type;
            }
        }

        static ScannedType ParseDeclaration(string declaration)
        {
            // "ENGINE_API AActor : public UObject", "UE_DEPRECATED(5.1, "...") UFoo final : public UBar, public IBaz"
            bool deprecated = declaration.Contains("DEPRECATED");
            var head = declaration;
            string bases = null;
            int colon = FindBaseColon(declaration);
            if (colon >= 0)
            {
                head = declaration.Substring(0, colon);
                bases = declaration.Substring(colon + 1);
            }
            head = ParenMacro.Replace(head, " ");
            var tokens = head.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string api = tokens.FirstOrDefault(t => t.EndsWith("_API", StringComparison.Ordinal));
            bool isFinal = tokens.Contains("final");
            var name = tokens.LastOrDefault(t => t != "final" && t != api);
            if (name == null || !Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) return null;

            string baseName = null;
            if (bases != null)
            {
                var first = bases.Split(',')[0];
                var baseTokens = first.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t != "public" && t != "protected" && t != "private" && t != "virtual").ToList();
                baseName = baseTokens.Count > 0 ? string.Concat(baseTokens) : null;
            }
            return new ScannedType { Name = name, BaseName = baseName, IsExported = api != null, IsFinal = isFinal, IsDeprecated = deprecated };
        }

        static int FindBaseColon(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != ':') continue;
                if (i + 1 < s.Length && s[i + 1] == ':') { i++; continue; }
                return i;
            }
            return -1;
        }

        static int MatchingParen(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                }
                else if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        static int SkipTrivia(string text, int pos)
        {
            while (pos < text.Length)
            {
                if (char.IsWhiteSpace(text[pos])) pos++;
                else if (string.CompareOrdinal(text, pos, "//", 0, 2) == 0)
                {
                    int nl = text.IndexOf('\n', pos);
                    pos = nl < 0 ? text.Length : nl + 1;
                }
                else if (string.CompareOrdinal(text, pos, "/*", 0, 2) == 0)
                {
                    int endComment = text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    pos = endComment < 0 ? text.Length : endComment + 2;
                }
                else break;
            }
            return pos;
        }

        static string ReadWord(string text, int pos)
        {
            int start = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
            return text.Substring(start, pos - start);
        }

        static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(text, @"//[^\n]*", " ");
        }

        static CacheFile LoadCache(string path)
        {
            if (path == null || !File.Exists(path)) return new CacheFile();
            try
            {
                var cache = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(path));
                if (cache?.Version == CacheVersion && cache.Files != null)
                {
                    cache.Files = new Dictionary<string, CacheEntry>(cache.Files, StringComparer.OrdinalIgnoreCase);
                    return cache;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException) { }
            return new CacheFile();
        }

        static void SaveCache(string path, IDictionary<string, CacheEntry> files)
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var cache = new CacheFile { Version = CacheVersion, Files = new Dictionary<string, CacheEntry>(files, StringComparer.OrdinalIgnoreCase) };
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(cache, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore }));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
