using System;
using System.Collections.Generic;
using System.Linq;
using UnrealSense.Cpp;
using UnrealSense.Project;
using UnrealSense.Workspace;

namespace UnrealSense.Templates
{
    /// <summary>
    /// Every class a new project class can derive from (project, project plugins, engine and the engine plugins the
    /// project enables), with the hierarchy needed to pick the prefix and the template.
    /// </summary>
    public sealed class ParentClassCatalog
    {
        readonly Dictionary<string, ParentClassInfo> byName = new Dictionary<string, ParentClassInfo>(StringComparer.Ordinal);

        /// <summary>The "Common" list, as in Unreal's and Rider's New Class dialogs, plus Struct and Enum.</summary>
        public static IReadOnlyList<ParentClassInfo> CommonParents { get; } = new[]
        {
            Common("UObject", "UObject", "UObject/Object.h", "CoreUObject", null),
            Common("Actor", "AActor", "GameFramework/Actor.h", "Engine", "UObject"),
            Common("Actor Component", "UActorComponent", "Components/ActorComponent.h", "Engine", "UObject"),
            Common("Scene Component", "USceneComponent", "Components/SceneComponent.h", "Engine", "UActorComponent"),
            Common("Character", "ACharacter", "GameFramework/Character.h", "Engine", "APawn"),
            Common("Pawn", "APawn", "GameFramework/Pawn.h", "Engine", "AActor"),
            new ParentClassInfo { DisplayName = "Empty", Kind = NewClassKind.Empty },
            new ParentClassInfo { DisplayName = "Interface", Name = "UInterface", Kind = NewClassKind.Interface, IncludePath = "UObject/Interface.h", ModuleName = "CoreUObject", IsEngine = true, BaseName = "UObject" },
            new ParentClassInfo { DisplayName = "Struct", Kind = NewClassKind.Struct },
            new ParentClassInfo { DisplayName = "Enum", Kind = NewClassKind.Enum },
            Common("Player Controller", "APlayerController", "GameFramework/PlayerController.h", "Engine", "AController"),
            Common("Player Camera Manager", "APlayerCameraManager", "Camera/PlayerCameraManager.h", "Engine", "AActor"),
            Common("Game Mode Base", "AGameModeBase", "GameFramework/GameModeBase.h", "Engine", "AInfo"),
            Common("Game State Base", "AGameStateBase", "GameFramework/GameStateBase.h", "Engine", "AInfo"),
            Common("Player State", "APlayerState", "GameFramework/PlayerState.h", "Engine", "AInfo"),
            Common("HUD", "AHUD", "GameFramework/HUD.h", "Engine", "AActor"),
            Common("World Settings", "AWorldSettings", "GameFramework/WorldSettings.h", "Engine", "AInfo"),
            Common("Game Instance", "UGameInstance", "Engine/GameInstance.h", "Engine", "UObject"),
            Common("Game Instance Subsystem", "UGameInstanceSubsystem", "Subsystems/GameInstanceSubsystem.h", "Engine", "USubsystem"),
            Common("World Subsystem", "UWorldSubsystem", "Subsystems/WorldSubsystem.h", "Engine", "USubsystem"),
            Common("Local Player Subsystem", "ULocalPlayerSubsystem", "Subsystems/LocalPlayerSubsystem.h", "Engine", "USubsystem"),
            Common("Blueprint Function Library", "UBlueprintFunctionLibrary", "Kismet/BlueprintFunctionLibrary.h", "Engine", "UObject"),
            Common("Anim Instance", "UAnimInstance", "Animation/AnimInstance.h", "Engine", "UObject"),
            Common("User Widget", "UUserWidget", "Blueprint/UserWidget.h", "UMG", "UWidget"),
            Common("Developer Settings", "UDeveloperSettings", "Engine/DeveloperSettings.h", "DeveloperSettings", "UObject"),
            Common("Data Asset", "UDataAsset", "Engine/DataAsset.h", "Engine", "UObject"),
            Common("Primary Data Asset", "UPrimaryDataAsset", "Engine/DataAsset.h", "Engine", "UDataAsset"),
        };

        static ParentClassInfo Common(string display, string name, string include, string module, string baseName) =>
            new ParentClassInfo { DisplayName = display, Name = name, IncludePath = include, ModuleName = module, BaseName = baseName, IsEngine = true, Kind = NewClassKind.Class };

        /// <summary>All parents, sorted by name; Common entries are not repeated here.</summary>
        public IReadOnlyList<ParentClassInfo> All { get; private set; } = Array.Empty<ParentClassInfo>();

