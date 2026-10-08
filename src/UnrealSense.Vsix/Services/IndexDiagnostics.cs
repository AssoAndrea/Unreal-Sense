using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnrealSense.Clang;
using UnrealSense.Extension.Options;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Explains why clangd reported "Failed to compile" for an indexed translation unit: re-parses it with
    /// clangd --check (bodies included) and logs the errors grouped by kind, with the source file they come from.
    /// </summary>
    internal static class IndexDiagnostics
    {
        static readonly ConcurrentDictionary<string, bool> failedUnits = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex Diagnostic = new Regex(@"^E\[[^\]]*\]\s+\[(?<code>[^\]]+)\]\s+Line\s+(?<line>\d+):\s+(?<message>.*)$");

        public static int Count => failedUnits.Count;

        public static bool HadErrors(string unit) => failedUnits.ContainsKey(unit.Replace('/', '\\'));

        // Unit -> the sources its errors are in, once the unit has been checked.
        static readonly ConcurrentDictionary<string, HashSet<string>> errorSources = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the index of <paramref name="source"/> (part of <paramref name="unit"/>) may miss references: its unit had
        /// errors and, once checked, the errors are in this very source. Errors stay local (e.g. one line of generated
        /// code in Module.X.gen.cpp), so the other sources of the unit are indexed completely.
        /// </summary>
        public static bool IsUntrusted(string source, string unit)
        {
            if (unit == null || !HadErrors(unit)) return false;
            if (!errorSources.TryGetValue(unit.Replace('/', '\\'), out var sources)) return true;   // not checked yet
            return sources.Contains(Path.GetFullPath(source));
        }

        /// <summary>Errors of the units clangd could not compile cleanly, one per line as soon as each is found.</summary>
        public static readonly string ErrorsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "Logs", "index-errors.log");
        static readonly object errorsLock = new object();
        // Each check is one more full parse next to the indexing threads: two at a time.
        static readonly System.Threading.SemaphoreSlim checkSlots = new System.Threading.SemaphoreSlim(2);
        static readonly ConcurrentDictionary<string, Dictionary<string, JObject>> entriesByDirectory = new ConcurrentDictionary<string, Dictionary<string, JObject>>(StringComparer.OrdinalIgnoreCase);

        static void AppendError(string line)
        {
            lock (errorsLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ErrorsFile));
                    File.AppendAllText(ErrorsFile, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Starts a new indexing session in the errors file (emptied when the whole index is rebuilt).</summary>
        public static void BeginSession(string compileCommandsDirectory, bool fromScratch)
        {
            failedUnits.Clear();
            errorSources.Clear();
            entriesByDirectory.TryRemove(compileCommandsDirectory ?? "", out _);
            lock (errorsLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ErrorsFile));
                    var header = $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} C++ indexing started{(fromScratch ? " from scratch" : "")}: compile errors of each unit are written here as they are found{Environment.NewLine}";
                    if (fromScratch) File.WriteAllText(ErrorsFile, header);
                    else File.AppendAllText(ErrorsFile, header);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>A unit that clangd indexed with errors: checked in the background right away, errors go to <see cref="ErrorsFile"/>.</summary>
        public static void Record(string unit)
        {
            unit = unit.Replace('/', '\\');
            if (!failedUnits.TryAdd(unit, true)) return;
            var directory = ClangdService.CompileCommandsDirectory;
            if (directory == null) return;
            Task.Run(() => CheckQueuedAsync(directory, unit)).FireAndForgetLogged("index diagnosis");
        }

        static async Task CheckQueuedAsync(string compileCommandsDirectory, string unit)
        {
            await checkSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var entries = entriesByDirectory.GetOrAdd(compileCommandsDirectory, LoadEntries);
                if (!entries.TryGetValue(unit, out var entry))
                {
                    AppendError($"{Path.GetFileName(unit)}: not in the current compile database, not checked");
                    return;
                }
                var clangd = ClangdClient.FindClangd(General.Instance.ClangdPath);
                var report = await Task.Run(() => Check(clangd, compileCommandsDirectory, unit, entry)).ConfigureAwait(false);
                Log.Write("Index errors: " + report[0] + $" (details in {ErrorsFile})");
            }
            catch (Exception ex)
            {
                Log.Error("Index errors check", ex);
            }
            finally
            {
                checkSlots.Release();
            }
        }

        /// <summary>The menu command: checks again every unit that reported errors and logs a summary per unit.</summary>
        public static async Task RunAsync(string compileCommandsDirectory)
        {
            if (failedUnits.IsEmpty)
            {
                Log.Write("Diagnose C++ index: no translation unit has reported compile errors so far.");
                return;
            }
            var units = failedUnits.Keys.ToList();
            entriesByDirectory.TryRemove(compileCommandsDirectory, out _);
            AppendError($"===== checking again {units.Count} unit(s) with errors");
            Log.Write($"Diagnose C++ index: checking {units.Count} unit(s); errors are written as found to {ErrorsFile}");
            await Task.WhenAll(units.Select(unit => CheckQueuedAsync(compileCommandsDirectory, unit))).ConfigureAwait(false);
            Log.Write($"Diagnose C++ index: done, details in {ErrorsFile}");
        }

        static Dictionary<string, JObject> LoadEntries(string directory)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "compile_commands.json", CompileDatabase.FilesVariant })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                foreach (var e in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                {
                    var file = ((string)e["file"])?.Replace('/', '\\');
                    if (file != null && !result.ContainsKey(file)) result[file] = e;
                }
            }
            return result;
        }

        sealed class CheckError
        {
            public string Code;
            public int Line;        // 1-based, in the checked file's own text
            public string Message;
            public string IncludeSpelling;  // for "in included file" errors: what the #include line names
        }

        /// <summary>
        /// Runs clangd --check on <paramref name="text"/> compiled with <paramref name="entry"/>'s command. A declaration is
        /// put before the text so clangd does not treat its #includes as a preamble (where bodies are skipped).
        /// </summary>
        static List<CheckError> RunCheck(string clangd, string diagDir, JObject entry, string text, string sourceDirectory)
        {
            Directory.CreateDirectory(diagDir);
            var copy = Path.Combine(diagDir, "check.cpp");
            File.WriteAllText(copy, "static int UnrealSenseIndexCheck;\n" + text);
            var lines = text.Split('\n');

            var args = entry["arguments"].Select(a => (string)a).ToList();
            var file = (string)entry["file"];
            for (int i = 0; i < args.Count; i++)
                if (string.Equals(args[i].Replace('\\', '/'), file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) args[i] = copy.Replace('\\', '/');
            // The copy lives elsewhere: quoted #includes of the original must still find their neighbours.
            if (sourceDirectory != null) args.Insert(1, "/I" + sourceDirectory.Replace('\\', '/'));
            var db = new JArray(new JObject { ["directory"] = entry["directory"], ["file"] = copy.Replace('\\', '/'), ["arguments"] = new JArray(args) });
            File.WriteAllText(Path.Combine(diagDir, "compile_commands.json"), db.ToString());

            var info = new ProcessStartInfo(clangd, $"\"--compile-commands-dir={diagDir}\" \"--check={copy}\" --log=error")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            var errors = new List<CheckError>();
            using (var process = Process.Start(info))
            {
                process.OutputDataReceived += (s, e) => { };
                process.BeginOutputReadLine();
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    var m = Diagnostic.Match(line);
                    if (!m.Success) continue;
                    // Line 1 is the added declaration.
                    int lineNumber = int.Parse(m.Groups["line"].Value) - 1;
                    var message = m.Groups["message"].Value.Trim();
                    string spelling = null;
                    if (message.StartsWith("in included file", StringComparison.OrdinalIgnoreCase) && lineNumber >= 1 && lineNumber <= lines.Length)
                    {
                        var include = Regex.Match(lines[lineNumber - 1], "^\\s*#\\s*include\\s*[\"<]([^\">]+)[\">]");
                        if (include.Success) spelling = include.Groups[1].Value;
                    }
                    errors.Add(new CheckError { Code = m.Groups["code"].Value, Line = lineNumber, Message = message, IncludeSpelling = spelling });
                }
                if (!process.WaitForExit(20 * 60 * 1000)) { try { process.Kill(); } catch (Exception) { } }
            }
            return errors;
        }

        /// <summary>
        /// Checks a unit, writes its errors to the errors file, then follows each kind of error that comes from an
        /// included file down the #include chain (checking that file's own text, and so on) until clangd reports it
        /// in the file's own lines: the errors file then shows the exact file, line and code.
        /// </summary>
        static List<string> Check(string clangd, string compileCommandsDirectory, string unit, JObject entry)
        {
            var unitName = Path.GetFileName(unit);
            var diagRoot = Path.Combine(compileCommandsDirectory, "diagnostics", Path.GetFileNameWithoutExtension(unit));
            var body = unit.IndexOf("\\unity\\", StringComparison.OrdinalIgnoreCase) >= 0
                ? File.ReadAllText(unit)
                : $"#include \"{unit.Replace('\\', '/')}\"\n";
            var errors = RunCheck(clangd, diagRoot, entry, body, null);
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in errors)
                sources.Add(e.IncludeSpelling != null ? (ResolveInclude(e.IncludeSpelling, null, (string)entry["directory"]) ?? unit) : unit);
            errorSources[unit] = sources;
            foreach (var e in errors)
                AppendError($"{unitName} | {(e.IncludeSpelling != null ? Path.GetFileName(e.IncludeSpelling) : unitName)} | [{e.Code}] {e.Message}");
            AppendError($"{unitName}: {errors.Count} error(s)");

            // One drill-down per kind of error (the same generated-code error repeats in every module).
            int followed = 0;
            foreach (var group in errors.Where(e => e.IncludeSpelling != null).GroupBy(e => e.Code + Regex.Replace(e.Message, "'[^']*'", "''")))
            {
                if (followed++ >= 3) break;
                var e = group.First();
                var current = ResolveInclude(e.IncludeSpelling, null, (string)entry["directory"]);
                string message = e.Message;
                for (int depth = 1; depth <= 4 && current != null; depth++)
                {
                    string text;
                    try { text = File.ReadAllText(current); }
                    catch (IOException) { break; }
                    var found = RunCheck(clangd, Path.Combine(diagRoot, "level" + depth), entry, text, Path.GetDirectoryName(current))
                        .FirstOrDefault(x => x.Code == e.Code);
                    if (found == null)
                    {
                        AppendError($"    {Path.GetFileName(current)}: [{e.Code}] not reproduced when checked on its own");
                        break;
                    }
                    if (found.IncludeSpelling == null)
                    {
                        var lines = text.Split('\n');
                        var sourceLine = found.Line >= 1 && found.Line <= lines.Length ? lines[found.Line - 1].Trim() : "";
                        AppendError($"    exact location: {current}:{found.Line}: [{found.Code}] {found.Message}");
                        AppendError($"        {sourceLine}");
                        // Context: the lines around it (generated code is not in the editor).
                        for (int i = Math.Max(1, found.Line - 3); i <= Math.Min(lines.Length, found.Line + 3); i++)
                            if (i != found.Line) AppendError($"        {i,6}: {lines[i - 1].TrimEnd()}");
                        break;
                    }
                    AppendError($"    -> {Path.GetFileName(current)} line {found.Line} includes {found.IncludeSpelling}");
                    current = ResolveInclude(found.IncludeSpelling, Path.GetDirectoryName(current), (string)entry["directory"]);
                    if (current == null) AppendError($"    could not locate {found.IncludeSpelling}");
                }
            }

            var report = new List<string> { $"{errors.Count} error(s) in {unitName}" };
            return report;
        }

        /// <summary>
        /// An #include spelling as a file path: absolute, next to the includer, or relative to the compiler's working
        /// directory (UBT's Module.*.gen.cpp include "../../projects/..." relative to Engine/Source).
        /// </summary>
        static string ResolveInclude(string spelling, string includerDirectory, string workingDirectory)
        {
            try
            {
                if (Path.IsPathRooted(spelling)) return File.Exists(spelling) ? Path.GetFullPath(spelling) : null;
                foreach (var directory in new[] { includerDirectory, workingDirectory })
                {
                    if (directory == null) continue;
                    var candidate = Path.GetFullPath(Path.Combine(directory, spelling));
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (ArgumentException) { }
            return null;
        }
    }
}
