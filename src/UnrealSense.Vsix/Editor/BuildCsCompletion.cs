using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UnrealSense.Project;
using UnrealSense.Extension.Options;
using UnrealSense.Extension.Services;

namespace UnrealSense.Extension.Editor
{
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("UnrealSense Build.cs module completion")]
    [ContentType("CSharp")]
    internal sealed class BuildCsCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new BuildCsCompletionSource(DocumentFactory));
    }

    /// <summary>Module names inside string literals of *.Build.cs / *.Target.cs files.</summary>
    internal sealed class BuildCsCompletionSource : IAsyncCompletionSource
    {
        readonly ITextDocumentFactoryService documentFactory;

        public BuildCsCompletionSource(ITextDocumentFactoryService documentFactory) => this.documentFactory = documentFactory;

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (!General.Instance.EnableBuildCsCompletion) return CompletionStartData.DoesNotParticipateInCompletion;
            if (!documentFactory.TryGetTextDocument(triggerLocation.Snapshot.TextBuffer, out var document)) return CompletionStartData.DoesNotParticipateInCompletion;
            var name = Path.GetFileName(document.FilePath);
            if (!name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
                return CompletionStartData.DoesNotParticipateInCompletion;

            if (trigger.Reason == CompletionTriggerReason.Insertion && !(char.IsLetterOrDigit(trigger.Character) || trigger.Character == '"' || trigger.Character == '_'))
                return CompletionStartData.DoesNotParticipateInCompletion;

            var span = FindStringPrefix(triggerLocation);
            return span.HasValue
                ? new CompletionStartData(CompletionParticipation.ProvidesItems, span.Value)
                : CompletionStartData.DoesNotParticipateInCompletion;
        }

        /// <summary>Span between the opening quote of the string literal containing the caret and the caret.</summary>
        static SnapshotSpan? FindStringPrefix(SnapshotPoint point)
        {
            var line = point.GetContainingLine();
            var before = line.Snapshot.GetText(line.Start, point.Position - line.Start);
            int quotes = before.Count(c => c == '"');
            if (quotes % 2 == 0) return null;
            int open = before.LastIndexOf('"');
            if (open > 0 && before[open - 1] == '@') return null;
            return new SnapshotSpan(point.Snapshot, line.Start + open + 1, before.Length - open - 1);
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            documentFactory.TryGetTextDocument(triggerLocation.Snapshot.TextBuffer, out var document);
            var uproject = UnrealProject.FindUProject(document?.FilePath);
            if (uproject == null) return CompletionContext.Empty;
            var workspace = WorkspaceService.EnsureLoaded(uproject);

            // Engine module names are scanned lazily (first call walks Engine/Source and Engine/Plugins).
            var names = await Task.Run(() => workspace.AllModuleNames.ToList(), token);
            var projectModules = workspace.Project?.AllModules.Select(m => m.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) ?? ImmutableHashSet<string>.Empty;
            var items = names.Select(n => new CompletionItem(n, this, Images.Module, ImmutableArray<CompletionFilter>.Empty,
                projectModules.Contains(n) ? "project" : string.Empty, n, (projectModules.Contains(n) ? "0" : "1") + n, n, ImmutableArray<ImageElement>.Empty));
            return new CompletionContext(items.ToImmutableArray());
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token) =>
            Task.FromResult<object>("Unreal module " + item.DisplayText);
    }
}
