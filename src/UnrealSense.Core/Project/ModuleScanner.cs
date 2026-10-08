using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Project
{
    /// <summary>Discovers *.Build.cs modules and *.uplugin plugins on disk.</summary>
    public static class ModuleScanner
    {
        static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Saved", "DerivedDataCache", "Content", ".git", ".vs", "node_modules",
        };

        public static IEnumerable<UnrealModule> ScanModules(string sourceDirectory, ModuleOwnerKind owner, string pluginName)
        {
            if (!Directory.Exists(sourceDirectory)) yield break;
            foreach (var buildCs in EnumerateFiles(sourceDirectory, "*.Build.cs"))
            {
                var module = new UnrealModule
                {
                    Name = Path.GetFileName(buildCs).Replace(".Build.cs", ""),
                    Directory = Path.GetDirectoryName(buildCs),
                    BuildCsPath = buildCs,
                    Owner = owner,
                    PluginName = pluginName,
                };
                try
                {
                    var deps = BuildCsParser.ParseDependencies(File.ReadAllText(buildCs));
                    module.PublicDependencies = deps.Public;
                    module.PrivateDependencies = deps.Private;
                }
                catch (IOException) { }
                yield return module;
            }
        }

        public static IEnumerable<UnrealPlugin> ScanPlugins(string pluginsDirectory, bool isEngine)
        {
            if (!Directory.Exists(pluginsDirectory)) yield break;
            foreach (var upluginPath in EnumerateFiles(pluginsDirectory, "*.uplugin"))
            {
                var plugin = new UnrealPlugin
                {
                    Name = Path.GetFileNameWithoutExtension(upluginPath),
                    UPluginPath = upluginPath,
                    IsEngine = isEngine,
                };
                try
                {
                    var json = JObject.Parse(File.ReadAllText(upluginPath));
                    plugin.FriendlyName = (string)json["FriendlyName"];
                    plugin.CanContainContent = (bool?)json["CanContainContent"] ?? false;
                    plugin.EnabledByDefault = (bool?)json["EnabledByDefault"] ?? true;
                }
                catch (Exception) { }

                var owner = isEngine ? ModuleOwnerKind.EnginePlugin : ModuleOwnerKind.ProjectPlugin;
                plugin.Modules.AddRange(ScanModules(Path.Combine(plugin.Directory, "Source"), owner, plugin.Name));
                yield return plugin;
            }
        }

        /// <summary>Recursive file search that skips build/output folders and never throws.</summary>
        public static IEnumerable<string> EnumerateFiles(string root, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files = Array.Empty<string>();
                string[] subdirs = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(dir, pattern);
                    subdirs = Directory.GetDirectories(dir);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                foreach (var f in files) yield return f;
                foreach (var d in subdirs)
                    if (!SkippedFolders.Contains(Path.GetFileName(d)))
                        stack.Push(d);
            }
        }
    }

    /// <summary>Lightweight, regex based reader of ModuleRules dependency lists.</summary>
    public static class BuildCsParser
    {
        static readonly Regex DependencyCall = new Regex(
            @"(?<kind>Public|Private)(?:Include)?DependencyModuleNames\s*\.\s*(?:AddRange|Add)\s*\((?<args>[^;]*?)\)\s*;",
            RegexOptions.Compiled | RegexOptions.Singleline);

        static readonly Regex StringLiteral = new Regex("\"(?<v>[^\"\\\\]*)\"", RegexOptions.Compiled);

        public static (List<string> Public, List<string> Private) ParseDependencies(string text)
        {
            text = StripComments(text);
            var pub = new List<string>();
            var priv = new List<string>();
            foreach (Match call in DependencyCall.Matches(text))
            {
                var list = call.Groups["kind"].Value == "Public" ? pub : priv;
                foreach (Match s in StringLiteral.Matches(call.Groups["args"].Value))
                    if (!list.Contains(s.Groups["v"].Value))
                        list.Add(s.Groups["v"].Value);
            }
            return (pub, priv);
        }

        static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(text, @"//[^\n]*", " ");
        }
    }
}
