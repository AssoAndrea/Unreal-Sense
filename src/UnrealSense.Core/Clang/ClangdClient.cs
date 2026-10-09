using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Clang
{
    public sealed class SourceLocation
    {
        public string FilePath { get; set; }
        /// <summary>0-based line and UTF-16 column, as in LSP.</summary>
        public int Line { get; set; }
        public int Column { get; set; }
        public int EndColumn { get; set; }
        public override string ToString() => $"{FilePath}({Line + 1},{Column + 1})";
    }

    public enum ClangdState { Stopped, Starting, Indexing, Ready, Failed }

    /// <summary>
    /// Runs clangd with a background index over the project's compile_commands.json and exposes the queries
    /// we need (references, definition). The index persists next to the database, so restarts are cheap.
    /// </summary>
    public sealed class ClangdClient : IDisposable
    {
        readonly ConcurrentDictionary<string, int> openDocuments = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, string> openTexts = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsOpen(string path) => openDocuments.ContainsKey(path);
        Process process;
        LspConnection connection;

        public ClangdState State { get; private set; }
        public int IndexPercentage { get; private set; }
        public string IndexMessage { get; private set; }
        public string ClangdPath { get; private set; }

        /// <summary>Raised when a background indexing run completes (all queued files indexed).</summary>
        public event EventHandler IndexingFinished;
        /// <summary>Raised (background thread) on state/progress changes.</summary>
        public event EventHandler StatusChanged;
        public event Action<string> Log;

        /// <summary>clangd from settings, the UnrealSense download folder, LLVM or PATH.</summary>
        public static string FindClangd(string configuredPath = null)
        {
            if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath)) return configuredPath;
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "clangd");
            if (Directory.Exists(local))
            {
                var found = Directory.GetFiles(local, "clangd.exe", SearchOption.AllDirectories).OrderByDescending(f => f).FirstOrDefault();
                if (found != null) return found;
            }
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clangd.exe"),
            })
                if (File.Exists(candidate)) return candidate;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "clangd.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>
        /// When the last indexing cycle ended. clangd reports several begin/end cycles (loading the database and
        /// shards, opened files, then the real work), so one "end" does not mean the queue is done for good.
        /// </summary>
        public DateTime LastIndexingEndUtc { get; private set; }

        /// <summary>Translation units clangd indexed with compile errors (their index may be incomplete).</summary>
        public int UnitsWithErrors => unitsWithErrors;
        int unitsWithErrors;

        /// <summary>Raised for each translation unit that failed to compile cleanly while indexing (absolute path).</summary>
        public event Action<string> UnitFailed;

        /// <summary>clangd's stderr at info level: keep errors and failed units, drop the request chatter.</summary>
        void OnClangdLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            const string failed = "Failed to compile ";
            int at = line.IndexOf(failed, StringComparison.Ordinal);
            if (at >= 0)
            {
                Interlocked.Increment(ref unitsWithErrors);
                var rest = line.Substring(at + failed.Length);
                int comma = rest.IndexOf(", index may be incomplete", StringComparison.Ordinal);
                UnitFailed?.Invoke(comma >= 0 ? rest.Substring(0, comma) : rest);
                return;
            }
            if (line.StartsWith("E[", StringComparison.Ordinal)) Log?.Invoke("clangd: " + line);
        }

        /// <summary>Upper bound of <see cref="AutomaticJobs"/>.</summary>
        public const int MaxAutomaticJobs = 8;

        /// <summary>Indexing threads actually used (after the automatic choice).</summary>
        public int Jobs { get; private set; }

        /// <summary>CPU time used by the clangd process so far (zero when not running).</summary>
        public TimeSpan CpuTime
        {
            get
            {
                try
                {
                    var p = process;
                    if (p == null || p.HasExited) return TimeSpan.Zero;
                    p.Refresh();
                    return p.TotalProcessorTime;
                }
                catch (Exception)
                {
                    return TimeSpan.Zero;
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

        /// <summary>
        /// CPU cycles used by clangd so far (0 when unknown). Unlike <see cref="CpuTime"/>, which some machines
        /// (tick-based accounting) undercount by up to 8x, cycles are counted exactly; divide by the TSC rate.
        /// </summary>
        public ulong CpuCycles
        {
            get
            {
                try
                {
                    var p = process;
                    if (p == null || p.HasExited) return 0;
                    return QueryProcessCycleTime(p.Handle, out var cycles) ? cycles : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>Rate of the counter behind <see cref="CpuCycles"/> (the processor's nominal frequency), in Hz.</summary>
        public static double CycleRateHz => cycleRate.Value;
        static readonly Lazy<double> cycleRate = new Lazy<double>(() =>
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (key?.GetValue("~MHz") is int mhz && mhz > 0) return mhz * 1e6;
            }
            catch (Exception) { }
            return 3e9;
        });

        /// <summary>Working set of the clangd process in bytes (0 when not running).</summary>
        public long MemoryBytes
        {
            get
            {
                try
                {
                    var p = process;
                    if (p == null || p.HasExited) return 0;
                    p.Refresh();
                    return p.WorkingSet64;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Automatic thread count: three quarters of the logical cores (at most 8), but no more than one thread per 4.5 GB of
        /// physical memory beyond 8 GB kept for Visual Studio and the rest. Each thread holds a whole Unreal
        /// translation unit (3-4 GB for a large unity group); exceeding RAM makes Windows page and indexing crawl.
        /// </summary>
        public static int AutomaticJobs()
        {
            // Measured on an i9-14900 (24 cores / 32 threads, like the target machines) over 388 Lyra translation units:
            // 8 threads 200 s, 16 threads 230 s, 24 threads 275 s. Past ~8 threads the extra work (headers indexed
            // by several units at once, thousands more index files written) saturates the single-threaded antivirus
            // scanner every file open/write goes through, and indexing gets slower, not faster.
            int byCpu = Math.Min(MaxAutomaticJobs, Math.Max(1, Environment.ProcessorCount * 3 / 4));
            const double gb = 1024.0 * 1024 * 1024;
            var totalGb = MemoryInfo.TotalPhysicalBytes / gb;
            if (totalGb <= 0) return byCpu;
            int byMemory = Math.Max(1, (int)((totalGb - 8) / 4.5)); // a 384-file unity unit peaks around 3-4 GB
            return Math.Min(Math.Min(byCpu, byMemory), ByAvailableMemory(MemoryInfo.AvailablePhysicalBytes / gb));
        }

        /// <summary>
        /// Threads that fit in the memory free right now (Visual Studio, the editor and their tools often hold half
        /// of it): measured on a source-built engine, 384-file units, 4 threads peaked at 15.5 GB of clangd (about
        /// 3.5 GB per thread plus the index); with 63.7 GB installed but 19 GB available, the 8 threads chosen from
        /// the installed memory alone would need about 28 GB at the peak, so Windows would page.
        /// </summary>
        public static int ByAvailableMemory(double availableGb) =>
            availableGb <= 0 ? int.MaxValue : Math.Max(2, (int)((availableGb - 2) / 3.5));

        /// <param name="jobs">Background indexing threads; 0 = automatic (see <see cref="AutomaticJobs"/>).</param>
        /// <param name="lowPriority">Index below normal priority: uses idle cores only, slower on a busy machine.</param>
        public async Task StartAsync(string clangdPath, string compileCommandsDirectory, string rootDirectory, CancellationToken cancellationToken = default,
            int jobs = 0, bool lowPriority = false)
        {
            ClangdPath = clangdPath;
            SetState(ClangdState.Starting);
            if (jobs <= 0) jobs = AutomaticJobs();
            Jobs = jobs;
            var args = string.Join(" ", new[]
            {
                $"\"--compile-commands-dir={compileCommandsDirectory}\"",
                "--background-index",
                lowPriority ? "--background-index-priority=low" : "--background-index-priority=normal",
                $"-j={jobs}",
                "--pch-storage=memory",
                "--header-insertion=never",
                "--limit-references=0",
                "--limit-results=0",
                "--clang-tidy=false",
                "--log=info", // needed for "Failed to compile ..., index may be incomplete" (filtered below)
            });
            var info = new ProcessStartInfo(clangdPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = compileCommandsDirectory,
            };
            process = Process.Start(info);
            if (lowPriority)
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            process.ErrorDataReceived += (s, e) => OnClangdLog(e.Data);
            process.BeginErrorReadLine();

            connection = new LspConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            connection.Notification += OnNotification;
            connection.RequestHandler = (method, p) =>
            {
                // window/workDoneProgress/create, workspace/configuration, client/registerCapability...
                return method == "workspace/configuration" ? new JArray() : null;
            };
            connection.Closed += ex =>
            {
                Log?.Invoke("clangd exited" + (ex != null ? ": " + ex.Message : ""));
                SetState(ClangdState.Failed);
            };

            var rootUri = ToUri(rootDirectory);
            await connection.RequestAsync("initialize", new JObject
            {
                ["processId"] = Process.GetCurrentProcess().Id,
                ["rootUri"] = rootUri,
                ["capabilities"] = new JObject
                {
                    ["window"] = new JObject { ["workDoneProgress"] = true },
                    ["textDocument"] = new JObject
                    {
                        ["references"] = new JObject { ["dynamicRegistration"] = false },
                        ["definition"] = new JObject { ["linkSupport"] = false },
                        ["synchronization"] = new JObject { ["didSave"] = true },
                    },
                },
                ["initializationOptions"] = new JObject { ["clangdFileStatus"] = false },
            }, cancellationToken).ConfigureAwait(false);
            await connection.NotifyAsync("initialized", new JObject()).ConfigureAwait(false);
            SetState(ClangdState.Ready);
        }

        void OnNotification(string method, JToken parameters)
        {
            if (method != "$/progress") return;
            var token = (string)parameters?["token"];
            var value = parameters?["value"];
            if (token == null || value == null || !token.Contains("backgroundIndexProgress")) return;
            Log?.Invoke($"progress {(string)value["kind"]} {(int?)value["percentage"]}% {(string)value["message"] ?? (string)value["title"]}");
            switch ((string)value["kind"])
            {
                case "begin":
                    IndexPercentage = 0;
                    IndexMessage = (string)value["title"];
                    SetState(ClangdState.Indexing);
                    break;
                case "report":
                    IndexPercentage = (int?)value["percentage"] ?? IndexPercentage;
                    IndexMessage = (string)value["message"] ?? IndexMessage;
                    SetState(ClangdState.Indexing);
                    break;
                case "end":
                    LastIndexingEndUtc = DateTime.UtcNow;
                    IndexPercentage = 100;
                    IndexMessage = null;
                    SetState(ClangdState.Ready);
                    IndexingFinished?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        void SetState(ClangdState state)
        {
            State = state;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Per-file commands pushed to clangd when a document is opened (see <see cref="CompileCommandIndex"/>).</summary>
        public CompileCommandIndex Commands { get; set; }

        /// <summary>Opens a document (or refreshes its content) so queries see unsaved edits.</summary>
        /// <param name="pushCommand">False to let clangd take the command from its database: opening such a file
        /// is what makes clangd load the database and start the background index.</param>
        public async Task SyncDocumentAsync(string path, string text, bool pushCommand = true)
        {
            var uri = ToUri(path);
            // Same text as last time: nothing to send, so clangd keeps the AST it already built.
            if (openTexts.TryGetValue(path, out var sent) && string.Equals(sent, text, StringComparison.Ordinal)) return;
            openTexts[path] = text;
            if (openDocuments.TryGetValue(path, out var version))
            {
                openDocuments[path] = version + 1;
                await connection.NotifyAsync("textDocument/didChange", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = version + 1 },
                    ["contentChanges"] = new JArray(new JObject { ["text"] = text }),
                }).ConfigureAwait(false);
            }
            else
            {
                openDocuments[path] = 1;
                // The database lists unity translation units: give clangd this file's own command first.
                var command = pushCommand ? Commands?.Find(path) : null;
                if (command != null)
                {
                    await connection.NotifyAsync("workspace/didChangeConfiguration", new JObject
                    {
                        ["settings"] = new JObject
                        {
                            ["compilationDatabaseChanges"] = new JObject
                            {
                                [CanonicalPath(path)] = new JObject
                                {
                                    ["workingDirectory"] = command.Directory,
                                    ["compilationCommand"] = new JArray(command.Arguments),
                                },
                            },
                        },
                    }).ConfigureAwait(false);
                }
                await connection.NotifyAsync("textDocument/didOpen", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri, ["languageId"] = "cpp", ["version"] = 1, ["text"] = text },
                }).ConfigureAwait(false);
            }
        }

        /// <summary>Tells clangd that files changed on disk so the background index re-indexes them.</summary>
        public Task NotifyFilesChangedAsync(IEnumerable<string> paths)
        {
            var changes = new JArray(paths.Select(p => new JObject { ["uri"] = ToUri(p), ["type"] = File.Exists(p) ? 2 : 3 }));
            return changes.Count == 0 ? Task.CompletedTask : connection.NotifyAsync("workspace/didChangeWatchedFiles", new JObject { ["changes"] = changes });
        }

        public async Task CloseDocumentAsync(string path)
        {
            openTexts.TryRemove(path, out _);
            if (openDocuments.TryRemove(path, out _))
                await connection.NotifyAsync("textDocument/didClose", new JObject { ["textDocument"] = new JObject { ["uri"] = ToUri(path) } }).ConfigureAwait(false);
        }

        public async Task<List<SourceLocation>> FindReferencesAsync(string path, int line, int column, bool includeDeclaration, CancellationToken cancellationToken = default)
        {
            var result = await connection.RequestAsync("textDocument/references", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = ToUri(path) },
                ["position"] = new JObject { ["line"] = line, ["character"] = column },
                ["context"] = new JObject { ["includeDeclaration"] = includeDeclaration },
            }, cancellationToken).ConfigureAwait(false);
            return ParseLocations(result);
        }

        public async Task<List<SourceLocation>> FindDefinitionAsync(string path, int line, int column, CancellationToken cancellationToken = default)
        {
            var result = await connection.RequestAsync("textDocument/definition", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = ToUri(path) },
                ["position"] = new JObject { ["line"] = line, ["character"] = column },
            }, cancellationToken).ConfigureAwait(false);
            return ParseLocations(result);
        }

        public async Task<List<SourceLocation>> FindDeclarationAsync(string path, int line, int column, CancellationToken cancellationToken = default)
        {
            var result = await connection.RequestAsync("textDocument/declaration", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = ToUri(path) },
                ["position"] = new JObject { ["line"] = line, ["character"] = column },
            }, cancellationToken).ConfigureAwait(false);
            return ParseLocations(result);
        }

        /// <summary>Hover text (type of the symbol) — used to label the searched symbol.</summary>
        public async Task<string> HoverAsync(string path, int line, int column, CancellationToken cancellationToken = default)
        {
            var result = await connection.RequestAsync("textDocument/hover", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = ToUri(path) },
                ["position"] = new JObject { ["line"] = line, ["character"] = column },
            }, cancellationToken).ConfigureAwait(false);
            if (!(result is JObject)) return null;
            var contents = result["contents"];
            return contents == null ? null : (string)(contents["value"] ?? contents);
        }

        static List<SourceLocation> ParseLocations(JToken result)
        {
            var list = new List<SourceLocation>();
            IEnumerable<JToken> items = result is JArray array ? array : result is JObject single ? new[] { single } : Enumerable.Empty<JToken>();
            foreach (var item in items)
            {
                var uri = (string)(item["uri"] ?? item["targetUri"]);
                var range = item["range"] ?? item["targetSelectionRange"];
                if (uri == null || range == null) continue;
                list.Add(new SourceLocation
                {
                    FilePath = FromUri(uri),
                    Line = (int)range["start"]["line"],
                    Column = (int)range["start"]["character"],
                    EndColumn = (int)range["end"]["character"],
                });
            }
            return list;
        }

        public static string ToUri(string path) => new Uri(CanonicalPath(path)).AbsoluteUri;

        static readonly ConcurrentDictionary<string, string> canonicalPaths = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The path the way clangd names the file in its index: the directory's real path (case on disk, links and
        /// substituted drives resolved, like llvm::sys::fs::real_path) plus the file name as it is on disk. clangd
        /// stores and finds a file's index by that exact string; a document opened as "projects\x\a.cpp" when the
        /// index says "Projects\X\a.cpp" is not recognized as indexed and gets indexed again from scratch.
        /// </summary>
        public static string CanonicalPath(string path) => canonicalPaths.GetOrAdd(Path.GetFullPath(path), full =>
        {
            try
            {
                string real;
                if (Directory.Exists(full)) real = RealDirectory(full);
                else
                {
                    var directory = Path.GetDirectoryName(full);
                    if (directory == null || !Directory.Exists(directory)) return full;
                    var name = Path.GetFileName(full);
                    var onDisk = Directory.EnumerateFiles(directory, name).FirstOrDefault();
                    real = Path.Combine(RealDirectory(directory), onDisk != null ? Path.GetFileName(onDisk) : name);
                }
                RememberView(full, real);
                return real;
            }
            catch (Exception) { return full; }
        });

        // Real path prefix -> the prefix the user works with (e.g. "D:\workspaces" -> "S:" for a substituted drive).
        static readonly List<(string Real, string View)> viewPrefixes = new List<(string, string)>();

        /// <summary>
        /// Records how a path the user works with maps to its real path, so that paths clangd returns (real ones) can
        /// be given back as the user knows them: a drive created with subst, a junction or a symbolic link would
        /// otherwise make the same file appear under two names (and look like another project).
        /// </summary>
        static void RememberView(string view, string real)
        {
            if (string.Equals(view, real, StringComparison.Ordinal)) return;
            var v = view.TrimEnd('\\').Split('\\');
            var r = real.TrimEnd('\\').Split('\\');
            int common = 0;
            while (common < v.Length && common < r.Length && string.Equals(v[v.Length - 1 - common], r[r.Length - 1 - common], StringComparison.OrdinalIgnoreCase))
                common++;
            var viewPrefix = string.Join("\\", v.Take(v.Length - common));
            var realPrefix = string.Join("\\", r.Take(r.Length - common));
            if (realPrefix.Length == 0 || viewPrefix.Length == 0 || string.Equals(viewPrefix, realPrefix, StringComparison.OrdinalIgnoreCase)) return;
            lock (viewPrefixes)
            {
                if (viewPrefixes.Any(p => string.Equals(p.Real, realPrefix, StringComparison.OrdinalIgnoreCase))) return;
                viewPrefixes.Add((realPrefix, viewPrefix));
                viewPrefixes.Sort((a, b) => b.Real.Length.CompareTo(a.Real.Length));
            }
        }

        /// <summary>The path as the user works with it (see <see cref="RememberView"/>).</summary>
        public static string ToViewPath(string realPath)
        {
            lock (viewPrefixes)
                foreach (var (real, view) in viewPrefixes)
                    if (realPath.StartsWith(real + "\\", StringComparison.OrdinalIgnoreCase))
                        return view + realPath.Substring(real.Length);
            return realPath;
        }

        static string RealDirectory(string directory)
        {
            var handle = NativeMethods.CreateFileW(directory, 0, 7 /* read | write | delete sharing */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: needed to open a directory */, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return directory;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                uint length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return directory;
                var real = buffer.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + real.Substring(8);
                if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) return real.Substring(4);
                return real;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern uint GetFinalPathNameByHandleW(IntPtr handle, System.Text.StringBuilder path, uint length, uint flags);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);
        }

        public static string FromUri(string uri) => ToViewPath(Path.GetFullPath(new Uri(uri).LocalPath));

        public void Dispose()
        {
            try
            {
                if (connection != null && process != null && !process.HasExited)
                {
                    connection.RequestAsync("shutdown", null).Wait(1000);
                    connection.NotifyAsync("exit", null).Wait(500);
                }
            }
            catch (Exception) { }
            connection?.Dispose();
            try { if (process != null && !process.HasExited) process.Kill(); } catch (Exception) { }
            process?.Dispose();
            SetState(ClangdState.Stopped);
        }
    }
}
