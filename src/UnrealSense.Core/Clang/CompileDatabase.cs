using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Project;

namespace UnrealSense.Clang
{
    /// <summary>
    /// Produces a compile_commands.json that clangd understands from UnrealBuildTool's ClangDatabase:
    /// UBT emits MSVC command lines with nested response files and MSVC-only switches (PCH, logging, /d2...),
    /// so we expand the per-file @file, keep the shared ones as cleaned @references, and drop what clang-cl
    /// can't or shouldn't process.
    /// </summary>
    public static class CompileDatabase
    {
        /// <summary>Switches removed together with the following argument.</summary>
        static readonly HashSet<string> DropWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/experimental:log", "/sourceDependencies", "/analyze:log", "/analyze:ruleset", "/Fo", "/Fd",
        };

        /// <summary>Switches removed exactly.</summary>
        static readonly HashSet<string> DropExact = new HashSet<string>(StringComparer.Ordinal)
        {
            "/c", "/nologo", "/sdl", "/FS", "/Zo", "/showIncludes", "/Bt", "/Bt+",
        };

        /// <summary>Unambiguous switch prefixes removed (output files, PCH, MSVC-internal and logging options).</summary>
        static readonly string[] DropPrefixes =
        {
            "/Fo", "/Fd", "/Fp", "/Yu", "/Yc", "/Fa", "/Fm", "/MP", "/d1", "/d2", "/dx", "/errorReport", "/experimental",
            "/sourceDependencies", "/analyze", "/diagnostics", "/cgthreads", "/guard",
        };

        /// <summary>
        /// Folder holding the databases, unity files and clangd's index. Defaults to %LOCALAPPDATA%; set it to a
        /// path that is the same on every machine (e.g. S:\UnrealSenseCache) to share one prebuilt index across a
        /// team: clangd keys its index by absolute path and content hash, so a copy made on another machine with the
        /// same engine and paths is reused as is.
        /// </summary>
        public static string CacheRoot
        {
            get => string.IsNullOrWhiteSpace(cacheRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "Cache")
                : cacheRoot;
            set => cacheRoot = value?.Trim().Trim('"');
        }
        static string cacheRoot;

        public static string GetOutputDirectory(UnrealProject project)
        {
            uint hash = 2166136261;
            foreach (var c in project.UProjectPath.ToUpperInvariant())
                hash = (hash ^ c) * 16777619;
            return Path.Combine(CacheRoot, $"{project.Name}-{hash:x8}", "clangd");
        }

        /// <summary>The editor target (XxxEditor.Target.cs) if present, else the first game target.</summary>
        public static string FindTarget(UnrealProject project)
        {
            if (!Directory.Exists(project.SourceDirectory)) return project.Name + "Editor";
            var targets = Directory.GetFiles(project.SourceDirectory, "*.Target.cs").Select(f => Path.GetFileName(f).Replace(".Target.cs", "")).ToList();
            return targets.FirstOrDefault(t => t.EndsWith("Editor", StringComparison.OrdinalIgnoreCase))
                   ?? targets.FirstOrDefault() ?? project.Name + "Editor";
        }

        /// <summary>Bump when the sanitized output changes shape, so existing databases are rewritten.</summary>
        public const int FormatVersion = 19;
        const string StampFile = "unrealsense-db.txt";
        /// <summary>One command per source file: used to give clangd the exact flags of a file opened in the editor.</summary>
        public const string FilesVariant = "compile_commands.files.json";
        /// <summary>Unity translation units of the project only (indexed first).</summary>
        public const string ProjectVariant = "compile_commands.project.json";
        /// <summary>Unity translation units of the project, the engine and all plugins.</summary>
        public const string FullVariant = "compile_commands.full.json";

        /// <summary>Runs UBT -mode=GenerateClangDatabase and writes the sanitized databases. Returns their directory.</summary>
        public static string Generate(UnrealProject project, Action<string> log, string engineExclusions = null, bool acrossModules = true)
        {
            var engine = project.Engine ?? throw new InvalidOperationException("Engine not found for " + project.UProjectPath);
            var output = GetOutputDirectory(project);
            var raw = Path.Combine(output, "ubt");
            Directory.CreateDirectory(raw);

            var target = FindTarget(project);
            // GenerateClangDatabase defaults to the Clang toolchain; we want the MSVC command lines the project
            // really builds with (clangd parses them in clang-cl mode).
            int exitCode = -1;
            foreach (var compiler in new[] { "VisualStudio2022", "VisualStudio2026", null })
            {
                var args = $"-mode=GenerateClangDatabase \"-project={project.UProjectPath}\" {target} Win64 Development \"-OutputDir={raw}\""
                           + (compiler != null ? $" -Compiler={compiler}" : "");
                log?.Invoke($"> UnrealBuildTool {args}");
                exitCode = RunTool(engine.UnrealBuildTool, args, project.ProjectDirectory, log);
                if (exitCode == 0) break;
            }
            if (exitCode != 0)
                throw new InvalidOperationException($"UnrealBuildTool failed with exit code {exitCode}.");

            if (!Resanitize(project, engineExclusions, log, acrossModules))
                throw new FileNotFoundException("UBT did not produce compile_commands.json", RawDatabase(project));
            return output;
        }

        /// <summary>
        /// Version of the stored clangd index. clangd never rewrites a file's stored symbols while the file is unchanged,
        /// except when the earlier pass had errors and the new one has none; so references lost to compile errors in an
        /// older database (no PCH, dllimport...) stay lost while the new unit still has any error. Raising this
        /// discards the stored index once.
        /// </summary>
        public const int IndexFormat = 1;

