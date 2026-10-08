using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace UnrealSense.Extension.ToolWindows
{
    internal static class UiHelpers
    {
        /// <summary>Applies VS theme colors to a code-built control.</summary>
        public static void ApplyTheme(FrameworkElement element)
        {
            Community.VisualStudio.Toolkit.Themes.SetUseVsTheme(element, true);
            element.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            element.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }

        public static StackPanel Label(string text, ImageMoniker icon, string secondary = null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new CrispImage { Moniker = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0) });
            var main = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            main.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            panel.Children.Add(main);
            if (!string.IsNullOrEmpty(secondary))
            {
                var extra = new TextBlock { Text = "  " + secondary, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
                extra.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                panel.Children.Add(extra);
            }
            return panel;
        }

        static readonly object Placeholder = new object();

        /// <summary>A tree node; <paramref name="children"/> is evaluated on first expansion.</summary>
        public static TreeViewItem Node(string text, ImageMoniker icon, Action activate = null, Func<IEnumerable<TreeViewItem>> children = null, string secondary = null, bool expanded = false)
        {
            var item = new TreeViewItem { Header = Label(text, icon, secondary), Tag = activate };
            if (children != null)
            {
                item.Items.Add(Placeholder);
                void Populate()
                {
                    if (item.Items.Count != 1 || item.Items[0] != Placeholder) return;
                    item.Items.Clear();
                    foreach (var child in children()) item.Items.Add(child);
                }
                item.Expanded += (s, e) => { if (e.OriginalSource == item) Populate(); };
                if (expanded)
                {
                    Populate();
                    item.IsExpanded = true;
                }
            }
            return item;
        }

        public static Button ToolbarButton(string text, ImageMoniker icon, Action onClick, string tooltip = null)
        {
            var button = new Button
            {
                Content = Label(text, icon),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(6, 2, 6, 2),
                ToolTip = tooltip ?? text,
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        /// <summary>
        /// Row style for a GridView ListView using VS theme colors. The default WPF ListViewItem template paints a light
        /// hover/selection background while the text keeps the (light) dark-theme foreground, making rows unreadable.
        /// </summary>
        public static Style ThemedListViewItemStyle()
        {
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, new Thickness(0, 1, 0, 1));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var presenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
            presenter.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(GridViewRowPresenter.ColumnsProperty, new TemplateBindingExtension(GridView.ColumnCollectionProperty));
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(ListViewItem)) { VisualTree = border };
            Trigger State(DependencyProperty property, object background, object text)
            {
                var trigger = new Trigger { Property = property, Value = true };
                trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(background), "Bd"));
                trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(text)));
                return trigger;
            }
            template.Triggers.Add(State(UIElement.IsMouseOverProperty, EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey, EnvironmentColors.CommandBarTextHoverBrushKey));
            template.Triggers.Add(State(ListBoxItem.IsSelectedProperty, TreeViewColors.SelectedItemInactiveBrushKey, TreeViewColors.SelectedItemInactiveTextBrushKey));
            var activeSelection = new MultiTrigger();
            activeSelection.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, true));
            activeSelection.Conditions.Add(new Condition(Selector.IsSelectionActiveProperty, true));
            activeSelection.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveBrushKey), "Bd"));
            activeSelection.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemActiveTextBrushKey)));
            template.Triggers.Add(activeSelection);

            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        public static IEnumerable<T> OrEmpty<T>(this IEnumerable<T> items) => items ?? Enumerable.Empty<T>();
    }
}
