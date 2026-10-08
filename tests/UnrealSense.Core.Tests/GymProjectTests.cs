using System.IO;
using System.Linq;
using UnrealSense.Assets;
using UnrealSense.Project;
using UnrealSense.Reflection;
using Xunit;

namespace UnrealSense.Tests
{
    /// <summary>
    /// Integration tests against a real UE 5.8 project. They are no-ops when the project is not on this machine
    /// (set UNREALSENSE_TEST_PROJECT to point elsewhere).
    /// </summary>
    public class GymProjectTests
    {
        static readonly string ProjectDir = System.Environment.GetEnvironmentVariable("UNREALSENSE_TEST_PROJECT") ?? @"C:\Unreal Project\Gym";
        static bool Available => File.Exists(Path.Combine(ProjectDir, "Gym.uproject"));

        [Fact]
        public void ReadsBlueprintPackageHeader()
        {
            if (!Available) return;
            var package = UAssetPackage.Read(Path.Combine(ProjectDir, @"Content\Variant_Shooter\Blueprints\BP_ShooterCharacter.uasset"));
            Assert.True(package.FileVersionUE5 >= 1000);
            Assert.Contains(package.Exports, e => e.ObjectName == "BP_ShooterCharacter_C");
            Assert.Contains(package.Imports, i => i.ClassName == "Function" && i.ObjectName == "DoStartFiring");
        }

        [Fact]
        public void LoadsProjectWithRedirects()
        {
            if (!Available) return;
            var project = UnrealProject.Load(Path.Combine(ProjectDir, "Gym.uproject"));
            Assert.Contains(project.Modules, m => m.Name == "Gym");
            Assert.Contains("Engine", project.Modules[0].PublicDependencies);
            Assert.Equal("/Script/Gym", project.Redirects.RedirectPackage("/Script/TP_FirstPerson"));
            Assert.Equal("GymCharacter", project.Redirects.RedirectClass("TP_FirstPersonCharacter"));
            Assert.NotNull(project.Engine);
        }

        [Fact]
        public void FindsBlueprintUsages()
        {
            if (!Available) return;
            var project = UnrealProject.Load(Path.Combine(ProjectDir, "Gym.uproject"));
            var index = new AssetIndex(project);
            index.Build();

            Assert.DoesNotContain(index.Assets, a => a.Error != null);

            var subclass = index.FindTypeUsages("Gym", "ShooterCharacter");
            Assert.Contains(subclass, u => u.Kind == AssetUsageKind.Subclass && u.Asset.AssetName == "BP_ShooterCharacter");

            var calls = index.FindFunctionUsages("Gym", "ShooterCharacter", "DoStartFiring");
            Assert.Contains(calls, u => u.Kind == AssetUsageKind.FunctionCall);

            var events = index.FindFunctionUsages("Gym", "ShooterCharacter", "BP_OnDeath");
            Assert.Single(events, u => u.Kind == AssetUsageKind.EventImplementation);
            Assert.DoesNotContain(events, u => u.Kind == AssetUsageKind.FunctionCall);

            // BP_ShooterWeapon_Rifle -> BP_ShooterWeaponBase -> AShooterWeapon
            var derived = index.GetDerivedBlueprints("Gym", "ShooterWeapon").Select(a => a.AssetName).ToList();
            Assert.Contains("BP_ShooterWeaponBase", derived);
            Assert.Contains("BP_ShooterWeapon_Rifle", derived);
        }

        [Fact]
        public void CatalogFromEngineIsAuthoritative()
        {
            if (!Available) return;
            var project = UnrealProject.Load(Path.Combine(ProjectDir, "Gym.uproject"));
            var catalog = SpecifierCatalog.Create(project.Engine.EngineDirectory);
            Assert.True(catalog.IsAuthoritative);
            Assert.NotNull(catalog.FindSpecifier(SpecifierTarget.Struct, "DisplayName"));
            Assert.NotNull(catalog.FindSpecifier(SpecifierTarget.Property, "EditAnywhere"));
            Assert.True(catalog.FindSpecifier(SpecifierTarget.Property, "ReplicatedUsing").TakesValue);
            Assert.False(string.IsNullOrEmpty(catalog.FindSpecifier(SpecifierTarget.Property, "Transient").Documentation));
            Assert.Null(catalog.FindSpecifier(SpecifierTarget.Property, "BlueprintImplementableEvent"));
        }

        [Fact]
        public void NewClassParentsComeFromEngineAndProject()
        {
            if (!Available) return;
            var project = UnrealProject.Load(Path.Combine(ProjectDir, "Gym.uproject"));
            var symbols = new Workspace.SymbolIndex(project);
            symbols.Build();
            var engine = Templates.EngineClassScanner.Scan(project.Engine, Templates.EngineClassScanner.DefaultCachePath(project.Engine));
            var catalog = Templates.ParentClassCatalog.Create(project, symbols, engine);

            var character = catalog.Find("ACharacter");
            Assert.Equal("GameFramework/Character.h", character.IncludePath);
            Assert.Equal("Engine", character.ModuleName);
            Assert.Equal("UMG", catalog.Find("UUserWidget").ModuleName);
            Assert.Equal("Variant_Shooter/ShooterCharacter.h", catalog.Find("AShooterCharacter").IncludePath);
            Assert.True(catalog.IsChildOf("AShooterCharacter", "AActor"));
            // GameplayAbilities is not enabled in Gym.
            Assert.Null(catalog.Find("UGameplayAbility"));
        }

        [Fact]
        public void NewActorMatchesRidersFile()
        {
            // Public\Variant_Shooter\MyClass.h was created by Rider's "Unreal Class..." with parent Actor.
            var riderFile = Path.Combine(ProjectDir, @"Source\Gym\Public\Variant_Shooter\MyClass.h");
            if (!Available || !File.Exists(riderFile)) return;
            var project = UnrealProject.Load(Path.Combine(ProjectDir, "Gym.uproject"));
            var generator = new Templates.NewClassGenerator(project, null);
            var result = generator.Generate(new Templates.NewClassRequest
            {
                Name = "MyClass",
                Parent = Templates.ParentClassCatalog.CommonParents.First(c => c.DisplayName == "Actor"),
                Module = project.Modules.First(m => m.Name == "Gym"),
                Location = Templates.ClassLocation.Public,
                SubFolder = "Variant_Shooter",
            });
            string Normalize(string s) => string.Join("\n", s.TrimStart('﻿').Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
            Assert.Equal(Normalize(File.ReadAllText(riderFile)), Normalize(result.HeaderText));
            Assert.Equal(riderFile, result.HeaderPath);
        }
    }
}
