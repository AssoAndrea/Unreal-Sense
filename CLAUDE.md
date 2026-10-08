# UnrealSense — instructions for Claude sessions

UnrealSense is a Visual Studio extension (VS 2022 17.14+ and VS 2026) that brings Rider-style Unreal Engine support
to Visual Studio. Read this file before changing anything; `README.md` describes the features for users,
`HANDOFF.md` (Italian) is the detailed history of the clangd indexing work.

## Working with the user

- Talk to the user in **Italian**. Code, comments, commit messages and this file are in English.
- The user tests on a **company PC** that you cannot access. They paste logs from
  `%LOCALAPPDATA%\UnrealSense\Logs\UnrealSense.log`. When a problem cannot be reproduced here, add log lines that
  will tell the cause on their machine, ship a version, and ask for exactly those lines.
- Git: public repository https://github.com/AssoAndrea/Unreal-Sense (branch `main`). Commit and push only when the
  user asks; `dist/`, `CLAUDE.local.md` and `.claude/settings.local.json` are ignored.
- **The repository is public.** Project names and paths of the company PC are in `CLAUDE.local.md` (not in git):
  never write them in tracked files or commit messages; call the projects GameA and GameB.
- Company PC facts: i9-14900KF (24C/32T), 64 GB RAM, Visual Assist installed (ReSharper cannot be installed), heavy
  file-system filtering (antivirus/EDR). The projects live on a **substituted drive** (`subst`: same files, two paths).
  - **GameA**: VS 2022 + `.sln`, UE 5.7.4. Its Blueprints reference the game module under another module name (see
    "Renamed modules" below); a framework of modules lives outside the project.
  - **GameB**: VS 2026 + `.slnx`, UE 5.8.3 modified source build, native layout.
- Hard rules: do not decompile or copy ReSharper/Rider code; do not change antivirus/Defender or other security
  settings; ask before downloading anything new; never send the user's email anywhere; solutions must not depend
  on antivirus exclusions.

## Current product direction

Since 0.3.0 the extension focuses on **Blueprint reflection support**: Blueprint usage counters (end-of-line hints),
the Blueprint Usages window, quick info, the Unreal Explorer, reflection specifier completion, UHT inspections,
declaration/`_Implementation` navigation, Build.cs completion, opening Blueprint usages in the running Unreal Editor,
the New Unreal Class dialog (`NewUnrealClassCommand`, log lines `NewUnrealClass:`).

The clangd semantic index (Find Usages, Go to Symbol/File, Go to Definition through clangd) is **off by default**
(option `UseSemanticIndex`) and its menu commands are **hidden** (buttons without a `<Parent>` in the `.vsct`, key
bindings removed). The code is kept dormant on purpose; do not delete it and do not re-enable it unless the user asks.
`experimental/` holds a paused prototype of a from-scratch C++ indexer (state in `experimental/PROGRESS.md`); leave
it alone unless asked.

## Layout

```
src/UnrealSense.Core      netstandard2.0, no Visual Studio dependency, unit-tested
  Assets/                 .uasset/.umap header reader (UAssetPackage) and AssetIndex (Blueprint usages, cached)
  Project/                .uproject/.uplugin/.Build.cs model (UnrealProject, ModuleScanner), engine locator,
                          config redirects (CoreRedirects)
  Cpp/                    tolerant lexer, reflection header parser (HeaderParser, ReflectionModel), macro context
  Reflection/             specifier catalog (built-in table + engine UHT sources + ObjectMacros.h docs)
  Analysis/               UHT inspections and fixes (HeaderAnalyzer)
  Workspace/              UnrealWorkspace (loads everything, watches files), SymbolIndex (reflected types of the
                          project), SymbolLocator (symbol under the caret)
  Navigation/             Go to Symbol/File index (dormant)
  Clang/                  clangd client, compile database, header maps, PCH resolver (dormant)
  Templates/              New Unreal Class: parent catalog (project types + cached engine header scan), generator
                          using the engine's Content/Editor/Templates/*.template (built-in copies as fallback), Build.cs edits
  Remote/                 Python Remote Execution client (talks to a running Unreal Editor) and its project setup
src/UnrealSense.Vsix      VSIX: net48, VSSDK + MEF + Community.VisualStudio.Toolkit
  UnrealSensePackage.cs   package registration, startup, `Version` constant
  VSCommandTable.vsct/.cs menus, buttons, key bindings / their ids
  Commands/               menu commands (BaseCommand<T>)
  Editor/                 MEF editor components: BlueprintHints, UnrealQuickInfo, SpecifierCompletion,
                          UnrealSuggestedActions, DiagnosticTagger, BuildCsCompletion (+ dormant clangd ones)
  Services/               WorkspaceService (which project is loaded), DocumentAnalysis (per-buffer parse), Log,
                          UnrealEditorBridge (open assets in the editor), ErrorListService (+ dormant ClangdService, FindUsagesService, IndexDiagnostics...)
  ToolWindows/            Unreal Explorer, Blueprint Usages (+ dormant Find Usages)
  Dialogs/                New Unreal Class dialog (code-built WPF); Services/NewClassService caches the engine scan
  Options/General.cs      Tools › Options › UnrealSense
tests/UnrealSense.Core.Tests   xUnit (net8.0); integration tests use C:\Unreal Project\Gym and skip if missing
tools/UnrealSense.Cli          debugging CLI (net8.0): run Core code against a real project
```

