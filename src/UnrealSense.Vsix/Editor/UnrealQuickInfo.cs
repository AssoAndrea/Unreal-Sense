using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Assets;
using UnrealSense.Cpp;
using UnrealSense.Reflection;
using UnrealSense.Extension.Services;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("UnrealSense quick info")]
    [ContentType("C/C++")]
    [Order(Before = "default")]
    internal sealed class UnrealQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            if (!DocumentFactory.TryGetTextDocument(textBuffer, out var document)) return null;
            var analysis = DocumentAnalysis.TryGet(textBuffer, document.FilePath);
            return analysis == null ? null : textBuffer.Properties.GetOrCreateSingletonProperty(() => new UnrealQuickInfoSource(textBuffer, analysis));
        }
    }

    internal sealed class UnrealQuickInfoSource : IAsyncQuickInfoSource
    {
        readonly ITextBuffer buffer;
        readonly DocumentAnalysis analysis;

        public UnrealQuickInfoSource(ITextBuffer buffer, DocumentAnalysis analysis)
        {
            this.buffer = buffer;
            this.analysis = analysis;
        }

        public Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            var trigger = session.GetTriggerPoint(buffer.CurrentSnapshot);
            if (!trigger.HasValue) return Task.FromResult<QuickInfoItem>(null);
            var snapshot = buffer.CurrentSnapshot;
            var file = analysis.GetCurrentFile();
            int offset = trigger.Value.Position;

            var specifierItem = SpecifierInfoAt(file, offset, snapshot);
            if (specifierItem != null) return Task.FromResult(specifierItem);

            return Task.Run(() => BlueprintInfoAt(file, offset, snapshot), cancellationToken);
        }

        QuickInfoItem SpecifierInfoAt(ParsedFile file, int offset, ITextSnapshot snapshot)
        {
            var catalog = analysis.Workspace?.Catalog ?? WorkspaceService.Current?.Catalog;
            if (catalog == null) return null;

            var macros = file.Types.Select(t => t.Macro)
                .Concat(file.Types.SelectMany(t => t.Functions.Select(f => f.Macro).Concat(t.Properties.Select(p => p.Macro))));
            foreach (var macro in macros.Where(m => offset >= m.Start && offset <= m.End))
            {
                var target = SpecifierCatalog.TargetForMacro(macro.Name);
                foreach (var spec in macro.Specifiers.Concat(macro.Meta))
                {
                    if (offset < spec.KeyStart || offset > spec.KeyEnd) continue;
                    var info = spec.IsMeta ? catalog.FindMeta(target, spec.Key) : catalog.FindSpecifier(target, spec.Key);
                    var title = new ClassifiedTextElement(
                        new ClassifiedTextRun("keyword", $"{macro.Name} {(spec.IsMeta ? "meta" : "specifier")} "),
                        new ClassifiedTextRun("identifier", spec.Key));
                    var body = new ClassifiedTextElement(new ClassifiedTextRun("text",
                        info == null ? "Not a known specifier for this macro." : string.IsNullOrEmpty(info.Documentation) ? "(no documentation)" : info.Documentation));
                    var span = snapshot.CreateTrackingSpan(spec.KeyStart, spec.Key.Length, SpanTrackingMode.EdgeInclusive);
                    return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked, title, body));
                }
            }
            return null;
        }

        QuickInfoItem BlueprintInfoAt(ParsedFile file, int offset, ITextSnapshot snapshot)
        {
            var workspace = analysis.Workspace;
            if (workspace?.Assets == null) return null;
            var symbol = SymbolLocator.FindDeclaredName(file, offset);
            if (symbol == null) return null;

            var usages = symbol.Member != null ? workspace.FindUsages(symbol.Member, analysis.FilePath) : workspace.FindUsages(symbol.Type, analysis.FilePath);
            var lines = new List<object>
            {
                new ClassifiedTextElement(
                    new ClassifiedTextRun("keyword", "Blueprint usages: "),
                    new ClassifiedTextRun("number", usages.Count.ToString()),
                    new ClassifiedTextRun("text", usages.Any(u => u.IsHeuristic) ? "  (property matches are name based)" : "")),
            };
            foreach (var group in usages.GroupBy(u => u.Kind).Take(5))
            {
                lines.Add(new ClassifiedTextElement(new ClassifiedTextRun("comment", Describe(group.Key) + ":")));
                foreach (var u in group.Take(8))
                    lines.Add(new ClassifiedTextElement(new ClassifiedTextRun("text", "   " + u.Asset.PackageName + (u.Detail != null ? $" ({u.Detail})" : ""))));
                if (group.Count() > 8)
                    lines.Add(new ClassifiedTextElement(new ClassifiedTextRun("comment", $"   … {group.Count() - 8} more (Alt+Shift+B)")));
            }

            int start = symbol.Member?.NameStart ?? symbol.Type.NameStart;
            int length = (symbol.Member?.Name ?? symbol.Type.Name).Length;
            if (start + length > snapshot.Length) return null;
            return new QuickInfoItem(snapshot.CreateTrackingSpan(start, length, SpanTrackingMode.EdgeInclusive),
                new ContainerElement(ContainerElementStyle.Stacked, lines));
        }

        public static string Describe(AssetUsageKind kind)
        {
            switch (kind)
            {
                case AssetUsageKind.Subclass: return "Derived Blueprints";
                case AssetUsageKind.FunctionCall: return "Called in";
                case AssetUsageKind.EventImplementation: return "Implemented / overridden in";
                case AssetUsageKind.TypeReference: return "Referenced by";
                default: return "Property used in";
            }
        }

        public void Dispose() { }
    }
}
