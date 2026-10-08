using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnrealSense.Project;
using UnrealSense.Workspace;

namespace UnrealSense.Templates
{
    /// <summary>Where the new files go inside the module, as in Rider's "Base folder".</summary>
    public enum ClassLocation
    {
        /// <summary>Header in Public\sub, source in Private\sub.</summary>
        Public,
        /// <summary>Both in Private\sub.</summary>
        Private,
        /// <summary>Both in the module folder\sub (modules without Public/Private, like Lyra's).</summary>
        Root,
    }

    public sealed class NewClassRequest
    {
        /// <summary>Name as typed; a prefix matching the kind (AMyActor for an Actor) is accepted and not doubled.</summary>
        public string Name { get; set; }
        public ParentClassInfo Parent { get; set; }
        public UnrealModule Module { get; set; }
        public ClassLocation Location { get; set; }
        /// <summary>Folder under Public/Private/the module ("Variant_Shooter\AI"); may be empty.</summary>
        public string SubFolder { get; set; }
    }

    public sealed class NewClassResult
    {
        public string ClassName { get; set; }
        public string FileName { get; set; }
        public string HeaderPath { get; set; }
        /// <summary>Null for header-only kinds (struct, enum).</summary>
        public string SourcePath { get; set; }
        public string HeaderText { get; set; }
        public string SourceText { get; set; }
        public string TemplateName { get; set; }
        /// <summary>Module the target module must depend on for the parent class, when its .Build.cs does not.</summary>
        public string MissingDependency { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Produces the .h/.cpp of a new Unreal class the way the Unreal Editor's New C++ Class wizard does: prefixed
    /// name, XXX_API, the parent's header and the .generated.h include, the template matching the parent.
    /// </summary>
    public sealed class NewClassGenerator
    {
        const string DefaultCopyright = "Fill out your copyright notice in the Description page of Project Settings.";
        static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        static readonly Regex Token = new Regex(@"%[A-Z_]+%", RegexOptions.Compiled);

        readonly UnrealProject project;
        readonly ParentClassCatalog catalog;
        readonly SymbolIndex symbols;

        public NewClassGenerator(UnrealProject project, ParentClassCatalog catalog, SymbolIndex symbols = null)
        {
            this.project = project;
            this.catalog = catalog ?? ParentClassCatalog.Create(project, symbols, null);
            this.symbols = symbols;
            var engineTemplates = project?.Engine == null ? null : Path.Combine(project.Engine.EngineDirectory, "Content", "Editor", "Templates");
            EngineTemplatesDirectory = engineTemplates != null && Directory.Exists(engineTemplates) ? engineTemplates : null;
        }

        /// <summary>The engine's template folder, or null when the built-in templates are used.</summary>
        public string EngineTemplatesDirectory { get; set; }

        /// <summary>Prefix of a new type deriving from <paramref name="parent"/>: A for actors, U for objects, F, E or none.</summary>
        public string Prefix(ParentClassInfo parent)
        {
            switch (parent.Kind)
            {
                case NewClassKind.Interface: return "U";
                case NewClassKind.Struct: return "F";
                case NewClassKind.Enum: return "E";
                case NewClassKind.Empty: return "";
                default: return catalog.IsChildOf(parent.Name, "AActor") ? "A" : "U";
            }
        }

        /// <summary>The name without the prefix the user may have typed ("AMyActor" → "MyActor").</summary>
        public static string StripPrefix(string name, string prefix)
        {
            name = (name ?? "").Trim();
            if (prefix.Length == 1 && name.Length > 2 && name[0] == prefix[0] && char.IsUpper(name[1]))
                return name.Substring(1);
            // Interfaces: "IMyThing" is as common as "UMyThing".
            if (prefix == "U" && name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]))
                return name.Substring(1);
            return name;
        }

        /// <summary>Header and source paths; the source is null for header-only kinds.</summary>
        public static (string Header, string Source) ComputePaths(UnrealModule module, ClassLocation location, string subFolder, string fileName, bool hasSource)
        {
            var sub = NormalizeSubFolder(subFolder);
            string headerDir, sourceDir;
            switch (location)
            {
                case ClassLocation.Public:
                    headerDir = Path.Combine(module.Directory, "Public", sub);
                    sourceDir = Path.Combine(module.Directory, "Private", sub);
                    break;
                case ClassLocation.Private:
                    headerDir = sourceDir = Path.Combine(module.Directory, "Private", sub);
                    break;
                default:
                    headerDir = sourceDir = Path.Combine(module.Directory, sub);
                    break;
            }
            return (Path.Combine(headerDir, fileName + ".h").TrimEnd('\\'), hasSource ? Path.Combine(sourceDir, fileName + ".cpp") : null);
        }

