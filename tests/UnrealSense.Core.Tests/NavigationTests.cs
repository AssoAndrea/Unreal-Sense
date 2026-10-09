using System;
using System.IO;
using System.Linq;
using UnrealSense.Cpp;
using UnrealSense.Navigation;
using UnrealSense.Project;
using Xunit;

namespace UnrealSense.Tests
{
    public class NavigationTests
    {
        [Theory]
        [InlineData("GAL", "GetActorLocation")]
        [InlineData("getactloc", "GetActorLocation")]
        [InlineData("shchar", "ShooterCharacter.h")]
        [InlineData("UWorld", "UWorld")]
        public void FuzzyMatches(string pattern, string candidate)
        {
            Assert.NotEqual(int.MinValue, new FuzzyMatcher(pattern).Score(candidate));
        }

        [Fact]
        public void FuzzyRanksPrefixAndHumpsHigher()
        {
            var m = new FuzzyMatcher("GAL");
            Assert.True(m.Score("GetActorLocation") > m.Score("GetMaxLocationAndAngle"));
            var exact = new FuzzyMatcher("UWorld");
            Assert.True(exact.Score("UWorld") > exact.Score("UWorldSubsystem"));
            Assert.Equal(int.MinValue, new FuzzyMatcher("xyz").Score("GetActorLocation"));
        }

        [Fact]
        public void ScannerFindsDeclarations()
        {
            const string text = @"
#define MY_MACRO(x) x
namespace UE::Net { struct FPacket { int32 Size; }; }
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHit, float, Damage);
class FForward;
friend class UWorld;
template <typename T> class TBox { T Value; };
UCLASS(Blueprintable)
class GAME_API AFoo final : public AActor
{
    GENERATED_BODY()
public:
    UPROPERTY(EditAnywhere)
    float Health = 100.f;
    UFUNCTION(BlueprintCallable)
    void Heal(float Amount) { Health += Amount; int32 Local = 0; }
    virtual ~AFoo();
    bool operator==(const AFoo& Other) const;
    using FMap = TMap<int32, int32>;
};
enum class EMode : uint8 { A, B = 2, C UMETA(Hidden) };
void AFoo::Heal2(float) {}
static int32 GCounter = 0;
";
            var d = DeclarationScanner.Scan(text);
            string Find(string name) => d.Where(x => x.Name == name).Select(x => $"{x.Kind}:{x.Container}").FirstOrDefault();

            Assert.Equal("Macro:", Find("MY_MACRO"));
            Assert.Equal("Struct:UE::Net", Find("FPacket"));
            Assert.Equal("Field:UE::Net::FPacket", Find("Size"));
            Assert.Equal("Delegate:", Find("FOnHit"));
            Assert.Null(Find("FForward"));
            Assert.Null(Find("UWorld"));
            Assert.Equal("Class:", Find("TBox"));
            Assert.Equal("Class:", Find("AFoo"));
            Assert.Equal("Field:AFoo", Find("Health"));
            Assert.Equal("Function:AFoo", Find("Heal"));
            Assert.Null(Find("Local"));               // function bodies are skipped
            Assert.Equal("Function:AFoo", Find("~AFoo"));
            Assert.Equal("Function:AFoo", Find("operator=="));
            Assert.Equal("Typedef:AFoo", Find("FMap"));
            Assert.Equal("Enum:", Find("EMode"));
            Assert.Equal(new[] { "A", "B", "C" }, d.Where(x => x.Kind == SymbolKind.EnumValue).Select(x => x.Name));
            Assert.Equal("Function:AFoo", Find("Heal2"));
            Assert.Equal("Variable:", Find("GCounter"));
            Assert.Equal(8, d.First(x => x.Name == "AFoo").Line); // 0-based (the literal starts with a newline)
        }

        [Fact]
        public void SplitsWindowsCommandLines()
        {
            var args = CompileDatabase.SplitCommandLine("\"C:/a b/cl.exe\" @\"C:/x y/f.rsp\" /I \"C:/inc dir\" /DX=\\\"q\\\"\n/FI\"C:/p.h\"");
            Assert.Equal(new[] { "C:/a b/cl.exe", "@C:/x y/f.rsp", "/I", "C:/inc dir", "/DX=\"q\"", "/FIC:/p.h" }, args);
        }

