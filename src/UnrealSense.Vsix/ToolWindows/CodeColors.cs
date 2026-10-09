using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using UnrealSense.Cpp;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.ToolWindows
{
    /// <summary>
    /// Brushes for <see cref="HighlightKind"/> taken from the user's editor colours (Fonts and Colors), so code
    /// previews in tool windows look like the editor in any theme. The C++ classifications exist only once the
    /// VC++ package has loaded; each kind falls back to the generic language classification, then to plain text.
    /// </summary>
    internal sealed class CodeColors
    {
        static readonly Dictionary<HighlightKind, string[]> Classifications = new Dictionary<HighlightKind, string[]>
        {
            [HighlightKind.Keyword] = new[] { "keyword" },
            [HighlightKind.Comment] = new[] { "comment" },
            [HighlightKind.String] = new[] { "string" },
            [HighlightKind.Number] = new[] { "number" },
            [HighlightKind.Preprocessor] = new[] { "preprocessor keyword" },
            [HighlightKind.Macro] = new[] { "cppMacro", "preprocessor keyword" },
            [HighlightKind.Type] = new[] { "cppType", "class name", "type" },
            [HighlightKind.Namespace] = new[] { "cppNamespace", "namespace name" },
            [HighlightKind.Function] = new[] { "cppFunction", "cppMemberFunction", "method name" },
            [HighlightKind.Member] = new[] { "cppMemberField", "field name" },
        };

        static bool logged;
        readonly Dictionary<HighlightKind, Brush> brushes = new Dictionary<HighlightKind, Brush>();

        /// <summary>Background of the editor's "Highlighted Reference" marker, for the searched name; null if unavailable.</summary>
        public Brush MatchBackground { get; private set; }

        /// <summary>Foreground for <paramref name="kind"/>, or null to keep the tool window's text colour.</summary>
        public Brush Get(HighlightKind kind) => brushes.TryGetValue(kind, out var brush) ? brush : null;

        /// <summary>Reads the current colours; call on the UI thread (again after a theme change).</summary>
        public static CodeColors Load()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var colors = new CodeColors();
            try
            {
                var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var registry = model?.GetService<IClassificationTypeRegistryService>();
                var formatMap = model?.GetService<IClassificationFormatMapService>()?.GetClassificationFormatMap("text");
                if (registry == null || formatMap == null) return colors;

                var missing = new List<string>();
                foreach (var pair in Classifications)
                {
                    Brush found = null;
                    foreach (var name in pair.Value)
                    {
                        var type = registry.GetClassificationType(name);
                        if (type == null) continue;
                        var properties = formatMap.GetExplicitTextProperties(type);
                        if (properties.ForegroundBrushEmpty) continue;
                        found = properties.ForegroundBrush;
                        break;
                    }
                    if (found != null) colors.brushes[pair.Key] = found;
                    else missing.Add(pair.Key.ToString());
                }

                var editorFormat = model.GetService<IEditorFormatMapService>()?.GetEditorFormatMap("text");
                var marker = editorFormat?.GetProperties("MarkerFormatDefinition/HighlightedReference");
                if (marker != null && marker.Contains(EditorFormatDefinition.BackgroundBrushId))
                    colors.MatchBackground = marker[EditorFormatDefinition.BackgroundBrushId] as Brush;

                if (!logged)
                {
                    logged = true;
                    Log.Write($"FindUsages: code colours loaded ({colors.brushes.Count} kinds"
                              + (missing.Count > 0 ? $", plain: {string.Join(", ", missing)}" : "")
                              + $", match background {(colors.MatchBackground != null ? "yes" : "no")})");
                }
            }
            catch (Exception ex)
            {
                Log.Error("FindUsages: code colours", ex);
            }
            return colors;
        }
    }
}
