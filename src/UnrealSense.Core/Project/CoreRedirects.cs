using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnrealSense.Project
{
    /// <summary>
    /// Package/class/function redirects declared in config (ActiveGameNameRedirects, ActiveClassRedirects,
    /// [CoreRedirects]). Assets keep the old names in their import tables, so we need these to map
    /// "/Script/TP_FirstPerson.TP_FirstPersonCharacter" back to the C++ class AGymCharacter.
    /// </summary>
    public sealed class CoreRedirects
    {
        static readonly Regex Entry = new Regex(@"^\s*[+.\-!]?(?<key>\w+)\s*=\s*\((?<body>.*)\)\s*$", RegexOptions.Compiled);
        static readonly Regex Field = new Regex(@"(?<k>\w+)\s*=\s*(?:""(?<v>[^""]*)""|(?<v>[^,)\s]+))", RegexOptions.Compiled);

        readonly Dictionary<string, string> packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count => packages.Count + classes.Count + functions.Count + properties.Count;

        public static CoreRedirects Load(UnrealProject project)
        {
            var redirects = new CoreRedirects();
            var files = new List<string>();
            if (Directory.Exists(project.ConfigDirectory))
                files.AddRange(Directory.GetFiles(project.ConfigDirectory, "Default*.ini"));
            foreach (var plugin in project.Plugins)
            {
                var config = Path.Combine(plugin.Directory, "Config");
                if (Directory.Exists(config))
                    files.AddRange(Directory.GetFiles(config, "*.ini"));
            }
            foreach (var file in files)
            {
                try { redirects.AddFromIni(File.ReadAllLines(file)); }
                catch (IOException) { }
            }
            return redirects;
        }

        public void AddFromIni(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                var m = Entry.Match(line);
                if (!m.Success) continue;
                var fields = Field.Matches(m.Groups["body"].Value).Cast<Match>()
                    .GroupBy(f => f.Groups["k"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value, StringComparer.OrdinalIgnoreCase);

                switch (m.Groups["key"].Value)
                {
                    case "ActiveGameNameRedirects":
                        if (fields.TryGetValue("OldGameName", out var oldGame) && fields.TryGetValue("NewGameName", out var newGame))
                            packages[NormalizePackage(oldGame)] = NormalizePackage(newGame);
                        break;
                    case "ActiveClassRedirects":
                        if (fields.TryGetValue("OldClassName", out var oldClass) && fields.TryGetValue("NewClassName", out var newClass))
                            classes[ShortName(oldClass)] = ShortName(newClass);
                        break;
                    case "PackageRedirects":
                        if (fields.TryGetValue("OldName", out var oldPkg) && fields.TryGetValue("NewName", out var newPkg))
                            packages[oldPkg] = newPkg;
                        break;
                    case "ClassRedirects":
                    case "StructRedirects":
                    case "EnumRedirects":
                        if (fields.TryGetValue("OldName", out var oldType) && fields.TryGetValue("NewName", out var newType))
                        {
                            classes[ShortName(oldType)] = ShortName(newType);
                            var oldPackage = PackageOf(oldType);
                            var newPackage = PackageOf(newType);
                            if (oldPackage != null && newPackage != null && !string.Equals(oldPackage, newPackage, StringComparison.OrdinalIgnoreCase))
                                packages[oldPackage] = newPackage;
                        }
                        break;
                    case "FunctionRedirects":
                        if (fields.TryGetValue("OldName", out var oldFn) && fields.TryGetValue("NewName", out var newFn))
                            functions[MemberKey(oldFn)] = MemberKey(newFn);
                        break;
                    case "PropertyRedirects":
                        if (fields.TryGetValue("OldName", out var oldProp) && fields.TryGetValue("NewName", out var newProp))
                            properties[MemberKey(oldProp)] = MemberKey(newProp);
                        break;
                }
            }
        }

        /// <summary>"/Script/TP_FirstPerson" → "/Script/Gym".</summary>
        public string RedirectPackage(string package)
        {
            for (int guard = 0; guard < 8 && package != null && packages.TryGetValue(package, out var next); guard++)
                package = next;
            return package;
        }

        /// <summary>"TP_FirstPersonCharacter" → "GymCharacter" (names without the C++ prefix).</summary>
        public string RedirectClass(string className)
        {
            for (int guard = 0; guard < 8 && className != null && classes.TryGetValue(className, out var next); guard++)
                className = next;
            return className;
        }

        /// <summary>Redirects a member name given its owner ("Class.Member" key, short names).</summary>
        public string RedirectFunction(string className, string functionName) => RedirectMember(functions, className, functionName);

        public string RedirectProperty(string className, string propertyName) => RedirectMember(properties, className, propertyName);

        static string RedirectMember(Dictionary<string, string> map, string className, string member)
        {
            if (map.TryGetValue(className + "." + member, out var full))
            {
                var dot = full.LastIndexOf('.');
                return dot >= 0 ? full.Substring(dot + 1) : full;
            }
            return member;
        }

        static string NormalizePackage(string name) =>
            name.StartsWith("/") ? name : "/Script/" + name;

        static string PackageOf(string path)
        {
            if (!path.StartsWith("/")) return null;
            var dot = path.IndexOf('.');
            return dot > 0 ? path.Substring(0, dot) : null;
        }

        static string ShortName(string path)
        {
            var dot = path.LastIndexOf('.');
            return dot >= 0 ? path.Substring(dot + 1) : path;
        }

        /// <summary>"/Script/Mod.Class.Func" or "Class.Func" → "Class.Func".</summary>
        static string MemberKey(string path)
        {
            var parts = path.Split('.', ':');
            return parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : path;
        }
    }
}
