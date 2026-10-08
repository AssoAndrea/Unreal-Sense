using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Project
{
    /// <summary>A resolved Unreal Engine installation.</summary>
    public sealed class EngineInstallation
    {
        public string RootDirectory { get; set; }
        public string EngineDirectory => Path.Combine(RootDirectory, "Engine");
        public Version Version { get; set; }
        public bool IsSourceBuild { get; set; }

        public string EditorExecutable => Path.Combine(EngineDirectory, "Binaries", "Win64", "UnrealEditor.exe");
        public string BuildBatch => Path.Combine(EngineDirectory, "Build", "BatchFiles", "Build.bat");
        public string UnrealBuildTool
        {
            get
            {
                var dir = Path.Combine(EngineDirectory, "Binaries", "DotNET", "UnrealBuildTool");
                var exe = Path.Combine(dir, "UnrealBuildTool.exe");
                return File.Exists(exe) ? exe : Path.Combine(dir, "UnrealBuildTool.dll");
            }
        }
        public string ObjectMacrosHeader => Path.Combine(EngineDirectory, "Source", "Runtime", "CoreUObject", "Public", "UObject", "ObjectMacros.h");

        public override string ToString() => $"UE {Version} ({RootDirectory})";
    }

    /// <summary>Resolves the EngineAssociation of a .uproject the same way UnrealVersionSelector does.</summary>
    public static class EngineLocator
    {
        public static EngineInstallation Resolve(string association, string projectDirectory)
        {
            string root = null;

            if (string.IsNullOrEmpty(association))
            {
                // Project lives inside an engine tree (e.g. a source build "Games" folder).
                root = FindEngineRootAbove(projectDirectory);
            }
            else if (association.StartsWith("{"))
            {
                root = ReadRegistry(Registry.CurrentUser, @"Software\Epic Games\Unreal Engine\Builds", association);
            }
            else if (Directory.Exists(association))
            {
                root = association;
            }
            else
            {
                root = ReadRegistry(Registry.LocalMachine, @"SOFTWARE\EpicGames\Unreal Engine\" + association, "InstalledDirectory")
                    ?? FromLauncherManifest(association)
                    ?? DefaultLauncherPath(association);
                // A source build can also be registered with a user-chosen name.
                if (root == null)
                    root = ReadRegistry(Registry.CurrentUser, @"Software\Epic Games\Unreal Engine\Builds", association);
            }

            if (root == null || !Directory.Exists(Path.Combine(root, "Engine")))
                return null;

            return new EngineInstallation
            {
                RootDirectory = Path.GetFullPath(root),
                Version = ReadBuildVersion(root),
                IsSourceBuild = File.Exists(Path.Combine(root, "Engine", "Build", "SourceDistribution.txt"))
                                || File.Exists(Path.Combine(root, "GenerateProjectFiles.bat")),
            };
        }

        static string FindEngineRootAbove(string directory)
        {
            var dir = new DirectoryInfo(directory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Engine", "Build", "Build.version")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        static string ReadRegistry(RegistryKey hive, string keyPath, string valueName)
        {
            try
            {
                using (var key = hive.OpenSubKey(keyPath))
                    return key?.GetValue(valueName) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string FromLauncherManifest(string association)
        {
            try
            {
                var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
                if (!File.Exists(manifest)) return null;
                var list = JObject.Parse(File.ReadAllText(manifest))["InstallationList"] as JArray;
                var entry = list?.FirstOrDefault(e => string.Equals((string)e["AppName"], "UE_" + association, StringComparison.OrdinalIgnoreCase));
                return (string)entry?["InstallLocation"];
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string DefaultLauncherPath(string association)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "UE_" + association);
            return Directory.Exists(path) ? path : null;
        }

        static Version ReadBuildVersion(string root)
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(root, "Engine", "Build", "Build.version")));
                return new Version((int)json["MajorVersion"], (int)json["MinorVersion"], (int?)json["PatchVersion"] ?? 0);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
