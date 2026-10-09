using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnrealSense.Clang;
using UnrealSense.Extension.Options;
using UnrealSense.Project;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Owns the clangd process used for semantic Find Usages: keeps compile_commands.json fresh (regenerated
    /// through UBT when *.Build.cs / *.Target.cs / .uproject change), starts clangd with a persistent background
    /// index and reports indexing progress in the status bar.
    /// </summary>
    internal static class ClangdService
    {
        public const string DownloadVersion = "23.1.0";
        public static readonly string DownloadUrl = $"https://github.com/clangd/clangd/releases/download/{DownloadVersion}/clangd-windows-{DownloadVersion}.zip";
        public const string DownloadSize = "29.7 MB";

        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static ClangdClient client;
        static UnrealWorkspace owner;
        static string compileCommandsDirectory;
        static DateTime lastStatus;

        public static ClangdClient Client => client;

        /// <summary>Folder of the compile database clangd currently uses (null before the first start).</summary>
        public static string CompileCommandsDirectory => compileCommandsDirectory;
        public static string StatusText { get; private set; } = "C++ semantic index not started";
        public static event EventHandler StatusChanged;

        public static bool IsClangdAvailable => ClangdClient.FindClangd(General.Instance.ClangdPath) != null;

        public static void OnWorkspaceReady(UnrealWorkspace workspace)
        {
            if (!General.Instance.UseSemanticIndex) return;
            StartAsync(workspace, regenerate: false).FireAndForgetLogged("clangd start");
        }

        /// <param name="fullPhase">
        /// False: index the project's own translation units first. True: the whole target, engine and plugins
        /// included (started automatically once the project phase finishes, when engine indexing is enabled).
        /// </param>
        public static async Task StartAsync(UnrealWorkspace workspace, bool regenerate, bool fullPhase = false,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = null)
        {
            Log.Write($"C++ index start requested by {caller} (regenerate: {regenerate}, phase: {(fullPhase ? "engine + plugins" : "project")})");
            // One indexer per project on the whole machine: a second Visual Studio waits here (without holding the gate).
            CompileDatabase.CacheRoot = General.Instance.IndexCacheFolder;
            await EnsureIndexLockAsync(CompileDatabase.GetOutputDirectory(workspace.Project)).ConfigureAwait(false);

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                StopCore();
                var clangd = ClangdClient.FindClangd(General.Instance.ClangdPath);
                if (clangd == null)
                {
                    SetStatus("clangd not found: Extensions › UnrealSense › Download clangd (needed for semantic Find Usages)");
                    return;
                }
                var project = workspace.Project;
                owner = workspace;
                // Learn how the project's folders map to their real paths (substituted drive, junction) before clangd answers with real paths.
                ClangdClient.CanonicalPath(project.ProjectDirectory);
                if (project.Engine?.EngineDirectory != null) ClangdClient.CanonicalPath(project.Engine.EngineDirectory);

                CompileDatabase.CacheRoot = General.Instance.IndexCacheFolder;
                var directory = CompileDatabase.GetOutputDirectory(project);
                var exclusions = General.Instance.EngineIndexExclusions;
                var acrossModules = General.Instance.UnityAcrossModules;
                if (!regenerate && !CompileDatabase.IsCurrentFormat(directory, exclusions, acrossModules))
                {
                    // Older format or different settings: rewrite from UBT's last output, no UBT run needed.
                    SetStatus("Rewriting compile_commands.json…");
                    if (!await Task.Run(() => CompileDatabase.Resanitize(project, exclusions, Log.Write, acrossModules)).ConfigureAwait(false))
                        regenerate = true;
                }
                string staleReason = null;
                if (regenerate || IsDatabaseStale(project, directory, out staleReason))
                {
                    if (staleReason != null) Log.Write("Compile database out of date: " + staleReason);
                    SetStatus("Generating compile_commands.json with UnrealBuildTool…");
                    directory = await Task.Run(() => CompileDatabase.Generate(project, Log.Write, exclusions, acrossModules)).ConfigureAwait(false);
                    RememberSourcesNotInTarget(project, directory);
                    fullPhase = false;
                }
                compileCommandsDirectory = directory;

                var counts = CompileDatabase.ReadCounts(directory);
                bool engineToIndex = General.Instance.IndexEngineSources && counts.HasValue && counts.Value.Full > counts.Value.Project;
                fullPhase = fullPhase && engineToIndex;
                CompileDatabase.Activate(directory, fullPhase);
                phase = fullPhase ? IndexPhase.Full : engineToIndex ? IndexPhase.ProjectThenEngine : IndexPhase.ProjectOnly;
                if (!fullPhase)
                {
                    bool fromScratch = await Task.Run(() => CompileDatabase.DiscardOutdatedIndex(directory, UnrealSensePackage.Version)).ConfigureAwait(false);
                    IndexDiagnostics.BeginSession(directory, fromScratch);
                    if (fromScratch)
                        Log.Write($"Discarded the C++ index stored by another UnrealSense version: {UnrealSensePackage.Version} indexes everything from scratch once (timings comparable between versions).");
                    // Headers added or renamed since the header maps were written: without this they would only be
                    // found through the slow fallback directories.
                    var mapsWatch = System.Diagnostics.Stopwatch.StartNew();
                    int changedMaps = await Task.Run(() => HeaderMaps.Refresh(Path.Combine(directory, HeaderMaps.FolderName))).ConfigureAwait(false);
                    Log.Write($"header maps: {changedMaps} updated in {mapsWatch.Elapsed.TotalSeconds:F1}s");
                }
                var commands = await Task.Run(() => CompileCommandIndex.Load(Path.Combine(directory, CompileDatabase.FilesVariant))).ConfigureAwait(false);

                var c = new ClangdClient { Commands = commands };
                c.Log += Log.Trace;
                c.StatusChanged += (s, e) => OnClientStatus(c);
                c.IndexingFinished += (s, e) => OnIndexingFinished(c);
                c.UnitFailed += unit =>
                {
                    Log.Write($"Indexed with compile errors, usages in it may be incomplete: {unit} (checking it now, errors go to {IndexDiagnostics.ErrorsFile})");
                    IndexDiagnostics.Record(unit);
                };
                await c.StartAsync(clangd, directory, project.ProjectDirectory,
                    jobs: General.Instance.SemanticIndexThreads, lowPriority: General.Instance.SemanticIndexLowPriority).ConfigureAwait(false);
                client = c;
                workspace.CodeFilesChanged += ForwardFileChanges;
                StartMonitor(c);
                Log.Write($"clangd indexing threads: {c.Jobs} (RAM {Gb(MemoryInfo.TotalPhysicalBytes)} GB, {Gb(MemoryInfo.AvailablePhysicalBytes)} GB available, " +
                          $"{Environment.ProcessorCount} logical cores, {(General.Instance.SemanticIndexLowPriority ? "low" : "normal")} priority)");
                if (counts.HasValue)
                    Log.Write(fullPhase
                        ? $"Indexing project + engine + plugins: {counts.Value.Full:N0} unity translation units"
                        : $"Indexing the project first: {counts.Value.Project:N0} unity translation units" +
                          (engineToIndex ? $" (then {counts.Value.Full - counts.Value.Project:N0} for the engine and plugins)" : ""));

                // Background indexing starts once clangd loads the database, i.e. on the first opened file.
                var firstSource = FirstTranslationUnit(directory);
                if (firstSource != null)
                {
                    await c.SyncDocumentAsync(firstSource, File.ReadAllText(firstSource), pushCommand: false).ConfigureAwait(false);
                    await c.CloseDocumentAsync(firstSource).ConfigureAwait(false);
                }
                Log.Write($"clangd started ({clangd}); compile database: {directory}");
                SetStatus("C++ semantic index: starting…");
            }
            catch (Exception ex)
            {
                Log.Error("clangd", ex);
                SetStatus("C++ semantic index failed: " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Stops clangd if it belongs to <paramref name="workspace"/> (null = whatever runs).</summary>
        public static void Stop(UnrealWorkspace workspace = null)
        {
            gate.Wait();
            try
            {
                if (workspace == null || owner == null || owner == workspace)
                {
                    StopCore();
                    indexLock?.Dispose(); // let another Visual Studio instance index this project
                    indexLock = null;
                }
            }
            finally { gate.Release(); }
        }

        static IndexLock indexLock;
        static readonly SemaphoreSlim lockGate = new SemaphoreSlim(1, 1);

        static async Task EnsureIndexLockAsync(string directory)
        {
            await lockGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (indexLock != null && string.Equals(indexLock.Directory, directory, StringComparison.OrdinalIgnoreCase)) return;
                indexLock?.Dispose();
                indexLock = await IndexLock.AcquireAsync(directory, () =>
                {
                    Log.Write("Another Visual Studio instance is indexing this project: this one waits and takes over when it closes.");
                    SetStatus("C++ semantic index: in use by another Visual Studio instance (waiting)");
                }).ConfigureAwait(false);
            }
            finally
            {
                lockGate.Release();
            }
        }

        static System.Threading.Timer monitor;

        /// <summary>
        /// Logs indexing progress, clangd memory and free RAM every minute, and warns when nothing completes for
        /// 10 minutes: enough to tell a slow-but-working index from a stuck or memory-starved one remotely.
        /// </summary>
        static void StartMonitor(ClangdClient c)
        {
            string lastProgress = null;
            var lastChange = DateTime.UtcNow;
            bool warned = false;
            ulong lastCycles = c.CpuCycles;
            var lastTick = DateTime.UtcNow;
            monitor = new System.Threading.Timer(_ =>
            {
                // Busy cores from CPU cycles: Windows' CPU time (Task Manager, TotalProcessorTime) undercounted clangd
                // by up to 8x on a machine with tick-based accounting, so it cannot tell CPU-bound from waiting.
                ulong cycles = c.CpuCycles;
                var now = DateTime.UtcNow;
                double cores = cycles > lastCycles ? (cycles - lastCycles) / ClangdClient.CycleRateHz / Math.Max(1, (now - lastTick).TotalSeconds) : 0;
                lastCycles = cycles;
                lastTick = now;
                if (c != client || c.State != ClangdState.Indexing) return;
                var progress = $"{c.IndexPercentage}% {c.IndexMessage}";
                if (progress != lastProgress) { lastProgress = progress; lastChange = DateTime.UtcNow; warned = false; }
                var line = $"index{PhaseLabel}: {progress}; clangd {Gb(c.MemoryBytes)} GB, {cores:F1} busy cores; free RAM {Gb(MemoryInfo.AvailablePhysicalBytes)}/{Gb(MemoryInfo.TotalPhysicalBytes)} GB" +
                           (c.UnitsWithErrors > 0 ? $"; {c.UnitsWithErrors} unit(s) with compile errors" : "");
                Log.Write(line);
                if (!warned && DateTime.UtcNow - lastChange > TimeSpan.FromMinutes(10))
                {
                    warned = true;
                    var lowRam = MemoryInfo.AvailablePhysicalBytes < 2L * 1024 * 1024 * 1024;
                    Log.Write("Warning: no indexing progress for 10 minutes." + (lowRam
                        ? " Memory is almost exhausted: lower Tools › Options › UnrealSense › Indexing threads."
                        : " Each unity group can take minutes on a large engine; if this persists, send this log."));
                }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1");

        static void StopCore()
        {
            monitor?.Dispose();
            monitor = null;
            if (owner != null) owner.CodeFilesChanged -= ForwardFileChanges;
            client?.Dispose();
            client = null;
            owner = null;
        }

        static void ForwardFileChanges(IReadOnlyList<string> paths)
        {
            var c = client;
            if (c == null) return;
            // Build rules changed: the database itself must be regenerated.
            var rules = paths.FirstOrDefault(p => (p.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
                                                  && p.IndexOf("\\Intermediate\\", StringComparison.OrdinalIgnoreCase) < 0);
            if (rules != null)
            {
                Log.Write($"Build rules changed ({rules}): regenerating the compile database.");
                var ws = owner;
                if (ws != null) StartAsync(ws, regenerate: true).FireAndForgetLogged("clangd restart");
                return;
            }
            c.NotifyFilesChangedAsync(paths.Where(p => p.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".inl", StringComparison.OrdinalIgnoreCase)))
                .FireAndForgetLogged("clangd file changes");
        }

        const string NotInTargetFile = "sources-not-in-target.txt";

        static bool IsDatabaseStale(UnrealProject project, string directory, out string reason)
        {
            reason = null;
            var db = Path.Combine(directory, "compile_commands.json");
            var raw = CompileDatabase.RawDatabase(project);
            if (!File.Exists(db) || !File.Exists(raw)) { reason = "no database yet"; return true; }
            // Compare against UBT's output: the sanitized file is rewritten without running UBT.
            var stamp = File.GetLastWriteTimeUtc(raw);
            if (File.GetLastWriteTimeUtc(project.UProjectPath) > stamp) { reason = Path.GetFileName(project.UProjectPath) + " changed"; return true; }
            var rules = project.AllModules.Select(m => m.BuildCsPath)
                .Concat(Directory.Exists(project.SourceDirectory) ? Directory.GetFiles(project.SourceDirectory, "*.Target.cs") : Array.Empty<string>());
            var changedRules = rules.FirstOrDefault(r => File.Exists(r) && File.GetLastWriteTimeUtc(r) > stamp);
            if (changedRules != null) { reason = changedRules + " changed"; return true; }

            // New source files are not in the database yet (files UBT left out last time, e.g. of a plugin that is
            // not part of the target, are ignored, or the database would be regenerated on every start).
            var newSource = FindSourceNotInDatabase(project, directory, ignoreKnownMissing: true);
            if (newSource != null) { reason = "new source file " + newSource; return true; }
            return false;
        }

        static string FindSourceNotInDatabase(UnrealProject project, string directory, bool ignoreKnownMissing)
        {
            var files = Path.Combine(directory, CompileDatabase.FilesVariant);
            if (!File.Exists(files)) return "(no per-file database)";
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in FileEntry.Matches(File.ReadAllText(files)))
                known.Add(Path.GetFileName(m.Groups[1].Value.Replace("\\\\", "\\")));
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ignoreFile = Path.Combine(directory, NotInTargetFile);
            if (ignoreKnownMissing && File.Exists(ignoreFile)) foreach (var line in File.ReadAllLines(ignoreFile)) ignored.Add(line.Trim());
            return project.AllModules.Where(m => Directory.Exists(m.Directory))
                .SelectMany(m => Directory.EnumerateFiles(m.Directory, "*.cpp", SearchOption.AllDirectories))
                .FirstOrDefault(f => !known.Contains(Path.GetFileName(f)) && !ignored.Contains(f));
        }

        /// <summary>After a UBT run, remembers the project sources UBT did not include (not part of the target).</summary>
        static void RememberSourcesNotInTarget(UnrealProject project, string directory)
        {
            try
            {
                var files = Path.Combine(directory, CompileDatabase.FilesVariant);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in FileEntry.Matches(File.ReadAllText(files)))
                    known.Add(Path.GetFileName(m.Groups[1].Value.Replace("\\\\", "\\")));
                var missing = project.AllModules.Where(m => Directory.Exists(m.Directory))
                    .SelectMany(m => Directory.EnumerateFiles(m.Directory, "*.cpp", SearchOption.AllDirectories))
                    .Where(f => !known.Contains(Path.GetFileName(f))).ToList();
                File.WriteAllLines(Path.Combine(directory, NotInTargetFile), missing);
                if (missing.Count > 0) Log.Write($"{missing.Count} project source file(s) are not part of the build target (e.g. {missing[0]}); they will not trigger a new UBT run.");
            }
            catch (Exception ex)
            {
                Log.Error("Listing sources outside the target", ex);
            }
        }

        static readonly System.Text.RegularExpressions.Regex FileEntry = new System.Text.RegularExpressions.Regex("\"file\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

        static string FirstTranslationUnit(string directory)
        {
            try
            {
                var json = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(directory, "compile_commands.json")));
                var files = json.Select(e => (string)e["file"]).Where(f => f != null && File.Exists(f)).ToList();
                // Prefer a generated unity file: it never gets a per-file command, so opening it makes clangd load
                // the database itself, which is what starts the background index.
                var unityDir = Path.GetFullPath(Path.Combine(directory, "unity")) + Path.DirectorySeparatorChar;
                return files.FirstOrDefault(f => Path.GetFullPath(f).StartsWith(unityDir, StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault(f => !f.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
        }

        enum IndexPhase { ProjectOnly, ProjectThenEngine, Full }
        static IndexPhase phase;

        /// <summary>
        /// Project phase done: switch clangd to the full database (its on-disk index is kept). clangd ends an
        /// indexing cycle after loading the database and again after each batch, so the switch waits for a quiet
        /// minute with no new cycle before concluding that the project queue is really empty.
        /// </summary>
        static void OnIndexingFinished(ClangdClient c)
        {
            if (c != client || phase != IndexPhase.ProjectThenEngine) return;
            var endedAt = c.LastIndexingEndUtc;
            Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
            {
                if (c != client || phase != IndexPhase.ProjectThenEngine) return;
                if (c.State == ClangdState.Indexing || c.LastIndexingEndUtc != endedAt) return; // a newer cycle will decide
                var ws = owner;
                if (ws == null) return;
                Log.Write("Project indexed; continuing with the engine and plugins.");
                StartAsync(ws, regenerate: false, fullPhase: true).FireAndForgetLogged("clangd engine phase");
            }, TaskScheduler.Default);
        }

        static string PhaseLabel => phase == IndexPhase.Full ? " (engine + plugins)" : phase == IndexPhase.ProjectThenEngine ? " (project)" : "";

        static void OnClientStatus(ClangdClient c)
        {
            switch (c.State)
            {
                case ClangdState.Indexing:
                    SetStatus($"C++ semantic index{PhaseLabel}: {c.IndexPercentage}% {c.IndexMessage}", throttle: true);
                    break;
                case ClangdState.Ready:
                    SetStatus(phase == IndexPhase.ProjectThenEngine ? "C++ semantic index: project ready, engine next" : "C++ semantic index: ready");
                    break;
                case ClangdState.Failed:
                    SetStatus("C++ semantic index: clangd stopped (see Output › UnrealSense)");
                    break;
            }
        }

        static void SetStatus(string text, bool throttle = false)
        {
            StatusText = text;
            StatusChanged?.Invoke(null, EventArgs.Empty);
            if (throttle && (DateTime.UtcNow - lastStatus).TotalMilliseconds < 1000) return;
            lastStatus = DateTime.UtcNow;
            Log.Status("UnrealSense: " + text);
            Log.Trace(text);
        }

        /// <summary>Downloads the official clangd release into %LOCALAPPDATA%\UnrealSense\clangd.</summary>
        public static async Task<string> DownloadAsync(IProgress<string> progress)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "clangd");
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, $"clangd-windows-{DownloadVersion}.zip");
            progress?.Report($"Downloading {DownloadUrl}…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var file = File.Create(zip))
                    await response.Content.CopyToAsync(file).ConfigureAwait(false);
            }
            progress?.Report("Extracting clangd…");
            var target = Path.Combine(root, "clangd_" + DownloadVersion);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            ZipFile.ExtractToDirectory(zip, root);
            File.Delete(zip);
            return ClangdClient.FindClangd();
        }
    }

    internal static class TaskExtensions
    {
        public static void FireAndForgetLogged(this Task task, string context)
        {
            task.ContinueWith(t => Log.Error(context, t.Exception?.GetBaseException()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
