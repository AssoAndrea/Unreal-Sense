using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using UnrealSense.Cpp;
using UnrealSense.Extension.Services;
using UnrealSense.Extension.ToolWindows;
using UnrealSense.Workspace;

namespace UnrealSense.Extension.Commands
{
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.ShowUnrealExplorer)]
    internal sealed class ShowUnrealExplorerCommand : BaseCommand<ShowUnrealExplorerCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => UnrealExplorerWindow.ShowAsync();
    }

    /// <summary>Caret context shared by the editor commands.</summary>
    internal static class CaretSymbol
    {
        public static async Task<(SymbolAtPosition Symbol, string FilePath, UnrealWorkspace Workspace, ParsedFile File, int Offset)> GetAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            var filePath = docView?.FilePath;
            if (docView?.TextView == null || filePath == null) return default;

            var workspace = WorkspaceService.GetForFile(filePath);
            if (workspace == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: this file is not part of a loaded Unreal project (or indexing is still running).");
                return default;
            }

            int offset = docView.TextView.Caret.Position.BufferPosition.Position;
            var analysis = DocumentAnalysis.TryGet(docView.TextBuffer, filePath);
            var file = analysis?.GetCurrentFile() ?? HeaderParser.Parse(docView.TextBuffer.CurrentSnapshot.GetText(), filePath);
            var symbol = SymbolLocator.Find(file, offset, workspace.Symbols);
            return (symbol, filePath, workspace, file, offset);
        }
    }

    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.FindBlueprintUsages)]
    internal sealed class FindBlueprintUsagesCommand : BaseCommand<FindBlueprintUsagesCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var (symbol, filePath, _, _, _) = await CaretSymbol.GetAsync();
            if (filePath == null) return;
            if (symbol?.Type == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: place the caret on a UCLASS/USTRUCT/UENUM, UFUNCTION or UPROPERTY.");
                return;
            }
            await BlueprintUsagesWindow.ShowForSymbolAsync(symbol, filePath);
        }
    }

    /// <summary>
    /// Header ⇄ source navigation that understands Unreal naming: a BlueprintNativeEvent/RPC jumps to
    /// Name_Implementation (and _Validate), and Name_Implementation jumps back to the UFUNCTION declaration.
    /// </summary>
    [Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.GoToUnrealCounterpart)]
    internal sealed class GoToUnrealCounterpartCommand : BaseCommand<GoToUnrealCounterpartCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var (symbol, filePath, workspace, _, _) = await CaretSymbol.GetAsync();
            if (filePath == null) return;
            if (symbol?.Type == null)
            {
                await VS.StatusBar.ShowMessageAsync("UnrealSense: no reflected symbol at the caret.");
                return;
            }

            bool inHeader = string.Equals(symbol.DeclaringFile, filePath, System.StringComparison.OrdinalIgnoreCase);
            if (inHeader && symbol.Member is ReflectedFunction function)
            {
                var macro = function.Macro;
                bool hasImplementation = macro.Has("BlueprintNativeEvent") || macro.Has("Server") || macro.Has("Client") || macro.Has("NetMulticast");
                var names = hasImplementation
                    ? new[] { function.Name + "_Implementation", function.Name + "_Validate", function.Name }
                    : new[] { function.Name };
                var definition = names.SelectMany(n => workspace.Symbols.FindDefinitions(symbol.Type.Name, n)).FirstOrDefault();
                if (definition != null)
                {
                    await EditorNavigation.OpenAtOffsetAsync(definition.FilePath, definition.Offset);
                    return;
                }
                await VS.StatusBar.ShowMessageAsync(function.Macro.Has("BlueprintImplementableEvent")
                    ? $"UnrealSense: {function.Name} is a BlueprintImplementableEvent (implemented in Blueprints)."
                    : $"UnrealSense: no definition of {symbol.Type.Name}::{names[0]} found.");
                return;
            }

            if (inHeader && symbol.Member == null)
            {
                // On a type in its header: go to the matching .cpp.
                var cpp = workspace.CreateAnalysisContext().FindSourceFile(filePath);
                if (cpp != null) await VS.Documents.OpenAsync(cpp);
                return;
            }

            // In a source file (AFoo::Bar_Implementation) or on a type usage: go to the declaration.
            int target = symbol.Member?.NameStart ?? symbol.Type.NameStart;
            if (symbol.DeclaringFile != null)
                await EditorNavigation.OpenAtOffsetAsync(symbol.DeclaringFile, target);
        }
    }
}
