using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Cpp;
using UnrealSense.Reflection;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("UnrealSense reflection specifier completion")]
    [ContentType("C/C++")]
    internal sealed class SpecifierCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new SpecifierCompletionSource(textView, DocumentFactory));
    }

    /// <summary>
    /// Completion inside UCLASS/UPROPERTY/UFUNCTION/... parentheses: specifiers, meta=(...) keys and values such as
    /// Category, ReplicatedUsing, BlueprintGetter, EditCondition, Units, Config.
    /// </summary>
    internal sealed class SpecifierCompletionSource : IAsyncCompletionSource
    {
        const int LookBehind = 4000;

        static readonly string[] Units =
        {
            "cm", "mm", "m", "km", "Inches", "Feet", "Miles", "cm/s", "m/s", "km/h", "cm/s2", "m/s2", "Degrees", "Radians",
            "deg/s", "rad/s", "s", "ms", "Minutes", "Hours", "Days", "kg", "g", "Grams", "N", "kgcm/s2", "kgcm2/s2", "Percent",
            "Multiplier", "Celsius", "Farenheit", "Kelvin", "Lumens", "Candela", "Lux", "EV", "Hz", "kHz", "MHz", "GHz", "RPM",
            "Bytes", "KB", "MB", "GB", "TB", "Pixels", "Times",
        };

        static readonly string[] ConfigNames = { "Game", "Engine", "Input", "Editor", "EditorPerProjectUserSettings", "GameUserSettings", "Scalability", "DeviceProfiles" };

        static readonly string[] CommonCategories =
        {
            "Actor", "Activation", "AssetUserData", "Collision", "Cooking", "DataLayers", "Events", "HLOD", "Input", "LOD", "Lighting",
            "Navigation", "Networking", "Physics", "Rendering", "Replication", "Tags", "Transform", "Variable", "WorldPartition",
        };

        static readonly HashSet<string> FunctionValuedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ReplicatedUsing", "BlueprintGetter", "BlueprintSetter", "Getter", "Setter", "GetOptions",
        };

        static readonly HashSet<string> TypeValuedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AllowedClasses", "DisallowedClasses", "MetaClass", "MustImplement", "Within", "BitmaskEnum", "RestrictedToClasses", "ProhibitedInterfaces",
        };

        readonly ITextView view;
        readonly ITextDocumentFactoryService documentFactory;

        public SpecifierCompletionSource(ITextView view, ITextDocumentFactoryService documentFactory)
        {
            this.view = view;
            this.documentFactory = documentFactory;
        }

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (!General.Instance.EnableCompletion || !IsUnrealBuffer(triggerLocation.Snapshot.TextBuffer))
                return CompletionStartData.DoesNotParticipateInCompletion;

            if (trigger.Reason == CompletionTriggerReason.Insertion && !IsTriggerChar(trigger.Character, triggerLocation))
                return CompletionStartData.DoesNotParticipateInCompletion;

            var context = FindContext(triggerLocation, out int windowStart);
            if (context == null)
                return CompletionStartData.DoesNotParticipateInCompletion;
            if ((trigger.Reason == CompletionTriggerReason.Backspace || trigger.Reason == CompletionTriggerReason.Deletion) && context.PrefixLength == 0)
                return CompletionStartData.DoesNotParticipateInCompletion;

            var span = new SnapshotSpan(triggerLocation.Snapshot, windowStart + context.PrefixStart, context.PrefixLength);
            return new CompletionStartData(CompletionParticipation.ProvidesItems, span);
        }

        static bool IsTriggerChar(char c, SnapshotPoint location)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '(' || c == ',' || c == '=' || c == '"') return true;
            if (c == ' ' && location.Position >= 2)
            {
                // "EditAnywhere, |" or "Category = |"
                var prev = location.Snapshot.GetText(Math.Max(0, location.Position - 3), Math.Min(3, location.Position)).TrimEnd();
                return prev.EndsWith(",") || prev.EndsWith("(") || prev.EndsWith("=");
            }
            return false;
        }

        MacroContext FindContext(SnapshotPoint point, out int windowStart)
        {
            windowStart = Math.Max(0, point.Position - LookBehind);
            var text = point.Snapshot.GetText(windowStart, point.Position - windowStart);
            return MacroContext.Find(text, text.Length);
        }

        public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            var context = FindContext(triggerLocation, out int windowStart);
            if (context == null) return Task.FromResult(CompletionContext.Empty);

            var filePath = GetFilePath(triggerLocation.Snapshot.TextBuffer);
            var workspace = WorkspaceService.GetForFile(filePath);
            var catalog = workspace?.Catalog ?? WorkspaceService.Current?.Catalog ?? SpecifierCatalog.CreateBuiltin();

            var items = context.IsValue
                ? ValueItems(context, workspace, triggerLocation, windowStart + context.MacroStart)
                : KeyItems(context, catalog);
            return Task.FromResult(new CompletionContext(items.ToImmutableArray()));
        }

        IEnumerable<CompletionItem> KeyItems(MacroContext context, SpecifierCatalog catalog)
        {
            var infos = context.InMeta ? catalog.GetMeta(context.Target) : catalog.GetSpecifiers(context.Target);
            foreach (var info in infos.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (context.UsedKeys.Contains(info.Name)) continue;
                string insert = info.Name;
                if (info.TakesValue)
                    insert = string.Equals(info.Name, "meta", StringComparison.OrdinalIgnoreCase) ? "meta = (" : info.Name + " = ";
                var item = new CompletionItem(info.Name, this, context.InMeta ? Images.Meta : Images.Specifier,
                    ImmutableArray<CompletionFilter>.Empty, info.TakesValue ? " =" : string.Empty, insert, info.Name, info.Name, ImmutableArray<ImageElement>.Empty);
                item.Properties["doc"] = info;
                yield return item;
            }
        }

        IEnumerable<CompletionItem> ValueItems(MacroContext context, UnrealWorkspace workspace, SnapshotPoint location, int macroStart)
        {
            var key = context.Key ?? string.Empty;
            string Quote(string v) => context.InString ? v : "\"" + v + "\"";

            if (key.Equals("Category", StringComparison.OrdinalIgnoreCase))
            {
                var categories = workspace?.Symbols?.GetCategories() ?? Enumerable.Empty<string>();
                foreach (var c in categories.Concat(CommonCategories).Distinct(StringComparer.Ordinal))
                    yield return Item(c, Quote(c), Images.Category);
                yield break;
            }

            if (key.Equals("Units", StringComparison.OrdinalIgnoreCase) || key.Equals("ForceUnits", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var u in Units) yield return Item(u, Quote(u), Images.Value);
                yield break;
            }

            if (key.Equals("Config", StringComparison.OrdinalIgnoreCase) && context.Target == SpecifierTarget.Class)
            {
                foreach (var c in ConfigNames) yield return Item(c, c, Images.Value);
                yield break;
            }

            if (key.Equals("HideCategories", StringComparison.OrdinalIgnoreCase) || key.Equals("ShowCategories", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in CommonCategories) yield return Item(c, c, Images.Category);
                yield break;
            }

            if (key.Equals("ClassGroup", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in new[] { "(Custom)", "Common", "Lights", "Physics", "Rendering", "Audio", "Camera", "AI", "Collision", "Utility" })
                    yield return Item(c, c, Images.Value);
                yield break;
            }

            var file = HeaderParser.Parse(location.Snapshot.GetText(), GetFilePath(location.Snapshot.TextBuffer));
            var owner = file.Types.FirstOrDefault(t => t.BodyOpen >= 0 && location.Position > t.BodyOpen && (t.BodyClose < 0 || location.Position < t.BodyClose));

            if (FunctionValuedKeys.Contains(key))
            {
                var functions = owner == null ? Enumerable.Empty<ReflectedFunction>()
                    : (workspace?.Symbols?.GetSelfAndBases(owner) ?? new[] { owner }).SelectMany(t => t.Functions);
                var names = functions.Select(f => f.Name).Distinct().ToList();

                if (key.Equals("ReplicatedUsing", StringComparison.OrdinalIgnoreCase))
                {
                    // Suggest OnRep_<Property> for the property this macro decorates.
                    var property = owner?.Properties.FirstOrDefault(p => p.Macro.Start == macroStart);
                    if (property != null && !names.Contains("OnRep_" + property.Name))
                        yield return Item("OnRep_" + property.Name, "OnRep_" + property.Name, Images.Function, "new");
                    names = names.OrderBy(n => n.StartsWith("OnRep_") ? 0 : 1).ThenBy(n => n).ToList();
                }
                foreach (var n in names) yield return Item(n, n, Images.Function);
                yield break;
            }

            if (key.Equals("EditCondition", StringComparison.OrdinalIgnoreCase) || key.Equals("EditConditionHides", StringComparison.OrdinalIgnoreCase))
            {
                var props = owner == null ? Enumerable.Empty<ReflectedProperty>()
                    : (workspace?.Symbols?.GetSelfAndBases(owner) ?? new[] { owner }).SelectMany(t => t.Properties);
                foreach (var p in props.Where(p => p.Type == "bool" || p.Type.StartsWith("uint8")).Select(p => p.Name).Distinct())
                    yield return Item(p, Quote(p), Images.Property);
                yield break;
            }

            if (TypeValuedKeys.Contains(key) && workspace?.Symbols != null)
            {
                bool wantEnums = key.Equals("BitmaskEnum", StringComparison.OrdinalIgnoreCase);
                foreach (var type in workspace.Symbols.Types.Where(t => (t.Kind == ReflectedKind.Enum) == wantEnums && !t.IsNativeInterfaceClass))
                {
                    var module = workspace.GetScriptModule(workspace.Symbols.FindTypeFile(type.Name));
                    var path = $"/Script/{module}.{type.ReflectedName}";
                    yield return Item(type.ReflectedName, Quote(path), Images.Class, path);
                }
            }
        }

        CompletionItem Item(string display, string insert, ImageElement icon, string suffix = "") =>
            new CompletionItem(display, this, icon, ImmutableArray<CompletionFilter>.Empty, suffix, insert, display, display, ImmutableArray<ImageElement>.Empty);

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
        {
            if (item.Properties.TryGetProperty("doc", out SpecifierInfo info))
            {
                var title = new ClassifiedTextElement(
                    new ClassifiedTextRun("keyword", info.IsMeta ? "meta " : "specifier "),
                    new ClassifiedTextRun("identifier", info.Name),
                    new ClassifiedTextRun("text", info.TakesValue ? " = …" : ""));
                var body = new ClassifiedTextElement(new ClassifiedTextRun("text", string.IsNullOrEmpty(info.Documentation) ? "(no documentation)" : info.Documentation));
                return Task.FromResult<object>(new ContainerElement(ContainerElementStyle.Stacked, title, body));
            }
            return Task.FromResult<object>(null);
        }

        bool IsUnrealBuffer(ITextBuffer buffer) => WorkspaceService.IsUnrealFile(GetFilePath(buffer));

        string GetFilePath(ITextBuffer buffer) =>
            documentFactory.TryGetTextDocument(buffer, out var document) ? document.FilePath : null;
    }
}
