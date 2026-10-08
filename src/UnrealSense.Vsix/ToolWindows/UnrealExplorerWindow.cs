using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Cpp;
using UnrealSense.Project;
using UnrealSense.Extension.Commands;
using UnrealSense.Extension.Services;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.ToolWindows
{
    /// <summary>Project view in Unreal terms: modules, plugins, reflected types, Blueprints and config.</summary>
    public class UnrealExplorerWindow : BaseToolWindow<UnrealExplorerWindow>
    {
        public override string GetTitle(int toolWindowId) => "Unreal Explorer";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken) =>
            Task.FromResult<FrameworkElement>(new UnrealExplorerControl());

        [Guid("a3f4b5c6-7d8e-4f90-a1b2-c3d4e5f6a766")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Hierarchy;
            }
        }
    }

    internal sealed class UnrealExplorerControl : UserControl
    {
        readonly TreeView tree = new TreeView { BorderThickness = new Thickness(0) };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(6, 2, 6, 4), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        readonly TextBox filter = new TextBox { Margin = new Thickness(6, 2, 6, 4), ToolTip = "Filter reflected types and members" };
        readonly DispatcherTimer refreshTimer;

        public UnrealExplorerControl()
        {
            UiHelpers.ApplyTheme(this);

            var toolbar = new WrapPanel { Margin = new Thickness(6, 4, 6, 2) };
            toolbar.Children.Add(UiHelpers.ToolbarButton("Refresh", KnownMonikers.Refresh, Rebuild, "Refresh the view"));
            toolbar.Children.Add(UiHelpers.ToolbarButton("New class", KnownMonikers.AddClass, () => NewUnrealClassCommand.RunAsync(null).FireAndForget(), "New Unreal class (.h/.cpp)"));
            toolbar.Children.Add(UiHelpers.ToolbarButton("Generate project files", KnownMonikers.BuildSolution, () => GenerateProjectFilesCommand.RunAsync().FireAndForget()));
            toolbar.Children.Add(UiHelpers.ToolbarButton("Open in Unreal Editor", KnownMonikers.Run, () => OpenInUnrealEditorCommand.Run()));

            refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (s, e) => { ((DispatcherTimer)s).Stop(); Rebuild(); }, Dispatcher);
            refreshTimer.Stop();
            filter.TextChanged += (s, e) => { refreshTimer.Stop(); refreshTimer.Start(); };
            tree.MouseDoubleClick += OnActivate;
            tree.KeyDown += (s, e) => { if (e.Key == Key.Enter) OnActivate(s, e); };

            var root = new DockPanel();
            foreach (var top in new FrameworkElement[] { toolbar, status, filter })
            {
                DockPanel.SetDock(top, Dock.Top);
                root.Children.Add(top);
            }
            root.Children.Add(tree);
            Content = root;

            WorkspaceService.Changed += OnWorkspaceChanged;
            Unloaded += (s, e) => WorkspaceService.Changed -= OnWorkspaceChanged;
            Rebuild();
        }

        void OnWorkspaceChanged(object sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => { refreshTimer.Stop(); refreshTimer.Start(); }));

        static void OnActivate(object sender, RoutedEventArgs e)
        {
            if ((sender as TreeView)?.SelectedItem is TreeViewItem item && item.Tag is Action action)
            {
                e.Handled = true;
                action();
            }
        }

        void Rebuild()
        {
            tree.Items.Clear();
            var workspace = WorkspaceService.Current;
            if (workspace?.Project == null)
            {
                status.Text = workspace?.StatusText ?? "Open the solution of an Unreal project (.uproject) to populate this view.";
                return;
            }
            status.Text = workspace.StatusText + (workspace.AssetState == WorkspaceState.Loading ? " — indexing Blueprints…" : "");

            var text = filter.Text?.Trim();
            if (!string.IsNullOrEmpty(text) && workspace.Symbols != null)
            {
                tree.Items.Add(SearchResults(workspace, text));
                return;
            }

            var project = workspace.Project;
            var engine = project.Engine != null ? $"UE {project.Engine.Version}{(project.Engine.IsSourceBuild ? " (source)" : "")}" : $"engine '{project.EngineAssociation}' not found";
            tree.Items.Add(UiHelpers.Node(project.Name, KnownMonikers.Application, () => Open(project.UProjectPath), () => ProjectChildren(workspace), engine, expanded: true));
        }

        IEnumerable<TreeViewItem> ProjectChildren(UnrealWorkspace workspace)
        {
            var project = workspace.Project;
            yield return UiHelpers.Node("Modules", KnownMonikers.ModulePublic, null,
                () => project.Modules.Select(m => ModuleNode(workspace, m)), $"{project.Modules.Count}", expanded: true);

            if (project.Plugins.Count > 0)
                yield return UiHelpers.Node("Plugins", KnownMonikers.Extension, null,
                    () => project.Plugins.OrderBy(p => p.Name).Select(p => UiHelpers.Node(p.FriendlyName ?? p.Name, KnownMonikers.Extension, () => Open(p.UPluginPath),
                        () => p.Modules.Select(m => ModuleNode(workspace, m)), p.CanContainContent ? "content" : null)), $"{project.Plugins.Count}");

            yield return UiHelpers.Node("Blueprints by C++ parent", KnownMonikers.ClassDetails, null, () => BlueprintGroups(workspace),
                workspace.Assets == null ? "indexing…" : $"{workspace.Assets.Assets.Count(a => a.IsBlueprint)} Blueprints");

            if (Directory.Exists(project.ConfigDirectory))
                yield return UiHelpers.Node("Config", KnownMonikers.ConfigurationFile, null,
                    () => Directory.GetFiles(project.ConfigDirectory, "*.ini").OrderBy(f => f).Select(f => UiHelpers.Node(Path.GetFileName(f), KnownMonikers.TextFile, () => Open(f))));
        }

        TreeViewItem ModuleNode(UnrealWorkspace workspace, UnrealModule module)
        {
            return UiHelpers.Node(module.Name, KnownMonikers.Module, () => Open(module.BuildCsPath), () => ModuleChildren(workspace, module), module.Type);
        }

        IEnumerable<TreeViewItem> ModuleChildren(UnrealWorkspace workspace, UnrealModule module)
        {
            yield return UiHelpers.Node(Path.GetFileName(module.BuildCsPath), KnownMonikers.CSFileNode, () => Open(module.BuildCsPath));
            if (module.PublicDependencies.Count > 0)
                yield return UiHelpers.Node("Public dependencies", KnownMonikers.Reference, null,
                    () => module.PublicDependencies.Select(d => UiHelpers.Node(d, KnownMonikers.Module)), $"{module.PublicDependencies.Count}");
            if (module.PrivateDependencies.Count > 0)
                yield return UiHelpers.Node("Private dependencies", KnownMonikers.Reference, null,
                    () => module.PrivateDependencies.Select(d => UiHelpers.Node(d, KnownMonikers.Module)), $"{module.PrivateDependencies.Count}");

            var prefix = module.Directory.TrimEnd('\\') + "\\";
            var types = workspace.Symbols?.Headers
                .Where(h => h.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(h => h.Types.Where(t => !t.IsNativeInterfaceClass).Select(t => (Type: t, File: h.FilePath)))
                .OrderBy(t => t.Type.Name)
                .ToList() ?? new List<(ReflectedType, string)>();

            foreach (var group in types.GroupBy(t => t.Type.Kind))
            {
                var label = group.Key == ReflectedKind.Class ? "Classes" : group.Key == ReflectedKind.Struct ? "Structs" : group.Key == ReflectedKind.Enum ? "Enums" : "Interfaces";
                yield return UiHelpers.Node(label, IconFor(group.Key), null, () => group.Select(t => TypeNode(workspace, t.Type, t.File)), $"{group.Count()}");
            }
        }

        TreeViewItem TypeNode(UnrealWorkspace workspace, ReflectedType type, string file)
        {
            var derived = workspace.Assets?.GetDerivedBlueprints(workspace.GetScriptModule(file), type.ReflectedName).Count() ?? 0;
            var secondary = (type.BaseType != null ? ": " + type.BaseType : "") + (derived > 0 ? $"   ⬡ {derived} BP" : "");
            Func<IEnumerable<TreeViewItem>> children = null;
            if (type.Functions.Count + type.Properties.Count > 0)
                children = () => type.Properties.Select(p => UiHelpers.Node(p.Name, KnownMonikers.PropertyPublic, () => OpenAt(file, p.NameStart), null, p.Type))
                    .Concat(type.Functions.Select(f => UiHelpers.Node(f.Name + "()", KnownMonikers.MethodPublic, () => OpenAt(file, f.NameStart), null, string.Join(", ", f.Macro.Specifiers.Select(s => s.Key).Take(3)))));
            return UiHelpers.Node(type.Name, IconFor(type.Kind), () => OpenAt(file, type.NameStart), children, secondary);
        }

        IEnumerable<TreeViewItem> BlueprintGroups(UnrealWorkspace workspace)
        {
            if (workspace.Assets == null) yield break;
            // ProjectModuleOf also maps packages that are another name of a project module (a renamed module).
            var assets = workspace.Assets;
            var groups = assets.Assets
                .Where(a => assets.ProjectModuleOf(a.NativeParent) != null)
                .GroupBy(a => a.NativeParent.Type)
                .OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var module = assets.ProjectModuleOf(group.First().NativeParent);
                var all = workspace.Assets.GetDerivedBlueprints(module, group.Key).ToList();
                var cppName = workspace.Symbols?.Types.FirstOrDefault(t => t.ReflectedName == group.Key && !t.IsNativeInterfaceClass)?.Name ?? group.Key;
                yield return UiHelpers.Node(cppName, KnownMonikers.Class, null,
                    () => all.OrderBy(a => a.PackageName).Select(a => UiHelpers.Node(a.AssetName, KnownMonikers.ClassFile, () => EditorNavigation.RevealInExplorer(a.FilePath), null,
                        a.BlueprintParent != null ? "via " + a.BlueprintParent.Split('/').Last() : a.AssetClass)),
                    $"{all.Count}");
            }
        }

        TreeViewItem SearchResults(UnrealWorkspace workspace, string text)
        {
            bool Match(string name) => name != null && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            var results = new List<TreeViewItem>();
            foreach (var header in workspace.Symbols.Headers.OrderBy(h => h.FilePath))
            {
                foreach (var type in header.Types.Where(t => !t.IsNativeInterfaceClass || t.Functions.Count > 0))
                {
                    if (Match(type.Name)) results.Add(TypeNode(workspace, type, header.FilePath));
                    foreach (var f in type.Functions.Where(f => Match(f.Name)))
                        results.Add(UiHelpers.Node($"{type.Name}::{f.Name}()", KnownMonikers.MethodPublic, () => OpenAt(header.FilePath, f.NameStart)));
                    foreach (var p in type.Properties.Where(p => Match(p.Name)))
                        results.Add(UiHelpers.Node($"{type.Name}::{p.Name}", KnownMonikers.PropertyPublic, () => OpenAt(header.FilePath, p.NameStart), null, p.Type));
                }
            }
            return UiHelpers.Node($"Results for \"{text}\"", KnownMonikers.Search, null, () => results.Take(500), $"{results.Count}", expanded: true);
        }

        static ImageMoniker IconFor(ReflectedKind kind) =>
            kind == ReflectedKind.Class ? KnownMonikers.ClassPublic
            : kind == ReflectedKind.Struct ? KnownMonikers.StructurePublic
            : kind == ReflectedKind.Enum ? KnownMonikers.EnumerationPublic
            : KnownMonikers.InterfacePublic;

        static void Open(string path)
        {
            if (File.Exists(path)) VS.Documents.OpenAsync(path).FireAndForget();
        }

        static void OpenAt(string path, int offset) => EditorNavigation.OpenAtOffsetAsync(path, offset).FireAndForget();
    }
}
