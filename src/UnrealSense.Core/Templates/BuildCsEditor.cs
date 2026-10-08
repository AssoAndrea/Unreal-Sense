using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnrealSense.Project;

namespace UnrealSense.Templates
{
    /// <summary>Adds a module to a .Build.cs's PublicDependencyModuleNames, keeping its formatting.</summary>
    public static class BuildCsEditor
    {
        static readonly Regex AddRange = new Regex(
            @"PublicDependencyModuleNames\s*\.\s*AddRange\s*\(\s*new\s+(?:string\s*)?\[\s*\]\s*\{(?<items>[^}]*)\}",
            RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Constructor = new Regex(@":\s*base\s*\(\s*Target\s*\)\s*\{", RegexOptions.Compiled);

        /// <summary>Returns the new text (the same text when the module is already listed), or null when nothing could be found to edit.</summary>
        public static string AddPublicDependency(string text, string moduleName)
        {
            var deps = BuildCsParser.ParseDependencies(text);
            if (deps.Public.Contains(moduleName) || deps.Private.Contains(moduleName)) return text;
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";

            var m = AddRange.Match(text);
            if (m.Success)
            {
                var items = m.Groups["items"];
                var literals = Regex.Matches(items.Value, "\"[^\"]*\"");
                if (literals.Count > 0)
                {
                    var last = literals[literals.Count - 1];
                    int insertAt = items.Index + last.Index + last.Length;
                    // Keep a trailing comma style: "Slate" or "Slate," before the brace.
                    var after = text.Substring(insertAt, items.Index + items.Length - insertAt);
                    bool trailingComma = after.TrimStart().StartsWith(",");
                    if (trailingComma) insertAt += after.IndexOf(',') + 1;
                    bool multiline = items.Value.Contains("\n");
                    string separator;
                    if (multiline)
                    {
                        int lineStart = text.LastIndexOf('\n', items.Index + last.Index) + 1;
                        var indent = Regex.Match(text.Substring(lineStart), @"^[ \t]*").Value;
                        separator = (trailingComma ? "" : ",") + newLine + indent;
                    }
                    else separator = trailingComma ? " " : ", ";
                    var insertion = separator + "\"" + moduleName + "\"" + (trailingComma ? "," : "");
                    return text.Insert(insertAt, insertion);
                }
                int braceClose = items.Index + items.Length;
                return text.Insert(braceClose, " \"" + moduleName + "\" ");
            }

            var ctor = Constructor.Match(text);
            if (!ctor.Success) return null;
            int lineEnd = ctor.Index + ctor.Length;
            int nextLine = text.IndexOf('\n', ctor.Index);
            var bodyIndent = Regex.Match(text.Substring(nextLine + 1), @"^[ \t]*").Value;
            if (bodyIndent.Length == 0) bodyIndent = "\t\t";
            return text.Insert(lineEnd, newLine + bodyIndent + $"PublicDependencyModuleNames.Add(\"{moduleName}\");" + newLine);
        }

        /// <summary>Edits the file in place; returns an error message or null.</summary>
        public static string AddPublicDependencyToFile(string buildCsPath, string moduleName, List<string> madeWritable = null)
        {
            try
            {
                var (text, encoding) = TextFiles.Read(buildCsPath);
                var updated = AddPublicDependency(text, moduleName);
                if (updated == null) return $"Could not find where to add {moduleName} in {Path.GetFileName(buildCsPath)}.";
                if (ReferenceEquals(updated, text)) return null;
                TextFiles.MakeWritable(buildCsPath, madeWritable);
                File.WriteAllText(buildCsPath, updated, encoding);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return e.Message;
            }
        }
    }
}