        /// <summary>
        /// Deletes clangd's stored index (&lt;dir&gt;/.cache/clangd/index) when it was built by an older index format or,
        /// with <paramref name="extensionVersion"/>, by another version of the extension (each release is measured
        /// from a clean index). True if deleted.
        /// </summary>
        public static bool DiscardOutdatedIndex(string directory, string extensionVersion = null)
        {
            var cache = Path.Combine(directory, ".cache", "clangd");
            var stamp = Path.Combine(cache, "unrealsense-index-format.txt");
            var expected = IndexFormat.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           + (extensionVersion != null ? ";" + extensionVersion : "");
            string current = null;
            try { current = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null; }
            catch (IOException) { }
            if (current == expected) return false;
            var index = Path.Combine(cache, "index");
            bool existed = Directory.Exists(index);
            if (existed) Directory.Delete(index, recursive: true);
            Directory.CreateDirectory(cache);
            File.WriteAllText(stamp, expected);
            return existed;
        }

        public static string RawDatabase(UnrealProject project) => Path.Combine(GetOutputDirectory(project), "ubt", "compile_commands.json");

        /// <summary>
        /// Rewrites the databases from UBT's last output (no UBT run): the per-file commands, then unity
        /// translation units for the project only (indexed first) and for everything (engine and plugins minus
        /// <paramref name="engineExclusions"/>, semicolon-separated path fragments). The project variant is
        /// activated. False if UBT never ran.
        /// </summary>
        public static bool Resanitize(UnrealProject project, string engineExclusions, Action<string> log, bool acrossModules = true)
        {
            var rawFile = RawDatabase(project);
            if (!File.Exists(rawFile)) return false;
            var output = GetOutputDirectory(project);
            var engineDir = project.Engine?.EngineDirectory;
            var enginePrefix = engineDir != null ? Normalize(engineDir).TrimEnd('/') + "/" : null;
            var exclusions = ParseExclusions(engineExclusions);
            Func<string, bool> isProject = file => enginePrefix == null || !Normalize(file).StartsWith(enginePrefix, StringComparison.OrdinalIgnoreCase);

            var sw = Stopwatch.StartNew();
            var files = new DatabaseVariant(Path.Combine(output, FilesVariant),
                file => !exclusions.Any(x => Normalize(file).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0));
            var pchWatch = Stopwatch.StartNew();
            var pchRoots = new List<string> { project.SourceDirectory, Path.Combine(project.ProjectDirectory, "Plugins") };
            if (engineDir != null) { pchRoots.Add(Path.Combine(engineDir, "Source")); pchRoots.Add(Path.Combine(engineDir, "Plugins")); }
            var pchResolver = new PchResolver(pchRoots);
            log?.Invoke($"precompiled headers: {pchResolver.ModuleCount:N0} module rules read in {pchWatch.Elapsed.TotalSeconds:F1}s");
            int total = Sanitize(rawFile, new[] { files }, pchResolver.Find);

            var unityDir = Path.Combine(output, "unity");
            if (Directory.Exists(unityDir)) Directory.Delete(unityDir, recursive: true);
            var mapsDir = Path.Combine(output, HeaderMaps.FolderName);
            if (Directory.Exists(mapsDir)) Directory.Delete(mapsDir, recursive: true);
            Func<string, bool> indexed = f => IndexGeneratedCode || !IsGeneratedSource(f);
            var (projectStats, engineStats) = WriteVariants(files.Path, output, unityDir, f => isProject(f) && indexed(f), acrossModules,
                isEngine: f => !isProject(f) && indexed(f));
            int projectUnits = projectStats.Units, fullUnits = projectStats.Units + engineStats.Units;
            var fullStats = engineStats;

            File.WriteAllText(Path.Combine(output, StampFile), string.Join(Environment.NewLine,
                Stamp(engineExclusions, acrossModules), $"project={projectUnits}", $"full={fullUnits}", $"files={files.Count}"));
            Activate(output, fullDatabase: false);
            log?.Invoke($"compile_commands: {files.Count:N0} source files ({total - files.Count:N0} excluded) -> {projectUnits:N0} project + " +
                        $"{fullUnits - projectUnits:N0} engine/plugin unity translation units in {sw.Elapsed.TotalSeconds:F1}s -> {output}");
            if (Directory.Exists(mapsDir))
                log?.Invoke($"header maps: {Directory.GetFiles(mapsDir, "*.hmap").Length:N0} include directories answered from memory");
            log?.Invoke("unity grouping (project): " + projectStats.ToString().Split('\n')[0]);
            log?.Invoke("unity grouping (engine + plugins): " + fullStats);
            return true;
        }

