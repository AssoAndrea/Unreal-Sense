using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Assets;
using UnrealSense.Cpp;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BlueprintHintsListener : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(BlueprintHints.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        internal AdornmentLayerDefinition HintsLayer = null;

        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        [Import]
        internal IEditorFormatMapService FormatMapService { get; set; }

        public void TextViewCreated(IWpfTextView view)
        {
            if (!DocumentFactory.TryGetTextDocument(view.TextBuffer, out var document)) return;
            var analysis = DocumentAnalysis.TryGet(view.TextBuffer, document.FilePath);
            if (analysis == null) return;

            // DocumentAnalysis lives on the buffer: dispose it when its last document view closes.
            var counter = view.TextBuffer.Properties.GetOrCreateSingletonProperty("UnrealSense.ViewCount", () => new int[1]);
            counter[0]++;
            var hints = new BlueprintHints(view, analysis, FormatMapService.GetEditorFormatMap(view));
            view.Closed += (s, e) =>
            {
                hints.Dispose();
                if (--counter[0] == 0)
                {
                    view.TextBuffer.Properties.RemoveProperty(typeof(DocumentAnalysis));
                    analysis.Dispose();
                }
            };
        }
    }

    /// <summary>
    /// End-of-line hints such as "3 Blueprint usages" next to reflected classes, functions and properties,
    /// like Rider's Blueprint code vision. Clicking opens the Blueprint Usages window.
    /// </summary>
    internal sealed class BlueprintHints : IDisposable
    {
        public const string LayerName = "UnrealSenseBlueprintHints";

        sealed class Hint
        {
            public int Offset;          // offset of the declared name in the analyzed snapshot
            public string Text;
            public bool IsZero;
            public SymbolAtPosition Symbol;
            public List<AssetUsage> Usages;
        }

        readonly IWpfTextView view;
        readonly DocumentAnalysis analysis;
        readonly IEditorFormatMap formatMap;
        readonly IAdornmentLayer layer;
        List<Hint> hints = new List<Hint>();
        ITextSnapshot hintsSnapshot;

        public BlueprintHints(IWpfTextView view, DocumentAnalysis analysis, IEditorFormatMap formatMap)
        {
            this.view = view;
            this.analysis = analysis;
            this.formatMap = formatMap;
            layer = view.GetAdornmentLayer(LayerName);
            analysis.Updated += OnAnalysisUpdated;
            view.LayoutChanged += OnLayoutChanged;
            if (analysis.File != null) OnAnalysisUpdated(this, EventArgs.Empty);
        }

        void OnAnalysisUpdated(object sender, EventArgs e)
        {
            // Runs on the analysis thread: compute usages here, draw on the UI thread.
            var file = analysis.File;
            var snapshot = analysis.Snapshot;
            var workspace = analysis.Workspace;
            var computed = new List<Hint>();
            if (General.Instance.ShowBlueprintHints && file != null && workspace?.Assets != null)
            {
                foreach (var type in file.Types)
                {
                    if (type.IsNativeInterfaceClass) continue;
                    var usages = workspace.FindUsages(type, analysis.FilePath);
                    if (usages.Count > 0)
                        computed.Add(new Hint { Offset = type.NameStart, Text = DescribeType(usages), Usages = usages, Symbol = new SymbolAtPosition { Type = type, DeclaringFile = analysis.FilePath } });

                    foreach (var member in type.Functions.Cast<ReflectedMember>().Concat(type.Properties))
                    {
                        var memberUsages = workspace.FindUsages(member, analysis.FilePath);
                        bool exposed = IsBlueprintExposed(member);
                        if (memberUsages.Count == 0 && !(exposed && General.Instance.ShowZeroUsages)) continue;
                        computed.Add(new Hint
                        {
                            Offset = member.NameStart,
                            Text = memberUsages.Count == 0 ? "no Blueprint usages" : Plural(memberUsages.Select(u => u.Asset.FilePath).Distinct().Count(), "Blueprint usage") + (memberUsages.All(u => u.IsHeuristic) ? " (by name)" : ""),
                            IsZero = memberUsages.Count == 0,
                            Usages = memberUsages,
                            Symbol = new SymbolAtPosition { Type = type, Member = member, DeclaringFile = analysis.FilePath },
                        });
                    }
                }
            }

            if (computed.Count > 0)
                Log.Trace($"Blueprint hints for {System.IO.Path.GetFileName(analysis.FilePath)}: " + string.Join("; ", computed.Select(h => $"{h.Symbol.DisplayName} → {h.Text}")));

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                hints = computed;
                hintsSnapshot = snapshot;
                Redraw();
            }).FireAndForget();
        }

        static string DescribeType(List<AssetUsage> usages)
        {
            int derived = usages.Count(u => u.Kind == AssetUsageKind.Subclass);
            int refs = usages.Count(u => u.Kind != AssetUsageKind.Subclass);
            var parts = new List<string>();
            if (derived > 0) parts.Add(Plural(derived, "derived Blueprint"));
            if (refs > 0) parts.Add(Plural(refs, "asset reference"));
            return string.Join(" · ", parts);
        }

        static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        static bool IsBlueprintExposed(ReflectedMember member)
        {
            var m = member.Macro;
            if (member is ReflectedFunction)
                return m.Has("BlueprintCallable") || m.Has("BlueprintPure") || m.Has("BlueprintImplementableEvent") || m.Has("BlueprintNativeEvent");
            return m.Has("BlueprintReadWrite") || m.Has("BlueprintReadOnly") || m.Has("BlueprintAssignable");
        }

        void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (e.NewOrReformattedLines.Count > 0 || e.VerticalTranslation) Redraw();
        }

        void Redraw()
        {
            if (view.IsClosed) return;
            layer.RemoveAllAdornments();
            if (hintsSnapshot == null || hints.Count == 0 || !General.Instance.ShowBlueprintHints) return;

            var snapshot = view.TextSnapshot;
            var props = formatMap.GetProperties("Plain Text");
            var typeface = props[ClassificationFormatDefinition.TypefaceId] as Typeface;
            var size = props[ClassificationFormatDefinition.FontRenderingSizeId] is double s ? s : 12.0;
            var commentProps = formatMap.GetProperties("Comment");
            var brush = (commentProps[EditorFormatDefinition.ForegroundBrushId] as Brush) ?? Brushes.Gray;

            foreach (var group in hints.GroupBy(h => SafeLine(h.Offset)))
            {
                if (group.Key < 0) continue;
                var hint = group.First();
                var point = new SnapshotPoint(hintsSnapshot, Math.Min(hint.Offset, hintsSnapshot.Length)).TranslateTo(snapshot, PointTrackingMode.Positive);
                var line = view.GetTextViewLineContainingBufferPosition(point);
                if (line == null || line.VisibilityState == VisibilityState.Unattached) continue;

                var block = new TextBlock
                {
                    Text = "  ⬡ " + hint.Text,
                    FontSize = size * 0.9,
                    FontFamily = typeface?.FontFamily ?? new FontFamily("Consolas"),
                    FontStyle = FontStyles.Italic,
                    Foreground = brush,
                    Opacity = hint.IsZero ? 0.45 : 0.8,
                    Cursor = Cursors.Hand,
                    ToolTip = "UnrealSense: click to list Blueprint usages",
                };
                block.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    BlueprintUsagesWindow.ShowUsagesAsync(hint.Symbol.DisplayName, hint.Usages).FireAndForget();
                };
                Canvas.SetLeft(block, line.TextRight + 8);
                Canvas.SetTop(block, line.TextTop + (line.TextHeight - size * 1.2) / 2);
                layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, hint, block, null);
            }

            int SafeLine(int offset) => offset >= 0 && offset <= hintsSnapshot.Length ? hintsSnapshot.GetLineNumberFromPosition(offset) : -1;
        }

        public void Dispose()
        {
            analysis.Updated -= OnAnalysisUpdated;
            view.LayoutChanged -= OnLayoutChanged;
        }
    }
}