How the Blueprint data flows: `UnrealWorkspace.LoadAsync` builds the `SymbolIndex` (project headers) and the
`AssetIndex` (all `.uasset`/`.umap` under the project's and its plugins' Content) in parallel, then learns module
aliases and wires `AssetIndex.NativeSubclasses` (C++ inheritance). `UnrealWorkspace.FindUsages(type|member, file)`
is the entry point used by hints, quick info and windows; the module of a symbol comes from the file's owning
`.Build.cs` module (`UnrealProject.FindModuleForFile`).

Things that already exist and must keep working:
- **Renamed modules**: `AssetIndex.LearnModuleAliases` maps a `/Script/X` package that is not a project module to the
  project module whose classes it mostly contains (GameA ⇐ its old module name). Compare modules through
  `AssetIndex.ProjectModuleOf` / `Matches`, never with raw `NativeSymbolRef.Module` strings.
- **C++ inheritance**: a Blueprint deriving from C++ class B counts for B's C++ bases too ("via C++ B"), including
  engine/external bases (`SymbolIndex.GetDerivedTypes`, `GetDerivedTypesOfExternal`).
- **Config redirects** (`ActiveGameNameRedirects`, `ActiveClassRedirects`, `[CoreRedirects]`) are applied when assets
  are read.
- **Plugins** from `Plugins/` and from the `.uproject`'s `AdditionalPluginDirectories` are part of the project.
- **Open in Unreal Editor** (double-click in Blueprint Usages): `UnrealEditorBridge` uses the PythonScriptPlugin's
  Remote Execution protocol (`RemoteExecutionClient`: UDP multicast 239.0.0.1:6766 on 127.0.0.1 for ping/pong, then a
  TCP connection the editor opens to our ephemeral loopback port; spec in the engine's `PythonScriptRemoteExecution.cpp`
  and `remote_execution.py`). No plugin of ours. The project's editor is chosen by `project_root` in its pong
  (canonical path, subst-drive safe). If the project lacks `PythonScriptPlugin` in the `.uproject` or `bRemoteExecution=True`
  in `Config/DefaultEngine.ini`, the user is asked **once**: Yes → `RemoteExecutionSetup.Enable` writes them (read-only
  Perforce files are made writable and listed so the user checks them out), then restart the editor; No → option
  `OpenAssetsInUnrealEditor` off, Explorer only; the menu command `EnableOpenAssetsInEditor` (visible only then) asks
  again. Editor not running/answering → Show in Explorer. Log lines start with `UnrealEditorBridge:`. Verified on the
  company PC (GameA) in 0.3.10.
- **Several VS versions** write to the same log; lines carry the process id and startup logs
  `UnrealSense <version> loaded in Visual Studio <version>`. The workspace load logs a `Blueprint index:` summary
  (references per module, unreadable assets, aliases): use it first when counters look wrong.

## Build, test, run

```powershell
# Unit/integration tests (must all pass before a release)
dotnet test tests/UnrealSense.Core.Tests -c Release

# CLI against a real project, e.g. Blueprint index summary + usages of a type/function/property
dotnet build tools/UnrealSense.Cli -c Release
dotnet run -c Release --no-build --project tools/UnrealSense.Cli -- assets "C:\Unreal Project\LyraStarterGame" LyraGame.LyraCharacter

# Running Unreal Editor: remote-execution setup + editors that answer; with an object path it opens the asset. Run from
# PowerShell (Git Bash rewrites "/Game/..." into a Windows path). editor-enable writes the project setup.
dotnet run -c Release --no-build --project tools/UnrealSense.Cli -- editor-open "C:\Unreal Project\Gym" /Game/FirstPerson/Blueprints/BP_FirstPersonCharacter.BP_FirstPersonCharacter
dotnet run -c Release --no-build --project tools/UnrealSense.Cli -- editor-enable "C:\Unreal Project\Gym"

# Debug build installed cleanly into the VS experimental instance, then opens Gym
.\deploy-exp.ps1

# Release: rebuilds, checks the versions, writes dist\UnrealSense-<ver>.vsix, Install-UnrealSense.ps1 and dist\gallery
.\build-release.ps1
```