        public static string NormalizeSubFolder(string subFolder) =>
            string.Join("\\", (subFolder ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));

        /// <summary>Which template a parent uses, following the Unreal Editor's choice (Character, Pawn, Actor, component, object).</summary>
        public string ChooseTemplate(ParentClassInfo parent, out string specifiers)
        {
            specifiers = "";
            switch (parent.Kind)
            {
                case NewClassKind.Interface:
                    specifiers = "MinimalAPI";
                    return "InterfaceClass";
                case NewClassKind.Struct: return "StructClass";
                case NewClassKind.Enum: return "EnumClass";
                case NewClassKind.Empty: return "EmptyClass";
            }
            var chain = catalog.SelfAndBases(parent.Name).ToList();
            if (chain.Contains("ACharacter") || chain.Contains("APawn")) return chain.Contains("ACharacter") ? "CharacterClass" : "PawnClass";
            if (chain.Contains("AActor")) return "ActorClass";
            if (chain.Contains("UActorComponent")) return "ActorComponentClass";
            if (chain.Contains("USubsystem")) return "SubsystemClass";
            if (chain.Contains("UAnimInstance")) return "AnimInstanceClass";
            if (chain.Contains("UUserWidget")) return "UserWidgetClass";
            if (chain.Contains("UDeveloperSettings")) specifiers = "Config=Game, DefaultConfig";
            return "UObjectClass";
        }

        public NewClassResult Generate(NewClassRequest request)
        {
            var result = new NewClassResult();
            var parent = catalog.Resolve(request.Parent);
            if (parent == null) { result.Errors.Add("Choose a parent class."); return result; }
            if (request.Module == null) { result.Errors.Add("Choose a module."); return result; }

            var prefix = Prefix(parent);
            var bare = StripPrefix(request.Name, prefix);
            if (bare.Length == 0) { result.Errors.Add("Enter a class name."); return result; }
            if (!Identifier.IsMatch(bare)) { result.Errors.Add($"'{bare}' is not a valid C++ identifier."); return result; }
            var className = prefix + bare;
            result.ClassName = className;
            result.FileName = bare;

            var template = ChooseTemplate(parent, out var specifiers);
            result.TemplateName = template;
            bool headerOnly = parent.Kind == NewClassKind.Struct || parent.Kind == NewClassKind.Enum;
            var (headerPath, sourcePath) = ComputePaths(request.Module, request.Location, request.SubFolder, bare, !headerOnly);
            result.HeaderPath = headerPath;
            result.SourcePath = sourcePath;

            Validate(request, parent, result);

            var values = new Dictionary<string, string>
            {
                ["%COPYRIGHT_LINE%"] = "// " + ReadCopyrightNotice(),
                ["%UNPREFIXED_CLASS_NAME%"] = bare,
                ["%PREFIXED_CLASS_NAME%"] = className,
                ["%PREFIXED_BASE_CLASS_NAME%"] = parent.Name ?? "",
                ["%CLASS_MODULE_API_MACRO%"] = request.Module.ApiMacro + " ",
                ["%UCLASS_SPECIFIER_LIST%"] = specifiers,
                ["%BASE_CLASS_INCLUDE_DIRECTIVE%"] = BaseInclude(parent),
                ["%BASE_CLASS_CLAUSE%"] = parent.Kind == NewClassKind.Struct && parent.Name != null ? " : public " + parent.Name : "",
                ["%MY_HEADER_INCLUDE_DIRECTIVE%"] = $"#include \"{OwnHeaderInclude(request, headerPath)}\"",
            };
            result.HeaderText = Render(BuiltinClassTemplates.Load(template + ".h", EngineTemplatesDirectory), values);
            if (!headerOnly)
                result.SourceText = Render(BuiltinClassTemplates.Load(template + ".cpp", EngineTemplatesDirectory), values);

            // The engine's interface template has no base for the I class: an interface extending another one needs it.
            if (parent.Kind == NewClassKind.Interface && parent.Name != null && parent.Name != "UInterface" && parent.Name.StartsWith("U"))
                result.HeaderText = Regex.Replace(result.HeaderText, @"(class\s+(?:\w+_API\s+)?I" + Regex.Escape(bare) + @")\s*\r?\n",
                    m => m.Groups[1].Value + " : public I" + parent.Name.Substring(1) + "\r\n");
            return result;
        }

