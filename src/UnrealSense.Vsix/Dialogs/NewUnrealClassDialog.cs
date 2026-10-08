using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;
using UnrealSense.Project;
using UnrealSense.Templates;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Dialogs
{
    /// <summary>
    /// "New Unreal Class" dialog, laid out like Rider's: name, parent (Common / All Classes), base folder and path,
    /// with the resulting file names. It only builds the request; <see cref="Commands.NewUnrealClassCommand"/> writes.
    /// </summary>
    internal sealed class NewUnrealClassDialog : DialogWindow
    {
        const int MaxListed = 400;

        readonly UnrealWorkspace workspace;
        readonly UnrealProject project;
        readonly List<UnrealModule> modules;
        ParentClassCatalog catalog;
        NewClassGenerator generator;
        bool engineLoaded;
        bool updatingPath;

        readonly TextBox nameBox = new TextBox();
        readonly ToggleButton commonButton = new ToggleButton { Content = "Common", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0), IsChecked = true };
        readonly ToggleButton allButton = new ToggleButton { Content = "All Classes", Padding = new Thickness(10, 2, 10, 2) };
        readonly TextBox filterBox = new TextBox { MinWidth = 160, Margin = new Thickness(8, 0, 0, 0) };
        readonly ListBox list = new ListBox { Height = 230 };
        readonly TextBlock listStatus = new TextBlock { Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        readonly RadioButton rootRadio = new RadioButton { Content = "Root", Margin = new Thickness(0, 0, 14, 0), GroupName = "Base" };
        readonly RadioButton publicRadio = new RadioButton { Content = "Public", Margin = new Thickness(0, 0, 14, 0), GroupName = "Base" };
        readonly RadioButton privateRadio = new RadioButton { Content = "Private", GroupName = "Base" };
        readonly TextBox pathBox = new TextBox();
        readonly TextBlock headerText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock sourceText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock messages = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        readonly CheckBox addDependency = new CheckBox { Margin = new Thickness(0, 6, 0, 0), IsChecked = true, Visibility = Visibility.Collapsed };
        readonly Button okButton = new Button { Content = "OK", MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        readonly Button cancelButton = new Button { Content = "Cancel", MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), IsCancel = true };

        UnrealModule module;
        ClassLocation location;
        string subFolder = "";
        ParentClassInfo selected;
        string pathError;

        /// <summary>The validated result, set when the dialog closes with OK.</summary>
        public NewClassResult Result { get; private set; }
        public NewClassRequest Request { get; private set; }
        public bool AddDependency => addDependency.IsChecked == true && Result?.MissingDependency != null;
        public UnrealModule Module => module;

        public NewUnrealClassDialog(UnrealWorkspace workspace, string startDirectory)
        {
            this.workspace = workspace;
            project = workspace.Project;
            modules = project.AllModules.Where(m => m.Owner == ModuleOwnerKind.Project || m.Owner == ModuleOwnerKind.ProjectPlugin)
                .OrderBy(m => m.Owner).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            catalog = NewClassService.CreateCatalog(workspace, null);
            generator = new NewClassGenerator(project, catalog, workspace.Symbols);

            Title = "New Unreal Class";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            Community.VisualStudio.Toolkit.Themes.SetUseVsTheme(this, true);

            Content = BuildLayout();
            InitLocation(startDirectory);

            nameBox.TextChanged += (s, e) => Refresh();
            commonButton.Click += (s, e) => SetAll(false);
            allButton.Click += (s, e) => SetAll(true);
            filterBox.TextChanged += (s, e) => FillList();
            list.SelectionChanged += (s, e) => { selected = (list.SelectedItem as ListBoxItem)?.Tag as ParentClassInfo ?? selected; Refresh(); };
            list.MouseDoubleClick += (s, e) => { if (okButton.IsEnabled) Accept(); };
            rootRadio.Checked += (s, e) => SetLocation(ClassLocation.Root);
            publicRadio.Checked += (s, e) => SetLocation(ClassLocation.Public);
            privateRadio.Checked += (s, e) => SetLocation(ClassLocation.Private);
            pathBox.TextChanged += (s, e) => { if (!updatingPath) ParsePath(pathBox.Text); Refresh(); };
            okButton.Click += (s, e) => Accept();
            PreviewKeyDown += OnPreviewKeyDown;

            FillList();
            list.SelectedIndex = 0;
            Loaded += (s, e) => { nameBox.Focus(); LoadEngineClassesAsync(); };
        }

        FrameworkElement BuildLayout()
        {
            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            void AddRow(string label, UIElement element, bool top = false)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (label != null)
                {
                    var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 12, 8), VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center };
                    Grid.SetRow(text, row);
                    grid.Children.Add(text);
                }
                if (element is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 0, 8);
                Grid.SetRow(element, row);
                Grid.SetColumn(element, 1);
                grid.Children.Add(element);
                row++;
            }

            ThemeInput(nameBox);
            ThemeInput(filterBox);
            ThemeInput(pathBox);
            AddRow("Class Name:", nameBox);

            var toggles = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(commonButton);
            buttons.Children.Add(allButton);
            DockPanel.SetDock(buttons, Dock.Left);
            toggles.Children.Add(buttons);
            filterBox.ToolTip = "Filter";
            toggles.Children.Add(filterBox);
            var parentPanel = new StackPanel();
            parentPanel.Children.Add(toggles);
            list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            parentPanel.Children.Add(list);
            parentPanel.Children.Add(listStatus);
            AddRow("Parent Class:", parentPanel, top: true);

            var radios = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            radios.Children.Add(rootRadio);
            radios.Children.Add(publicRadio);
            radios.Children.Add(privateRadio);
            AddRow("Base folder:", radios);

            var pathPanel = new DockPanel();
            var browse = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose a folder" };
            browse.Click += (s, e) => Browse();
            DockPanel.SetDock(browse, Dock.Right);
            pathPanel.Children.Add(browse);
            pathPanel.Children.Add(pathBox);
            AddRow("Path:", pathPanel);
            AddRow("Header file:", headerText);
            AddRow("Source file:", sourceText);

            var bottom = new StackPanel();
            bottom.Children.Add(messages);
            bottom.Children.Add(addDependency);
            var okCancel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            okCancel.Children.Add(okButton);
            okCancel.Children.Add(cancelButton);
            bottom.Children.Add(okCancel);
            Grid.SetColumnSpan(bottom, 2);
            AddRow(null, bottom);
            Grid.SetColumn(bottom, 0);
            return grid;
        }

        static void ThemeInput(TextBox box)
        {
            box.Padding = new Thickness(3, 2, 3, 2);
            box.SetResourceReference(BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ComboBoxTextBrushKey);
            box.SetResourceReference(BorderBrushProperty, EnvironmentColors.ComboBoxBorderBrushKey);
            box.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ComboBoxTextBrushKey);
        }

        async void LoadEngineClassesAsync()
        {
            listStatus.Text = "Loading engine and plugin classes…";
            try
            {
                var engineClasses = await NewClassService.GetEngineClassesAsync(project.Engine);
                catalog = NewClassService.CreateCatalog(workspace, engineClasses);
                generator = new NewClassGenerator(project, catalog, workspace.Symbols);
                engineLoaded = engineClasses.Count > 0;
                FillList();
                Refresh();
            }
            catch (Exception e)
            {
                Log.Error("NewUnrealClass: catalog", e);
                listStatus.Text = "Engine classes could not be read (see the UnrealSense log).";
            }
        }

        void SetAll(bool all)
        {
            commonButton.IsChecked = !all;
            allButton.IsChecked = all;
            FillList();
            filterBox.Focus();
        }

        void FillList()
        {
            bool all = allButton.IsChecked == true;
            var filter = filterBox.Text.Trim();
            IEnumerable<ParentClassInfo> source = all ? catalog.All : ParentClassCatalog.CommonParents;
            if (filter.Length > 0)
                source = source.Where(i => i.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            var matches = source.ToList();
            if (all && filter.Length > 0)
            {
                // Exact and prefix matches first ("Actor" → AActor before AActorComponent... and ADebugActor).
                matches = matches.OrderBy(i => Rank(i.Name, filter)).ThenBy(i => i.Name.Length).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var keep = selected;
            list.Items.Clear();
            foreach (var info in matches.Take(MaxListed))
            {
                var item = new ListBoxItem { Tag = info, Content = UiHelpers.Label(info.Text, Icon(info), all ? Secondary(info) : null) };
                list.Items.Add(item);
                // Switching Common ⇄ All Classes keeps the parent: "Actor" and AActor are the same class.
                if (keep != null && list.SelectedItem == null && (ReferenceEquals(info, keep) || (info.Name != null && info.Name == keep.Name && info.Kind == keep.Kind)))
                    list.SelectedItem = item;
            }

            if (all)
                listStatus.Text = (matches.Count > MaxListed ? $"Showing {MaxListed:N0} of {matches.Count:N0}: type to filter. " : $"{matches.Count:N0} classes. ")
                    + (engineLoaded ? "" : "Loading engine and plugin classes…");
            else
                listStatus.Text = engineLoaded || project.Engine == null ? "" : "Loading engine and plugin classes…";
            if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0;
            else if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        }

        static int Rank(string name, string filter)
        {
            var bare = ReflectedNameOf(name);
            if (string.Equals(name, filter, StringComparison.OrdinalIgnoreCase) || string.Equals(bare, filter, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) || bare.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        static string ReflectedNameOf(string name) => name.Length > 1 && char.IsUpper(name[1]) && "UAFI".IndexOf(name[0]) >= 0 ? name.Substring(1) : name;

        static string Secondary(ParentClassInfo info)
        {
            var where = info.PluginName != null ? $"{info.PluginName} › {info.ModuleName}" : info.ModuleName;
            return info.IsEngine ? where : where + " (project)";
        }

        static ImageMoniker Icon(ParentClassInfo info)
        {
            switch (info.Kind)
            {
                case NewClassKind.Interface: return KnownMonikers.Interface;
                case NewClassKind.Struct: return KnownMonikers.Structure;
                case NewClassKind.Enum: return KnownMonikers.Enumeration;
                case NewClassKind.Empty: return KnownMonikers.ClassFile;
                default: return info.IsEngine ? KnownMonikers.Class : KnownMonikers.ClassPublic;
            }
        }

        void InitLocation(string startDirectory)
        {
            if (startDirectory == null || !ParsePath(startDirectory))
            {
                module = modules.FirstOrDefault(m => string.Equals(m.Name, project.Name, StringComparison.OrdinalIgnoreCase)) ?? modules.FirstOrDefault();
                location = module != null && Directory.Exists(Path.Combine(module.Directory, "Public")) ? ClassLocation.Public : ClassLocation.Root;
                subFolder = "";
            }
            SyncRadios();
            WritePath();
        }

        /// <summary>Reads module, base folder and sub-folder from a path (relative to the project or absolute).</summary>
        bool ParsePath(string text)
        {
            pathError = null;
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(project.ProjectDirectory, text)).TrimEnd('\\'); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                pathError = "The path is not valid.";
                return false;
            }
            var owner = modules.Where(m => full.Equals(m.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.StartsWith(m.Directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Directory.Length).FirstOrDefault();
            if (owner == null)
            {
                pathError = "The path is not inside a module of the project (Source\\<Module> or Plugins\\...\\Source\\<Module>).";
                return false;
            }
            var relative = full.Length > owner.Directory.TrimEnd('\\').Length ? full.Substring(owner.Directory.TrimEnd('\\').Length + 1) : "";
            var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var newLocation = ClassLocation.Root;
            if (parts.Count > 0 && parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase)) { newLocation = ClassLocation.Public; parts.RemoveAt(0); }
            else if (parts.Count > 0 && parts[0].Equals("Private", StringComparison.OrdinalIgnoreCase)) { newLocation = ClassLocation.Private; parts.RemoveAt(0); }
            module = owner;
            location = newLocation;
            subFolder = string.Join("\\", parts);
            SyncRadios();
            return true;
        }

        void SetLocation(ClassLocation newLocation)
        {
            if (updatingPath || newLocation == location) return;
            location = newLocation;
            WritePath();
            Refresh();
        }

        void SyncRadios()
        {
            updatingPath = true;
            rootRadio.IsChecked = location == ClassLocation.Root;
            publicRadio.IsChecked = location == ClassLocation.Public;
            privateRadio.IsChecked = location == ClassLocation.Private;
            updatingPath = false;
        }

        void WritePath()
        {
            if (module == null) return;
            var dir = location == ClassLocation.Root ? module.Directory : Path.Combine(module.Directory, location.ToString());
            if (subFolder.Length > 0) dir = Path.Combine(dir, subFolder);
            updatingPath = true;
            pathBox.Text = MakeRelative(dir);
            updatingPath = false;
            pathError = null;
        }

        string MakeRelative(string path)
        {
            var root = project.ProjectDirectory.TrimEnd('\\') + "\\";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }

        void Browse()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder of the new class (inside a module)", ShowNewFolderButton = true })
            {
                var current = Path.Combine(project.ProjectDirectory, pathBox.Text);
                dialog.SelectedPath = Directory.Exists(current) ? current : module?.Directory ?? project.SourceDirectory;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                pathBox.Text = MakeRelative(dialog.SelectedPath);
            }
        }

        void Refresh()
        {
            Result = null;
            if (module == null || pathError != null)
            {
                Show(null, new[] { pathError ?? "The project has no module to add a class to." }, Array.Empty<string>());
                return;
            }
            Request = new NewClassRequest { Name = nameBox.Text, Parent = selected, Module = module, Location = location, SubFolder = subFolder };
            var result = generator.Generate(Request);
            var errors = result.Errors.ToList();
            if (string.IsNullOrWhiteSpace(nameBox.Text)) errors = new List<string>();
            Show(result, errors, result.Warnings.Where(w => result.MissingDependency == null || !w.Contains("does not depend on")).ToList());
            okButton.IsEnabled = result.IsValid && !string.IsNullOrWhiteSpace(nameBox.Text);
            if (okButton.IsEnabled) Result = result;
        }

        void Show(NewClassResult result, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        {
            headerText.Text = result?.HeaderPath != null ? "…\\" + ShortPath(result.HeaderPath) : "";
            headerText.ToolTip = result?.HeaderPath;
            sourceText.Text = result == null || result.HeaderPath == null ? "" : result.SourcePath != null ? "…\\" + ShortPath(result.SourcePath) : "(none: header only)";
            sourceText.ToolTip = result?.SourcePath;

            messages.Inlines.Clear();
            foreach (var e in errors) AddMessage(e, Brushes.IndianRed);
            foreach (var w in warnings) AddMessage(w, Brushes.Goldenrod);
            messages.Visibility = messages.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var dependency = result?.MissingDependency;
            addDependency.Visibility = dependency != null ? Visibility.Visible : Visibility.Collapsed;
            if (dependency != null)
                addDependency.Content = $"Add \"{dependency}\" to PublicDependencyModuleNames in {module.Name}.Build.cs (needed by {selected?.Name})";
            if (result == null) okButton.IsEnabled = false;
        }

        void AddMessage(string text, Brush brush)
        {
            if (messages.Inlines.Count > 0) messages.Inlines.Add(new System.Windows.Documents.LineBreak());
            messages.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = brush });
        }

        string ShortPath(string path)
        {
            // Rider shows the module-relative part: "…\Public\Variant_Shooter\MyClass.h".
            var root = module?.Directory.TrimEnd('\\') + "\\";
            return root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Up/Down move in the parent list while typing the name or the filter.
            if ((e.Key == Key.Down || e.Key == Key.Up) && (nameBox.IsKeyboardFocusWithin || filterBox.IsKeyboardFocusWithin) && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
        }

        void Accept()
        {
            Refresh();
            if (Result == null) return;
            DialogResult = true;
        }
    }
}
