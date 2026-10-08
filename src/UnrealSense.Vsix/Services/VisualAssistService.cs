using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace UnrealSense.Extension.Services
{
    internal enum VisualAssistState { NotInstalled, Enabled, Disabled, Unknown }

    /// <summary>
    /// Switches Visual Assist on and off so it does not compete with UnrealSense (both bind Alt+Shift+S/O/F and both
    /// add completion/navigation). Prefers Visual Assist's own Enable/Disable command, which takes effect immediately;
    /// falls back to enabling/disabling the extension through the VS Extension Manager, which needs a restart.
    /// </summary>
    internal static class VisualAssistService
    {
        // Known names first (cheap lookups); otherwise the command list is scanned once for VAssistX.*Enable*.
        static readonly string[] KnownToggleCommands = { "VAssistX.EnableDisableVA", "VAssistX.EnableDisable", "VAssistX.EnableDisableVisualAssist" };
        static bool searched;
        static string toggleCommand;
        static Guid toggleGuid;
        static uint toggleId;

        public static VisualAssistState GetState(out string description)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (FindToggleCommand())
            {
                // VA's toggle is a checkable menu item: latched means "enabled".
                var flags = QueryStatus(toggleGuid, toggleId);
                description = toggleCommand;
                if ((flags & (uint)OLECMDF.OLECMDF_SUPPORTED) == 0) return VisualAssistState.Unknown;
                return (flags & (uint)OLECMDF.OLECMDF_LATCHED) != 0 ? VisualAssistState.Enabled : VisualAssistState.Unknown;
            }

            var extension = ExtensionManager.FindVisualAssist();
            if (extension == null)
            {
                description = null;
                return VisualAssistState.NotInstalled;
            }
            description = extension.Name;
            return extension.IsEnabled ? VisualAssistState.Enabled : VisualAssistState.Disabled;
        }

        /// <summary>Returns a message for the status bar, or null when a restart prompt is needed (handled by the caller).</summary>
        public static ToggleResult Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (FindToggleCommand())
            {
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                dte.ExecuteCommand(toggleCommand);
                Log.Write($"Executed {toggleCommand}");
                var state = (QueryStatus(toggleGuid, toggleId) & (uint)OLECMDF.OLECMDF_LATCHED) != 0 ? "enabled" : "toggled";
                return new ToggleResult($"Visual Assist {state} ({toggleCommand}).", needsRestart: false);
            }

            var extension = ExtensionManager.FindVisualAssist();
            if (extension == null) return new ToggleResult("Visual Assist is not installed in this Visual Studio.", needsRestart: false);
            bool enable = !extension.IsEnabled;
            extension.SetEnabled(enable);
            Log.Write($"{(enable ? "Enabled" : "Disabled")} extension '{extension.Name}' through the Extension Manager");
            return new ToggleResult($"Visual Assist will be {(enable ? "enabled" : "disabled")} after Visual Studio restarts.", needsRestart: true);
        }

        public static void Restart()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell4 shell)
                shell.Restart((uint)__VSRESTARTTYPE.RESTART_Normal);
        }

        static bool FindToggleCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (searched) return toggleCommand != null;
            searched = true;
            if (!(Package.GetGlobalService(typeof(DTE)) is DTE dte)) return false;

            Command found = null;
            foreach (var name in KnownToggleCommands)
            {
                try { found = dte.Commands.Item(name); }
                catch (ArgumentException) { }
                catch (System.Runtime.InteropServices.COMException) { }
                if (found != null) break;
            }
            if (found == null)
            {
                foreach (Command command in dte.Commands)
                {
                    var name = command.Name;
                    if (name != null && name.StartsWith("VAssistX.", StringComparison.OrdinalIgnoreCase)
                        && name.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = command;
                        if (name.IndexOf("Disable", StringComparison.OrdinalIgnoreCase) >= 0) break;
                    }
                }
            }
            if (found == null) return false;
            toggleCommand = found.Name;
            toggleGuid = new Guid(found.Guid);
            toggleId = (uint)found.ID;
            Log.Write($"Visual Assist toggle command: {toggleCommand}");
            return true;
        }

        static uint QueryStatus(Guid group, uint id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SUIHostCommandDispatcher)) is IOleCommandTarget dispatcher)) return 0;
            var cmds = new[] { new OLECMD { cmdID = id } };
            return dispatcher.QueryStatus(ref group, 1, cmds, IntPtr.Zero) == VSConstants.S_OK ? cmds[0].cmdf : 0;
        }

        internal sealed class ToggleResult
        {
            public ToggleResult(string message, bool needsRestart)
            {
                Message = message;
                NeedsRestart = needsRestart;
            }

            public string Message { get; }
            public bool NeedsRestart { get; }
        }

        /// <summary>
        /// Late-bound access to Microsoft.VisualStudio.ExtensionManager (not part of the public SDK packages, and its
        /// assembly version follows the VS major version).
        /// </summary>
        sealed class ExtensionManager
        {
            readonly object manager;
            readonly Type managerInterface;
            readonly object extension;

            ExtensionManager(object manager, Type managerInterface, object extension, string name)
            {
                this.manager = manager;
                this.managerInterface = managerInterface;
                this.extension = extension;
                Name = name;
            }

            public string Name { get; }

            public bool IsEnabled => string.Equals(Get(extension, "State")?.ToString(), "Enabled", StringComparison.OrdinalIgnoreCase);

            public void SetEnabled(bool enable) =>
                managerInterface.GetMethod(enable ? "Enable" : "Disable").Invoke(manager, new[] { extension });

            public static ExtensionManager FindVisualAssist()
            {
                try
                {
                    var assembly = LoadAssembly();
                    var service = assembly?.GetType("Microsoft.VisualStudio.ExtensionManager.SVsExtensionManager");
                    var managerInterface = assembly?.GetType("Microsoft.VisualStudio.ExtensionManager.IVsExtensionManager");
                    if (service == null || managerInterface == null) return null;
                    var manager = Package.GetGlobalService(service);
                    if (manager == null) return null;

                    var installed = managerInterface.GetMethod("GetInstalledExtensions", Type.EmptyTypes)?.Invoke(manager, null) as IEnumerable;
                    if (installed == null) return null;
                    foreach (var ext in installed)
                    {
                        var header = Get(ext, "Header");
                        var name = Get(header, "Name") as string ?? "";
                        var author = Get(header, "Author") as string ?? "";
                        if (name.IndexOf("Visual Assist", StringComparison.OrdinalIgnoreCase) >= 0
                            || author.IndexOf("Whole Tomato", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new ExtensionManager(manager, managerInterface, ext, name);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"Extension Manager lookup failed: {ex.GetBaseException().Message}");
                }
                return null;
            }

            static Assembly LoadAssembly()
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Microsoft.VisualStudio.ExtensionManager");
                if (loaded != null) return loaded;
                foreach (var version in new[] { "18.0.0.0", "17.0.0.0" })
                {
                    try { return Assembly.Load($"Microsoft.VisualStudio.ExtensionManager, Version={version}, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"); }
                    catch (Exception) { }
                }
                return null;
            }

            static object Get(object target, string property)
            {
                if (target == null) return null;
                var type = target.GetType();
                var prop = type.GetProperty(property)
                           ?? type.GetInterfaces().Select(i => i.GetProperty(property)).FirstOrDefault(p => p != null);
                return prop?.GetValue(target);
            }
        }
    }
}