        void Validate(NewClassRequest request, ParentClassInfo parent, NewClassResult result)
        {
            var className = result.ClassName;
            if (catalog.Find(className) != null || symbols?.FindType(className) != null)
                result.Errors.Add($"A type named {className} already exists.");
            if (parent.Kind == NewClassKind.Interface && symbols?.FindType("I" + result.FileName) != null)
                result.Errors.Add($"A type named I{result.FileName} already exists.");
            if (File.Exists(result.HeaderPath)) result.Errors.Add($"{result.HeaderPath} already exists.");
            if (result.SourcePath != null && File.Exists(result.SourcePath)) result.Errors.Add($"{result.SourcePath} already exists.");

            if (request.Location != ClassLocation.Root && !Directory.Exists(Path.Combine(request.Module.Directory, request.Location == ClassLocation.Public ? "Public" : "Private")))
                result.Warnings.Add($"Module {request.Module.Name} has no {request.Location} folder: it will be created.");

            if (parent.IsPrivateHeader)
            {
                if (!string.Equals(parent.ModuleName, request.Module.Name, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"{parent.Name} is declared in a Private header of module {parent.ModuleName}: other modules cannot include it.");
                else if (request.Location == ClassLocation.Public)
                    result.Warnings.Add($"{parent.Name} is declared in a Private header: a Public header including it cannot be used by other modules. Consider Base folder: Private.");
            }

            if (parent.ModuleName != null && parent.Name != null && !string.Equals(parent.ModuleName, request.Module.Name, StringComparison.OrdinalIgnoreCase)
                && !request.Module.PublicDependencies.Contains(parent.ModuleName, StringComparer.OrdinalIgnoreCase)
                && !request.Module.PrivateDependencies.Contains(parent.ModuleName, StringComparer.OrdinalIgnoreCase))
            {
                result.MissingDependency = parent.ModuleName;
                result.Warnings.Add($"{request.Module.Name}.Build.cs does not depend on {parent.ModuleName}, the module of {parent.Name}.");
            }
        }

        static string BaseInclude(ParentClassInfo parent)
        {
            // The interface template already includes UObject/Interface.h.
            if (parent.IncludePath == null || parent.Name == "UInterface") return "";
            return $"#include \"{parent.IncludePath}\"";
        }

        static string OwnHeaderInclude(NewClassRequest request, string headerPath)
        {
            // In Public the header is included relative to Public (on every dependent module's include path);
            // elsewhere the .cpp sits next to it.
            if (request.Location != ClassLocation.Public) return Path.GetFileName(headerPath);
            var sub = NormalizeSubFolder(request.SubFolder);
            return (sub.Length == 0 ? "" : sub.Replace('\\', '/') + "/") + Path.GetFileName(headerPath);
        }

        /// <summary>
        /// Replaces the tokens and tidies the result like the editor's output: lines holding only empty tokens go away,
        /// no trailing whitespace, no blank line before a closing brace, CRLF.
        /// </summary>
        public static string Render(string template, IDictionary<string, string> values)
        {
            if (template == null) return null;
            var lines = new List<string>();
            foreach (var raw in template.Replace("\r\n", "\n").Split('\n'))
            {
                bool hadToken = Token.IsMatch(raw);
                var line = Token.Replace(raw, m => values.TryGetValue(m.Value, out var v) ? v : "").TrimEnd();
                if (hadToken && line.Trim().Length == 0) continue;
                if (line.TrimStart().StartsWith("}") && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                    while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                if (line.Length == 0 && lines.Count >= 2 && lines[lines.Count - 1].Length == 0 && lines[lines.Count - 2].Length == 0) continue;
                lines.Add(line);
            }
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>CopyrightNotice from Config/DefaultGame.ini, as the editor writes it at the top of new files.</summary>
        public string ReadCopyrightNotice()
        {
            var ini = project == null ? null : Path.Combine(project.ConfigDirectory, "DefaultGame.ini");
            try
            {
                if (ini != null && File.Exists(ini))
                    foreach (var line in File.ReadLines(ini))
                    {
                        var m = Regex.Match(line, @"^\s*CopyrightNotice\s*=\s*(.*)$");
                        if (!m.Success) continue;
                        var value = m.Groups[1].Value.Trim();
                        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                        if (value.Length > 0) return value;
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return DefaultCopyright;
        }

        /// <summary>Writes the files (UTF-8, no BOM, like the editor). Never overwrites.</summary>
        public static void Write(NewClassResult result)
        {
            if (!result.IsValid) throw new InvalidOperationException(string.Join(" ", result.Errors));
            var encoding = new UTF8Encoding(false);
            Directory.CreateDirectory(Path.GetDirectoryName(result.HeaderPath));
            if (File.Exists(result.HeaderPath)) throw new IOException(result.HeaderPath + " already exists.");
            if (result.SourcePath != null && File.Exists(result.SourcePath)) throw new IOException(result.SourcePath + " already exists.");
            File.WriteAllText(result.HeaderPath, result.HeaderText, encoding);
            if (result.SourcePath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result.SourcePath));
                File.WriteAllText(result.SourcePath, result.SourceText, encoding);
            }
        }
    }
}
