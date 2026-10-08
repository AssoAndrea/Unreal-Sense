using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Assets;
using UnrealSense.Extension.Editor;
using UnrealSense.Extension.Services;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.ToolWindows
{
    /// <summary>Lists the Blueprints/assets that use a C++ class, function or property.</summary>
    public class BlueprintUsagesWindow : BaseToolWindow<BlueprintUsagesWindow>
    {
        static BlueprintUsagesControl control;
        static (string Title, List<AssetUsage> Usages)? pending;

        public override string GetTitle(int toolWindowId) => "Blueprint Usages";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            control = new BlueprintUsagesControl();
            if (pending.HasValue) control.SetUsages(pending.Value.Title, pending.Value.Usages);
            return Task.FromResult<FrameworkElement>(control);
        }

        public static async Task ShowUsagesAsync(string title, List<AssetUsage> usages)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pending = (title, usages);
            await ShowAsync();
            control?.SetUsages(title, usages);
        }

        public static async Task ShowForSymbolAsync(SymbolAtPosition symbol, string filePath)
        {
            var workspace = WorkspaceService.GetForFile(symbol.DeclaringFile ?? filePath);
            if (workspace?.Assets == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: the Blueprint index is still being built…");
                return;
            }
            var declaring = symbol.DeclaringFile ?? filePath;
            var usages = await Task.Run(() => symbol.Member != null ? workspace.FindUsages(symbol.Member, declaring) : workspace.FindUsages(symbol.Type, declaring));
            await ShowUsagesAsync(symbol.DisplayName, usages);
        }

        [Guid("b7c1d2e3-4f5a-4b6c-8d7e-9f0a1b2c3d55")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.FindSymbol;
            }
        }
    }

    internal sealed class BlueprintUsagesControl : UserControl
    {
        sealed class Row
        {
            public AssetUsage Usage { get; set; }
            public string Kind => UnrealQuickInfoSource.Describe(Usage.Kind);
            public string Asset => Usage.Asset.AssetName;
            public string Type => Usage.Asset.AssetClass;
            public string Package => Usage.Asset.PackageName;
            public string Detail => Usage.Detail ?? (Usage.IsHeuristic ? "name match" : "");
        }

        readonly TextBlock header = new TextBlock { Margin = new Thickness(6, 4, 6, 4), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        readonly ListView list = new ListView { BorderThickness = new Thickness(0) };

        public BlueprintUsagesControl()
        {
            UiHelpers.ApplyTheme(this);

            var grid = new GridView();
            foreach (var (title, path, width) in new[] { ("Usage", "Kind", 190.0), ("Asset", "Asset", 220.0), ("Type", "Type", 130.0), ("Package", "Package", 380.0), ("Detail", "Detail", 140.0) })
                grid.Columns.Add(new GridViewColumn { Header = title, DisplayMemberBinding = new Binding(path), Width = width });
            list.View = grid;
            list.ItemContainerStyle = UiHelpers.ThemedListViewItemStyle();
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            list.MouseDoubleClick += (s, e) => OpenSelected();

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 4) };
            toolbar.Children.Add(UiHelpers.ToolbarButton("Open in Unreal Editor", KnownMonikers.Run, OpenSelected, "Open the asset in the running Unreal Editor (double-click does the same)"));
            toolbar.Children.Add(UiHelpers.ToolbarButton("Show in Explorer", KnownMonikers.OpenFolder, RevealSelected));
            toolbar.Children.Add(UiHelpers.ToolbarButton("Copy reference", KnownMonikers.Copy, CopySelected, "Copy the asset object path (e.g. /Game/BP/BP_Foo.BP_Foo)"));

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(toolbar);
            root.Children.Add(list);
            Content = root;
            header.Text = "Use \"Find Blueprint Usages\" (Alt+Shift+B) on a UCLASS, UFUNCTION or UPROPERTY.";
        }

        public void SetUsages(string title, List<AssetUsage> usages)
        {
            int assets = usages.Select(u => u.Asset.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            header.Text = usages.Count == 0
                ? $"{title}: no Blueprint usages found."
                : $"{title}: {usages.Count} usage{(usages.Count == 1 ? "" : "s")} in {assets} asset{(assets == 1 ? "" : "s")}"
                  + (usages.Any(u => u.IsHeuristic) ? " — property matches are based on the asset name table" : "");
            list.ItemsSource = usages.Select(u => new Row { Usage = u }).ToList();
        }

        void OpenSelected()
        {
            if (!(list.SelectedItem is Row row)) return;
            var asset = row.Usage.Asset;
            var project = (WorkspaceService.GetForFile(asset.FilePath) ?? WorkspaceService.Current)?.Project;
            UnrealEditorBridge.OpenAssetAsync(project, asset).FireAndForget();
        }

        void RevealSelected()
        {
            if (list.SelectedItem is Row row) EditorNavigation.RevealInExplorer(row.Usage.Asset.FilePath);
        }

        void CopySelected()
        {
            if (!(list.SelectedItem is Row row) || row.Package == null) return;
            Clipboard.SetText($"{row.Package}.{row.Asset}");
        }
    }
}
