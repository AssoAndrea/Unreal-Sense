using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnrealSense.Assets;
using UnrealSense.Clang;
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
        public bool IsSemantic { get; set; }
        public string Source { get; set; }   // shown in the summary; null = clangd or text search
        public string Warning { get; set; }
        public List<CodeUsage> Code { get; } = new List<CodeUsage>();
        public List<AssetUsage> Blueprints { get; } = new List<AssetUsage>();
        public double Milliseconds { get; set; }
    }

    /// <summary>
    /// Find Usages: semantic references from clangd (the symbol is resolved like the compiler does, so
    /// pippo->Get() only matches the Get of pippo's class), plus Blueprint usages for reflected symbols.
    /// Without clangd it falls back to a whole-word text search, clearly labelled as such.
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
            var client = ClangdService.Client;

            var own = General.Instance.UseOwnIndex ? await OwnIndexService.FindAsync(filePath, line, wordStart, cancellationToken).ConfigureAwait(false) : null;
            if (own != null && own.Symbol != null)
            {
                results.IsSemantic = true;
                results.Source = "own index (experimental)";
                results.Title = $"{own.Kind.ToLowerInvariant()} {own.Symbol}";
                var fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(filePath) && File.ReadAllText(filePath) != documentText)
                    results.Warning = "The document has unsaved changes: the own index reflects the saved files.";
                int len = Math.Max(1, (own.Name ?? word).Length);
                foreach (var u in own.Usages)
                {
                    var text = LineOf(fileCache, u.FilePath, u.Line);
                    var kind = ReferenceClassifier.Classify(u.FilePath, text, u.Column, u.Column + len, u.Kind == "Decl" || u.Kind == "Def");
                    if (kind == ReferenceKind.Generated && !General.Instance.ShowGeneratedReferences) continue;
                    results.Code.Add(new CodeUsage { FilePath = u.FilePath, Line = u.Line, Column = u.Column, Length = len, LineText = text, Kind = kind });
                }
                if (own.Uncertain.Count > 0)
                    results.Warning = (results.Warning == null ? "" : results.Warning + " ") +
                        $"{own.Uncertain.Count} more use(s) of the name '{own.Name}' on an object whose type could not be inferred are not listed.";
                Log.Write($"Find Usages '{word}' (own index): {results.Code.Count} usages, {own.Uncertain.Count} uncertain, {sw.Elapsed.TotalMilliseconds:F0} ms");
                if (workspace?.Assets != null)
                    foreach (var u in own.Usages.Where(x => x.Kind == "Decl" || x.Kind == "Def"))
                    {
                        var symbol = ResolveReflected(workspace, new SourceLocation { FilePath = u.FilePath, Line = u.Line, Column = u.Column });
                        if (symbol == null) continue;
                        results.Blueprints.AddRange(symbol.Member != null ? workspace.FindUsages(symbol.Member, symbol.DeclaringFile) : workspace.FindUsages(symbol.Type, symbol.DeclaringFile));
                        break;
                    }
            }
            else if (client != null && client.State != ClangdState.Failed && client.State != ClangdState.Stopped)
            {
                bool wasOpen = client.IsOpen(filePath);
                await client.SyncDocumentAsync(filePath, documentText).ConfigureAwait(false);
                // All four wait for the same parse of the document: ask at once.
                var referencesTask = client.FindReferencesAsync(filePath, line, wordStart, includeDeclaration: true, cancellationToken);
                var declarationsTask = client.FindDeclarationAsync(filePath, line, wordStart, cancellationToken);
                var definitionsTask = client.FindDefinitionAsync(filePath, line, wordStart, cancellationToken);
                var hoverTask = client.HoverAsync(filePath, line, wordStart, cancellationToken);
                await Task.WhenAll(referencesTask, declarationsTask, definitionsTask, hoverTask).ConfigureAwait(false);
                var references = referencesTask.Result;
                var declarations = declarationsTask.Result;
                var definitions = definitionsTask.Result;
                var hover = hoverTask.Result;
                var queried = sw.Elapsed.TotalSeconds;

                results.IsSemantic = true;
                results.Title = DescribeHover(hover) ?? word;
                if (client.State == ClangdState.Indexing)
                    results.Warning = $"The C++ index is {client.IndexPercentage}% complete: results may be incomplete until indexing finishes.";

                var declared = new HashSet<(string, int, int)>(declarations.Concat(definitions).Select(d => (d.FilePath.ToLowerInvariant(), d.Line, d.Column)));
                var fileCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [filePath] = lines };
                foreach (var r in references)
                {
                    var text = LineOf(fileCache, r.FilePath, r.Line);
                    var kind = ReferenceClassifier.Classify(r.FilePath, text, r.Column, r.EndColumn, declared.Contains((r.FilePath.ToLowerInvariant(), r.Line, r.Column)));
                    if (kind == ReferenceKind.Generated && !General.Instance.ShowGeneratedReferences) continue;
                    results.Code.Add(new CodeUsage { FilePath = r.FilePath, Line = r.Line, Column = r.Column, Length = Math.Max(1, r.EndColumn - r.Column), LineText = text, Kind = kind });
                }
                int indexed = results.Code.Count;
                int verified = workspace == null ? 0 : await VerifySuspectFilesAsync(client, workspace, word, declared, results, fileCache, cancellationToken).ConfigureAwait(false);
                if (verified > 0)
                    results.Warning = (results.Warning == null ? "" : results.Warning + " ") +
                        $"{verified} usage(s) found by re-checking files whose index had compile errors.";
                Log.Write($"Find Usages '{word}': {indexed} from the index in {queried:F1}s (document {(wasOpen ? "already parsed" : "parsed now")}), " +
                          $"{verified} more from re-checked files, {sw.Elapsed.TotalSeconds:F1}s in all");

                // Blueprint usages: resolve the declaration to a reflected symbol in our header index.
                if (workspace?.Assets != null)
                    foreach (var decl in declarations.Concat(definitions))
                    {
                        var symbol = ResolveReflected(workspace, decl);
                        if (symbol == null) continue;
                        results.Blueprints.AddRange(symbol.Member != null ? workspace.FindUsages(symbol.Member, symbol.DeclaringFile) : workspace.FindUsages(symbol.Type, symbol.DeclaringFile));
                        break;
                    }
            }
            else
            {
                results.Warning = ClangdService.IsClangdAvailable
                    ? "C++ semantic index not running: showing whole-word text matches (like Visual Assist). " + ClangdService.StatusText
                    : "clangd is not installed: showing whole-word text matches. Use Extensions › UnrealSense › Download clangd for exact results.";
                if (workspace != null)
                {
                    await Task.Run(() => TextSearch(workspace, word, results, cancellationToken), cancellationToken).ConfigureAwait(false);
                    // Blueprint usages do not need clangd: the reflected symbol under the caret comes from our header parser.
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

        const int MaxFilesToVerify = 8;
        const int MaxHitsPerFile = 40;

        /// <summary>
        /// Files that mention <paramref name="word"/> but have no indexed reference, and whose index cannot be trusted:
        /// their unity unit had compile errors (not files merely not indexed yet: opening dozens of files while indexing
        /// only slows the index down). Each is parsed on its own and every
        /// occurrence resolved; an occurrence counts only when it resolves to the searched declaration, so results
        /// stay exact. Returns the number of usages added.
        /// </summary>
        static async Task<int> VerifySuspectFilesAsync(ClangdClient client, UnrealWorkspace workspace, string word,
            HashSet<(string, int, int)> declared, UsageResults results, Dictionary<string, string[]> fileCache, CancellationToken token)
        {
            if (declared.Count == 0) return 0;
            if (IndexDiagnostics.Count == 0) return 0;
            var covered = new HashSet<string>(results.Code.Select(c => Path.GetFullPath(c.FilePath)), StringComparer.OrdinalIgnoreCase);
            var units = UnitsBySource(ClangdService.CompileCommandsDirectory);
            var regex = new Regex(@"\b" + Regex.Escape(word) + @"\b");

            var suspects = await Task.Run(() =>
            {
                var list = new List<(string File, string Text, List<(int Line, int Column)> Hits)>();
                foreach (var file in SourceFiles(workspace).OrderBy(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                {
                    token.ThrowIfCancellationRequested();
                    if (covered.Contains(file)) continue;
                    bool untrusted = units.TryGetValue(file, out var unit) && IndexDiagnostics.IsUntrusted(file, unit);
                    if (!untrusted) continue;
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; }
                    if (text.IndexOf(word, StringComparison.Ordinal) < 0) continue;
                    var hits = new List<(int, int)>();
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length && hits.Count < MaxHitsPerFile; i++)
                        foreach (Match m in regex.Matches(lines[i]))
                            hits.Add((i, m.Index));
                    if (hits.Count == 0) continue;
                    list.Add((file, text, hits));
                    if (list.Count >= MaxFilesToVerify) break;
                }
                return list;
            }, token).ConfigureAwait(false);
            if (suspects.Count == 0) return 0;

            // clangd parses the files in parallel; each query waits for its file.
            var checks = suspects.Select(async s =>
            {
                bool wasOpen = client.IsOpen(s.File);
                await client.SyncDocumentAsync(s.File, s.Text).ConfigureAwait(false);
                var found = new List<(int Line, int Column)>();
                try
                {
                    foreach (var (line, column) in s.Hits)
                    {
                        var targets = (await client.FindDeclarationAsync(s.File, line, column, token).ConfigureAwait(false))
                            .Concat(await client.FindDefinitionAsync(s.File, line, column, token).ConfigureAwait(false));
                        if (targets.Any(t => declared.Contains((t.FilePath.ToLowerInvariant(), t.Line, t.Column)))) found.Add((line, column));
                    }
                }
                finally
                {
                    if (!wasOpen) await client.CloseDocumentAsync(s.File).ConfigureAwait(false);
                }
                return (s.File, found);
            }).ToList();
            await Task.WhenAll(checks).ConfigureAwait(false);

            int added = 0;
            foreach (var check in checks)
            {
                var (file, found) = check.Result;
                foreach (var (line, column) in found)
                {
                    var text = LineOf(fileCache, file, line);
                    bool isDeclaration = declared.Contains((file.ToLowerInvariant(), line, column));
                    var kind = ReferenceClassifier.Classify(file, text, column, column + word.Length, isDeclaration);
                    if (kind == ReferenceKind.Generated && !General.Instance.ShowGeneratedReferences) continue;
                    results.Code.Add(new CodeUsage { FilePath = file, Line = line, Column = column, Length = word.Length, LineText = text, Kind = kind });
                    added++;
                }
            }
            return added;
        }

        static IEnumerable<string> SourceFiles(UnrealWorkspace workspace) =>
            workspace.Project.AllModules
                .SelectMany(m => Directory.Exists(m.Directory) ? Directory.EnumerateFiles(m.Directory, "*.*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                .Where(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".inl", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath);

        static string unitsDirectory;
        static Dictionary<string, string> unitsBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Source file → the unity translation unit that includes it (from the unit files' #include lines).</summary>
        static Dictionary<string, string> UnitsBySource(string compileCommandsDirectory)
        {
            if (compileCommandsDirectory == null) return unitsBySource;
            var unity = Path.Combine(compileCommandsDirectory, "unity");
            var key = compileCommandsDirectory + "|" + (Directory.Exists(unity) ? Directory.GetLastWriteTimeUtc(unity).Ticks : 0);
            if (string.Equals(unitsDirectory, key, StringComparison.OrdinalIgnoreCase)) return unitsBySource;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(unity))
                foreach (var unit in Directory.EnumerateFiles(unity, "*.cpp"))
                    try
                    {
                        foreach (var line in File.ReadLines(unit))
                            if (line.StartsWith("#include \"", StringComparison.Ordinal) && line.EndsWith("\"", StringComparison.Ordinal))
                                map[Path.GetFullPath(line.Substring(10, line.Length - 11))] = Path.GetFullPath(unit);
                    }
                    catch (IOException) { }
            unitsDirectory = key;
            return unitsBySource = map;
        }

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

        static string DescribeHover(string hover)
        {
            if (string.IsNullOrWhiteSpace(hover)) return null;
            // clangd markdown: "### instance-method `Get`\n\n---\n→ `UObject *`\n...\n```cpp\npublic: UObject *Get() const\n```"
            var code = Regex.Match(hover, "```cpp\\s*(?:// In \\w+\\s*)?(?<c>[\\s\\S]*?)```");
            if (code.Success)
                return Regex.Replace(code.Groups["c"].Value.Trim(), @"\s+", " ");
            return hover.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim('#', ' ', '`');
        }

        static SymbolAtPosition ResolveReflected(UnrealWorkspace workspace, SourceLocation location)
        {
            var header = workspace.Symbols?.Headers.FirstOrDefault(h => string.Equals(h.FilePath, location.FilePath, StringComparison.OrdinalIgnoreCase));
            if (header == null) return null;
            int offset = 0, currentLine = 0;
            var text = header.Text;
            while (currentLine < location.Line && offset < text.Length)
            {
                int nl = text.IndexOf('\n', offset);
                if (nl < 0) break;
                offset = nl + 1;
                currentLine++;
            }
            return SymbolLocator.FindDeclaredName(header, offset + location.Column);
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
