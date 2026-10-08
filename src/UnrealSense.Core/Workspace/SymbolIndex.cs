using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnrealSense.Cpp;
using UnrealSense.Project;

namespace UnrealSense.Workspace
{
    /// <summary>A function definition found in a source file: "AFoo::Bar(".</summary>
    public sealed class FunctionDefinition
    {
        public string ClassName { get; set; }
        public string FunctionName { get; set; }
        public string FilePath { get; set; }
        public int Offset { get; set; }
    }

    /// <summary>
    /// Index of reflected types declared in project/plugin headers plus "Class::Function" definitions in
    /// source files. Rebuilt incrementally per file.
    /// </summary>
    public sealed class SymbolIndex
    {
        static readonly Regex DefinitionRegex = new Regex(@"(?<![\w:~])(?<cls>[A-Z]\w*)\s*::\s*(?<fn>~?\w+)\s*\(", RegexOptions.Compiled);

        readonly UnrealProject project;
        readonly ConcurrentDictionary<string, ParsedFile> headers = new ConcurrentDictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, List<FunctionDefinition>> definitions = new ConcurrentDictionary<string, List<FunctionDefinition>>(StringComparer.OrdinalIgnoreCase);

        volatile Dictionary<string, (ReflectedType Type, string File)> typesByName = new Dictionary<string, (ReflectedType, string)>();
        volatile Dictionary<string, DelegateDeclaration> delegatesByName = new Dictionary<string, DelegateDeclaration>();
        volatile ILookup<string, FunctionDefinition> definitionsByClass = Enumerable.Empty<FunctionDefinition>().ToLookup(d => d.ClassName);
        volatile ILookup<string, ReflectedType> childrenByBase = Enumerable.Empty<ReflectedType>().ToLookup(t => t.BaseType);
        volatile ILookup<string, ReflectedType> typesByReflectedName = Enumerable.Empty<ReflectedType>().ToLookup(t => t.Name);
        volatile ILookup<string, ReflectedType> childrenByReflectedBase = Enumerable.Empty<ReflectedType>().ToLookup(t => t.Name);

        public SymbolIndex(UnrealProject project)
        {
            this.project = project;
        }

        public event EventHandler Updated;

        public IEnumerable<ParsedFile> Headers => headers.Values;
        public IEnumerable<ReflectedType> Types => typesByName.Values.Select(v => v.Type);

        public void Build(CancellationToken cancellationToken = default)
        {
            var roots = project.AllModules.Select(m => m.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var files = roots.SelectMany(r => ModuleScanner.EnumerateFiles(r, "*.h")
                    .Concat(ModuleScanner.EnumerateFiles(r, "*.cpp"))
                    .Concat(ModuleScanner.EnumerateFiles(r, "*.inl")))
                .ToList();

            Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken }, file =>
            {
                try { Update(file, File.ReadAllText(file), notify: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            Rebuild();
        }

        /// <summary>Updates one file from its current text (open editor buffer or disk).</summary>
        public void Update(string file, string text, bool notify = true)
        {
            var parsed = HeaderParser.Parse(text, file);
            if (file.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase))
                headers[file] = parsed;

            var defs = new List<FunctionDefinition>();
            foreach (Match m in DefinitionRegex.Matches(text))
                defs.Add(new FunctionDefinition { ClassName = m.Groups["cls"].Value, FunctionName = m.Groups["fn"].Value, FilePath = file, Offset = m.Groups["fn"].Index });
            definitions[file] = defs;

            if (notify) Rebuild();
        }

        public void Remove(string file)
        {
            headers.TryRemove(file, out _);
            definitions.TryRemove(file, out _);
            Rebuild();
        }

        void Rebuild()
        {
            var types = new Dictionary<string, (ReflectedType, string)>(StringComparer.Ordinal);
            var delegates = new Dictionary<string, DelegateDeclaration>(StringComparer.Ordinal);
            foreach (var header in headers.Values)
            {
                foreach (var type in header.Types)
                    if (type.Name != null) types[type.Name] = (type, header.FilePath);
                foreach (var d in header.Delegates)
                    if (d.Name != null) delegates[d.Name] = d;
            }
            typesByName = types;
            delegatesByName = delegates;
            var all = types.Values.Select(v => v.Item1).ToList();
            childrenByBase = all.Where(t => t.BaseType != null).ToLookup(t => t.BaseType, StringComparer.Ordinal);
            typesByReflectedName = all.ToLookup(t => t.ReflectedName, StringComparer.OrdinalIgnoreCase);
            childrenByReflectedBase = all.Where(t => t.BaseType != null).ToLookup(t => ReflectedType.GetReflectedName(t.BaseType, t.Kind), StringComparer.OrdinalIgnoreCase);
            definitionsByClass = definitions.Values.SelectMany(d => d).ToLookup(d => d.ClassName, StringComparer.Ordinal);
            Updated?.Invoke(this, EventArgs.Empty);
        }

        public ReflectedType FindType(string cppName) =>
            cppName != null && typesByName.TryGetValue(cppName, out var entry) ? entry.Type : null;

        public string FindTypeFile(string cppName) =>
            cppName != null && typesByName.TryGetValue(cppName, out var entry) ? entry.File : null;

        public DelegateDeclaration FindDelegate(string name) =>
            name != null && delegatesByName.TryGetValue(name, out var d) ? d : null;

        public IEnumerable<FunctionDefinition> FindDefinitions(string className, string functionName) =>
            definitionsByClass[className].Where(d => d.FunctionName == functionName);

        public bool HasDefinition(string className, string functionName) => FindDefinitions(className, functionName).Any();

        /// <summary>Project classes deriving from <paramref name="type"/>, directly or not (C++ inheritance only).</summary>
        public IEnumerable<ReflectedType> GetDerivedTypes(ReflectedType type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { type.Name };
            var queue = new Queue<ReflectedType>(childrenByBase[type.Name]);
            while (queue.Count > 0)
            {
                var child = queue.Dequeue();
                if (child.Name == null || !seen.Add(child.Name)) continue;
                yield return child;
                foreach (var grandChild in childrenByBase[child.Name]) queue.Enqueue(grandChild);
            }
        }

        /// <summary>
        /// Project classes deriving, at any depth, from a class that is not in the project (engine or external plugin),
        /// known by the name Unreal uses (no prefix): the project's headers say what they derive from.
        /// </summary>
        public IEnumerable<ReflectedType> GetDerivedTypesOfExternal(string reflectedName)
        {
            foreach (var child in childrenByReflectedBase[reflectedName ?? ""])
            {
                yield return child;
                foreach (var grandChild in GetDerivedTypes(child)) yield return grandChild;
            }
        }

        /// <summary>Types by the name Unreal and assets use (no U/A/F prefix).</summary>
        public IEnumerable<ReflectedType> FindTypesByReflectedName(string reflectedName) =>
            reflectedName == null ? Enumerable.Empty<ReflectedType>() : typesByReflectedName[reflectedName];

        /// <summary>The type and all its known (project) ancestors, nearest first.</summary>
        public IEnumerable<ReflectedType> GetSelfAndBases(ReflectedType type)
        {
            var seen = new HashSet<string>();
            while (type != null && seen.Add(type.Name))
            {
                yield return type;
                type = FindType(type.BaseType);
            }
        }

        /// <summary>Every Category= value used in the project, for completion.</summary>
        public IEnumerable<string> GetCategories() =>
            headers.Values.SelectMany(h => h.AllMembers)
                .Select(m => m.Macro.Get("Category")?.Value)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

        public UnrealModule FindModule(string file) => project.FindModuleForFile(file);
    }
}
