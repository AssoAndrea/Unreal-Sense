using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;
using UnrealSense.Navigation;

namespace UnrealSense.Extension.GoTo
{
    internal enum GoToMode { Symbols, Types, Files }

    /// <summary>
    /// Keyboard-driven "Go to symbol / type / file" popup. Every keystroke cancels the previous query and runs a
    /// new one on a background thread against the in-memory <see cref="GoToIndex"/>; only the top results are
    /// materialized as UI elements, so typing stays instant even with millions of symbols.
    /// </summary>
    internal sealed class GoToWindow : Window
    {
        const int MaxResults = 150;
        static readonly HashSet<SymbolKind> TypeKinds = new HashSet<SymbolKind>
        {
            SymbolKind.Class, SymbolKind.Struct, SymbolKind.Union, SymbolKind.Enum, SymbolKind.Typedef, SymbolKind.Delegate,
        };

        static GoToWindow current;
        static string lastQuery = string.Empty;

        readonly TextBox input = new TextBox { FontSize = 15, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        readonly ListBox list = new ListBox { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 3, 8, 4), Opacity = 0.7, FontSize = 11 };
        readonly Dictionary<GoToMode, ToggleButton> modeButtons = new Dictionary<GoToMode, ToggleButton>();
        GoToMode mode;
        CancellationTokenSource search;

        public static void ShowPopup(GoToMode mode, string initialText = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            current?.Close();
            var window = new GoToWindow(mode, initialText ?? lastQuery);
            current = window;
            KeyboardInputFilter.Attach(window);
            window.Show();
            window.Activate();
            window.input.Focus();
            window.input.SelectAll();
        }

        GoToWindow(GoToMode mode, string initialText)
        {
            this.mode = mode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Width = 860;
            Height = 540;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            SetResourceReference(BorderBrushProperty, EnvironmentColors.AccentBorderBrushKey);
            BorderThickness = new Thickness(1);
            Community.VisualStudio.Toolkit.Themes.SetUseVsTheme(this, true);

            var header = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (m, label, key) in new[] { (GoToMode.Symbols, "Symbols", "Ctrl+1"), (GoToMode.Types, "Types", "Ctrl+2"), (GoToMode.Files, "Files", "Ctrl+3") })
            {
                var button = new ToggleButton { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = $"{label} ({key}, Tab cycles)", Focusable = false };
                button.Click += (s, e) => SetMode(m);
                modeButtons[m] = button;
                modes.Children.Add(button);
            }
            var hint = new TextBlock { Text = "Enter: open · Esc: close · Container::Name · file.cpp:120", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            DockPanel.SetDock(hint, Dock.Right);
            header.Children.Add(hint);
            header.Children.Add(modes);

            input.SetResourceReference(BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            input.SetResourceReference(ForegroundProperty, EnvironmentColors.ComboBoxTextBrushKey);
            input.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ComboBoxTextBrushKey);
            input.TextChanged += (s, e) => RunSearch();
            list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.MouseDoubleClick += (s, e) => OpenSelected();
            VirtualizingPanel.SetIsVirtualizing(list, true);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(input, Dock.Top);
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(input);
            root.Children.Add(status);
            root.Children.Add(list);
            Content = root;

            PreviewKeyDown += OnPreviewKeyDown;
            Deactivated += (s, e) => { if (IsVisible) Close(); };
            Closed += (s, e) =>
            {
                search?.Cancel();
                lastQuery = input.Text;
                if (current == this) current = null;
            };

            input.Text = initialText ?? string.Empty;
            SetMode(mode);
        }

