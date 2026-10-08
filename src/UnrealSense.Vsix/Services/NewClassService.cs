using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnrealSense.Project;
using UnrealSense.Templates;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Engine classes for the New Unreal Class dialog: scanned once per engine and session in the background (the
    /// first scan of an engine reads ~26,000 headers, later ones only check time stamps against the disk cache).
    /// </summary>
    internal static class NewClassService
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, Task<List<ParentClassInfo>>> scans = new Dictionary<string, Task<List<ParentClassInfo>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The engine scan, started on first request; a completed scan is reused, a failed or empty one retried.</summary>
        public static Task<List<ParentClassInfo>> GetEngineClassesAsync(EngineInstallation engine)
        {
            if (engine == null) return Task.FromResult(new List<ParentClassInfo>());
            lock (gate)
            {
                if (scans.TryGetValue(engine.RootDirectory, out var running) && !(running.IsCompleted && running.Result.Count == 0))
                    return running;
                var task = Task.Run(() =>
                {
                    try
                    {
                        return EngineClassScanner.Scan(engine, EngineClassScanner.DefaultCachePath(engine), line => Log.Write("NewUnrealClass: " + line));
                    }
                    catch (Exception e)
                    {
                        Log.Error("NewUnrealClass: engine class scan", e);
                        return new List<ParentClassInfo>();
                    }
                });
                scans[engine.RootDirectory] = task;
                return task;
            }
        }

        /// <summary>Catalog of the project's current types plus the engine's (when scanned).</summary>
        public static ParentClassCatalog CreateCatalog(UnrealWorkspace workspace, List<ParentClassInfo> engineClasses) =>
            ParentClassCatalog.Create(workspace.Project, workspace.Symbols, engineClasses);
    }
}
