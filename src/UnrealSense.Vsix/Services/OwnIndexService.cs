using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Clang;
using UnrealSense.Extension.Options;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    /// <summary>One usage reported by the own index (0-based line and UTF-16 column, as the editor uses).</summary>
    internal sealed class OwnIndexUsage
    {
        public string FilePath;
        public int Line, Column;
        public string Kind;      // Decl, Def, Type, Read, Call, Member, Macro, Soup
    }

    internal sealed class OwnIndexResult
    {
        public string Symbol, Name, Kind;
        public List<OwnIndexUsage> Usages = new List<OwnIndexUsage>();
        public List<OwnIndexUsage> Uncertain = new List<OwnIndexUsage>();
    }

    /// <summary>
    /// The experimental own C++ index (option UseOwnIndex): the <c>usindex serve</c> process shipped in the VSIX's OwnIndexer folder,
    /// one per Visual Studio, for the loaded project. It answers from the last saved index at once and rebuilds incrementally in the
    /// background: when the workspace loads, and after C++ files are saved. Log lines start with "OwnIndex:".
    /// </summary>
    internal static class OwnIndexService
    {
        static readonly object gate = new object();
        static Process process;
        static StreamWriter input;
        static string processProject;
        static bool processEngine;
        static readonly Dictionary<int, TaskCompletionSource<JObject>> pending = new Dictionary<int, TaskCompletionSource<JObject>>();
        static int nextId;
        static volatile bool ready;
        static bool hooked;
        static Timer saveTimer;
        static readonly HashSet<string> savedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool IsReady => ready;
        public static string StatusText { get; private set; } = "own index off";

        static string ExePath => Path.Combine(Path.GetDirectoryName(typeof(OwnIndexService).Assembly.Location), "OwnIndexer", "usindex.exe");

        public static void OnWorkspaceReady(UnrealWorkspace workspace)
        {
            Hook();
            if (!General.Instance.UseOwnIndex || workspace?.Project == null) { Stop(); return; }
            Start(workspace.Project.UProjectPath, General.Instance.OwnIndexEngine);
        }

        static void Hook()
        {
            lock (gate)
            {
                if (hooked) return;
                hooked = true;
            }
            VS.Events.DocumentEvents.Saved += OnSaved;
            General.Saved += options =>
            {
                var workspace = WorkspaceService.Current;
                if (!options.UseOwnIndex) Stop();
                else if (workspace?.Project != null) Start(workspace.Project.UProjectPath, options.OwnIndexEngine);
            };
        }

        static void Start(string uproject, bool engine)
        {
            lock (gate)
            {
                if (process != null && !process.HasExited && string.Equals(processProject, uproject, StringComparison.OrdinalIgnoreCase) && processEngine == engine) return;
                StopLocked();
                var exe = ExePath;
                if (!File.Exists(exe))
                {
                    StatusText = "own index: indexer not installed";
                    Log.Write($"OwnIndex: {exe} not found; Find Usages uses the other back-ends");
                    return;
                }
                var psi = new ProcessStartInfo(exe, $"serve \"{uproject}\"" + (engine ? " --engine" : ""))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe),
                };
                Process p;
                try { p = Process.Start(psi); }
                catch (Exception ex) { Log.Error("OwnIndex: start", ex); StatusText = "own index: failed to start"; return; }
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                process = p;
                processProject = uproject;
                processEngine = engine;
                input = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                ready = false;
                StatusText = "own index: starting";
                Log.Write($"OwnIndex: started {exe} (pid {p.Id}) for {uproject}{(engine ? " with the engine" : "")}");
                var reader = new Thread(() => ReadAnswers(p)) { IsBackground = true, Name = "UnrealSense own index" };
                reader.Start();
                var errors = new Thread(() => ReadLog(p)) { IsBackground = true, Name = "UnrealSense own index log" };
                errors.Start();
            }
            InitializeAsync().FireAndForgetLogged("OwnIndex start");
        }

        static async Task InitializeAsync()
        {
            // the previous index answers at once; the incremental build that follows replaces it
            var status = await RequestAsync(new JObject { ["cmd"] = "status" }, CancellationToken.None).ConfigureAwait(false);
            if ((bool?)status?["ready"] == true) { ready = true; StatusText = "own index: ready (updating)"; }
            var clock = Stopwatch.StartNew();
            var built = await RequestAsync(new JObject { ["cmd"] = "build" }, CancellationToken.None).ConfigureAwait(false);
            if ((bool?)built?["ok"] == true)
            {
                ready = true;
                StatusText = "own index: ready";
                Log.Write($"OwnIndex: index ready in {clock.Elapsed.TotalSeconds:F1}s: {(int?)built["files"]} files, " +
                          ((bool?)built["incremental"] == true ? $"incremental ({(int?)built["changed"]} files changed)" : "full build"));
            }
            else
            {
                StatusText = "own index: build failed";
                Log.Write("OwnIndex: build failed (see the OwnIndex lines above)");
            }
        }

        static void ReadAnswers(Process p)
        {
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    JObject answer;
                    try { answer = JObject.Parse(line); } catch (JsonException) { continue; }
                    int id = (int?)answer["id"] ?? -1;
                    TaskCompletionSource<JObject> tcs;
                    lock (gate)
                    {
                        if (!pending.TryGetValue(id, out tcs)) continue;
                        pending.Remove(id);
                    }
                    tcs.TrySetResult(answer);
                }
            }
            catch (Exception ex) { Log.Trace("OwnIndex: reader stopped: " + ex.Message); }
            lock (gate)
            {
                foreach (var tcs in pending.Values) tcs.TrySetResult(null);
                pending.Clear();
                if (process == p) { ready = false; StatusText = "own index: stopped"; }
            }
            if (!p.HasExited || p.ExitCode != 0) Log.Write("OwnIndex: the indexer process ended" + (p.HasExited ? $" (exit code {p.ExitCode})" : ""));
        }

        static void ReadLog(Process p)
        {
            try
            {
                string line;
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    // the summary lines go to the visible log, the per-phase details to the log file only
                    if (line.Contains("full build") || line.Contains("abandoned") || line.Contains("failed") || line.Contains("index ready") || line.Contains("unreadable"))
                        Log.Write("OwnIndex: " + line.Trim());
                    else Log.Trace("OwnIndex: " + line.Trim());
                }
            }
            catch (Exception) { }
        }

        static async Task<JObject> RequestAsync(JObject request, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (process == null || process.HasExited || input == null) return null;
                int id = ++nextId;
                request["id"] = id;
                pending[id] = tcs;
                try { input.WriteLine(request.ToString(Formatting.None)); }
                catch (IOException) { pending.Remove(id); return null; }
            }
            using (token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>Usages of the symbol at a position (0-based line and column), or null when the index is not available.</summary>
        public static async Task<OwnIndexResult> FindAsync(string filePath, int line, int column, CancellationToken token)
        {
            if (!ready) return null;
            var canonical = ClangdClient.CanonicalPath(filePath);
            var answer = await RequestAsync(new JObject
            {
                ["cmd"] = "refs", ["file"] = canonical, ["altFile"] = filePath, ["line"] = line + 1, ["col"] = column + 1,
            }, token).ConfigureAwait(false);
            if (answer == null || (bool?)answer["ok"] != true) return null;
            var result = new OwnIndexResult { Symbol = (string)answer["symbol"], Name = (string)answer["name"], Kind = (string)answer["kind"] };
            OwnIndexUsage Usage(JToken u) => new OwnIndexUsage
            {
                FilePath = ClangdClient.ToViewPath(((string)u["file"]).Replace('/', '\\')), Line = (int)u["line"] - 1, Column = (int)u["col"] - 1, Kind = (string)u["kind"],
            };
            foreach (var u in answer["usages"] ?? new JArray()) result.Usages.Add(Usage(u));
            foreach (var u in answer["uncertain"] ?? new JArray()) result.Uncertain.Add(Usage(u));
            return result;
        }

        /// <summary>C++ files saved: one incremental build a moment later (several saves make one build).</summary>
        static void OnSaved(string path)
        {
            if (!ready || string.IsNullOrEmpty(path)) return;
            var ext = Path.GetExtension(path);
            if (!(ext.Equals(".h", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                  ext.Equals(".inl", StringComparison.OrdinalIgnoreCase) || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase))) return;
            lock (gate)
            {
                savedFiles.Add(path);
                saveTimer?.Dispose();
                saveTimer = new Timer(_ => UpdateAfterSaveAsync().FireAndForgetLogged("OwnIndex update"), null, 1500, Timeout.Infinite);
            }
        }

        static async Task UpdateAfterSaveAsync()
        {
            int count;
            lock (gate) { count = savedFiles.Count; savedFiles.Clear(); }
            var clock = Stopwatch.StartNew();
            var built = await RequestAsync(new JObject { ["cmd"] = "build" }, CancellationToken.None).ConfigureAwait(false);
            if (built != null)
                Log.Write($"OwnIndex: updated after {count} saved file(s) in {clock.Elapsed.TotalSeconds:F1}s ({(int?)built["changed"]} files changed)");
        }

        public static void Stop()
        {
            lock (gate) StopLocked();
        }

        static void StopLocked()
        {
            ready = false;
            var p = process;
            process = null;
            processProject = null;
            if (p == null) return;
            StatusText = "own index off";
            try
            {
                if (!p.HasExited)
                {
                    input?.WriteLine("{\"cmd\":\"exit\"}");
                    if (!p.WaitForExit(3000)) p.Kill();
                }
            }
            catch (Exception) { }
            input = null;
            Log.Write("OwnIndex: stopped");
        }
    }
}
