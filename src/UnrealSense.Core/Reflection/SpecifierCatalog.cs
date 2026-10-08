using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnrealSense.Reflection
{
    [Flags]
    public enum SpecifierTarget
    {
        None = 0,
        Class = 1 << 0,
        Interface = 1 << 1,
        Struct = 1 << 2,
        Enum = 1 << 3,
        Property = 1 << 4,
        Function = 1 << 5,
        Param = 1 << 6,
        EnumValue = 1 << 7,
        Delegate = 1 << 8,
        AnyField = Class | Interface | Struct | Enum | Property | Function | Param | EnumValue | Delegate,
    }

    public sealed class SpecifierInfo
    {
        public string Name { get; set; }
        public SpecifierTarget Targets { get; set; }
        public bool IsMeta { get; set; }
        public bool TakesValue { get; set; }
        public string Documentation { get; set; }
        public override string ToString() => (IsMeta ? "meta:" : "") + Name;
    }

    /// <summary>
    /// Known reflection specifiers and metadata keys. A built-in table provides value/target information
    /// for everything UHT accepts; when an engine is available, documentation is refreshed from
    /// Engine/Source/Runtime/CoreUObject/Public/UObject/ObjectMacros.h so it always matches the engine version.
    /// </summary>
    public sealed class SpecifierCatalog
    {
        readonly Dictionary<string, SpecifierInfo> specifiers = new Dictionary<string, SpecifierInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SpecifierInfo> meta = new Dictionary<string, SpecifierInfo>(StringComparer.OrdinalIgnoreCase);

        public static SpecifierTarget TargetForMacro(string macroName)
        {
            switch (macroName)
            {
                case "UCLASS": return SpecifierTarget.Class;
                case "UINTERFACE": return SpecifierTarget.Interface;
                case "USTRUCT": return SpecifierTarget.Struct;
                case "UENUM": return SpecifierTarget.Enum;
                case "UPROPERTY": return SpecifierTarget.Property;
                case "UFUNCTION": return SpecifierTarget.Function;
                case "UPARAM": return SpecifierTarget.Param;
                case "UMETA": return SpecifierTarget.EnumValue;
                case "UDELEGATE": return SpecifierTarget.Delegate;
                default: return SpecifierTarget.None;
            }
        }

        public static readonly string[] MacroNames = { "UCLASS", "UINTERFACE", "USTRUCT", "UENUM", "UPROPERTY", "UFUNCTION", "UPARAM", "UMETA", "UDELEGATE" };

        public static SpecifierCatalog CreateBuiltin()
        {
            var catalog = new SpecifierCatalog();
            catalog.LoadTable(BuiltinTable.Data);
            return catalog;
        }

        /// <summary>
        /// True when the specifier list comes from the engine's UnrealHeaderTool sources, i.e. it is exactly
        /// what UHT accepts. Only then is it safe to report unknown specifiers.
        /// </summary>
        public bool IsAuthoritative { get; private set; }

        /// <summary>
        /// Built-in table, plus the exact specifier tables of the engine's UHT sources and the documentation of
        /// ObjectMacros.h when an engine directory is available.
        /// </summary>
        public static SpecifierCatalog Create(string engineDirectory)
        {
            var catalog = CreateBuiltin();
            if (engineDirectory == null) return catalog;
            try
            {
                var uhtSpecifiers = Path.Combine(engineDirectory, "Source", "Programs", "Shared", "EpicGames.UHT", "Specifiers");
                if (Directory.Exists(uhtSpecifiers))
                    catalog.MergeUhtSources(Directory.GetFiles(uhtSpecifiers, "*.cs").Select(File.ReadAllText));

                var objectMacros = Path.Combine(engineDirectory, "Source", "Runtime", "CoreUObject", "Public", "UObject", "ObjectMacros.h");
                if (File.Exists(objectMacros))
                    catalog.MergeObjectMacros(File.ReadAllText(objectMacros));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return catalog;
        }

        static readonly Regex UhtSpecifierRegex = new Regex(
            @"\[UhtSpecifier\((?<args>[^\]]*)\)\]\s*(?:\[[^\]]*\]\s*)*(?:(?:private|public|internal|protected)\s+)?static\s+void\s+(?<method>\w+)\s*\(",
            RegexOptions.Compiled);
        static readonly Regex UhtArgRegex = new Regex(@"(?<k>\w+)\s*=\s*(?:""(?<v>[^""]*)""|(?:\w+\.)?(?<v>\w+))", RegexOptions.Compiled);

        /// <summary>Reads [UhtSpecifier] attributes from EpicGames.UHT/Specifiers/*.cs.</summary>
        public void MergeUhtSources(IEnumerable<string> sources)
        {
            int added = 0;
            foreach (var source in sources)
            {
                foreach (Match m in UhtSpecifierRegex.Matches(source))
                {
                    var args = UhtArgRegex.Matches(m.Groups["args"].Value).Cast<Match>()
                        .ToDictionary(a => a.Groups["k"].Value, a => a.Groups["v"].Value, StringComparer.Ordinal);
                    if (!args.TryGetValue("Extends", out var table)) continue;
                    var targets = TargetsForUhtTable(table);
                    if (targets == SpecifierTarget.None) continue;

                    var method = m.Groups["method"].Value;
                    var name = args.TryGetValue("Name", out var explicitName) ? explicitName
                        : method.EndsWith("Specifier", StringComparison.Ordinal) ? method.Substring(0, method.Length - "Specifier".Length) : method;
                    args.TryGetValue("ValueType", out var valueType);
                    bool takesValue = valueType == "String" || valueType == "SingleString" || valueType == "StringList"
                                      || valueType == "NonEmptyStringList" || valueType == "KeyValuePairList";

                    Add(new SpecifierInfo { Name = name, Targets = targets, IsMeta = false, TakesValue = takesValue });
                    added++;
                }
            }
            IsAuthoritative = added > 50;
        }

        static SpecifierTarget TargetsForUhtTable(string table)
        {
            switch (table)
            {
                case "Default": return SpecifierTarget.Class | SpecifierTarget.Interface | SpecifierTarget.Struct | SpecifierTarget.Enum
                                       | SpecifierTarget.Property | SpecifierTarget.Function | SpecifierTarget.Param | SpecifierTarget.Delegate;
                case "Object":
                case "Field": return SpecifierTarget.Class | SpecifierTarget.Interface | SpecifierTarget.Struct | SpecifierTarget.Enum | SpecifierTarget.Function | SpecifierTarget.Delegate;
                case "Struct": return SpecifierTarget.Class | SpecifierTarget.Interface | SpecifierTarget.Struct | SpecifierTarget.Function | SpecifierTarget.Delegate;
                case "ClassBase": return SpecifierTarget.Class | SpecifierTarget.Interface;
                case "Class": return SpecifierTarget.Class;
                case "Interface": return SpecifierTarget.Interface;
                case "Enum": return SpecifierTarget.Enum;
                case "Function": return SpecifierTarget.Function | SpecifierTarget.Delegate;
                case "ScriptStruct": return SpecifierTarget.Struct;
                case "PropertyMember": return SpecifierTarget.Property;
                case "PropertyArgument": return SpecifierTarget.Param;
                default: return SpecifierTarget.None; // Global, NativeInterface, VModule, Partial
            }
        }

        public IEnumerable<SpecifierInfo> GetSpecifiers(SpecifierTarget target) =>
            specifiers.Values.Where(s => (s.Targets & target) != 0).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

        public IEnumerable<SpecifierInfo> GetMeta(SpecifierTarget target) =>
            meta.Values.Where(s => (s.Targets & target) != 0).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

        public SpecifierInfo FindSpecifier(SpecifierTarget target, string name) =>
            specifiers.TryGetValue(Key(target, name), out var s) ? s : null;

        public SpecifierInfo FindMeta(SpecifierTarget target, string name) =>
            meta.TryGetValue(Key(target, name), out var s) ? s : null;

        public int Count => specifiers.Count + meta.Count;

        // Each info is stored once per target bit so lookups are O(1).
        static string Key(SpecifierTarget target, string name) => ((int)target).ToString() + ":" + name;

        void Add(SpecifierInfo info)
        {
            var map = info.IsMeta ? meta : specifiers;
            foreach (SpecifierTarget bit in Enum.GetValues(typeof(SpecifierTarget)))
            {
                if (bit == SpecifierTarget.None || bit == SpecifierTarget.AnyField || (info.Targets & bit) == 0) continue;
                var key = Key(bit, info.Name);
                if (map.TryGetValue(key, out var existing))
                {
                    if (string.IsNullOrEmpty(existing.Documentation)) existing.Documentation = info.Documentation;
                    existing.TakesValue |= info.TakesValue;
                    existing.Targets |= info.Targets & bit;
                }
                else
                {
                    map[key] = new SpecifierInfo { Name = info.Name, Targets = bit, IsMeta = info.IsMeta, TakesValue = info.TakesValue, Documentation = info.Documentation };
                }
            }
        }

        /// <summary>Rows: "targets|name|=|doc" where targets are letters (C I S E P F A M D), 'm' prefix = meta, '=' = takes a value.</summary>
        void LoadTable(string table)
        {
            foreach (var raw in table.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { '|' }, 4);
                if (parts.Length < 2) continue;
                var targets = parts[0];
                bool isMeta = targets.StartsWith("m");
                Add(new SpecifierInfo
                {
                    Name = parts[1].Trim(),
                    Targets = ParseTargets(isMeta ? targets.Substring(1) : targets),
                    IsMeta = isMeta,
                    TakesValue = parts.Length > 2 && parts[2].Trim() == "=",
                    Documentation = parts.Length > 3 ? parts[3].Trim() : null,
                });
            }
        }

        static SpecifierTarget ParseTargets(string letters)
        {
            var result = SpecifierTarget.None;
            foreach (var c in letters)
            {
                switch (c)
                {
                    case 'C': result |= SpecifierTarget.Class; break;
                    case 'I': result |= SpecifierTarget.Interface; break;
                    case 'S': result |= SpecifierTarget.Struct; break;
                    case 'E': result |= SpecifierTarget.Enum; break;
                    case 'P': result |= SpecifierTarget.Property; break;
                    case 'F': result |= SpecifierTarget.Function; break;
                    case 'A': result |= SpecifierTarget.Param; break;
                    case 'M': result |= SpecifierTarget.EnumValue; break;
                    case 'D': result |= SpecifierTarget.Delegate; break;
                    case '*': result |= SpecifierTarget.AnyField; break;
                }
            }
            return result;
        }

        static readonly Regex NamespaceRegex = new Regex(@"^namespace\s+(U[CIFPSM])\s*$", RegexOptions.Compiled);
        static readonly Regex EnumeratorRegex = new Regex(@"^\s*([A-Za-z_]\w*)\s*(=\s*[^,]*)?,?\s*(//.*)?$", RegexOptions.Compiled);
        static readonly Regex MetaTagRegex = new Regex(@"^\[(\w+)Metadata\]\s*", RegexOptions.Compiled);

        /// <summary>Reads the documented UC/UI/UF/UP/US/UM enums of ObjectMacros.h.</summary>
        public void MergeObjectMacros(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            string ns = null;
            int braceDepth = 0;
            var metaTarget = SpecifierTarget.AnyField;
            var doc = new StringBuilder();

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (ns == null)
                {
                    var m = NamespaceRegex.Match(line);
                    if (m.Success) { ns = m.Groups[1].Value; braceDepth = 0; }
                    continue;
                }

                if (line.StartsWith("{")) { braceDepth++; continue; }
                if (line.StartsWith("}"))
                {
                    if (--braceDepth <= 0) ns = null;
                    doc.Clear();
                    continue;
                }

                if (line.StartsWith("// Metadata usable in", StringComparison.Ordinal))
                {
                    metaTarget = MetaTargetFromComment(line);
                    continue;
                }
                if (line.StartsWith("///"))
                {
                    if (doc.Length > 0) doc.Append(' ');
                    doc.Append(line.Substring(3).Trim());
                    continue;
                }
                if (line.StartsWith("//") || line.Length == 0 || line.StartsWith("enum")) continue;

                var e = EnumeratorRegex.Match(line);
                if (!e.Success || braceDepth < 2) { doc.Clear(); continue; }

                var name = e.Groups[1].Value;
                var documentation = doc.ToString();
                doc.Clear();
                if (ns == "UM")
                {
                    var tag = MetaTagRegex.Match(documentation);
                    var target = metaTarget;
                    if (tag.Success)
                    {
                        target = TagTarget(tag.Groups[1].Value, metaTarget);
                        documentation = documentation.Substring(tag.Length);
                    }
                    var existing = meta.Values.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    Add(new SpecifierInfo { Name = name, Targets = target, IsMeta = true, TakesValue = existing?.TakesValue ?? false, Documentation = documentation });
                    UpdateDocumentation(meta, name, target, documentation);
                }
                else
                {
                    var target = ns == "UC" ? SpecifierTarget.Class
                        : ns == "UI" ? SpecifierTarget.Interface
                        : ns == "UF" ? SpecifierTarget.Function
                        : ns == "UP" ? SpecifierTarget.Property
                        : SpecifierTarget.Struct;
                    Add(new SpecifierInfo { Name = name, Targets = target, IsMeta = false, Documentation = documentation });
                    UpdateDocumentation(specifiers, name, target, documentation);
                }
            }
        }

        // Engine docs win over built-in docs.
        static void UpdateDocumentation(Dictionary<string, SpecifierInfo> map, string name, SpecifierTarget target, string documentation)
        {
            if (string.IsNullOrWhiteSpace(documentation)) return;
            foreach (SpecifierTarget bit in Enum.GetValues(typeof(SpecifierTarget)))
            {
                if (bit == SpecifierTarget.None || bit == SpecifierTarget.AnyField || (target & bit) == 0) continue;
                if (map.TryGetValue(Key(bit, name), out var info))
                    info.Documentation = documentation;
            }
        }

        static SpecifierTarget MetaTargetFromComment(string comment)
        {
            if (comment.Contains("any UField")) return SpecifierTarget.AnyField;
            if (comment.Contains("UCLASS")) return SpecifierTarget.Class;
            if (comment.Contains("USTRUCT")) return SpecifierTarget.Struct;
            if (comment.Contains("UPROPERTY")) return SpecifierTarget.Property;
            if (comment.Contains("UFUNCTION")) return SpecifierTarget.Function | SpecifierTarget.Delegate;
            if (comment.Contains("UINTERFACE")) return SpecifierTarget.Interface;
            if (comment.Contains("UENUM")) return SpecifierTarget.Enum;
            return SpecifierTarget.AnyField;
        }

        static SpecifierTarget TagTarget(string tag, SpecifierTarget fallback)
        {
            switch (tag)
            {
                case "Class": return SpecifierTarget.Class;
                case "Struct": return SpecifierTarget.Struct;
                case "Property": return SpecifierTarget.Property;
                case "Function": return SpecifierTarget.Function | SpecifierTarget.Delegate;
                case "Interface": return SpecifierTarget.Interface;
                case "Enum": return SpecifierTarget.Enum;
                case "Param": return SpecifierTarget.Param;
                default: return fallback;
            }
        }
    }
}
