using System.ComponentModel;
using System.Runtime.InteropServices;
using Community.VisualStudio.Toolkit;

namespace UnrealSense.Extension.Options
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralPage : BaseOptionPage<General> { }
    }

    public enum ClangdGoToDefinitionMode { Auto, Always, Never }

    /// <summary>Tools › Options › UnrealSense › General.</summary>
    public class General : BaseOptionModel<General>
    {
        [Category("Project")]
        [DisplayName("Unreal project")]
        [Description("Optional path of the .uproject to load. Needed only when a solution at the engine root contains several games and the startup project is not the one you work on.")]
        [DefaultValue("")]
        public string UProjectPath { get; set; } = "";

        [Category("Code completion")]
        [DisplayName("Reflection specifier completion")]
        [Description("Complete UCLASS/UPROPERTY/UFUNCTION/USTRUCT/UENUM specifiers, meta keys and their values.")]
        [DefaultValue(true)]
        public bool EnableCompletion { get; set; } = true;

        [Category("Code completion")]
        [DisplayName("Module name completion in .Build.cs")]
        [Description("Complete module names inside *.Build.cs files.")]
        [DefaultValue(true)]
        public bool EnableBuildCsCompletion { get; set; } = true;

        [Category("Inspections")]
        [DisplayName("Enable Unreal inspections")]
        [Description("Report UHT errors and Unreal conventions in headers, with quick fixes.")]
        [DefaultValue(true)]
        public bool EnableInspections { get; set; } = true;

        [Category("Inspections")]
        [DisplayName("Suggest TObjectPtr for raw UPROPERTY pointers")]
        [DefaultValue(true)]
        public bool SuggestObjectPtr { get; set; } = true;

        [Category("Inspections")]
        [DisplayName("Show errors and warnings in the Error List")]
        [DefaultValue(true)]
        public bool ShowInErrorList { get; set; } = true;

        [Category("Find Usages (own index, experimental)")]
        [DisplayName("Use the own C++ index (experimental)")]
        [Description("Find Usages from UnrealSense's own C++ indexer (an external process, no clangd): built in seconds, updated incrementally when C++ files are saved. Shows the Find Usages command. Off by default.")]
        [DefaultValue(false)]
        public bool UseOwnIndex { get; set; } = false;

        [Category("Find Usages (own index, experimental)")]
        [DisplayName("Index the whole engine")]
        [Description("Also index every engine and plugin source file (about 100k files, 4 GB of memory while building), not only the engine headers the project includes.")]
        [DefaultValue(false)]
        public bool OwnIndexEngine { get; set; } = false;

        [Category("Find Usages (clangd)")]
        [DisplayName("Enable semantic C++ index (experimental)")]
        [Description("Run clangd with a background index of the project and engine, so Find Usages resolves the real symbol (pippo->Get() only finds the Get of pippo's class). Off by default: indexing a large engine takes long. When off, Find Usages shows whole-word text matches plus Blueprint usages.")]
        [DefaultValue(false)]
        // New name on purpose: the old EnableSemanticIndex value saved by earlier versions (on) must not carry over.
        public bool UseSemanticIndex { get; set; } = false;

        [Category("Find Usages (clangd)")]
        [DisplayName("clangd path")]
        [Description("Optional path to clangd.exe. Empty = %LOCALAPPDATA%\\UnrealSense\\clangd, then LLVM, then PATH.")]
        [DefaultValue("")]
        public string ClangdPath { get; set; } = "";

        [Category("Find Usages (clangd)")]
        [DisplayName("Index folder (shareable)")]
        [Description("Where the C++ index is stored. Empty = %LOCALAPPDATA%\\UnrealSense\\Cache. Use a path that is identical on every machine (e.g. S:\\UnrealSenseCache) to index the engine once and copy that folder to the rest of the team: files that did not change are not indexed again.")]
        [DefaultValue("")]
        public string IndexCacheFolder { get; set; } = "";

        [Category("Find Usages (clangd)")]
        [DisplayName("Index engine and plugin source files")]
        [Description("Also index the .cpp files of a source-built engine and of all plugins, so Find Usages covers engine and cross-project code. The project is indexed first; files are grouped by module into unity translation units, which makes the first full index many times faster.")]
        [DefaultValue(true)]
        public bool IndexEngineSources { get; set; } = true;

        [Category("Find Usages (clangd)")]
        [DisplayName("Group files across modules")]
        [Description("Index source files of different modules that share compiler options in the same translation unit (with the union of their include paths and definitions). Far fewer units, so a much faster first index of a source-built engine. Disable if a module's references look incomplete (clashing header or local names between modules).")]
        [DefaultValue(true)]
        public bool UnityAcrossModules { get; set; } = true;

        [Category("Find Usages (clangd)")]
        [DisplayName("Engine folders excluded from indexing")]
        [Description("Semicolon-separated path fragments to skip, e.g. Engine/Source/Developer/;Engine/Plugins/Online/. Empty = index everything in the build target.")]
        [DefaultValue("")]
        public string EngineIndexExclusions { get; set; } = "";

        [Category("Find Usages (clangd)")]
        [DisplayName("Indexing threads")]
        [Description("Threads used by the background index. 0 = automatic: up to 8 (more is slower on machines with real-time antivirus scanning), at most one per 4.5 GB of RAM beyond 8 GB.")]
        [DefaultValue(0)]
        public int SemanticIndexThreads { get; set; } = 0;

        [Category("Find Usages (clangd)")]
        [DisplayName("Index at low priority")]
        [Description("Run indexing below normal priority, so builds and the editor get the CPU first (indexing then takes longer while you work). Off by default: indexing uses all the power it can.")]
        [DefaultValue(false)]
        public bool SemanticIndexLowPriority { get; set; } = false;

        [Category("Find Usages (clangd)")]
        [DisplayName("Go to Definition with clangd")]
        [Description("Auto: F12 uses clangd when Visual Studio's C++ database is disabled (Extensions › UnrealSense › Use UnrealSense instead of Visual Studio indexing). Always / Never force it on or off.")]
        [DefaultValue(ClangdGoToDefinitionMode.Auto)]
        public ClangdGoToDefinitionMode ClangdGoToDefinition { get; set; } = ClangdGoToDefinitionMode.Auto;

        [Category("Find Usages (clangd)")]
        [DisplayName("Show UHT-generated references")]
        [Description("Include references from .gen.cpp/.generated.h files (Blueprint thunks).")]
        [DefaultValue(false)]
        public bool ShowGeneratedReferences { get; set; } = false;

        [Category("Go to Symbol / File")]
        [DisplayName("Include engine symbols and files")]
        [DefaultValue(true)]
        public bool GoToIncludeEngine { get; set; } = true;

        [Category("Blueprints")]
        [DisplayName("Show Blueprint usage hints")]
        [Description("Show how many Blueprints derive from / use a reflected class, function or property next to its declaration.")]
        [DefaultValue(true)]
        public bool ShowBlueprintHints { get; set; } = true;

        [Category("Blueprints")]
        [DisplayName("Show hints for unused Blueprint API")]
        [Description("Also show '0 Blueprint usages' on BlueprintCallable/BlueprintPure/events and Blueprint-visible properties.")]
        [DefaultValue(true)]
        public bool ShowZeroUsages { get; set; } = true;

        [Category("Blueprints")]
        [DisplayName("Open Blueprints in the running Unreal Editor")]
        [Description("Double-clicking a Blueprint Usages entry opens the asset in the Unreal Editor of the project when it is running (through the Python plugin's Remote Execution, which UnrealSense offers to enable once). Off: always show the file in Explorer.")]
        [DefaultValue(true)]
        public bool OpenAssetsInUnrealEditor { get; set; } = true;
    }
}
