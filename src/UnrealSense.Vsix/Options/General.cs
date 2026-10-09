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
        [Description("Find Usages from UnrealSense's own C++ indexer (an external process): built in seconds, updated incrementally when C++ files are saved. Shows the Find Usages command. Off by default.")]
        [DefaultValue(false)]
        public bool UseOwnIndex { get; set; } = false;

        [Category("Find Usages (own index, experimental)")]
        [DisplayName("Index the whole engine")]
        [Description("Also index every engine and plugin source file (about 100k files, 4 GB of memory while building), not only the engine headers the project includes.")]
        [DefaultValue(false)]
        public bool OwnIndexEngine { get; set; } = false;

        [Category("Find Usages (own index, experimental)")]
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
