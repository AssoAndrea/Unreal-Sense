using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnrealSense.Analysis;
using UnrealSense.Assets;
using UnrealSense.Cpp;
using UnrealSense.Project;
using UnrealSense.Reflection;

namespace UnrealSense.Workspace
{
    public enum WorkspaceState { NotLoaded, Loading, Ready, Failed }

    /// <summary>
    /// Everything UnrealSense knows about one .uproject: project model, specifier catalog, C++ symbol index and
    /// Blueprint/asset index. Host-agnostic (used by the VSIX, the CLI and tests).
    /// </summary>
    public sealed class UnrealWorkspace : IDisposable
    {
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly ConcurrentDictionary<string, DateTime> pendingChanges = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly CancellationTokenSource disposal = new CancellationTokenSource();
        Timer debounceTimer;
        Lazy<IReadOnlyList<string>> engineModuleNames;

        public UnrealProject Project { get; private set; }
        public SpecifierCatalog Catalog { get; private set; } = SpecifierCatalog.CreateBuiltin();
        public SymbolIndex Symbols { get; private set; }
        public AssetIndex Assets { get; private set; }
        /// <summary>Go to symbol/file index (project + engine); cached results are usable while it refreshes.</summary>
        public Navigation.GoToIndex GoTo { get; } = new Navigation.GoToIndex();
        public WorkspaceState GoToState { get; private set; }
        IReadOnlyList<Navigation.IndexRoot> goToRoots;
        public WorkspaceState State { get; private set; }
        public WorkspaceState AssetState { get; private set; }
        public string StatusText { get; private set; } = "No Unreal project";
        public Exception LastError { get; private set; }

        /// <summary>Raised (on a background thread) whenever indexes or state change.</summary>
        public event EventHandler Changed;

        /// <summary>Per-phase load timings (for the log), raised on background threads.</summary>
        public event Action<string> Timing;

        public AnalysisContext CreateAnalysisContext() => new AnalysisContext { Catalog = Catalog, Symbols = Symbols, Project = Project };

        public async Task LoadAsync(string uprojectPath, IProgress<string> progress = null, bool watch = true)
        {
            var token = disposal.Token;
            try
            {
                SetState(WorkspaceState.Loading, "Loading " + Path.GetFileName(uprojectPath), progress);
                var total = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    Project = UnrealProject.Load(uprojectPath);
                    Catalog = SpecifierCatalog.Create(Project.Engine?.EngineDirectory);
                    engineModuleNames = new Lazy<IReadOnlyList<string>>(ScanEngineModules, LazyThreadSafetyMode.ExecutionAndPublication);
                }, token).ConfigureAwait(false);
                Timing?.Invoke($"project + specifier catalog: {total.Elapsed.TotalSeconds:F1}s");

