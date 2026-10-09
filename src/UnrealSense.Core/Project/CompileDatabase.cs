using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Project
{
    /// <summary>
    /// The per-file compile database the own C++ indexer reads (include directories, /D defines, forced Definitions.h):
    /// UnrealBuildTool's ClangDatabase mode writes MSVC command lines with nested response files and MSVC-only switches,
    /// so each entry's own @file is inlined, the shared per-module ones are cleaned once into "rsp/" and kept as
    /// @references, and output/PCH/logging switches are dropped.
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

        /// <summary>%LOCALAPPDATA%\UnrealSense\Cache (UNREALSENSE_CACHE overrides it, for experiments in a separate root).</summary>
        public static string CacheRoot
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable("UNREALSENSE_CACHE");
                return string.IsNullOrWhiteSpace(overridden)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "Cache")
                    : overridden.Trim().Trim('"');
            }
        }

        /// <summary>Folder of the project's cache (one per .uproject path).</summary>
        public static string GetProjectCacheDirectory(UnrealProject project)
        {
            uint hash = 2166136261;
            foreach (var c in project.UProjectPath.ToUpperInvariant())
                hash = (hash ^ c) * 16777619;
            return Path.Combine(CacheRoot, $"{project.Name}-{hash:x8}");
        }

        /// <summary>Folder of the compile database (the own indexer looks for it in &lt;cache&gt;\&lt;Project&gt;-*\compiledb).</summary>
        public static string GetOutputDirectory(UnrealProject project) => Path.Combine(GetProjectCacheDirectory(project), "compiledb");

        /// <summary>Folder used by versions up to 0.3.18 (with the clangd index); its UBT output is reused once.</summary>
        static string LegacyDirectory(UnrealProject project) => Path.Combine(GetProjectCacheDirectory(project), "clangd");

        /// <summary>The editor target (XxxEditor.Target.cs) if present, else the first game target.</summary>
        public static string FindTarget(UnrealProject project)
        {
            if (!Directory.Exists(project.SourceDirectory)) return project.Name + "Editor";
            var targets = Directory.GetFiles(project.SourceDirectory, "*.Target.cs").Select(f => Path.GetFileName(f).Replace(".Target.cs", "")).ToList();
            return targets.FirstOrDefault(t => t.EndsWith("Editor", StringComparison.OrdinalIgnoreCase))
                   ?? targets.FirstOrDefault() ?? project.Name + "Editor";
        }

        /// <summary>One command per source file: what the own indexer reads.</summary>
        public const string FilesVariant = "compile_commands.files.json";

        public static string FilesDatabase(UnrealProject project) => Path.Combine(GetOutputDirectory(project), FilesVariant);

        public static string RawDatabase(UnrealProject project) => Path.Combine(GetOutputDirectory(project), "ubt", "compile_commands.json");

        const string NotInTargetFile = "sources-not-in-target.txt";

        /// <summary>
        /// Makes sure the per-file database is there and current: rewritten from UBT's last output when only that exists,
        /// UnrealBuildTool run again when there is none, the build rules changed or a project source file is missing from
        /// it. Returns the path of <see cref="FilesVariant"/>. Slow only when UBT has to run (about a minute).
        /// </summary>
        public static string Ensure(UnrealProject project, Action<string> log)
        {
            var files = FilesDatabase(project);
            AdoptLegacyOutput(project, log);
            if (!IsStale(project, out var reason))
            {
                if (File.Exists(files)) return files;
                if (Resanitize(project, log)) return files;
                reason = "no database yet";
            }
            log?.Invoke("Compile database out of date: " + reason);
            Generate(project, log);
            return files;
        }

        /// <summary>Copies the UBT output written by an older version (clangd folder), so the first start needs no UBT run.</summary>
        static void AdoptLegacyOutput(UnrealProject project, Action<string> log)
        {
            try
            {
                var raw = RawDatabase(project);
                var legacy = Path.Combine(LegacyDirectory(project), "ubt", "compile_commands.json");
                if (File.Exists(raw) || !File.Exists(legacy)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(raw));
                File.Copy(legacy, raw);
                // Keep its timestamp: staleness is judged against the build rules' write times.
                File.SetLastWriteTimeUtc(raw, File.GetLastWriteTimeUtc(legacy));
                var notInTarget = Path.Combine(LegacyDirectory(project), NotInTargetFile);
                if (File.Exists(notInTarget)) File.Copy(notInTarget, Path.Combine(GetOutputDirectory(project), NotInTargetFile), overwrite: true);
                log?.Invoke($"Compile database: reusing UnrealBuildTool's output of the old clangd folder {LegacyDirectory(project)} " +
                            "(that folder, with clangd's index, is no longer used and can be deleted)");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("Compile database: old clangd folder not reusable: " + ex.Message);
            }
        }

        /// <summary>True when UnrealBuildTool must run again (see <see cref="Ensure"/>).</summary>
        public static bool IsStale(UnrealProject project, out string reason)
        {
            reason = null;
            var raw = RawDatabase(project);
            if (!File.Exists(raw)) { reason = "no database yet"; return true; }
            var stamp = File.GetLastWriteTimeUtc(raw);
            if (File.GetLastWriteTimeUtc(project.UProjectPath) > stamp) { reason = Path.GetFileName(project.UProjectPath) + " changed"; return true; }
            var rules = project.AllModules.Select(m => m.BuildCsPath)
                .Concat(Directory.Exists(project.SourceDirectory) ? Directory.GetFiles(project.SourceDirectory, "*.Target.cs") : Array.Empty<string>());
            var changedRules = rules.FirstOrDefault(r => File.Exists(r) && File.GetLastWriteTimeUtc(r) > stamp);
            if (changedRules != null) { reason = changedRules + " changed"; return true; }

            // New source files are not in the database yet (files UBT left out last time, e.g. of a plugin that is not part
            // of the target, are ignored, or UBT would run on every start).
            var files = FilesDatabase(project);
            if (!File.Exists(files)) return false; // rewritten from the raw output, no UBT run
            var known = KnownSourceNames(files);
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ignoreFile = Path.Combine(GetOutputDirectory(project), NotInTargetFile);
            if (File.Exists(ignoreFile)) foreach (var line in File.ReadAllLines(ignoreFile)) ignored.Add(line.Trim());
            var newSource = ProjectSources(project).FirstOrDefault(f => !known.Contains(Path.GetFileName(f)) && !ignored.Contains(f));
            if (newSource != null) { reason = "new source file " + newSource; return true; }
            return false;
        }

        static IEnumerable<string> ProjectSources(UnrealProject project) =>
            project.AllModules.Where(m => Directory.Exists(m.Directory))
                .SelectMany(m => Directory.EnumerateFiles(m.Directory, "*.cpp", SearchOption.AllDirectories));

        static readonly Regex FileEntry = new Regex("\"file\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        static HashSet<string> KnownSourceNames(string filesDatabase)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in FileEntry.Matches(File.ReadAllText(filesDatabase)))
                known.Add(Path.GetFileName(m.Groups[1].Value.Replace("\\\\", "\\")));
            return known;
        }

        /// <summary>Runs UBT -mode=GenerateClangDatabase and writes the per-file database. Returns its directory.</summary>
        public static string Generate(UnrealProject project, Action<string> log)
        {
            var engine = project.Engine ?? throw new InvalidOperationException("Engine not found for " + project.UProjectPath);
            var output = GetOutputDirectory(project);
            var raw = Path.Combine(output, "ubt");
            Directory.CreateDirectory(raw);

            var target = FindTarget(project);
            // GenerateClangDatabase defaults to the Clang toolchain; we want the MSVC command lines the project really
            // builds with (its include paths and definitions).
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

            if (!Resanitize(project, log))
                throw new FileNotFoundException("UBT did not produce compile_commands.json", RawDatabase(project));
            RememberSourcesNotInTarget(project, log);
            return output;
        }

        /// <summary>Rewrites the per-file database from UBT's last output (no UBT run). False if UBT never ran.</summary>
        public static bool Resanitize(UnrealProject project, Action<string> log)
        {
            var rawFile = RawDatabase(project);
            if (!File.Exists(rawFile)) return false;
            var sw = Stopwatch.StartNew();
            var files = FilesDatabase(project);
            int count = Sanitize(rawFile, files);
            log?.Invoke($"compile database: {count:N0} source files in {sw.Elapsed.TotalSeconds:F1}s -> {files}");
            return true;
        }

        /// <summary>After a UBT run, remembers the project sources UBT did not include (not part of the target).</summary>
        static void RememberSourcesNotInTarget(UnrealProject project, Action<string> log)
        {
            try
            {
                var known = KnownSourceNames(FilesDatabase(project));
                var missing = ProjectSources(project).Where(f => !known.Contains(Path.GetFileName(f))).ToList();
                File.WriteAllLines(Path.Combine(GetOutputDirectory(project), NotInTargetFile), missing);
                if (missing.Count > 0) log?.Invoke($"{missing.Count} project source file(s) are not part of the build target (e.g. {missing[0]}); they will not trigger a new UBT run.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("Listing sources outside the target failed: " + ex.Message);
            }
        }

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

        public static int Sanitize(string inputFile, string outputFile) => Sanitize(inputFile, outputFile, null, out _);

        /// <summary>
        /// Writes the cleaned per-file database in one pass over UBT's output. Each entry's own response file is inlined,
        /// but the large per-module shared response files (hundreds of /I and /D switches) are cleaned once into "rsp/" and
        /// referenced with @file; inlining them made databases of big projects grow past a gigabyte. Returns the number of
        /// entries written.
        /// </summary>
        public static int Sanitize(string inputFile, string outputFile, Func<string, bool> includeFile, out int skipped)
        {
            var outputDir = Path.GetDirectoryName(outputFile);
            var rspDir = Path.Combine(outputDir, "rsp");
            if (Directory.Exists(rspDir)) Directory.Delete(rspDir, recursive: true);
            Directory.CreateDirectory(rspDir);
            var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int total = 0, written = 0;
            using (var writer = new JsonTextWriter(new StreamWriter(outputFile + ".tmp")) { Formatting = Formatting.None })
            {
                writer.WriteStartArray();
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
                        if (includeFile != null && !includeFile(file)) continue;

                        List<string> argv;
                        if (entry["arguments"] is JArray arr) argv = arr.Select(x => (string)x).ToList();
                        else argv = SplitCommandLine((string)entry["command"] ?? "");
                        if (argv.Count == 0) continue;

                        var expanded = new List<string> { argv[0] };
                        ExpandResponseFiles(argv.Skip(1), directory, directory, expanded, 0, rspDir, shared);
                        var cleaned = Clean(expanded, keepFirst: true);
                        // Machine-independent compiler name.
                        if (Path.GetFileName(cleaned[0].Trim('"')).Equals("cl.exe", StringComparison.OrdinalIgnoreCase)) cleaned[0] = "cl.exe";

                        writer.WriteStartObject();
                        writer.WritePropertyName("directory"); writer.WriteValue(directory);
                        writer.WritePropertyName("file"); writer.WriteValue(file);
                        writer.WritePropertyName("arguments");
                        writer.WriteStartArray();
                        foreach (var x in cleaned) writer.WriteValue(x);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        written++;
                    }
                }
                writer.WriteEndArray();
            }
            if (File.Exists(outputFile)) File.Delete(outputFile);
            File.Move(outputFile + ".tmp", outputFile);
            skipped = total - written;
            return written;
        }

        /// <summary>
        /// Inlines the arguments of response files referenced directly by the command (depth 0); nested ones (shared by
        /// every file of a module) are written once, cleaned, and kept as @references.
        /// </summary>
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

        /// <summary>Quotes an argument for Windows command-line tokenization (response files).</summary>
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

                // The MSVC toolset and Windows SDK folders become /imsvc (system folders, searched last), as the own
                // indexer's accuracy was measured with: its predefined environment is clang-cl's, which finds the
                // compiler intrinsics headers before the toolset's.
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
