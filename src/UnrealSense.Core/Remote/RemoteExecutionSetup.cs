using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Project;

namespace UnrealSense.Remote
{
    /// <summary>
    /// Whether a project lets external tools run Python in its editor (PythonScriptPlugin enabled and
    /// "Enable Remote Execution?" on), and turning it on. Both are off by default in Unreal.
    /// </summary>
    public static class RemoteExecutionSetup
    {
        public const string PluginName = "PythonScriptPlugin";
        public const string SettingsSection = "/Script/PythonScriptPlugin.PythonScriptPluginSettings";
        const string SettingKey = "bRemoteExecution";

        public static string EngineIniPath(string projectDirectory) => Path.Combine(projectDirectory, "Config", "DefaultEngine.ini");

        /// <summary>The .uproject turns the Python plugin on (it may also come in as a dependency of another plugin).</summary>
        public static bool IsPythonPluginListed(string uprojectPath)
        {
            try
            {
                var plugins = JObject.Parse(File.ReadAllText(uprojectPath))["Plugins"] as JArray;
                return plugins?.Any(p => string.Equals((string)p["Name"], PluginName, StringComparison.OrdinalIgnoreCase) && ((bool?)p["Enabled"] ?? false)) ?? false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Config/DefaultEngine.ini sets bRemoteExecution=True (the project setting the editor writes).</summary>
        public static bool IsRemoteExecutionEnabled(string projectDirectory)
        {
            var ini = EngineIniPath(projectDirectory);
            if (!File.Exists(ini)) return false;
            string section = null;
            bool enabled = false;
            foreach (var raw in File.ReadAllLines(ini))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                if (!string.Equals(section, SettingsSection, StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0 || !string.Equals(line.Substring(0, eq).Trim(), SettingKey, StringComparison.OrdinalIgnoreCase)) continue;
                enabled = IsTrue(line.Substring(eq + 1));
            }
            return enabled;
        }

        public static bool IsEnabled(UnrealProject project) => IsRemoteExecutionEnabled(project.ProjectDirectory);

        /// <summary>
        /// Enables the Python plugin in the .uproject and remote execution in Config/DefaultEngine.ini, leaving the rest
        /// of both files as they are. Read-only files (not checked out in Perforce) are made writable and added to
        /// <paramref name="madeWritable"/>, so the user can be told to check them out. Returns null on success,
        /// otherwise a message for the user.
        /// </summary>
        public static string Enable(string uprojectPath, List<string> madeWritable = null)
        {
            var projectDirectory = Path.GetDirectoryName(uprojectPath);
            var ini = EngineIniPath(projectDirectory);
            try
            {
                if (!IsPythonPluginListed(uprojectPath))
                {
                    TextFiles.MakeWritable(uprojectPath, madeWritable);
                    EnablePlugin(uprojectPath);
                }
                if (!IsRemoteExecutionEnabled(projectDirectory))
                {
                    TextFiles.MakeWritable(ini, madeWritable);
                    EnableSetting(ini);
                }
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                return e.Message;
            }
        }

        static void EnablePlugin(string uprojectPath)
        {
            var (text, encoding) = TextFiles.Read(uprojectPath);
            var json = JObject.Parse(text);
            if (!(json["Plugins"] is JArray plugins)) json["Plugins"] = plugins = new JArray();
            var entry = plugins.OfType<JObject>().FirstOrDefault(p => string.Equals((string)p["Name"], PluginName, StringComparison.OrdinalIgnoreCase));
            if (entry != null) entry["Enabled"] = true;
            else plugins.Add(new JObject { ["Name"] = PluginName, ["Enabled"] = true });

            // The editor writes .uproject files with tabs and CRLF.
            var builder = new StringBuilder();
            using (var writer = new JsonTextWriter(new StringWriter(builder)) { Formatting = Formatting.Indented, Indentation = 1, IndentChar = '\t' })
                json.WriteTo(writer);
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            File.WriteAllText(uprojectPath, builder.ToString().Replace("\r\n", "\n").Replace("\n", newLine) + (text.EndsWith("\n") ? newLine : ""), encoding);
        }

        static void EnableSetting(string ini)
        {
            var (text, encoding) = File.Exists(ini) ? TextFiles.Read(ini) : ("", new UTF8Encoding(false));
            var newLine = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
            var lines = Regex.Split(text, "\r?\n").ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            int sectionStart = lines.FindIndex(l => string.Equals(l.Trim(), "[" + SettingsSection + "]", StringComparison.OrdinalIgnoreCase));
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + SettingsSection + "]");
                lines.Add(SettingKey + "=True");
            }
            else
            {
                int end = sectionStart + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
                var existing = Enumerable.Range(sectionStart + 1, end - sectionStart - 1)
                    .Where(i => Regex.IsMatch(lines[i], @"^\s*" + SettingKey + @"\s*=", RegexOptions.IgnoreCase)).ToList();
                foreach (var i in existing) lines[i] = SettingKey + "=True";
                if (existing.Count == 0)
                {
                    // After the section's last non-blank line, so the blank line before the next section stays.
                    int insert = end;
                    while (insert > sectionStart + 1 && lines[insert - 1].Trim().Length == 0) insert--;
                    lines.Insert(insert, SettingKey + "=True");
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ini));
            File.WriteAllText(ini, string.Join(newLine, lines) + newLine, encoding);
        }

        static bool IsTrue(string value)
        {
            value = value.Trim();
            return value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
