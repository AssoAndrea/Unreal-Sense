using System;
using System.IO;
using System.Linq;
using UnrealSense.Project;
using UnrealSense.Templates;
using UnrealSense.Workspace;
using Xunit;

namespace UnrealSense.Tests
{
    public class NewClassTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "UnrealSenseNewClass_" + Guid.NewGuid().ToString("N"));
        readonly UnrealProject project;
        readonly SymbolIndex symbols;
        readonly ParentClassCatalog catalog;
        readonly NewClassGenerator generator;

        public NewClassTests()
        {
            Write("Game.uproject", "{ \"FileVersion\": 3, \"EngineAssociation\": \"\", \"Modules\": [ { \"Name\": \"Game\", \"Type\": \"Runtime\" }, { \"Name\": \"Other\", \"Type\": \"Runtime\" } ] }");
            Write(@"Source\Game\Game.Build.cs", "public class Game : ModuleRules { public Game(ReadOnlyTargetRules Target) : base(Target) { PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"CoreUObject\", \"Engine\" }); } }");
            Write(@"Source\Game\Public\Weapons\GameWeapon.h", "#pragma once\n#include \"GameWeapon.generated.h\"\nUCLASS()\nclass GAME_API AGameWeapon : public AActor\n{\n\tGENERATED_BODY()\n};\n");
            Write(@"Source\Game\Public\Interactable.h", "#pragma once\n#include \"Interactable.generated.h\"\nUINTERFACE(MinimalAPI)\nclass UInteractable : public UInterface\n{\n\tGENERATED_BODY()\n};\nclass GAME_API IInteractable\n{\n\tGENERATED_BODY()\n};\n");
            Write(@"Source\Other\Other.Build.cs", "public class Other : ModuleRules { public Other(ReadOnlyTargetRules Target) : base(Target) { } }");
            Write(@"Source\Other\Private\Hidden.h", "#pragma once\n#include \"Hidden.generated.h\"\nUCLASS()\nclass UHidden : public UObject\n{\n\tGENERATED_BODY()\n};\n");
            Directory.CreateDirectory(Path.Combine(root, @"Source\Game\Private"));

            project = UnrealProject.Load(Path.Combine(root, "Game.uproject"));
            symbols = new SymbolIndex(project);
            symbols.Build();
            catalog = ParentClassCatalog.Create(project, symbols, null);
            // No engine on purpose: the built-in templates must reproduce the editor's output on their own.
            generator = new NewClassGenerator(project, catalog, symbols) { EngineTemplatesDirectory = null };
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        UnrealModule Game => project.Modules.First(m => m.Name == "Game");
        static ParentClassInfo Common(string label) => ParentClassCatalog.CommonParents.First(c => c.DisplayName == label);

        NewClassResult Generate(ParentClassInfo parent, string name, ClassLocation location = ClassLocation.Public, string sub = "Variant_Shooter", UnrealModule module = null) =>
            generator.Generate(new NewClassRequest { Name = name, Parent = parent, Module = module ?? Game, Location = location, SubFolder = sub });

        [Fact]
        public void ActorMatchesTheEditorsOutput()
        {
            var result = Generate(Common("Actor"), "MyClass");
            Assert.True(result.IsValid, string.Join(" ", result.Errors));
            Assert.Equal(string.Join("\r\n",
                "// Fill out your copyright notice in the Description page of Project Settings.",
                "",
                "#pragma once",
                "",
                "#include \"CoreMinimal.h\"",
                "#include \"GameFramework/Actor.h\"",
                "#include \"MyClass.generated.h\"",
                "",
                "UCLASS()",
                "class GAME_API AMyClass : public AActor",
                "{",
                "\tGENERATED_BODY()",
                "",
                "public:",
                "\t// Sets default values for this actor's properties",
                "\tAMyClass();",
                "",
                "protected:",
                "\t// Called when the game starts or when spawned",
                "\tvirtual void BeginPlay() override;",
                "",
                "public:",
                "\t// Called every frame",
                "\tvirtual void Tick(float DeltaTime) override;",
                "};",
                ""), result.HeaderText);
            Assert.Contains("#include \"Variant_Shooter/MyClass.h\"", result.SourceText);
            Assert.Contains("void AMyClass::Tick(float DeltaTime)", result.SourceText);
            Assert.Equal(Path.Combine(Game.Directory, @"Public\Variant_Shooter\MyClass.h"), result.HeaderPath);
            Assert.Equal(Path.Combine(Game.Directory, @"Private\Variant_Shooter\MyClass.cpp"), result.SourcePath);
        }

        [Theory]
        [InlineData("MyActor", "AMyActor", "MyActor")]
        [InlineData("AMyActor", "AMyActor", "MyActor")]
        [InlineData("Ability", "AAbility", "Ability")]
        [InlineData("AIController2", "AIController2", "IController2")]
        public void ActorPrefixIsAddedOnce(string typed, string className, string fileName)
        {
            var result = Generate(Common("Actor"), typed);
            Assert.Equal(className, result.ClassName);
            Assert.Equal(fileName, result.FileName);
        }

        [Fact]
        public void PrefixFollowsTheParent()
        {
            Assert.Equal("UMyComp", Generate(Common("Actor Component"), "MyComp").ClassName);
            Assert.Equal("AMyWeapon", Generate(catalog.Find("AGameWeapon"), "MyWeapon").ClassName);
            Assert.Equal("FMyRow", Generate(Common("Struct"), "MyRow").ClassName);
            Assert.Equal("EMyState", Generate(Common("Enum"), "MyState").ClassName);
            Assert.Equal("FMyHelper", Generate(Common("Empty"), "FMyHelper").ClassName);
        }

        [Fact]
        public void LocationsPlaceTheFiles()
        {
            var priv = Generate(Common("UObject"), "Thing", ClassLocation.Private, @"AI\Tasks");
            Assert.Equal(Path.Combine(Game.Directory, @"Private\AI\Tasks\Thing.h"), priv.HeaderPath);
            Assert.Equal(Path.Combine(Game.Directory, @"Private\AI\Tasks\Thing.cpp"), priv.SourcePath);
            Assert.Contains("#include \"Thing.h\"", priv.SourceText);

            var rootLayout = Generate(Common("UObject"), "Thing", ClassLocation.Root, "");
            Assert.Equal(Path.Combine(Game.Directory, "Thing.h"), rootLayout.HeaderPath);
            Assert.Equal(Path.Combine(Game.Directory, "Thing.cpp"), rootLayout.SourcePath);
        }

        [Fact]
        public void ProjectParentIsIncludedRelativeToPublic()
        {
            var result = Generate(catalog.Find("AGameWeapon"), "Rifle");
            Assert.Contains("#include \"Weapons/GameWeapon.h\"", result.HeaderText);
            Assert.Contains("class GAME_API ARifle : public AGameWeapon", result.HeaderText);
            Assert.Equal("ActorClass", result.TemplateName);
            Assert.Null(result.MissingDependency);
        }

        [Fact]
        public void InterfaceDeclaresBothClasses()
        {
            var result = Generate(Common("Interface"), "IUsable");
            Assert.Equal("UUsable", result.ClassName);
            Assert.Contains("UINTERFACE(MinimalAPI)\r\nclass UUsable : public UInterface\r\n", result.HeaderText);
            Assert.Contains("class GAME_API IUsable\r\n{", result.HeaderText);
            Assert.Contains("#include \"UObject/Interface.h\"", result.HeaderText);
            Assert.Single(result.HeaderText.Split(new[] { "#include \"UObject/Interface.h\"" }, StringSplitOptions.None).Skip(1));
            Assert.NotNull(result.SourcePath);
        }

        [Fact]
        public void InterfaceExtendingAnotherInterface()
        {
            var result = Generate(catalog.Find("UInteractable"), "Pickup");
            Assert.Contains("class UPickup : public UInteractable", result.HeaderText);
            Assert.Contains("class GAME_API IPickup : public IInteractable\r\n", result.HeaderText);
            Assert.Contains("#include \"Interactable.h\"", result.HeaderText);
        }

        [Fact]
        public void StructAndEnumAreHeaderOnly()
        {
            var plain = Generate(Common("Struct"), "Stats");
            Assert.Null(plain.SourcePath);
            Assert.Contains("USTRUCT(BlueprintType)\r\nstruct GAME_API FStats\r\n{\r\n\tGENERATED_BODY()\r\n};", plain.HeaderText);

            var row = Generate(new ParentClassInfo { Name = "FTableRowBase", Kind = NewClassKind.Struct, IncludePath = "Engine/DataTable.h", ModuleName = "Engine" }, "WeaponRow");
            Assert.Contains("struct GAME_API FWeaponRow : public FTableRowBase", row.HeaderText);
            Assert.Contains("#include \"Engine/DataTable.h\"", row.HeaderText);

            var e = Generate(Common("Enum"), "EFireMode");
            Assert.Null(e.SourcePath);
            Assert.Contains("UENUM(BlueprintType)\r\nenum class EFireMode : uint8", e.HeaderText);
            Assert.DoesNotContain("GAME_API", e.HeaderText);
        }

        [Fact]
        public void EmptyClassHasNoReflection()
        {
            var result = Generate(Common("Empty"), "FMathHelpers");
            Assert.DoesNotContain("generated.h", result.HeaderText);
            Assert.DoesNotContain("UCLASS", result.HeaderText);
            Assert.Contains("class GAME_API FMathHelpers", result.HeaderText);
            Assert.Contains("FMathHelpers::~FMathHelpers()", result.SourceText);
        }

        [Fact]
        public void SpecialParentsGetTheirOverrides()
        {
            var subsystem = Generate(Common("World Subsystem"), "Spawner");
            Assert.Contains("virtual void Initialize(FSubsystemCollectionBase& Collection) override;", subsystem.HeaderText);
            Assert.Contains("Super::Deinitialize();", subsystem.SourceText);
            Assert.Contains("#include \"Subsystems/WorldSubsystem.h\"", subsystem.HeaderText);

            var component = Generate(Common("Scene Component"), "Mount");
            Assert.Contains("UCLASS(ClassGroup=(Custom), meta=(BlueprintSpawnableComponent))", component.HeaderText);
            Assert.Contains("TickComponent", component.SourceText);

            var pawn = Generate(Common("Character"), "Hero");
            Assert.Contains("SetupPlayerInputComponent", pawn.HeaderText);

            var anim = Generate(Common("Anim Instance"), "HeroAnim");
            Assert.Contains("NativeUpdateAnimation(float DeltaSeconds)", anim.SourceText);
        }

        [Fact]
        public void EveryCommonParentHasBuiltinTemplates()
        {
            foreach (var parent in ParentClassCatalog.CommonParents)
            {
                var result = Generate(parent, "Probe" + parent.DisplayName.Replace(" ", ""));
                Assert.True(result.IsValid, parent.DisplayName + ": " + string.Join(" ", result.Errors));
                Assert.NotNull(result.HeaderText);
                Assert.Equal(result.SourcePath != null, result.SourceText != null);
                Assert.DoesNotContain("%", result.HeaderText + result.SourceText);
            }
        }

        [Fact]
        public void MissingModuleDependencyIsReported()
        {
            var widget = Generate(Common("User Widget"), "Hud");
            Assert.Equal("UMG", widget.MissingDependency);
            Assert.Contains("virtual void NativeConstruct() override;", widget.HeaderText);

            var settings = Generate(Common("Developer Settings"), "GameSettings");
            Assert.Equal("DeveloperSettings", settings.MissingDependency);
            Assert.Contains("UCLASS(Config=Game, DefaultConfig)", settings.HeaderText);

            Assert.Null(Generate(Common("Actor"), "Fine").MissingDependency);
        }

        [Fact]
        public void PrivateParentOfAnotherModuleIsRejected()
        {
            var hidden = catalog.Find("UHidden");
            Assert.True(hidden.IsPrivateHeader);
            Assert.False(Generate(hidden, "Peek").IsValid);
            var other = project.Modules.First(m => m.Name == "Other");
            Assert.True(Generate(hidden, "Peek", ClassLocation.Private, "", other).IsValid);
        }

        [Fact]
        public void InvalidOrExistingNamesAreRejected()
        {
            Assert.False(Generate(Common("Actor"), "").IsValid);
            Assert.False(Generate(Common("Actor"), "My Class").IsValid);
            Assert.False(Generate(Common("Actor"), "GameWeapon").IsValid);

            var first = Generate(Common("Actor"), "Twice");
            NewClassGenerator.Write(first);
            Assert.True(File.Exists(first.HeaderPath) && File.Exists(first.SourcePath));
            Assert.False(Generate(Common("Actor"), "Twice").IsValid);
        }

        [Fact]
        public void CopyrightNoticeComesFromDefaultGameIni()
        {
            Write(@"Config\DefaultGame.ini", "[/Script/EngineSettings.GeneralProjectSettings]\r\nCopyrightNotice=Copyright Acme. All Rights Reserved.\r\n");
            Assert.StartsWith("// Copyright Acme. All Rights Reserved.\r\n", Generate(Common("Actor"), "Branded").HeaderText);
        }

        [Fact]
        public void BuildCsGetsTheDependency()
        {
            var multi = "\t\tPublicDependencyModuleNames.AddRange(new string[] {\r\n\t\t\t\"Core\",\r\n\t\t\t\"Engine\"\r\n\t\t});";
            Assert.Equal("\t\tPublicDependencyModuleNames.AddRange(new string[] {\r\n\t\t\t\"Core\",\r\n\t\t\t\"Engine\",\r\n\t\t\t\"UMG\"\r\n\t\t});",
                BuildCsEditor.AddPublicDependency(multi, "UMG"));

            var single = "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"Engine\" });";
            Assert.Equal("PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"Engine\", \"UMG\" });", BuildCsEditor.AddPublicDependency(single, "UMG"));

            var trailing = "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", });";
            Assert.Equal("PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"UMG\", });", BuildCsEditor.AddPublicDependency(trailing, "UMG"));

            var none = "public class X : ModuleRules\n{\n\tpublic X(ReadOnlyTargetRules Target) : base(Target)\n\t{\n\t\tPCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;\n\t}\n}";
            var added = BuildCsEditor.AddPublicDependency(none, "UMG");
            Assert.Contains("PublicDependencyModuleNames.Add(\"UMG\");", added);
            Assert.Contains("UMG", BuildCsParser.ParseDependencies(added).Public);

            Assert.Same(single, BuildCsEditor.AddPublicDependency(single, "Engine"));
        }

        [Fact]
        public void HeaderScanReadsDeclarations()
        {
            var text = string.Join("\n",
                "// UCLASS(NotAMacro) in a comment",
                "UCLASS(Abstract, meta=(DisplayName=\"A (b)\"))",
                "class ENGINE_API AThing : public AActor, public IFoo",
                "{",
                "};",
                "UINTERFACE(MinimalAPI)",
                "class UUsable : public UInterface { };",
                "UCLASS()",
                "// a comment between",
                "class UE_DEPRECATED(5.1, \"x\") ENGINE_API UOld : public UObject {};",
                "UCLASS(MinimalAPI)",
                "class UMin final : public UObject {};",
                "USTRUCT(BlueprintType)",
                "struct FRow : public FTableRowBase {};",
                "UCLASS()",
                "class ULocal : public UObject {};");
            var types = EngineClassScanner.ScanHeaderText(text).ToDictionary(t => t.Name);
            Assert.Equal(6, types.Count);
            Assert.True(types["AThing"].IsAbstract && types["AThing"].IsExported);
            Assert.Equal("AActor", types["AThing"].BaseName);
            Assert.Equal(NewClassKind.Interface, types["UUsable"].Kind);
            Assert.True(types["UUsable"].IsMinimalApi);
            Assert.True(types["UOld"].IsDeprecated);
            Assert.True(types["UMin"].IsFinal);
            Assert.Equal(NewClassKind.Struct, types["FRow"].Kind);
            Assert.False(types["ULocal"].IsExported);
        }

        [Fact]
        public void IncludePathsFollowTheModuleLayout()
        {
            var module = @"C:\P\Source\Game";
            Assert.Equal("GameFramework/Actor.h", ParentClassInfo.ComputeIncludePath(@"C:\P\Source\Game\Classes\GameFramework\Actor.h", module, out var p1));
            Assert.False(p1);
            Assert.Equal("Weapons/Gun.h", ParentClassInfo.ComputeIncludePath(@"C:\P\Source\Game\Public\Weapons\Gun.h", module, out _));
            Assert.Equal("AI/Brain.h", ParentClassInfo.ComputeIncludePath(@"C:\P\Source\Game\Private\AI\Brain.h", module, out var p3));
            Assert.True(p3);
            Assert.Equal("Character/LyraCharacter.h", ParentClassInfo.ComputeIncludePath(@"C:\P\Source\Game\Character\LyraCharacter.h", module, out _));
        }
    }
}
