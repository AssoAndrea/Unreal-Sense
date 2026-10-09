# UnrealSense

Unreal Engine productivity for Visual Studio 2022 (17.14+) and Visual Studio 2026: Rider-style Blueprint and
reflection support on top of Visual Studio's own C++ IntelliSense.

## Features (v0.3)

| Area | What it does |
|---|---|
| **Blueprint usages** | An offline index of `.uasset`/`.umap` headers (works with the editor closed). For each reflected class, function and property it finds the Blueprints deriving from it, function calls, overridden/implemented events, type references, and property usage matched by name. Shown as end-of-line hints ("12 derived Blueprints", "3 Blueprint usages"), in quick info, and in the **Blueprint Usages** window (`Alt+Shift+B`). |
| **Open in Unreal Editor** | Double-click an entry of the **Blueprint Usages** window (or its **Open in Unreal Editor** button) to open the asset in the Unreal Editor that has the project loaded. It uses the Python plugin's Remote Execution (local machine only): the first time, UnrealSense offers to enable it, adding `PythonScriptPlugin` to the `.uproject` and `bRemoteExecution=True` under `[/Script/PythonScriptPlugin.PythonScriptPluginSettings]` in `Config/DefaultEngine.ini` (restart the editor afterwards). Files that are read-only because they are not checked out in Perforce are made writable, and UnrealSense lists them so you can check them out. If you decline, or no editor is running or answering, the file is shown in Explorer; **Open Blueprints in Unreal Editor...** in Extensions › UnrealSense asks again. |
| **What the counters understand** | Blueprints deriving through other Blueprints ("via BP_Base") and through C++ subclasses ("via C++ AMyCharacter"), also when the base class is in the engine or an external plugin. Config redirects (`ActiveGameNameRedirects`, `ActiveClassRedirects`, `[CoreRedirects]`). Renamed modules whose redirect is not in a config file (registered from C++ or in a plugin's config) are detected from the classes the assets look for, and logged. Plugins in the project's `Plugins/` folder and in the `.uproject`'s `AdditionalPluginDirectories`. |
| **Reflection completion** | Inside `UCLASS/USTRUCT/UENUM/UINTERFACE/UPROPERTY/UFUNCTION/UPARAM/UMETA(...)`: specifiers, `meta=(...)` keys, and values (`Category` from the project, `ReplicatedUsing`/`BlueprintGetter`/`BlueprintSetter` from the class's UFUNCTIONs with an `OnRep_<Property>` suggestion, `EditCondition`, `Units`, `Config`, `AllowedClasses`/`MetaClass`/`BitmaskEnum`...). The specifier list comes from the installed engine's UnrealHeaderTool sources, and the documentation comes from `ObjectMacros.h`, so both always match your engine version. |
| **Quick info** | Hover a specifier to see its documentation. Hover a reflected class, function or property name to see where it's used in Blueprints. |
| **UHT inspections and quick fixes** | Missing, misplaced or misnamed `.generated.h` include. Missing `GENERATED_BODY()`. Invalid A/U/F/E/I prefixes. Private `BlueprintReadWrite`/`BlueprintReadOnly` without `AllowPrivateAccess`. Conflicting specifiers. `BlueprintPure` with no output. UFUNCTION inside a USTRUCT. `ReplicatedUsing` pointing at a missing UFUNCTION. Missing `GetLifetimeReplicatedProps`. Missing `_Implementation`/`_Validate` (the fix generates the stub in the .cpp). Unknown specifiers, or specifiers missing a value. Suggests `TObjectPtr` instead of raw pointers. `BlueprintAssignable` on a non-dynamic delegate. Results appear as squiggles, light bulbs and Error List entries. |
| **Navigation** | `Alt+Shift+I` jumps between a UFUNCTION declaration and its definition, `_Implementation` or `_Validate`. The **Unreal Explorer** window lists modules (`.Build.cs`, dependencies, reflected types), plugins, Blueprints grouped by C++ parent, and config files, with a search box. |
| **New Unreal Class** | **Unreal Class...** (Solution Explorer › Add on a project or folder, Extensions › UnrealSense, or the Unreal Explorer's **New class** button) opens a Rider-style dialog: class name, parent from **Common** (Actor, Character, Actor Component, Interface, Struct, Enum, Empty, subsystems, User Widget...) or **All Classes** (project, project plugins, engine and the engine plugins the project enables, read in the background and cached), base folder Root/Public/Private and path. It writes the `.h` (in `Public`) and `.cpp` (in `Private`) like the Unreal Editor: prefixed name (`A`/`U`/`F`/`E`, `UMyX`+`IMyX` for interfaces), `<MODULE>_API`, `CoreMinimal.h`, the parent's header and the `.generated.h`, with the engine's own templates (constructor, `BeginPlay`, `Tick`, `SetupPlayerInputComponent`, `TickComponent`, subsystem `Initialize`/`Deinitialize`...). Structs and enums are header only. If the module's `.Build.cs` does not depend on the parent's module (e.g. `UMG`, `DeveloperSettings`), it offers to add it. The files are added to the game's `.vcxproj` under the matching filter and opened; IntelliSense resolves their includes after the next **Generate Project Files** (UBT's Makefile projects cannot give a new file its module's include paths without reloading the project). |
| **Build.cs** | Completes module names inside string literals of `*.Build.cs`/`*.Target.cs` (project, engine and plugin modules). |

Commands (Extensions › UnrealSense, and the editor's context menu): **Find Blueprint Usages**, **Go to Declaration /
_Implementation**, **Unreal Explorer**, **Unreal Class...**, **Rebuild UnrealSense Index** (re-reads headers and assets). **Restore Visual
Studio Indexing** appears only if an earlier version turned Visual Studio's own C++ database off.

Options: Tools › Options › UnrealSense. Log: Output › UnrealSense and `%LOCALAPPDATA%\UnrealSense\Logs\UnrealSense.log`.
When the project loads, the log shows a `Blueprint index:` summary (assets read, references per C++ module, detected
module aliases): check it first when a counter looks wrong.

### Experimental, off by default

A semantic C++ index built with clangd (exact Find Usages, Go to Symbol/File, Go to Definition) is in the code but
disabled (`Enable semantic C++ index (experimental)` in the options) and its commands are hidden: on large source-built
engines the first index takes too long.

## Installing and updating

`build-release.ps1` writes `dist\UnrealSense-<version>.vsix` and `dist\Install-UnrealSense.ps1`. Run the script (it picks
the highest version next to it and installs it into the newest Visual Studio), or double-click the `.vsix`. For automatic
updates, publish `dist\gallery` (`atom.xml` + `UnrealSense.vsix`) to a shared folder and add it under Tools › Options ›
Environment › Extensions › Additional Extension Galleries.

## Layout

```
src/UnrealSense.Core     netstandard2.0, no VS dependency, unit-tested
  Assets/                .uasset reader (FPackageFileSummary, name/import/export maps) + AssetIndex (cached)
  Project/               .uproject/.uplugin/.Build.cs model, engine locator, config redirects
  Cpp/                   tolerant C++ lexer, reflection header parser, completion context
  Reflection/            specifier catalog (built-in + UHT sources + ObjectMacros.h docs)
  Analysis/              inspections and code fixes
  Workspace/             symbol index, symbol locator, UnrealWorkspace (loading, file watching)
  Templates/             New Unreal Class: parent class catalog, engine header scan, class generator, Build.cs edits
  Navigation/, Clang/    Go to Symbol/File index and clangd integration (experimental, off)
src/UnrealSense.Vsix     VSIX (net48, VSSDK + MEF + Community.VisualStudio.Toolkit)
tests/                   xUnit tests (unit tests + integration tests against a real UE project)
tools/UnrealSense.Cli    debugging CLI: dump/parse/assets/analyze...
experimental/            paused prototype of a C++ usage indexer written from scratch
```

## Build and debug

No "Visual Studio extension development" workload is needed: the VSSDK comes from NuGet.

```bash
dotnet test tests/UnrealSense.Core.Tests -c Release
```

```powershell
.\deploy-exp.ps1      # Debug build, clean install into the experimental instance, opens C:\Unreal Project\Gym
.\build-release.ps1   # Release build + dist\ (checks that the version in the .vsix matches the source manifest)
```

The version lives in `src/UnrealSense.Vsix/source.extension.vsixmanifest` and in `UnrealSensePackage.Version`; change
both. To debug from Visual Studio, open `UnrealSense.sln`, set `UnrealSense.Vsix` as the startup project and press F5.

Integration tests use `C:\Unreal Project\Gym`; set `UNREALSENSE_TEST_PROJECT` to point at another project.

CLI examples:

```bash
dotnet run --project tools/UnrealSense.Cli -- analyze "C:\Unreal Project\Gym"
```

```bash
dotnet run --project tools/UnrealSense.Cli -- assets "C:\Unreal Project\Gym" Gym.ShooterCharacter Gym.ShooterCharacter:DoStartFiring
```

## Roadmap

1. **UnrealSenseLink editor plugin** (the "live" half of the hybrid Blueprint approach, like RiderLink): open an asset in the editor from VS, live Blueprint and Asset Registry data, the UE log in a VS tool window, Play/Stop, Live Coding.
2. Completion in `.ini` files (sections `[/Script/Module.Class]` and `Config` properties) and in `.uproject`/`.uplugin`.
3. Name-matched property usages for Blueprints that reference a renamed module (their name maps are not kept today), and indexing of engine headers (auto `#include` for UE types, base-class checks).
4. More inspections and refactorings: `Super::` calls in overrides, rename a UPROPERTY/UFUNCTION with a CoreRedirect, delegate binding signature checks, `UE_LOG` category completion.
5. Unreal unit test adapter (Automation tests) in Test Explorer.