                // The three indexes are independent: build them concurrently so a large Content folder does not
                // delay Go to Symbol (which serves its cache immediately anyway).
                GoToState = WorkspaceState.Loading;
                AssetState = WorkspaceState.Loading;
                goToRoots = Navigation.GoToRoots.For(Project);
                var goTo = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    GoTo.Build(goToRoots, Navigation.GoToRoots.CachePath(Project), progress, token);
                    GoToState = WorkspaceState.Ready;
                    Timing?.Invoke($"go-to index: {sw.Elapsed.TotalSeconds:F1}s ({GoTo.SymbolCount:N0} symbols, {GoTo.FileCount:N0} files)");
                    RaiseChanged();
                }, token);

                var assetsTask = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var assets = new AssetIndex(Project);
                    assets.Updated += (s, e) => RaiseChanged();
                    assets.Build(AssetIndex.DefaultCachePath(Project),
                        new Progress<(int Done, int Total)>(p => progress?.Report($"Indexing Blueprints {p.Done:N0}/{p.Total:N0}…")), token);
                    Assets = assets;
                    AssetState = WorkspaceState.Ready;
                    Timing?.Invoke($"asset index: {sw.Elapsed.TotalSeconds:F1}s ({assets.Count:N0} assets, {assets.LastBuildAnalyzed:N0} parsed)");
                }, token);

                await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    SetState(WorkspaceState.Loading, "Indexing C++ symbols…", progress);
                    var symbols = new SymbolIndex(Project);
                    symbols.Build(token);
                    symbols.Updated += (s, e) => RaiseChanged();
                    Symbols = symbols;
                    Timing?.Invoke($"reflection index: {sw.Elapsed.TotalSeconds:F1}s ({symbols.Types.Count()} reflected types)");
                }, token).ConfigureAwait(false);
                SetState(WorkspaceState.Ready, $"{Project.Name}: {Symbols.Types.Count()} reflected types", progress);
                if (watch) StartWatching();

                await assetsTask.ConfigureAwait(false);
                // Packages that are another name of a project module (renamed without a redirect we can read), then a summary.
                var projectTypes = Symbols.Headers.SelectMany(h =>
                {
                    var module = Project.FindModuleForFile(h.FilePath)?.Name;
                    return h.Types.Select(t => (module, t.ReflectedName));
                }).ToList();
                foreach (var line in Assets.LearnModuleAliases(projectTypes)) Timing?.Invoke("Blueprint index: " + line);
                // Blueprints of a C++ subclass count for its base classes too.
                Assets.NativeSubclasses = (module, type) =>
                {
                    if (Symbols == null) return Enumerable.Empty<(string, string)>();
                    var own = Symbols.FindTypesByReflectedName(type)
                        .Where(t => module == null || string.Equals(GetScriptModule(Symbols.FindTypeFile(t.Name)), module, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    // A class of the project: follow its subclasses. Otherwise (engine, external plugin) the project's
                    // classes that derive from it by name.
                    var derived = own.Count > 0 ? own.SelectMany(t => Symbols.GetDerivedTypes(t)) : Symbols.GetDerivedTypesOfExternal(type);
                    return derived.Select(d => (GetScriptModule(Symbols.FindTypeFile(d.Name)), d.ReflectedName)).ToList();
                };
                foreach (var line in Assets.DescribeCoverage()) Timing?.Invoke("Blueprint index: " + line);
                SetState(WorkspaceState.Ready, $"{Project.Name}: {Symbols.Types.Count()} reflected types, {Assets.Count:N0} assets", progress);

                await goTo.ConfigureAwait(false);
                Timing?.Invoke($"workspace loaded in {total.Elapsed.TotalSeconds:F1}s");
                RaiseChanged();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LastError = ex;
                if (Project == null || Symbols == null) SetState(WorkspaceState.Failed, "UnrealSense failed: " + ex.Message, progress);
                else AssetState = WorkspaceState.Failed;
                RaiseChanged();
            }
        }

        void SetState(WorkspaceState state, string status, IProgress<string> progress)
        {
            State = state;
            StatusText = status;
            progress?.Report(status);
            RaiseChanged();
        }

        void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // ------------------------------------------------------------ Blueprint usages

        /// <summary>Script module ("/Script/X") name for a source file: its owning UBT module.</summary>
        public string GetScriptModule(string sourceFile) => Project?.FindModuleForFile(sourceFile)?.Name;

        public List<AssetUsage> FindUsages(ReflectedType type, string sourceFile)
        {
            if (Assets == null || type?.Name == null) return new List<AssetUsage>();
            return Assets.FindTypeUsages(GetScriptModule(sourceFile), type.ReflectedName);
        }

        public List<AssetUsage> FindUsages(ReflectedMember member, string sourceFile)
        {
            if (Assets == null || member?.Owner?.Name == null) return new List<AssetUsage>();
            var module = GetScriptModule(sourceFile);
            var owner = member.Owner.ReflectedName;
            return member is ReflectedFunction
                ? Assets.FindFunctionUsages(module, owner, member.Name)
                : Assets.FindPropertyUsages(module, owner, member.Name);
        }

        // ------------------------------------------------------------ module names (Build.cs completion)

        public IReadOnlyList<string> EngineModuleNames => engineModuleNames?.Value ?? Array.Empty<string>();

        public IEnumerable<string> AllModuleNames =>
            (Project?.AllModules.Select(m => m.Name) ?? Enumerable.Empty<string>())
                .Concat(EngineModuleNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> ScanEngineModules()
        {
            var engine = Project?.Engine;
            if (engine == null) return Array.Empty<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { "Source", "Plugins" })
            {
                var dir = Path.Combine(engine.EngineDirectory, root);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in ModuleScanner.EnumerateFiles(dir, "*.Build.cs"))
                    names.Add(Path.GetFileName(file).Replace(".Build.cs", ""));
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------------------ file watching

        void StartWatching()
        {
            debounceTimer = new Timer(_ => FlushChanges(), null, Timeout.Infinite, Timeout.Infinite);
            var roots = new List<string> { Project.SourceDirectory, Project.ContentDirectory, Path.Combine(Project.ProjectDirectory, "Plugins") };
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
                    watcher.Changed += OnFileEvent;
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += (s, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception) { }
            }
        }

        void OnFileEvent(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

        void Queue(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".h" && ext != ".cpp" && ext != ".inl" && ext != ".uasset" && ext != ".umap" && !path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase)) return;
            if (path.IndexOf("\\Intermediate\\", StringComparison.OrdinalIgnoreCase) >= 0) return;
            pendingChanges[path] = DateTime.UtcNow;
            debounceTimer?.Change(750, Timeout.Infinite);
        }

        void FlushChanges()
        {
            bool goToDirty = false;
            var changedCode = new List<string>();
            foreach (var path in pendingChanges.Keys.ToList())
            {
                pendingChanges.TryRemove(path, out _);
                try
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".uasset" && ext != ".umap") changedCode.Add(path);
                    if (ext == ".cs") continue;
                    if (ext == ".uasset" || ext == ".umap")
                        Assets?.Refresh(path);
                    else if (File.Exists(path))
                    {
                        var text = File.ReadAllText(path);
                        Symbols?.Update(path, text);
                        if (goToRoots != null) { GoTo.Refresh(path, goToRoots, text, publish: false); goToDirty = true; }
                    }
                    else
                    {
                        Symbols?.Remove(path);
                        if (goToRoots != null) { GoTo.Refresh(path, goToRoots, publish: false); goToDirty = true; }
                    }
                }
                catch (IOException)
                {
                    pendingChanges[path] = DateTime.UtcNow; // file still locked (editor saving): retry
                    debounceTimer?.Change(1500, Timeout.Infinite);
                }
                catch (Exception) { }
            }
            if (goToDirty) GoTo.Commit(goToRoots);
            if (changedCode.Count > 0) CodeFilesChanged?.Invoke(changedCode);
        }

        /// <summary>Source files created/changed/deleted on disk (after debouncing), e.g. to refresh clangd.</summary>
        public event Action<IReadOnlyList<string>> CodeFilesChanged;

        public void Dispose()
        {
            disposal.Cancel();
            foreach (var w in watchers) w.Dispose();
            watchers.Clear();
            debounceTimer?.Dispose();
        }
    }
}
