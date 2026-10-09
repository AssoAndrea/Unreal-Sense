using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnrealSense.Assets;
using UnrealSense.Cpp;
using UnrealSense.Extension.Options;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Services
{
    internal sealed class CodeUsage
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string LineText { get; set; }
        public ReferenceKind Kind { get; set; }
    }

    internal sealed class UsageResults
    {
        public string Title { get; set; }
        public string Symbol { get; set; }
        public string Source { get; set; }   // shown in the summary; null = text search
        public string Warning { get; set; }
        public List<CodeUsage> Code { get; } = new List<CodeUsage>();
        /// <summary>Own index: uses of the name on an object whose type could not be inferred (may or may not be this symbol).</summary>
        public List<CodeUsage> Uncertain { get; } = new List<CodeUsage>();
        public List<AssetUsage> Blueprints { get; } = new List<AssetUsage>();
        public double Milliseconds { get; set; }
    }

    /// <summary>
    /// Find Usages: references from the own C++ index (the symbol is resolved, so pippo->Get() only matches the Get of
    /// pippo's class), plus Blueprint usages for reflected symbols. When the index cannot answer it falls back to a
    /// whole-word text search, clearly labelled as such.
    /// </summary>
    internal static class FindUsagesService
    {
        public static async Task<UsageResults> FindAsync(string filePath, string documentText, int line, int column, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var lines = documentText.Split('\n');
            var lineText = line < lines.Length ? lines[line].TrimEnd('\r') : string.Empty;
            var (wordStart, wordEnd) = SymbolLocator.WordAt(lineText, Math.Min(column, lineText.Length));
            if (wordEnd <= wordStart) return null;
            var word = lineText.Substring(wordStart, wordEnd - wordStart);

            var results = new UsageResults { Symbol = word, Title = word };
            var workspace = WorkspaceService.GetForFile(filePath);

            var own = General.Instance.UseOwnIndex ? await OwnIndexService.FindAsync(filePath, line, wordStart, cancellationToken).ConfigureAwait(false) : null;
            if (own != null && own.Symbol != null)
            {
                results.Source = "own index (experimental)";
                results.Title = $"{own.Kind.ToLowerInvariant()} {own.Symbol}";
                var fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(filePath) && File.ReadAllText(filePath) != documentText)
                    results.Warning = "The document has unsaved changes: the own index reflects the saved files.";
                int len = Math.Max(1, (own.Name ?? word).Length);
                void AddOwn(List<OwnIndexUsage> from, List<CodeUsage> to)
                {
                    foreach (var u in from)
                    {
                        var text = LineOf(fileCache, u.FilePath, u.Line);
                        var kind = ReferenceClassifier.Classify(u.FilePath, text, u.Column, u.Column + len, u.Kind == "Decl" || u.Kind == "Def");
                        if (kind == ReferenceKind.Generated && !General.Instance.ShowGeneratedReferences) continue;
                        to.Add(new CodeUsage { FilePath = u.FilePath, Line = u.Line, Column = u.Column, Length = len, LineText = text, Kind = kind });
                    }
                }
                AddOwn(own.Usages, results.Code);
                AddOwn(own.Uncertain, results.Uncertain);
                if (results.Uncertain.Count > 0)
                    results.Warning = (results.Warning == null ? "" : results.Warning + " ") +
                        $"{results.Uncertain.Count} use(s) of the name '{own.Name}' on an object whose type could not be inferred are listed under \"Uncertain\" at the bottom.";
                Log.Write($"Find Usages '{word}' (own index): {results.Code.Count} usages, {results.Uncertain.Count} uncertain, {sw.Elapsed.TotalMilliseconds:F0} ms");
                if (workspace?.Assets != null)
                    foreach (var u in own.Usages.Where(x => x.Kind == "Decl" || x.Kind == "Def"))
                    {
                        var symbol = ResolveReflected(workspace, u.FilePath, u.Line, u.Column);
                        if (symbol == null) continue;
                        results.Blueprints.AddRange(symbol.Member != null ? workspace.FindUsages(symbol.Member, symbol.DeclaringFile) : workspace.FindUsages(symbol.Type, symbol.DeclaringFile));
                        break;
                    }
            }
            else
            {
                if (own != null)
                    Log.Write($"Find Usages '{word}': own index could not answer ({own.Failure}) for {filePath}:{line + 1}:{wordStart + 1}; text search");
                results.Warning = own != null
                    ? $"Showing whole-word text matches: {own.Failure}."
                    : "The own C++ index is off (Tools › Options › UnrealSense): showing whole-word text matches.";
                if (workspace != null)
                {
                    await Task.Run(() => TextSearch(workspace, word, results, cancellationToken), cancellationToken).ConfigureAwait(false);
                    // Blueprint usages do not need the C++ index: the reflected symbol under the caret comes from our header parser.
                    int offset = 0;
                    for (int i = 0; i < line && i < lines.Length; i++) offset += lines[i].Length + 1;
                    var symbol = SymbolLocator.Find(HeaderParser.Parse(documentText, filePath), offset + wordStart, workspace.Symbols);
                    if (symbol?.Type != null)
                        results.Blueprints.AddRange(symbol.Member != null ? workspace.FindUsages(symbol.Member, filePath) : workspace.FindUsages(symbol.Type, filePath));
                }
            }

            results.Milliseconds = sw.Elapsed.TotalMilliseconds;
            return results;
        }

        static IEnumerable<string> SourceFiles(UnrealWorkspace workspace) =>
            workspace.Project.AllModules
                .SelectMany(m => Directory.Exists(m.Directory) ? Directory.EnumerateFiles(m.Directory, "*.*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                .Where(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".inl", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath);

        static string LineOf(Dictionary<string, string[]> cache, string path, int line)
        {
            if (!cache.TryGetValue(path, out var lines))
            {
                try { lines = File.ReadAllLines(path); }
                catch (IOException) { lines = Array.Empty<string>(); }
                cache[path] = lines;
            }
            return line < lines.Length ? lines[line].TrimEnd('\r') : string.Empty;
        }

        static SymbolAtPosition ResolveReflected(UnrealWorkspace workspace, string filePath, int line, int column)
        {
            var header = workspace.Symbols?.Headers.FirstOrDefault(h => string.Equals(h.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (header == null) return null;
            int offset = 0, currentLine = 0;
            var text = header.Text;
            while (currentLine < line && offset < text.Length)
            {
                int nl = text.IndexOf('\n', offset);
                if (nl < 0) break;
                offset = nl + 1;
                currentLine++;
            }
            return SymbolLocator.FindDeclaredName(header, offset + column);
        }

        static void TextSearch(UnrealWorkspace workspace, string word, UsageResults results, CancellationToken token)
        {
            var regex = new Regex(@"\b" + Regex.Escape(word) + @"\b", RegexOptions.Compiled);
            foreach (var file in SourceFiles(workspace))
            {
                token.ThrowIfCancellationRequested();
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match m in regex.Matches(lines[i]))
                        results.Code.Add(new CodeUsage
                        {
                            FilePath = file, Line = i, Column = m.Index, Length = m.Length, LineText = lines[i],
                            Kind = ReferenceClassifier.Classify(file, lines[i], m.Index, m.Index + m.Length, false),
                        });
            }
        }
    }
}
