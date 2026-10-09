using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Restores the Visual Studio indexing that versions up to 0.3.18 could turn off for the clangd index (removed): the C++
    /// browsing database (Browse.VC.db) and the Blueprint scanning of Microsoft's Unreal Engine integration. The values
    /// the user had were saved in UnrealSense's settings, so "Restore" puts back exactly those.
    /// </summary>
    internal static class VisualStudioTuning
    {
        const string BackupCollection = "UnrealSense\\VisualStudioTuning";
        const string UePrefix = "Microsoft.VisualStudio.VC.UnrealEngineTools.ToolsOptionsPage.";

        sealed class Tweak
        {
            public string Id;
            public string Label;
            public bool Desired;
            public Func<bool?> Read;
            public Action<bool> Write;
        }

        static IEnumerable<Tweak> Tweaks()
        {
            yield return CppOption("DisableDatabase", "C++ browsing database (Browse.VC.db)", desired: true);
            // Defaults from Microsoft's UnrealEngineToolsRegistration.json (used when the user never changed them).
            yield return UeSetting("IsBlueprintSupportEnabled", "Unreal Engine integration: Blueprint scanning", desired: false, defaultValue: true);
            yield return UeSetting("IsEngineBlueprintSupportEnabled", "Unreal Engine integration: engine Blueprint scanning", desired: false, defaultValue: false);
            yield return UeSetting("UEConfigurationPageShowOnStartup", "Unreal Engine integration: configuration page on startup", desired: false, defaultValue: true);
        }

        public static bool IsApplied
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Store().CollectionExists(BackupCollection);
            }
        }

        public static List<string> Restore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = Store();
            var log = new List<string>();
            if (!store.CollectionExists(BackupCollection)) return log;
            foreach (var t in Tweaks())
            {
                if (!store.PropertyExists(BackupCollection, t.Id)) continue;
                try
                {
                    t.Write(store.GetBoolean(BackupCollection, t.Id));
                    log.Add($"{t.Label}: restored");
                }
                catch (Exception ex)
                {
                    log.Add($"{t.Label}: restore failed ({ex.GetBaseException().Message})");
                }
            }
            store.DeleteCollection(BackupCollection);
            foreach (var line in log) Log.Write("VS tuning: " + line);
            return log;
        }

        static WritableSettingsStore Store() =>
            new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);

        /// <summary>Tools › Options › Text Editor › C/C++ › Advanced, through DTE (same names as the options page).</summary>
        static Tweak CppOption(string name, string label, bool desired) => new Tweak
        {
            Id = "cpp." + name,
            Label = label,
            Desired = desired,
            Read = () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var property = CppProperty(name);
                return property == null ? (bool?)null : Convert.ToBoolean(property.Value);
            },
            Write = value =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var property = CppProperty(name) ?? throw new InvalidOperationException("C/C++ option not found: " + name);
                property.Value = value;
            },
        };

        static Property CppProperty(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                return dte?.Properties["TextEditor", "C/C++ Specific"]?.Item(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Settings of Microsoft's Unreal Engine integration (stored in the VS settings manager).</summary>
        static Tweak UeSetting(string name, string label, bool desired, bool defaultValue) => new Tweak
        {
            Id = "ue." + name,
            Label = label,
            Desired = desired,
            Read = () =>
            {
                var manager = SettingsManager();
                if (manager == null) return null;
                return manager.GetValueOrDefault(UePrefix + name, defaultValue);
            },
            Write = value =>
            {
                var manager = SettingsManager() ?? throw new InvalidOperationException("Settings manager unavailable");
                ThreadHelper.JoinableTaskFactory.Run(() => manager.SetValueAsync(UePrefix + name, value, isMachineLocal: false));
            },
        };

        static ISettingsManager SettingsManager() =>
            Package.GetGlobalService(typeof(Microsoft.Internal.VisualStudio.Shell.Interop.SVsSettingsPersistenceManager)) as ISettingsManager;
    }
}