        public ParentClassInfo Find(string name) => name != null && byName.TryGetValue(name, out var info) ? info : null;

        /// <summary>
        /// Builds the catalog from the project's indexed headers and the engine scan (<see cref="EngineClassScanner"/>).
        /// Engine plugin classes are kept only when the plugin is on by default or enabled in the .uproject.
        /// </summary>
        public static ParentClassCatalog Create(UnrealProject project, SymbolIndex symbols, IEnumerable<ParentClassInfo> engineClasses)
        {
            var catalog = new ParentClassCatalog();
            var enabledPlugins = new HashSet<string>(project?.EnabledPluginNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var info in engineClasses ?? Enumerable.Empty<ParentClassInfo>())
            {
                if (info.PluginName != null && !info.PluginEnabledByDefault && !enabledPlugins.Contains(info.PluginName)) continue;
                catalog.byName[info.Name] = info;
            }

            if (symbols != null && project != null)
                foreach (var header in symbols.Headers)
                {
                    var module = project.FindModuleForFile(header.FilePath);
                    foreach (var type in header.Types)
                    {
                        if (type.Name == null || type.IsNativeInterfaceClass) continue;
                        var kind = type.Kind == ReflectedKind.Class ? NewClassKind.Class
                            : type.Kind == ReflectedKind.Interface ? NewClassKind.Interface
                            : type.Kind == ReflectedKind.Struct ? NewClassKind.Struct : (NewClassKind?)null;
                        if (kind == null) continue;
                        // Project types win over a same-named engine type: the project is what gets compiled.
                        catalog.byName[type.Name] = new ParentClassInfo
                        {
                            Name = type.Name,
                            Kind = kind.Value,
                            BaseName = type.BaseType,
                            HeaderPath = header.FilePath,
                            IncludePath = ParentClassInfo.ComputeIncludePath(header.FilePath, module?.Directory, out bool isPrivate),
                            IsPrivateHeader = isPrivate,
                            ModuleName = module?.Name,
                            PluginName = module?.PluginName,
                            PluginEnabledByDefault = true,
                            IsAbstract = type.Macro?.Has("Abstract") == true,
                        };
                    }
                }

            // The fixed Common entries fill in for an engine that could not be scanned.
            foreach (var common in CommonParents)
                if (common.Name != null && !catalog.byName.ContainsKey(common.Name))
                    catalog.byName[common.Name] = new ParentClassInfo
                    {
                        Name = common.Name, Kind = common.Kind, BaseName = common.BaseName, IncludePath = common.IncludePath,
                        ModuleName = common.ModuleName, IsEngine = true, PluginEnabledByDefault = true,
                    };

            catalog.All = catalog.byName.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return catalog;
        }

        /// <summary>
        /// The catalog's entry for a Common item (it knows the real header and module), or the Common item itself.
        /// </summary>
        public ParentClassInfo Resolve(ParentClassInfo parent)
        {
            if (parent?.Name == null || parent.DisplayName == null) return parent;
            var found = Find(parent.Name);
            if (found == null) return parent;
            return new ParentClassInfo
            {
                DisplayName = parent.DisplayName, Name = found.Name, Kind = parent.Kind, BaseName = found.BaseName ?? parent.BaseName,
                HeaderPath = found.HeaderPath, IncludePath = found.IncludePath ?? parent.IncludePath, ModuleName = found.ModuleName ?? parent.ModuleName,
                PluginName = found.PluginName, PluginEnabledByDefault = found.PluginEnabledByDefault, IsEngine = found.IsEngine,
                IsAbstract = found.IsAbstract, IsPrivateHeader = found.IsPrivateHeader,
            };
        }

        /// <summary>The class and its known ancestors, nearest first.</summary>
        public IEnumerable<string> SelfAndBases(string name)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (name != null && seen.Add(name))
            {
                yield return name;
                name = Find(name)?.BaseName ?? KnownBase(name);
            }
        }

        public bool IsChildOf(string name, string ancestor) => SelfAndBases(name).Contains(ancestor);

        // Gaps an unscanned engine would leave in the chains the templates depend on.
        static string KnownBase(string name)
        {
            switch (name)
            {
                case "AController": case "AInfo": case "APawn": return "AActor";
                case "AActor": case "UActorComponent": case "USubsystem": case "UWidget": return "UObject";
                case "UGameInstanceSubsystem": case "UWorldSubsystem": case "ULocalPlayerSubsystem": case "UEngineSubsystem": return "USubsystem";
                case "UUserWidget": return "UWidget";
                case "UInterface": return "UObject";
                default: return CommonParents.FirstOrDefault(c => c.Name == name)?.BaseName;
            }
        }
    }
}