Test projects on this machine: `C:\Unreal Project\Gym` (small, has config redirects), `C:\Unreal Project\LyraStarterGame`
(UE 5.8 launcher engine at `C:\Program Files\Epic Games\UE_5.8`, ~8,700 assets). Prefer reproducing a reported bug
with the CLI or a unit test on these before changing code.

## Adding a feature

1. Put the logic in `UnrealSense.Core` (no VS types) and cover it with a test in `tests/UnrealSense.Core.Tests`
   (synthetic files in a temp folder, or Gym/Lyra when real assets are needed). Keep the VSIX layer thin.
2. Editor behaviour: a MEF component in `src/UnrealSense.Vsix/Editor` (`[Export]`, `[ContentType("C/C++")]`,
   `[TextViewRole(PredefinedTextViewRoles.Document)]`). Get the project with `WorkspaceService.GetForFile(path)` and the
   parsed buffer with `DocumentAnalysis.TryGet(buffer, path)`; do heavy work off the UI thread.
3. A command: add the `<Button>` (with a `<Parent>` group to make it visible) and an `<IDSymbol>` in
   `VSCommandTable.vsct`, the same id in `VSCommandTable.cs` (`PackageIds`), and a `BaseCommand<T>` class in
   `Commands/` with `[Command(PackageGuids.UnrealSenseCmdSetString, PackageIds.X)]`. Use the next free id
   (currently 0x0110). Avoid Alt+Shift+S/O/F: Visual Assist uses them on the company PC.
4. An option: a property in `Options/General.cs` with `[Category]`, `[DisplayName]`, `[Description]`,
   `[DefaultValue]`. Renaming a property is the way to reset a value users already saved.
5. Log what the user will need to diagnose it on their machine (`Log.Write`), without flooding the log.
6. Update `README.md` when user-visible behaviour changes.

## Releasing a version

- Bump the version in **both** `src/UnrealSense.Vsix/source.extension.vsixmanifest` (`Identity Version`) and
  `UnrealSensePackage.Version`; `build-release.ps1` refuses to publish when they differ, and checks the version
  inside the built `.vsix` (the VSSDK sometimes ships a stale manifest from `obj`).
- Run the tests, then `build-release.ps1`, and give the user `dist\UnrealSense-<ver>.vsix`. They install with
  `Install-UnrealSense.ps1` (picks the highest version next to it, installs into the newest VS) or the VSIX file.
- The clangd index (when enabled) is discarded once per new extension version, by the user's request, so indexing
  times are comparable between versions (`CompileDatabase.DiscardOutdatedIndex`). Keep this until they say otherwise.
- After a release, tell the user (in Italian) what changed, what was verified and how, and what to send back.

## Code conventions

- Match the surrounding code: C# `latest`, block-scoped `namespace UnrealSense.X { }` as in the existing files, `var`,
  expression-bodied members where the file uses them, XML `<summary>` on public members. Comments explain *why*
  (measurements, Unreal behaviour), not what.
- `UnrealSense.Core` must stay netstandard2.0 and free of Visual Studio references.
- Paths: compare with `StringComparison.OrdinalIgnoreCase`; remember subst drives: the same file has two paths (see
  `ClangdClient.CanonicalPath`/`ToViewPath`, `WorkspaceService.SameFile`) — the same project must never load twice.
- Never block the UI thread; the asset index and symbol index rebuild in the background and raise `Changed`.

## Tooling pitfalls on this machine

- Bash heredocs mangle backslashes (`'\\'`, `\U`, `\[`): write C#/Python with the Write/Edit tools or a script file.
- Windows PowerShell 5.1 `Get-Content`/`Set-Content` re-encode files (mojibake); edit files with the Edit/Write tools.
- A plain `VSIXInstaller` upgrade of the experimental instance can leave a stale MEF cache ("package did not load
  correctly"): use `deploy-exp.ps1`, which uninstalls, clears the caches and runs `/updateconfiguration`.
