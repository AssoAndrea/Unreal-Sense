using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Cpp;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.ToolWindows
{
    /// <summary>Find Usages results: code grouped by file (with usage kind) and Blueprint usages.</summary>
    public class FindUsagesWindow : BaseToolWindow<FindUsagesWindow>
    {
        static FindUsagesControl control;
        static UsageResults pending;

        public override string GetTitle(int toolWindowId) => "Find Usages";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            control = new FindUsagesControl();
            if (pending != null) control.Show(pending);
            return Task.FromResult<FrameworkElement>(control);
        }

        internal static async Task ShowResultsAsync(UsageResults results)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pending = results;
            await ShowAsync();
            control?.Show(results);
        }

        [Guid("c8d9e0f1-2a3b-4c5d-8e6f-7a8b9c0d1e77")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.FindInFile;
            }
        }
    }

    internal sealed class FindUsagesControl : UserControl
    {
        readonly TextBlock title = new TextBlock { Margin = new Thickness(6, 4, 6, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock summary = new TextBlock { Margin = new Thickness(6, 2, 6, 2), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock warning = new TextBlock { Margin = new Thickness(6, 2, 6, 4), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        readonly TreeView tree = new TreeView { BorderThickness = new Thickness(0) };
        readonly Dictionary<ReferenceKind, ToggleButton> filters = new Dictionary<ReferenceKind, ToggleButton>();
        UsageResults results;

        public FindUsagesControl()
        {
            UiHelpers.ApplyTheme(this);
            warning.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);

            var toolbar = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
            foreach (var (kind, label) in new[] { (ReferenceKind.Declaration, "Declarations"), (ReferenceKind.Call, "Calls"), (ReferenceKind.Read, "Reads"), (ReferenceKind.Write, "Writes"), (ReferenceKind.Generated, "Generated") })
            {
                var toggle = new ToggleButton { Content = label, IsChecked = true, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 1, 8, 1) };
                toggle.Click += (s, e) => Rebuild();
                filters[kind] = toggle;
                toolbar.Children.Add(toggle);
            }

            tree.MouseDoubleClick += (s, e) => Activate();
            tree.KeyDown += (s, e) => { if (e.Key == Key.Enter) Activate(); };

            var root = new DockPanel();
            foreach (var top in new FrameworkElement[] { title, summary, warning, toolbar })
            {
                DockPanel.SetDock(top, Dock.Top);
                root.Children.Add(top);
            }
            root.Children.Add(tree);
            Content = root;
            title.Text = "Find Usages (editor context menu or Extensions › UnrealSense) on a symbol to see where it is used.";
        }

        public void Show(UsageResults r)
        {
            results = r;
            title.Text = r.Title;
            warning.Text = r.Warning ?? "";
            warning.Visibility = string.IsNullOrEmpty(r.Warning) ? Visibility.Collapsed : Visibility.Visible;
            Rebuild();
        }

        void Rebuild()
        {
            tree.Items.Clear();
            if (results == null) return;
            var visible = results.Code.Where(u => filters[u.Kind].IsChecked == true).ToList();
            var uncertain = results.Uncertain.Where(u => filters[u.Kind].IsChecked == true).ToList();
            int files = visible.Select(u => u.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            summary.Text = $"{visible.Count} code usage{(visible.Count == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}"
                           + (uncertain.Count > 0 ? $" · {uncertain.Count} uncertain" : "")
                           + (results.Blueprints.Count > 0 ? $" · {results.Blueprints.Count} Blueprint usage{(results.Blueprints.Count == 1 ? "" : "s")}" : "")
                           + $" · {results.Source ?? "text search"} · {results.Milliseconds:F0} ms";

            foreach (var fileNode in FileNodes(visible))
                tree.Items.Add(fileNode);

            if (results.Blueprints.Count > 0)
            {
                var bp = UiHelpers.Node("Blueprints", KnownMonikers.ClassDetails, null, null, $"({results.Blueprints.Count})");
                bp.IsExpanded = true;
                foreach (var u in results.Blueprints)
                    bp.Items.Add(UiHelpers.Node(u.Asset.AssetName, KnownMonikers.ClassFile, () => EditorNavigation.RevealInExplorer(u.Asset.FilePath), null,
                        $"{UnrealSense.Extension.Editor.UnrealQuickInfoSource.Describe(u.Kind)} · {u.Asset.PackageName}"));
                tree.Items.Add(bp);
            }

            // Last: the own index found the name on an object whose type it could not infer; the user decides.
            if (uncertain.Count > 0)
            {
                var node = UiHelpers.Node("Uncertain", KnownMonikers.StatusHelp, null, null,
                    $"({uncertain.Count}) the name on an object whose type could not be inferred: may be other symbols");
                node.IsExpanded = true;
                foreach (var fileNode in FileNodes(uncertain))
                    node.Items.Add(fileNode);
                tree.Items.Add(node);
            }
        }

        static IEnumerable<TreeViewItem> FileNodes(IEnumerable<CodeUsage> usages)
        {
            var project = WorkspaceService.Current?.Project?.ProjectDirectory;
            foreach (var group in usages.GroupBy(u => u.FilePath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            {
                var display = project != null && group.Key.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? group.Key.Substring(project.Length).TrimStart('\\') : group.Key;
                var fileNode = UiHelpers.Node(Path.GetFileName(group.Key), FileIcon(group.Key), null, null, $"{Path.GetDirectoryName(display)}  ({group.Count()})", expanded: false);
                fileNode.IsExpanded = true;
                foreach (var usage in group.OrderBy(u => u.Line))
                    fileNode.Items.Add(UsageNode(usage));
                yield return fileNode;
            }
        }

        static TreeViewItem UsageNode(CodeUsage u)
        {
            var text = u.LineText ?? "";
            int lead = text.Length - text.TrimStart().Length;
            var trimmed = text.Trim();
            int start = Math.Max(0, u.Column - lead), end = Math.Min(trimmed.Length, start + u.Length);

            var block = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            block.Inlines.Add(new Run($"{u.Line + 1,5}  ") { FontStyle = FontStyles.Italic });
            if (start <= trimmed.Length)
            {
                block.Inlines.Add(new Run(trimmed.Substring(0, start)));
                var match = new Run(trimmed.Substring(start, Math.Max(0, end - start))) { FontWeight = FontWeights.Bold };
                match.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
                block.Inlines.Add(match);
                block.Inlines.Add(new Run(trimmed.Substring(Math.Min(end, trimmed.Length))));
            }
            var kindLabel = new TextBlock { Text = u.Kind.ToString().ToLowerInvariant(), Opacity = 0.55, Margin = new Thickness(0, 0, 8, 0), Width = 70 };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new CrispImage { Moniker = KindIcon(u.Kind), Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
            panel.Children.Add(kindLabel);
            panel.Children.Add(block);
            Action open = () => EditorNavigation.OpenAtLineAsync(u.FilePath, u.Line, u.Column).FireAndForget();
            return new TreeViewItem { Header = panel, Tag = open };
        }

        void Activate()
        {
            if (tree.SelectedItem is TreeViewItem item && item.Tag is Action action) action();
        }

        static ImageMoniker KindIcon(ReferenceKind kind)
        {
            switch (kind)
            {
                case ReferenceKind.Declaration: return KnownMonikers.GoToDeclaration;
                case ReferenceKind.Call: return KnownMonikers.MethodPublic;
                case ReferenceKind.Write: return KnownMonikers.Edit;
                case ReferenceKind.Generated: return KnownMonikers.GenerateFile;
                default: return KnownMonikers.FieldPublic;
            }
        }

        static ImageMoniker FileIcon(string path) =>
            path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ? KnownMonikers.CPPHeaderFile : KnownMonikers.CPPSourceFile;
    }
}