        /// <summary>
        /// Writes the project and full unity databases. The full one is the project one, unchanged, plus the
        /// engine/plugin units: clangd loads the stored index only for translation units present in its database,
        /// so regrouping project files differently in the full phase would hide the project's finished index
        /// (Find Usages then found only declarations) and index those files again.
        /// </summary>
        public static (UnityStats Project, UnityStats Engine) WriteVariants(string filesDatabase, string outputDir, string unityDir,
            Func<string, bool> isProject, bool acrossModules = true, bool groupProject = true, bool headerMaps = true,
            Func<string, bool> isEngine = null)
        {
            var maps = headerMaps ? new HeaderMaps(Path.Combine(outputDir, HeaderMaps.FolderName)) : null;
            var projectPath = Path.Combine(outputDir, ProjectVariant);
            var enginePath = Path.Combine(outputDir, "compile_commands.engine.json");
            // Project files are grouped per module only (like UBT's own unity builds, code known to compile) in units
            // of up to 64 files: enough units to keep every indexing thread busy, far fewer header parses than one
            // unit per file (818 files took too long). groupProject = false indexes every file on its own (exact).
            var project = groupProject
                ? BuildUnity(filesDatabase, projectPath, unityDir, isProject, maxFiles: ProjectUnityFiles, acrossModules: false, namePrefix: "p.", headerMaps: maps)
                : CopyEntries(filesDatabase, projectPath, isProject);
            var engine = BuildUnity(filesDatabase, enginePath, unityDir, isEngine ?? (f => !isProject(f)), acrossModules: acrossModules, namePrefix: "e.", headerMaps: maps);
            maps?.SaveManifest();

            var full = JArray.Parse(File.ReadAllText(projectPath));
            foreach (var entry in JArray.Parse(File.ReadAllText(enginePath))) full.Add(entry);
            File.WriteAllText(Path.Combine(outputDir, FullVariant), full.ToString(Formatting.None));
            File.Delete(enginePath);
            return (project, engine);
        }

        /// <summary>Largest unity unit for project code (per module).</summary>
        public const int ProjectUnityFiles = 64;

        /// <summary>Writes the entries of <paramref name="filesDatabase"/> that pass <paramref name="include"/>, unchanged.</summary>
        static UnityStats CopyEntries(string filesDatabase, string outputFile, Func<string, bool> include)
        {
            var kept = new JArray(JArray.Parse(File.ReadAllText(filesDatabase)).OfType<JObject>().Where(e => include((string)e["file"])));
            File.WriteAllText(outputFile, kept.ToString(Formatting.None));
            return new UnityStats { Units = kept.Count };
        }