        [Fact]
        public void SanitizesUbtDatabase()
        {
            var dir = Path.Combine(Path.GetTempPath(), "us-db-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "shared.rsp"), "/X\n/Yu\"PCH.h\"\n/Fp\"x.pch\"\n/I \"C:/inc\"\n/d2ExtendedWarningInfo\n/std:c++20");
                File.WriteAllText(Path.Combine(dir, "a.rsp"), $"\"C:/src/a.cpp\"\n@\"{dir.Replace('\\', '/')}/shared.rsp\"\n/Fo\"a.obj\"\n/experimental:log \"a.sarif\"\n/c");
                File.WriteAllText(Path.Combine(dir, "in.json"),
                    "[{\"file\":\"C:/src/a.cpp\",\"directory\":\"C:/eng\",\"command\":\"\\\"C:/cl.exe\\\" @\\\"" + dir.Replace('\\', '/') + "/a.rsp\\\"\"}]");
                Assert.Equal(1, CompileDatabase.Sanitize(Path.Combine(dir, "in.json"), Path.Combine(dir, "out.json")));
                var direct = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dir, "out.json")))[0]["arguments"].Select(a => (string)a).ToList();
                // The shared response file stays a (cleaned) @reference instead of being inlined in every entry.
                var shared = direct.Single(a => a.StartsWith("@")).Substring(1);
                Assert.StartsWith(Path.Combine(dir, "rsp"), shared);
                var args = direct.Where(a => !a.StartsWith("@")).Concat(CompileDatabase.SplitCommandLine(File.ReadAllText(shared))).ToList();
                Assert.Equal("cl.exe", args[0]); // machine-independent, so an index can be shared
                Assert.Contains("C:/src/a.cpp", args);
                Assert.Contains("/X", args);
                Assert.Contains("/std:c++20", args);
                Assert.Contains("C:/inc", args);
                Assert.DoesNotContain(args, a => a.StartsWith("/Yu") || a.StartsWith("/Fp") || a.StartsWith("/Fo") || a.StartsWith("/d2") || a == "/c" || a.Contains("sarif"));

                // Engine translation units can be filtered out.
                File.WriteAllText(Path.Combine(dir, "in2.json"),
                    "[{\"file\":\"C:/src/a.cpp\",\"directory\":\"C:/eng\",\"command\":\"cl.exe a.cpp\"},{\"file\":\"C:/UE/Engine/Source/b.cpp\",\"directory\":\"C:/eng\",\"command\":\"cl.exe b.cpp\"}]");
                Assert.Equal(1, CompileDatabase.Sanitize(Path.Combine(dir, "in2.json"), Path.Combine(dir, "out2.json"), f => !f.StartsWith("C:/UE/Engine/"), out int skipped));
                Assert.Equal(1, skipped);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void FindsEngineResponseFilesRelativeToTheSourceFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-enginersp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var source = Path.Combine(root, "Engine", "Source");
                var entries = new Newtonsoft.Json.Linq.JArray();
                foreach (var module in new[] { "Core", "Engine" })
                {
                    var intermediate = Path.Combine(root, "Engine", "Intermediate", "Build", module);
                    Directory.CreateDirectory(intermediate);
                    Directory.CreateDirectory(Path.Combine(source, "Runtime", module, "Private"));
                    File.WriteAllText(Path.Combine(source, "Runtime", module, module + ".Build.cs"), "class X : ModuleRules { }");
                    // UBT style: the module's own API is dllexport, its dependencies' are dllimport.
                    File.WriteAllText(Path.Combine(intermediate, "Definitions.h"), module == "Core"
                        ? "#define CORE_API DLLEXPORT\n#define UE_VALIDATE_INTERNAL_API 1\n"
                        : "#define CORE_API DLLIMPORT\n#define ENGINE_API DLLEXPORT\n#define UE_VALIDATE_EXPERIMENTAL_API 0\n");
                    // Shared .rsp with per-module warnings (irrelevant for indexing) and includes relative to Engine/Source.
                    File.WriteAllText(Path.Combine(intermediate, module + ".Shared.rsp"),
                        $"/I \"Runtime/{module}/Public\"\n/W4\n/we{(module == "Core" ? "4668" : "4456")}\n/std:c++20\n/GR-");
                    foreach (var name in new[] { "A", "B" })
                    {
                        var cpp = Path.Combine(source, "Runtime", module, "Private", module + name + ".cpp");
                        File.WriteAllText(cpp, "int x;");
                        var perFile = Path.Combine(intermediate, module + name + ".cpp.obj.rsp");
                        // UBT style: source relative to Engine/Source, shared .rsp relative to Engine/Source too.
                        File.WriteAllText(perFile, $"\"Runtime/{module}/Private/{module}{name}.cpp\"\n@\"../Intermediate/Build/{module}/{module}.Shared.rsp\"\n/FI\"../Intermediate/Build/{module}/Definitions.h\"\n/Fo\"{module}{name}.obj\"");
                        entries.Add(new Newtonsoft.Json.Linq.JObject
                        {
                            ["directory"] = source.Replace('\\', '/'),
                            ["file"] = cpp.Replace('\\', '/'),
                            ["command"] = $"\"C:/MSVC/cl.exe\" @\"{perFile.Replace('\\', '/')}\"",
                        });
                    }
                }
                var raw = Path.Combine(root, "raw.json");
                File.WriteAllText(raw, entries.ToString());
                var files = Path.Combine(root, "db", "files.json");
                Directory.CreateDirectory(Path.GetDirectoryName(files));
                Assert.Equal(4, CompileDatabase.Sanitize(raw, files));

                // The shared .rsp files were found (relative to Engine/Source) and rewritten as cleaned copies.
                var rsp = Directory.GetFiles(Path.Combine(root, "db", "rsp"));
                Assert.Equal(2, rsp.Length);
                Assert.All(rsp, f => Assert.Contains("/std:c++20", File.ReadAllText(f)));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void TreatsARenamedModuleAsTheProjectModule()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-alias-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Source", "Game"));
                File.WriteAllText(Path.Combine(root, "Game.uproject"), "{ \"FileVersion\": 3, \"Modules\": [ { \"Name\": \"Game\" } ] }");
                File.WriteAllText(Path.Combine(root, "Source", "Game", "Game.Build.cs"), "public class Game : ModuleRules { }");
                Directory.CreateDirectory(Path.Combine(root, "Content"));
                var asset = Path.Combine(root, "Content", "BP_Hero.uasset");
                File.WriteAllBytes(asset, new byte[] { 1 });
                var info = new FileInfo(asset);
                // Saved before the module was renamed: the asset still looks for /Script/OldGame, and no redirect in config says so.
                var cache = Path.Combine(root, "assets.json");
                File.WriteAllText(cache, Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    Version = 3,
                    Records = new[]
                    {
                        new
                        {
                            FilePath = info.FullName, PackageName = "/Game/BP_Hero", FileSize = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                            AssetClass = "Blueprint",
                            NativeParent = new { Module = "OldGame", Type = "Hero" },
                            ClassReferences = new[] { new { Module = "OldGame", Type = "Weapon" }, new { Module = "Engine", Type = "Actor" } },
                        },
                    },
                }));
                var project = UnrealProject.Load(Path.Combine(root, "Game.uproject"));
                var index = new UnrealSense.Assets.AssetIndex(project);
                index.Build(cache);
                Assert.Equal(0, index.LastBuildAnalyzed);
                Assert.Empty(index.FindTypeUsages("Game", "Hero"));

                var learned = index.LearnModuleAliases(new[] { ("Game", "Hero"), ("Game", "Weapon"), ("Game", "Pickup"), ("Game", "Actor") });
                Assert.Single(learned);
                Assert.Contains(index.FindTypeUsages("Game", "Hero"), u => u.Asset.AssetName == "BP_Hero");
                Assert.Contains(index.FindTypeUsages("Game", "Weapon"), u => u.Asset.AssetName == "BP_Hero");
                // Engine packages are not taken for project modules because of a shared class name.
                Assert.DoesNotContain(learned, l => l.Contains("/Script/Engine"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void GivesRealPathsAndTheUserTheirOwn()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-view-" + Guid.NewGuid().ToString("N"));
            try
            {
                var real = Path.Combine(root, "workspaces", "Game");
                Directory.CreateDirectory(Path.Combine(real, "Source"));
                File.WriteAllText(Path.Combine(real, "Source", "Actor.cpp"), "");
                // A junction stands in for a substituted drive (S:\Game -> D:\Work\Game).
                var view = Path.Combine(root, "S");
                var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{view}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false });
                mklink.WaitForExit();
                Assert.True(Directory.Exists(Path.Combine(view, "Source")));

                var canonical = RealPaths.CanonicalPath(Path.Combine(view, "source", "actor.cpp"));
                Assert.Equal(Path.Combine(real, "Source", "Actor.cpp"), canonical, ignoreCase: true);
                Assert.EndsWith(@"Source\Actor.cpp", canonical);   // case as on disk
                // Real paths (from the indexer) are given back under the folder the user works with.
                Assert.Equal(Path.Combine(view, "Source", "Other.h"), RealPaths.ToViewPath(Path.Combine(real, "Source", "Other.h")), ignoreCase: true);
            }
            finally
            {
                try { Directory.Delete(Path.Combine(root, "S")); } catch (IOException) { }
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void FindsProjectsOfAnEngineRootSolution()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-native-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "projects", "gameb"));
                Directory.CreateDirectory(Path.Combine(root, "projects", "other"));
                Directory.CreateDirectory(Path.Combine(root, "Engine", "Intermediate", "ProjectFiles"));
                File.WriteAllText(Path.Combine(root, "Default.uprojectdirs"), "; comment\nprojects/\n");
                var game = Path.Combine(root, "projects", "gameb", "gameb.uproject");
                File.WriteAllText(game, "{}");
                File.WriteAllText(Path.Combine(root, "projects", "other", "other.uproject"), "{}");
                File.WriteAllText(Path.Combine(root, "Engine", "Intermediate", "ProjectFiles", "gameb.vcxproj"),
                    "<NMakeBuildCommandLine>Build.bat gamebEditor Win64 Development -Project=&quot;$(SolutionDir)projects\\gameb\\gameb.uproject&quot; -WaitMutex</NMakeBuildCommandLine>");
                var slnx = Path.Combine(root, "UE5.slnx");
                File.WriteAllText(slnx, "<Solution><Folder Name=\"/Games/\"><Project Path=\"Engine/Intermediate/ProjectFiles/gameb.vcxproj\" /></Folder></Solution>");

                var found = UnrealProject.FindUProjectsForSolution(slnx);
                Assert.Contains(found, f => string.Equals(f, game, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(2, found.Count); // both projects listed by .uprojectdirs, no duplicates

                // Only the .vcxproj route (no .uprojectdirs).
                File.Delete(Path.Combine(root, "Default.uprojectdirs"));
                Assert.Equal(new[] { game }, UnrealProject.FindUProjectsForSolution(slnx), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void MovesToolchainHeadersToSystemIncludes()
        {
            var dir = Path.Combine(Path.GetTempPath(), "us-imsvc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var msvc = "C:/Program Files/Microsoft Visual Studio/18/Professional/VC/Tools/MSVC/14.50.35717/INCLUDE";
                var sdk = "C:/Program Files (x86)/Windows Kits/10/include/10.0.22621.0/ucrt";
                File.WriteAllText(Path.Combine(dir, "shared.rsp"), $"/external:I\n\"{msvc}\"\n/I \"{sdk}\"\n/I \"Runtime/Core/Public\"\n/external:I\"C:/ThirdParty/Include\"");
                File.WriteAllText(Path.Combine(dir, "a.rsp"), $"\"C:/src/a.cpp\"\n@\"{dir.Replace('\\', '/')}/shared.rsp\"");
                File.WriteAllText(Path.Combine(dir, "in.json"),
                    "[{\"file\":\"C:/src/a.cpp\",\"directory\":\"C:/eng\",\"command\":\"cl.exe @\\\"" + dir.Replace('\\', '/') + "/a.rsp\\\"\"}]");
                CompileDatabase.Sanitize(Path.Combine(dir, "in.json"), Path.Combine(dir, "out.json"));
                var shared = Directory.GetFiles(Path.Combine(dir, "rsp")).Single();
                var args = CompileDatabase.SplitCommandLine(File.ReadAllText(shared));
                Assert.Contains("/imsvc" + msvc, args);
                Assert.Contains("/imsvc" + sdk, args);
                Assert.Contains("Runtime/Core/Public", args);       // project/engine folders stay ordinary includes
                Assert.Contains("/external:IC:/ThirdParty/Include", args); // not a toolchain folder: unchanged
                Assert.DoesNotContain(args, a => a.StartsWith("/external:I") && CompileDatabase.IsSystemIncludeDir(a.Substring(11)));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void CompileDatabaseReusesTheOldUbtOutputAndSeesNewSources()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-compiledb-" + Guid.NewGuid().ToString("N"));
            var previousCache = Environment.GetEnvironmentVariable("UNREALSENSE_CACHE");
            try
            {
                var game = Path.Combine(root, "Game");
                Directory.CreateDirectory(Path.Combine(game, "Source", "Game"));
                File.WriteAllText(Path.Combine(game, "Game.uproject"), "{ \"FileVersion\": 3, \"Modules\": [ { \"Name\": \"Game\" } ] }");
                File.WriteAllText(Path.Combine(game, "Source", "Game", "Game.Build.cs"), "public class Game : ModuleRules { }");
                var a = Path.Combine(game, "Source", "Game", "A.cpp");
                File.WriteAllText(a, "int a;");
                Environment.SetEnvironmentVariable("UNREALSENSE_CACHE", Path.Combine(root, "Cache"));
                var project = UnrealProject.Load(Path.Combine(game, "Game.uproject"));

                // UBT's output as versions with the clangd index left it, newer than the build rules.
                var legacy = Path.Combine(CompileDatabase.GetProjectCacheDirectory(project), "clangd", "ubt", "compile_commands.json");
                Directory.CreateDirectory(Path.GetDirectoryName(legacy));
                File.WriteAllText(legacy, "[{\"file\":\"" + a.Replace('\\', '/') + "\",\"directory\":\"C:/eng\",\"command\":\"cl.exe /DX=1 a.cpp\"}]");
                File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(1));

                var logged = new System.Collections.Generic.List<string>();
                var files = CompileDatabase.Ensure(project, logged.Add); // no engine: it would throw if UBT had to run
                Assert.Equal(Path.Combine(CompileDatabase.GetOutputDirectory(project), CompileDatabase.FilesVariant), files);
                Assert.Contains("A.cpp", File.ReadAllText(files));
                Assert.Contains(logged, l => l.Contains("old clangd folder"));
                Assert.False(CompileDatabase.IsStale(project, out _));

                // A new source file of the project is not in the database: UBT has to run again.
                File.WriteAllText(Path.Combine(game, "Source", "Game", "B.cpp"), "int b;");
                Assert.True(CompileDatabase.IsStale(project, out var reason));
                Assert.Contains("B.cpp", reason);
            }
            finally
            {
                Environment.SetEnvironmentVariable("UNREALSENSE_CACHE", previousCache);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Theory]
        [InlineData("/IC:/inc dir")]
        [InlineData("/DX=\"q\"")]
        [InlineData("C:\\path with space\\")]
        [InlineData("/std:c++20")]
        public void QuotedArgumentsRoundTrip(string arg)
        {
            Assert.Equal(new[] { arg }, CompileDatabase.SplitCommandLine(CompileDatabase.QuoteArgument(arg)));
        }

        [Theory]
        [InlineData("Weapon->GetMesh()->Attach();", 8, 15, ReferenceKind.Call)]
        [InlineData("Health = 10;", 0, 6, ReferenceKind.Write)]
        [InlineData("Health += Amount;", 0, 6, ReferenceKind.Write)]
        [InlineData("++Count;", 2, 7, ReferenceKind.Write)]
        [InlineData("if (Health == 0)", 4, 10, ReferenceKind.Read)]
        [InlineData("auto X = Cast<AFoo>(Y);", 9, 13, ReferenceKind.Call)]
        public void ClassifiesReferences(string line, int column, int end, ReferenceKind expected)
        {
            Assert.Equal(expected, ReferenceClassifier.Classify("C:/a.cpp", line, column, end, false));
        }

        [Fact]
        public void GeneratedFilesAreLabelled()
        {
            Assert.Equal(ReferenceKind.Generated, ReferenceClassifier.Classify("C:/x/Foo.gen.cpp", "P_THIS->Get();", 8, 11, false));
        }
    }
}