        void SetMode(GoToMode newMode)
        {
            mode = newMode;
            foreach (var pair in modeButtons) pair.Value.IsChecked = pair.Key == mode;
            RunSearch();
            input.Focus();
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); e.Handled = true; break;
                case Key.Enter: OpenSelected(); e.Handled = true; break;
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.PageDown: Move(12); e.Handled = true; break;
                case Key.PageUp: Move(-12); e.Handled = true; break;
                case Key.Tab: SetMode((GoToMode)(((int)mode + 1) % 3)); e.Handled = true; break;
                case Key.D1 when Keyboard.Modifiers == ModifierKeys.Control: SetMode(GoToMode.Symbols); e.Handled = true; break;
                case Key.D2 when Keyboard.Modifiers == ModifierKeys.Control: SetMode(GoToMode.Types); e.Handled = true; break;
                case Key.D3 when Keyboard.Modifiers == ModifierKeys.Control: SetMode(GoToMode.Files); e.Handled = true; break;
            }
        }

        void Move(int delta)
        {
            if (list.Items.Count == 0) return;
            int index = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + delta));
            list.SelectedIndex = index;
            list.ScrollIntoView(list.SelectedItem);
        }

        void RunSearch()
        {
            search?.Cancel();
            var cts = search = new CancellationTokenSource();
            var query = input.Text;
            var searchMode = mode;
            var workspace = WorkspaceService.Current;
            if (workspace?.GoTo == null || workspace.GoTo.SymbolCount + workspace.GoTo.FileCount == 0)
            {
                list.Items.Clear();
                status.Text = workspace == null ? "No Unreal project loaded." : "Building the go-to index… (first run indexes the engine, then it is cached)";
                return;
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                list.Items.Clear();
                status.Text = $"{workspace.GoTo.SymbolCount:N0} symbols · {workspace.GoTo.FileCount:N0} files" + (workspace.GoToState == Workspace.WorkspaceState.Loading ? " · refreshing…" : "");
                return;
            }

            bool includeEngine = General.Instance.GoToIncludeEngine;
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                object results = searchMode == GoToMode.Files
                    ? (object)workspace.GoTo.SearchFiles(query, MaxResults, cts.Token, includeEngine)
                    : workspace.GoTo.SearchSymbols(query, searchMode == GoToMode.Types ? TypeKinds : null, MaxResults, cts.Token, includeEngine);
                return (results, sw.Elapsed.TotalMilliseconds);
            }, cts.Token).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.IsFaulted || t.IsCanceled) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    Populate(t.Result.results, t.Result.TotalMilliseconds, workspace);
                }));
            }, TaskScheduler.Default);
        }

        void Populate(object results, double milliseconds, Workspace.UnrealWorkspace workspace)
        {
            list.Items.Clear();
            int count = 0;
            if (results is List<SymbolResult> symbols)
            {
                foreach (var r in symbols) list.Items.Add(SymbolItem(r));
                count = symbols.Count;
            }
            else if (results is List<FileResult> files)
            {
                foreach (var r in files) list.Items.Add(FileItem(r));
                count = files.Count;
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            status.Text = $"{count}{(count == MaxResults ? "+" : "")} results in {milliseconds:F1} ms · {workspace.GoTo.SymbolCount:N0} symbols · {workspace.GoTo.FileCount:N0} files";
        }

        ListBoxItem SymbolItem(SymbolResult r)
        {
            var grid = Row(IconFor(r.Kind), Highlighted(r.Name, r.Highlights),
                r.Container != null ? r.Container + "::" : null,
                $"{Path.GetFileName(r.File)}:{r.Line + 1}", r.IsEngine);
            return new ListBoxItem { Content = grid, Tag = r, ToolTip = r.File };
        }

        ListBoxItem FileItem(FileResult r)
        {
            var grid = Row(IconForFile(r.Path), Highlighted(Path.GetFileName(r.Path), r.Highlights), null,
                Path.GetDirectoryName(r.RelativePath), r.IsEngine);
            return new ListBoxItem { Content = grid, Tag = r, ToolTip = r.Path };
        }

        static Grid Row(ImageMoniker icon, TextBlock name, string container, string location, bool isEngine)
        {
            var grid = new Grid { Opacity = isEngine ? 0.82 : 1.0 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var image = new CrispImage { Moniker = icon, Width = 16, Height = 16, Margin = new Thickness(2, 1, 6, 1) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            if (container != null)
                left.Children.Add(new TextBlock { Text = container, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(name);
            var right = new TextBlock { Text = location + (isEngine ? "  · engine" : ""), Opacity = 0.6, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(left, 1);
            Grid.SetColumn(right, 2);
            grid.Children.Add(image);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        static TextBlock Highlighted(string text, int[] positions)
        {
            var block = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var set = new HashSet<int>(positions ?? Array.Empty<int>());
            int i = 0;
            while (i < text.Length)
            {
                bool bold = set.Contains(i);
                int j = i;
                while (j < text.Length && set.Contains(j) == bold) j++;
                var run = new Run(text.Substring(i, j - i));
                if (bold)
                {
                    run.FontWeight = FontWeights.Bold;
                    run.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ControlLinkTextBrushKey);
                }
                block.Inlines.Add(run);
                i = j;
            }
            return block;
        }

        void OpenSelected()
        {
            var tag = (list.SelectedItem as ListBoxItem)?.Tag;
            if (tag == null) return;
            Close();
            if (tag is SymbolResult s)
                OpenSymbolAsync(s).FireAndForget();
            else if (tag is FileResult f)
                (f.Line >= 0 ? EditorNavigation.OpenAtLineAsync(f.Path, f.Line, 0) : EditorNavigation.OpenAtLineAsync(f.Path, 0, 0)).FireAndForget();
        }

        static async Task OpenSymbolAsync(SymbolResult s)
        {
            // Put the caret on the name, not at the start of the line.
            int column = 0;
            try
            {
                var line = File.ReadLines(s.File).Skip(s.Line).FirstOrDefault();
                if (line != null) column = Math.Max(0, IndexOfWord(line, s.Name.TrimStart('~')));
            }
            catch (IOException) { }
            await EditorNavigation.OpenAtLineAsync(s.File, s.Line, column);
        }

        static int IndexOfWord(string line, string word)
        {
            for (int i = line.IndexOf(word, StringComparison.Ordinal); i >= 0; i = line.IndexOf(word, i + 1, StringComparison.Ordinal))
            {
                bool startOk = i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_');
                int end = i + word.Length;
                bool endOk = end >= line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] == '_');
                if (startOk && endOk) return i;
            }
            return -1;
        }

        static ImageMoniker IconFor(SymbolKind kind)
        {
            switch (kind)
            {
                case SymbolKind.Class: return KnownMonikers.ClassPublic;
                case SymbolKind.Struct: return KnownMonikers.StructurePublic;
                case SymbolKind.Union: return KnownMonikers.UnionPublic;
                case SymbolKind.Enum: return KnownMonikers.EnumerationPublic;
                case SymbolKind.EnumValue: return KnownMonikers.EnumerationItemPublic;
                case SymbolKind.Function: return KnownMonikers.MethodPublic;
                case SymbolKind.Field: return KnownMonikers.FieldPublic;
                case SymbolKind.Variable: return KnownMonikers.LocalVariable;
                case SymbolKind.Macro: return KnownMonikers.MacroPublic;
                case SymbolKind.Typedef: return KnownMonikers.TypeDefinitionPublic;
                case SymbolKind.Namespace: return KnownMonikers.Namespace;
                case SymbolKind.Delegate: return KnownMonikers.DelegatePublic;
                default: return KnownMonikers.QuestionMark;
            }
        }

        static ImageMoniker IconForFile(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".h":
                case ".hpp":
                case ".inl": return KnownMonikers.CPPHeaderFile;
                case ".cpp":
                case ".c": return KnownMonikers.CPPSourceFile;
                case ".cs": return KnownMonikers.CSFileNode;
                case ".ini": return KnownMonikers.ConfigurationFile;
                case ".json":
                case ".uplugin": return KnownMonikers.JSONScript;
                default: return KnownMonikers.Document;
            }
        }
    }
}