        /// <summary>
        /// Groups source files compiled with identical flags (in practice: the files of one module) into unity
        /// translation units, like UnrealBuildTool does for builds. Parsing Unreal's headers is a fixed cost paid
        /// by every translation unit (measured, 1 thread: Gym 24 sources in 19 s as one unit, 34 s as four; Lyra 388 sources
        /// in 72 s as one unit, 95 s as four, 266 s as thirteen, ~1,440 s of CPU as single files), while the
        /// sources' own bodies, generated code included, add little; so fewer, larger units index the engine
        /// several times faster. clangd still records symbols and references per included file. Modules that opt
        /// out of unity builds (bUseUnity = false, usually because of clashing file-local names) are grouped in
        /// small units: a clash costs at most a few references there, while one unit per file costs minutes.
        /// With <paramref name="acrossModules"/>, files of different modules that share compiler options are
        /// grouped too (UBT's database has no PCH, so a module boundary does not change what has to be parsed):
        /// a unit then gets the union of its modules' include paths and Definitions.h. Engines have ~1,500
        /// modules, so per-module grouping alone cannot go below ~1,500 units.
        /// </summary>
        public static UnityStats BuildUnity(string filesDatabase, string outputFile, string unityDir, Func<string, bool> include,
            int maxFiles = 384, long maxBytes = 8 * 1024 * 1024, int maxFilesOptOut = 16, bool acrossModules = true, string namePrefix = "",
            HeaderMaps headerMaps = null)
        {
            var stats = new UnityStats();
            Directory.CreateDirectory(unityDir);
            var entries = JArray.Parse(File.ReadAllText(filesDatabase)).OfType<JObject>().ToList();
            var groups = new Dictionary<string, List<UnityMember>>(StringComparer.Ordinal);
            var order = new List<string>();
            var singles = new List<JObject>();
            var unityOptOut = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var apiMacros = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var responseFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var file = (string)entry["file"];
                if (file == null || (include != null && !include(file))) continue;
                var directory = (string)entry["directory"];
                var args = ((JArray)entry["arguments"]).Select(x => (string)x).ToList();
                var fullFile = FullPath(file, directory);
                // UBT writes engine sources relative to Engine/Source: compare resolved paths, not strings.
                int source = args.FindIndex(1, x => !x.StartsWith("/") && !x.StartsWith("-") && !x.StartsWith("@")
                                                     && string.Equals(FullPath(x, directory), fullFile, StringComparison.OrdinalIgnoreCase));
                string reason = source < 0 ? "no source argument"
                    : !file.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ? "not a .cpp" : null;
                if (reason != null)
                {
                    stats.Exclude(reason, file);
                    singles.Add(entry);
                    continue;
                }

                // Arguments naming this very file (per-file outputs that survived cleaning) are dropped.
                var fileName = Path.GetFileName(fullFile);
                var member = new UnityMember { Entry = entry, Compiler = args[0], File = fullFile };
                Classify(args.Where((x, i) => i != 0 && i != source && x.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0),
                    member, responseFiles, 0, directory);

                bool optOut = ModuleOptsOutOfUnity(fullFile, unityOptOut);
                // Files of different modules share a unit when their compiler options match: the unit gets the union
                // of their include paths and definitions. Opt-out modules, or "per module" mode, also key on the module.
                var module = string.Join("\u0001", member.Forced) + "\u0001" + string.Join("\u0001", member.Includes);
                var key = (optOut ? OptOutPrefix : "") + directory + "\u0001" + member.Compiler + "\u0001" + string.Join("\u0001", member.Options)
                          + (optOut || !acrossModules ? "\u0002" + module : "");
                if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new List<UnityMember>(); order.Add(key); }
                list.Add(member);
            }

            int units = 0;
            var temp = outputFile + ".tmp";
            using (var writer = new JsonTextWriter(new StreamWriter(temp)) { Formatting = Formatting.None })
            {
                writer.WriteStartArray();
                foreach (var single in singles) { single.WriteTo(writer); units++; }
                foreach (var key in order)
                {
                    var members = groups[key];
                    if (members.Count == 1) { stats.Exclude("only file with these flags", (string)members[0].Entry["file"]); members[0].Entry.WriteTo(writer); units++; continue; }
                    stats.Grouped += members.Count;
                    bool optOut = key.StartsWith(OptOutPrefix, StringComparison.Ordinal);
                    if (optOut) stats.GroupedOptOut += members.Count;
                    int limit = optOut ? maxFilesOptOut : maxFiles;
                    uint hash = 2166136261;
                    foreach (var c in key) hash = (hash ^ c) * 16777619;
                    var stem = members.Select(m => ModuleName(m.Entry)).FirstOrDefault(n => n != null) ?? "Module";
                    int chunk = 0;
                    for (int start = 0; start < members.Count;)
                    {
                        long bytes = 0;
                        int end = start;
                        while (end < members.Count && end - start < limit && (end == start || bytes < maxBytes))
                        {
                            bytes += SafeLength(members[end].File);
                            end++;
                        }
                        var name = $"{namePrefix}{stem}-{hash:x8}.{chunk++}";
                        var unityFile = Normalize(Path.Combine(unityDir, name + ".cpp"));
                        var text = new StringBuilder("// UnrealSense unity translation unit (index only)\n");
                        for (int i = start; i < end; i++)
                            text.Append("#include \"").Append(Normalize(members[i].File)).Append("\"\n");
                        WriteIfChanged(unityFile, text.ToString());

                        // Union of the members' include paths, defines and forced includes (Definitions.h of each module).
                        var flags = new List<string>();
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var list in new Func<UnityMember, List<string>>[] { m => m.Includes, m => m.Defines, m => m.Forced })
                            for (int i = start; i < end; i++)
                                foreach (var flag in list(members[i]))
                                    if (seen.Add(flag)) flags.Add(flag);
                        // Each module's Definitions.h defines its own *_API as dllexport and its dependencies' as
                        // dllimport; with several modules in one unit the last definition wins for all of them, and
                        // a module's own definitions then become "dllimport" errors (measured: 242 errors in Lyra's
                        // 388-file unit). Linkage is irrelevant for indexing: define every *_API macro as empty.
                        if (headerMaps != null) flags = headerMaps.Rewrite(flags, (string)members[start].Entry["directory"]);
                        var apiHeader = WriteApiNeutralizer(unityDir, name, flags.Where(f => f.StartsWith("/FI")).Select(f => f.Substring(3)),
                            (string)members[start].Entry["directory"], apiMacros);
                        if (apiHeader != null) flags.Add("/FI" + apiHeader);
                        var flagsFile = Normalize(Path.Combine(unityDir, name + ".rsp"));
                        WriteIfChanged(flagsFile, string.Join("\n", flags.Select(QuoteArgument)) + "\n");

                        var first = members[start];
                        writer.WriteStartObject();
                        writer.WritePropertyName("directory"); writer.WriteValue((string)first.Entry["directory"]);
                        writer.WritePropertyName("file"); writer.WriteValue(unityFile);
                        writer.WritePropertyName("arguments");
                        writer.WriteStartArray();
                        writer.WriteValue(first.Compiler);
                        // Order of forced includes: Definitions.h (in the .rsp), the *_API neutralizer (last in the
                        // .rsp), then the precompiled header, which needs both, then the sources.
                        foreach (var option in first.Options.Where(o => !IsPchInclude(o))) writer.WriteValue(option);
                        writer.WriteValue("@" + flagsFile);
                        foreach (var option in first.Options.Where(IsPchInclude)) writer.WriteValue(option);
                        writer.WriteValue(unityFile);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        units++;
                        start = end;
                    }
                }
                writer.WriteEndArray();
            }
            if (File.Exists(outputFile)) File.Delete(outputFile);
            File.Move(temp, outputFile);
            stats.Units = units;

            // Diagnostics: how many distinct option sets keep files apart, and what distinguishes the largest ones.
            var sets = order.Where(k => !k.StartsWith(OptOutPrefix, StringComparison.Ordinal))
                .Select(k => groups[k]).OrderByDescending(g => g.Count).ToList();
            stats.OptionSets = sets.Count;
            if (sets.Count > 1)
            {
                var reference = new HashSet<string>(sets[0][0].Options);
                foreach (var g in sets.Skip(1).Take(5))
                {
                    var options = new HashSet<string>(g[0].Options);
                    var added = options.Where(o => !reference.Contains(o)).Take(6);
                    var removed = reference.Where(o => !options.Contains(o)).Take(6);
                    stats.Differences.Add($"{g.Count} files (e.g. {Path.GetFileName(g[0].File)}): " +
                        string.Join(" ", added.Select(o => "+" + o).Concat(removed.Select(o => "-" + o))));
                }
            }
            return stats;
        }

        sealed class UnityMember
        {
            public JObject Entry;
            public string File;
            public string Compiler;
            public readonly List<string> Options = new List<string>();
            public readonly List<string> Includes = new List<string>();
            public readonly List<string> Defines = new List<string>();
            public readonly List<string> Forced = new List<string>();
        }

        /// <summary>Splits compiler arguments (expanding @files) into options, include paths, defines and forced includes.</summary>
        static void Classify(IEnumerable<string> args, UnityMember member, Dictionary<string, List<string>> responseFiles, int depth, string workingDirectory)
        {
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                string Next() => i + 1 < list.Count ? list[++i] : "";
                if (a.StartsWith("@") && depth < 8)
                {
                    // Relative @files are relative to the command's directory (UBT's convention).
                    var path = ResolveResponseFile(a.Substring(1).Trim('"'), workingDirectory, null) ?? a.Substring(1).Trim('"');
                    if (!responseFiles.TryGetValue(path, out var tokens))
                    {
                        try { tokens = SplitCommandLine(System.IO.File.ReadAllText(path)); }
                        catch (Exception) { tokens = null; }
                        responseFiles[path] = tokens;
                    }
                    if (tokens != null) { Classify(tokens, member, responseFiles, depth + 1, workingDirectory); continue; }
                    member.Options.Add(a);
                }
                else if (a == "/I" || a == "-I") member.Includes.Add("/I" + Next());
                else if (a.StartsWith("/imsvc", StringComparison.OrdinalIgnoreCase)) member.Includes.Add(a.Length > 6 ? a : "/imsvc" + Next());
                else if (a.StartsWith("/external:I", StringComparison.OrdinalIgnoreCase))
                    member.Includes.Add("/external:I" + (a.Length > 11 ? a.Substring(11) : Next()));
                else if (a.StartsWith("/I") || a.StartsWith("-I")) member.Includes.Add("/I" + a.Substring(2));
                else if (a == "/FI" || a == "-include") member.Forced.Add("/FI" + Next());
                // The PCH stays in the options (part of the grouping key): a unit never mixes precompiled headers.
                else if (IsPchInclude(a)) member.Options.Add(a);
                else if (a.StartsWith("/FI")) member.Forced.Add(a);
                else if (a == "/D" || a == "-D") member.Defines.Add("/D" + Next());
                else if (a.StartsWith("/D") || a.StartsWith("-D")) member.Defines.Add("/D" + a.Substring(2));
                else if (!IrrelevantForIndexing.IsMatch(a)) member.Options.Add(a);
            }
        }

        /// <summary>
        /// MSVC switches that cannot change how the code parses: warnings (UE sets them per module, and we pass /w
        /// anyway), optimization, debug info and code generation hardening. Dropping them lets the files of
        /// different modules share a translation unit. Language switches (/std, /GR, /EH, /Zc, /Zp, /MD, /arch,
        /// /permissive, /utf-8...) are kept.
        /// </summary>
        static readonly System.Text.RegularExpressions.Regex IrrelevantForIndexing = new System.Text.RegularExpressions.Regex(
            @"^/(W[0-4X]-?|Wall|WX-?|w[1-4]\d{4}|we\d+|wd\d+|wo\d+|external:W\d|external:templates-?|analyze.*|" +
            @"O[12bdgistxy]-?\d?|Ob\d|Oi-?|GL-?|Gy-?|Gw-?|GF-?|GS-?|Gs\d*|guard:.*|Qspectre.*|Qpar.*|Qvec.*|fp:\w+|" +
            @"Z7|Zi|ZI|Zo-?|FS|bigobj|FC|Brepro|hotpatch|RTC\w*|sdl-?|d1.*|d2.*|cgthreads\d*|diagnostics:.*|errorReport:.*|nologo|MP\d*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        // Only linkage macros (defined as DLLEXPORT/DLLIMPORT): Definitions.h also has switches named *_API that are
        // tested with #if (UE_VALIDATE_INTERNAL_API, UE_VALIDATE_EXPERIMENTAL_API) and must keep their value.
        static readonly System.Text.RegularExpressions.Regex ApiDefine = new System.Text.RegularExpressions.Regex(
            @"^\s*#\s*define\s+(\w+_API)\s+(DLLEXPORT|DLLIMPORT)\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Writes "&lt;unit&gt;.api.h", which redefines every *_API macro found in the unit's forced includes as empty.
        /// Returns its path, or null when there is nothing to neutralize.
        /// </summary>
        static string WriteApiNeutralizer(string unityDir, string name, IEnumerable<string> forcedIncludes, string directory,
            Dictionary<string, List<string>> cache)
        {
            var macros = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var include in forcedIncludes)
            {
                var path = FullPath(include, directory);
                if (!cache.TryGetValue(path, out var found))
                {
                    found = new List<string>();
                    try
                    {
                        foreach (System.Text.RegularExpressions.Match m in ApiDefine.Matches(File.ReadAllText(path))) found.Add(m.Groups[1].Value);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    cache[path] = found;
                }
                foreach (var macro in found) macros.Add(macro);
            }
            if (macros.Count == 0) return null;
            var text = new StringBuilder("// UnrealSense: linkage does not matter for indexing; avoids dllimport errors across modules\n");
            foreach (var macro in macros) text.Append("#undef ").Append(macro).Append("\n#define ").Append(macro).Append("\n");
            var header = Normalize(Path.Combine(unityDir, name + ".api.h"));
            WriteIfChanged(header, text.ToString());
            return header;
        }

        /// <summary>A forced include of a precompiled header (EngineSharedPCH.h, MyModulePrivatePCH.h...).</summary>
        static bool IsPchInclude(string arg) =>
            arg.StartsWith("/FI", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(arg.Substring(3).Trim('"')).IndexOf("PCH", StringComparison.OrdinalIgnoreCase) >= 0;

        static void WriteIfChanged(string path, string text)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        }

        const string OptOutPrefix = "<bUseUnity=false>\u0001";

        /// <summary>What <see cref="BuildUnity"/> did; logged so a poor grouping can be diagnosed remotely.</summary>
        public sealed class UnityStats
        {
            readonly Dictionary<string, (int Count, string Example)> excluded = new Dictionary<string, (int, string)>();

            public int Units { get; internal set; }
            public int Grouped { get; internal set; }
            public int GroupedOptOut { get; internal set; }
            /// <summary>Distinct compiler option sets (each needs its own units).</summary>
            public int OptionSets { get; internal set; }
            /// <summary>How the largest option sets differ from the most common one.</summary>
            public List<string> Differences { get; } = new List<string>();

            internal void Exclude(string reason, string example)
            {
                excluded.TryGetValue(reason, out var current);
                excluded[reason] = (current.Count + 1, current.Example ?? example);
            }

            public override string ToString() =>
                $"{Grouped:N0} files in unity units ({GroupedOptOut:N0} from bUseUnity = false modules), {Units:N0} translation units, " +
                $"{OptionSets:N0} distinct option sets" +
                string.Concat(excluded.Select(e => $"; {e.Value.Count:N0} alone ({e.Key}, e.g. {e.Value.Example})")) +
                string.Concat(Differences.Select(d => "\n    option set: " + d));
        }

        static string FullPath(string path, string directory)
        {
            try
            {
                path = path.Trim('"').Replace('/', '\\');
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(directory)) path = Path.Combine(directory.Replace('/', '\\'), path);
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 16 * 1024; }
        }

        /// <summary>Module folder name for readable unity file names ("Engine", "GameplayAbilities"...); null for generated files.</summary>
        static string ModuleName(JObject entry)
        {
            var parts = Normalize((string)entry["file"]).Split('/');
            int i = Array.FindLastIndex(parts, p => p == "Private" || p == "Public" || p == "Classes" || p == "Internal");
            return i > 0 ? new string(parts[i - 1].Where(char.IsLetterOrDigit).ToArray()) : null;
        }

        /// <summary>True when the nearest *.Build.cs above the file sets bUseUnity = false.</summary>
        static bool ModuleOptsOutOfUnity(string file, Dictionary<string, bool> cache)
        {
            var dir = Path.GetDirectoryName(file.Replace('/', '\\'));
            var visited = new List<string>();
            bool result = false;
            while (!string.IsNullOrEmpty(dir))
            {
                if (cache.TryGetValue(dir, out var known)) { result = known; break; }
                visited.Add(dir);
                string[] rules;
                try { rules = Directory.GetFiles(dir, "*.Build.cs"); }
                catch (Exception) { rules = Array.Empty<string>(); }
                if (rules.Length > 0)
                {
                    try { result = System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(rules[0]), @"\bbUseUnity\s*=\s*false"); }
                    catch (Exception) { }
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
            foreach (var v in visited) cache[v] = result;
            return result;
        }

        /// <summary>Makes the project-only or the full variant the compile_commands.json clangd reads.</summary>
        public static void Activate(string directory, bool fullDatabase)
        {
            var source = Path.Combine(directory, fullDatabase ? FullVariant : ProjectVariant);
            var target = Path.Combine(directory, "compile_commands.json");
            var temp = target + ".tmp";
            File.Copy(source, temp, overwrite: true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }

        /// <summary>Translation units in the (project, full) unity variants, or null when unknown.</summary>
        public static (int Project, int Full)? ReadCounts(string directory)
        {
            try
            {
                var lines = File.ReadAllLines(Path.Combine(directory, StampFile));
                int Get(string key) => int.Parse(lines.First(l => l.StartsWith(key + "=")).Substring(key.Length + 1));
                return (Get("project"), Get("full"));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when the databases in <paramref name="directory"/> were written by this format/settings.</summary>
        public static bool IsCurrentFormat(string directory, string engineExclusions, bool acrossModules = true)
        {
            try { return File.ReadLines(Path.Combine(directory, StampFile)).FirstOrDefault()?.Trim() == Stamp(engineExclusions, acrossModules); }
            catch (IOException) { return false; }
        }

        static string Stamp(string engineExclusions, bool acrossModules = true) =>
            $"format={FormatVersion};modules={(acrossModules ? "merged" : "separate")};generated={(IndexGeneratedCode ? "yes" : "no")};exclude={string.Join(";", ParseExclusions(engineExclusions))}";

        /// <summary>
        /// Whether UnrealHeaderTool's generated sources (*.gen.cpp: class registration, UFUNCTION exec thunks, property
        /// offsets) are indexed. Off by default: Find Usages hides their references anyway, they are a large share of the
        /// code to parse, and clang rejects some of their MSVC-only constructs. The .generated.h headers are always parsed
        /// (the classes' own headers include them).
        /// </summary>
        public static bool IndexGeneratedCode { get; set; }

        static bool IsGeneratedSource(string file) => file.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase);

        static List<string> ParseExclusions(string exclusions) =>
            (exclusions ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Normalize(x.Trim())).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        internal static string Normalize(string path) => path.Replace('\\', '/');

        static int RunTool(string fileName, string args, string workingDirectory, Action<string> log)
        {
            var info = new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            };
            using (var process = Process.Start(info))
            {
                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        /// <summary>One sanitized output: the entries whose file passes <see cref="Include"/>.</summary>
        public sealed class DatabaseVariant
        {
            public DatabaseVariant(string path, Func<string, bool> include)
            {
                Path = path;
                Include = include;
            }

            public string Path { get; }
            public Func<string, bool> Include { get; }
            public int Count { get; internal set; }
        }

        public static int Sanitize(string inputFile, string outputFile) => Sanitize(inputFile, outputFile, null, out _);

        public static int Sanitize(string inputFile, string outputFile, Func<string, bool> includeFile, out int skipped)
        {
            var variant = new DatabaseVariant(outputFile, includeFile ?? (_ => true));
            int total = Sanitize(inputFile, new[] { variant });
            skipped = total - variant.Count;
            return variant.Count;
        }

        /// <summary>
        /// Writes clangd-friendly databases in one pass over UBT's output. Each entry's own response file is
        /// inlined, but the large per-module shared response files (hundreds of /I and /D switches) are cleaned
        /// once into "rsp/" and referenced with @file, which clangd expands; inlining them made databases of big
        /// projects grow past a gigabyte. Returns the number of entries read.
        /// </summary>
        /// <param name="pchFor">Precompiled header UBT would force-include for a source file (see <see cref="PchResolver"/>).</param>
        public static int Sanitize(string inputFile, IReadOnlyList<DatabaseVariant> variants, Func<string, string> pchFor = null)
        {
            var outputDir = System.IO.Path.GetDirectoryName(variants[0].Path);
            var rspDir = System.IO.Path.Combine(outputDir, "rsp");
            if (Directory.Exists(rspDir)) Directory.Delete(rspDir, recursive: true);
            Directory.CreateDirectory(rspDir);
            var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int total = 0;
            var writers = variants.Select(v => new JsonTextWriter(new StreamWriter(v.Path + ".tmp")) { Formatting = Formatting.None }).ToList();
            try
            {
                foreach (var w in writers) w.WriteStartArray();
                using (var reader = new JsonTextReader(new StreamReader(inputFile)))
                {
                    var serializer = new JsonSerializer();
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.StartObject) continue;
                        var entry = serializer.Deserialize<JObject>(reader);
                        var file = (string)entry["file"];
                        var directory = (string)entry["directory"];
                        if (file == null) continue;
                        total++;
                        var targets = Enumerable.Range(0, variants.Count).Where(i => variants[i].Include(file)).ToList();
                        if (targets.Count == 0) continue;

                        List<string> argv;
                        if (entry["arguments"] is JArray arr) argv = arr.Select(x => (string)x).ToList();
                        else argv = SplitCommandLine((string)entry["command"] ?? "");
                        if (argv.Count == 0) continue;

                        var expanded = new List<string> { argv[0] };
                        ExpandResponseFiles(argv.Skip(1), directory, expanded, 0, rspDir, shared);
                        var cleaned = Clean(expanded, keepFirst: true);
                        // Machine-independent compiler name (clangd only needs it to pick clang-cl mode; it finds
                        // MSVC itself), so an index built on another machine with another VS edition stays valid.
                        if (Path.GetFileName(cleaned[0].Trim('"')).Equals("cl.exe", StringComparison.OrdinalIgnoreCase)) cleaned[0] = "cl.exe";
                        // Quiet, error-tolerant parsing: we use clangd for navigation, not diagnostics.
                        cleaned.Insert(1, "/w");
                        cleaned.Insert(2, "-Wno-everything");
                        cleaned.Insert(3, "-ferror-limit=0");
                        // UBT's database is generated with -NoPCH: add the PCH the real build force-includes, right
                        // after the module's Definitions.h, so code relying on it for some includes still compiles.
                        var pch = pchFor?.Invoke(FullPath(file, directory));
                        if (pch != null)
                        {
                            int definitions = cleaned.FindLastIndex(x => x.StartsWith("/FI", StringComparison.OrdinalIgnoreCase)
                                                                        && x.EndsWith("Definitions.h", StringComparison.OrdinalIgnoreCase));
                            cleaned.Insert(definitions >= 0 ? definitions + 1 : cleaned.Count, "/FI" + pch);
                        }

                        foreach (var i in targets)
                        {
                            var writer = writers[i];
                            writer.WriteStartObject();
                            writer.WritePropertyName("directory"); writer.WriteValue(directory);
                            writer.WritePropertyName("file"); writer.WriteValue(file);
                            writer.WritePropertyName("arguments");
                            writer.WriteStartArray();
                            foreach (var x in cleaned) writer.WriteValue(x);
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                            variants[i].Count++;
                        }
                    }
                }
                foreach (var w in writers) w.WriteEndArray();
            }
            finally
            {
                foreach (var w in writers) w.Close();
            }
            foreach (var v in variants)
            {
                if (File.Exists(v.Path)) File.Delete(v.Path);
                File.Move(v.Path + ".tmp", v.Path);
            }
            return total;
        }

        /// <summary>
        /// Inlines the arguments of response files referenced directly by the command (depth 0); nested ones
        /// (shared by every file of a module) are written once, cleaned, and kept as @references.
        /// </summary>
        static void ExpandResponseFiles(IEnumerable<string> args, string directory, List<string> output, int depth, string rspDir, Dictionary<string, string> shared) =>
            ExpandResponseFiles(args, directory, directory, output, depth, rspDir, shared);

        /// <param name="workingDirectory">The command's directory: UBT writes relative @files (engine modules:
        /// "@../Intermediate/.../Engine.Shared.rsp") relative to it, not to the response file that mentions them.</param>
        /// <param name="fileDirectory">Folder of the response file being read (fallback for relative paths).</param>
        static void ExpandResponseFiles(IEnumerable<string> args, string workingDirectory, string fileDirectory, List<string> output, int depth, string rspDir, Dictionary<string, string> shared)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("@") && depth < 8)
                {
                    var path = ResolveResponseFile(arg.Substring(1).Trim('"'), workingDirectory, fileDirectory);
                    if (path != null)
                    {
                        if (depth == 0)
                            ExpandResponseFiles(SplitCommandLine(File.ReadAllText(path)), workingDirectory, Path.GetDirectoryName(path), output, depth + 1, rspDir, shared);
                        else
                            output.Add("@" + SharedResponseFile(path, workingDirectory, rspDir, shared));
                        continue;
                    }
                }
                output.Add(arg);
            }
        }

        static string ResolveResponseFile(string path, string workingDirectory, string fileDirectory)
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
            foreach (var baseDir in new[] { workingDirectory, fileDirectory })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                var candidate = Path.GetFullPath(Path.Combine(baseDir.Replace('/', '\\'), path.Replace('/', '\\')));
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static string SharedResponseFile(string source, string workingDirectory, string rspDir, Dictionary<string, string> shared)
        {
            var key = Path.GetFullPath(source);
            if (shared.TryGetValue(key, out var existing)) return existing;
            uint hash = 2166136261;
            foreach (var c in key.ToUpperInvariant()) hash = (hash ^ c) * 16777619;
            var target = Path.Combine(rspDir, $"{Path.GetFileNameWithoutExtension(source)}-{hash:x8}.rsp");
            shared[key] = target; // registered before recursing: guards against cycles

            var inner = new List<string>();
            ExpandResponseFiles(SplitCommandLine(File.ReadAllText(source)), workingDirectory, Path.GetDirectoryName(source), inner, 1, rspDir, shared);
            var cleaned = Clean(inner, keepFirst: false);
            File.WriteAllLines(target, cleaned.Select(x => x.StartsWith("@") ? QuoteArgument(x.Substring(1)).Insert(0, "@") : QuoteArgument(x)));
            return target;
        }

        /// <summary>Quotes an argument for Windows command-line tokenization (used by clangd for @files).</summary>
        public static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static List<string> Clean(List<string> args, bool keepFirst)
        {
            var result = new List<string>(args.Count);
            for (int i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (i == 0 && keepFirst) { result.Add(a); continue; }
                if (DropWithValue.Contains(a)) { i++; continue; }
                if (DropExact.Contains(a) || DropPrefixes.Any(p => a.StartsWith(p, StringComparison.Ordinal))) continue;

                // UBT passes the MSVC toolset and Windows SDK headers as ordinary (/I) or external (/external:I)
                // include folders, which clang searches before its own intrinsics: <xmmintrin.h> then comes from MSVC
                // (__m128 as a union) while Unreal's code, seeing __clang__, expects clang's vector types, giving an
                // error in nearly every file using Unreal's vector math. /imsvc = system folders as if from %INCLUDE%,
                // searched after clang's builtin headers; same toolset, right lookup order.
                string includeDir = null;
                if ((a == "/I" || a == "-I" || a == "/external:I") && i + 1 < args.Count && IsSystemIncludeDir(args[i + 1])) includeDir = args[++i];
                else if (a.StartsWith("/external:I", StringComparison.Ordinal) && a.Length > 11 && IsSystemIncludeDir(a.Substring(11))) includeDir = a.Substring(11);
                else if ((a.StartsWith("/I", StringComparison.Ordinal) || a.StartsWith("-I", StringComparison.Ordinal)) && a.Length > 2 && IsSystemIncludeDir(a.Substring(2))) includeDir = a.Substring(2);
                result.Add(includeDir != null ? "/imsvc" + includeDir : a);
            }
            return result;
        }

        /// <summary>Include folders of the MSVC toolset or the Windows SDK.</summary>
        public static bool IsSystemIncludeDir(string path)
        {
            var p = path.Trim('"').Replace('\\', '/');
            return p.IndexOf("/VC/Tools/MSVC/", StringComparison.OrdinalIgnoreCase) >= 0
                   || p.IndexOf("/VC/Auxiliary/VS/", StringComparison.OrdinalIgnoreCase) >= 0
                   || p.IndexOf("/Windows Kits/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Windows command-line splitting (quotes, backslash-quote escapes, newlines as separators).</summary>
        public static List<string> SplitCommandLine(string text)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false, hasToken = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    // Windows rules: backslashes are literal unless they precede a quote; 2n+1 before a quote
                    // produce n backslashes and a literal quote, 2n produce n backslashes and a delimiter.
                    int n = 0;
                    while (i < text.Length && text[i] == '\\') { n++; i++; }
                    if (i < text.Length && text[i] == '"')
                    {
                        current.Append('\\', n / 2);
                        if (n % 2 == 1) current.Append('"');
                        else inQuotes = !inQuotes;
                    }
                    else
                    {
                        current.Append('\\', n);
                        i--;
                    }
                    hasToken = true;
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                }
                else if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (hasToken) result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }
            if (hasToken) result.Add(current.ToString());
            return result;
        }
    }
}
