using System;
using System.IO;
using System.Linq;
using UnrealSense.Clang;
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
        public void GroupsModuleSourcesIntoUnityUnits()
        {
            var dir = Path.Combine(Path.GetTempPath(), "us-unity-" + Guid.NewGuid().ToString("N"));
            try
            {
                string Make(string relative, string text = "int x;")
                {
                    var path = Path.Combine(dir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, text);
                    return path.Replace('\\', '/');
                }
                Make("Game/Game.Build.cs", "public class Game : ModuleRules { }");
                Make("NoUnity/NoUnity.Build.cs", "public class NoUnity : ModuleRules { public NoUnity() { bUseUnity = false; } }");
                var sources = new[] { Make("Game/Private/A.cpp"), Make("Game/Private/B.cpp"), Make("Game/Private/Sub/C.cpp"), Make("NoUnity/Private/D.cpp"), Make("NoUnity/Private/E.cpp") };
                var entries = new Newtonsoft.Json.Linq.JArray(sources.Select(f => new Newtonsoft.Json.Linq.JObject
                {
                    ["directory"] = dir,
                    ["file"] = f,
                    ["arguments"] = new Newtonsoft.Json.Linq.JArray("cl.exe", "/w", f, "/I" + (f.Contains("/Game/") ? "game" : "nounity")),
                }));
                // Engine style: sources relative to Engine/Source, plus a per-file argument that must not split the group.
                Make("Engine/Source/Runtime/Core/Core.Build.cs", "public class Core : ModuleRules { }");
                foreach (var name in new[] { "X", "Y" })
                {
                    var absolute = Make($"Engine/Source/Runtime/Core/Private/{name}.cpp");
                    entries.Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["directory"] = Path.Combine(dir, "Engine", "Source").Replace('\\', '/'),
                        ["file"] = absolute,
                        ["arguments"] = new Newtonsoft.Json.Linq.JArray("cl.exe", "/w", $"Runtime/Core/Private/{name}.cpp", "/Icore", $"/Fd:{name}.cpp.pdb"),
                    });
                }
                var files = Path.Combine(dir, "files.json");
                File.WriteAllText(files, entries.ToString());

                var stats = CompileDatabase.BuildUnity(files, Path.Combine(dir, "unity.json"), Path.Combine(dir, "unity"), null);
                Assert.Equal(3, stats.Units); // Game, Core and the bUseUnity = false module as one small unit each
                Assert.Equal(7, stats.Grouped);
                Assert.Equal(2, stats.GroupedOptOut);

                var db = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dir, "unity.json")));
                string Flags(Newtonsoft.Json.Linq.JToken e) =>
                    File.ReadAllText(((string)e["arguments"].Single(a => ((string)a).StartsWith("@"))).Substring(1));
                var core = db.Single(e => ((string)e["file"]).Contains("/unity/Core-"));
                Assert.DoesNotContain(core["arguments"], a => ((string)a).Contains(".pdb"));
                Assert.DoesNotContain(".pdb", Flags(core));
                var unity = db.Single(e => ((string)e["file"]).Contains("/unity/Game-"));
                var unityFile = (string)unity["file"];
                Assert.Equal(unityFile, (string)unity["arguments"].Last());
                Assert.Equal("/w", (string)unity["arguments"][1]);
                Assert.Contains("/Igame", Flags(unity));
                var text = File.ReadAllText(unityFile);
                Assert.Contains("A.cpp", text);
                Assert.Contains("Sub/C.cpp", text);

                // Project-only variant via the include filter.
                Assert.Equal(1, CompileDatabase.BuildUnity(files, Path.Combine(dir, "game.json"), Path.Combine(dir, "unity"), f => f.Contains("/Game/")).Units);

                // A header borrows the command of a source of its module, with itself as the main file.
                var index = CompileCommandIndex.Load(files);
                var header = Path.Combine(dir, "Game", "Public", "A.h");
                var command = index.Find(header);
                Assert.NotNull(command);
                Assert.Equal(header.Replace('\\', '/'), command.Arguments[2]);
                Assert.Equal("/Igame", command.Arguments[3]);
                Assert.Equal(sources[3], index.Find(sources[3]).Arguments[2]);
                // Files outside any module (the generated unity files) never borrow flags: clangd must use its database.
                Assert.Null(index.Find(Path.Combine(dir, "unity", "Game-0.cpp")));
                Assert.Equal("Runtime/Core/Private/X.cpp", index.Find(Path.Combine(dir, "Engine/Source/Runtime/Core/Private/X.cpp")).Arguments[2]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void MergesModulesWithTheSameOptions()
        {
            var dir = Path.Combine(Path.GetTempPath(), "us-merge-" + Guid.NewGuid().ToString("N"));
            try
            {
                var entries = new Newtonsoft.Json.Linq.JArray();
                foreach (var module in new[] { "Alpha", "Beta" })
                {
                    var moduleDir = Path.Combine(dir, module);
                    Directory.CreateDirectory(Path.Combine(moduleDir, "Private"));
                    File.WriteAllText(Path.Combine(moduleDir, module + ".Build.cs"), "class X : ModuleRules { }");
                    var shared = Path.Combine(dir, module + ".Shared.rsp");
                    File.WriteAllText(shared, $"/I \"{module}/Public\"\n/I \"Core/Public\"\n/DWITH_{module.ToUpperInvariant()}=1\n/std:c++20");
                    foreach (var name in new[] { "One", "Two" })
                    {
                        var source = Path.Combine(moduleDir, "Private", module + name + ".cpp").Replace('\\', '/');
                        File.WriteAllText(source, "int x;");
                        entries.Add(new Newtonsoft.Json.Linq.JObject
                        {
                            ["directory"] = dir.Replace('\\', '/'),
                            ["file"] = source,
                            ["arguments"] = new Newtonsoft.Json.Linq.JArray("cl.exe", "/w", source, "@" + shared.Replace('\\', '/'), $"/FI{module}/Definitions.h"),
                        });
                    }
                }
                var files = Path.Combine(dir, "files.json");
                File.WriteAllText(files, entries.ToString());

                var merged = CompileDatabase.BuildUnity(files, Path.Combine(dir, "merged.json"), Path.Combine(dir, "unity"), null);
                Assert.Equal(1, merged.Units); // two modules, same options: one translation unit
                var unit = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(dir, "merged.json"))).Single();
                Assert.Equal(new[] { "cl.exe", "/w", "/std:c++20" }, unit["arguments"].Take(3).Select(a => (string)a));
                var flags = CompileDatabase.SplitCommandLine(File.ReadAllText(((string)unit["arguments"][3]).Substring(1)));
                Assert.Equal(new[] { "/IAlpha/Public", "/ICore/Public", "/IBeta/Public", "/DWITH_ALPHA=1", "/DWITH_BETA=1", "/FIAlpha/Definitions.h", "/FIBeta/Definitions.h" }, flags);
                Assert.Equal(4, File.ReadAllText((string)unit["file"]).Split('\n').Count(l => l.StartsWith("#include")));

                var separate = CompileDatabase.BuildUnity(files, Path.Combine(dir, "separate.json"), Path.Combine(dir, "unity2"), null, acrossModules: false);
                Assert.Equal(2, separate.Units);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void MergesEngineModulesWithRelativeSharedResponseFiles()
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

                var stats = CompileDatabase.BuildUnity(files, Path.Combine(root, "db", "unity.json"), Path.Combine(root, "db", "unity"), null);
                Assert.Equal(1, stats.OptionSets);
                Assert.Equal(1, stats.Units); // Core and Engine share one translation unit
                var unit = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(root, "db", "unity.json"))).Single();
                var flags = CompileDatabase.SplitCommandLine(File.ReadAllText(((string)unit["arguments"].Single(a => ((string)a).StartsWith("@"))).Substring(1)));
                Assert.Contains("/IRuntime/Core/Public", flags);
                Assert.Contains("/IRuntime/Engine/Public", flags);
                // Both Definitions.h, then the header that makes every *_API macro empty (last, so it wins).
                Assert.Equal(3, flags.Count(f => f.StartsWith("/FI")));
                var api = flags.Last(f => f.StartsWith("/FI")).Substring(3);
                Assert.EndsWith(".api.h", api);
                var apiText = File.ReadAllText(api);
                Assert.Contains("#undef CORE_API\n#define CORE_API\n", apiText);
                Assert.Contains("#undef ENGINE_API\n#define ENGINE_API\n", apiText);
                // Switches tested with #if keep their value (emptying them broke every unit with generated code).
                Assert.DoesNotContain("UE_VALIDATE_INTERNAL_API", apiText);
                Assert.DoesNotContain("UE_VALIDATE_EXPERIMENTAL_API", apiText);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void WritesIndexFixupsOnlyForAngelScriptEngines()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-fixups-" + Guid.NewGuid().ToString("N"));
            try
            {
                var engine = Path.Combine(root, "Engine");
                var coreNative = Path.Combine(engine, "Source", "Runtime", "CoreUObject", "Public", "UObject", "CoreNative.h");
                Directory.CreateDirectory(Path.GetDirectoryName(coreNative));
                var output = Path.Combine(root, "db");
                File.WriteAllText(coreNative,
                    "#define ERASE_METHOD_PTR(c, m, p, r) ASAutoCaller::GetReflectedFunctionPointers(&c::m)\n#define ERASE_NO_FUNCTION() {}\n");

                var path = CompileDatabase.WriteIndexFixups(output, engine);
                Assert.Equal(Path.Combine(output, CompileDatabase.FixupsFile), path);
                Assert.Contains("#define ERASE_METHOD_PTR(c, m, p, r) ((void)static_cast<r(c::*)p>(&c::m), ERASE_NO_FUNCTION())", File.ReadAllText(path));

                // A stock engine needs none: no file, and a stale one from an earlier engine is removed.
                File.WriteAllText(coreNative, "#pragma once\n");
                Assert.Null(CompileDatabase.WriteIndexFixups(output, engine));
                Assert.False(File.Exists(path));
                Assert.Null(CompileDatabase.WriteIndexFixups(output, null));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void ForcesIndexFixupsAfterThePrecompiledHeader()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-fixupsdb-" + Guid.NewGuid().ToString("N"));
            try
            {
                var source = Path.Combine(root, "Game", "Source", "Game");
                Directory.CreateDirectory(Path.Combine(source, "Private"));
                File.WriteAllText(Path.Combine(source, "Game.Build.cs"), "class X : ModuleRules { }");
                var entries = new Newtonsoft.Json.Linq.JArray();
                foreach (var name in new[] { "A.cpp", "B.cpp", "C.c", "NoPch.cpp" })
                {
                    var file = Path.Combine(source, "Private", name).Replace('\\', '/');
                    File.WriteAllText(file, "int x;");
                    entries.Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["directory"] = root.Replace('\\', '/'),
                        ["file"] = file,
                        ["arguments"] = new Newtonsoft.Json.Linq.JArray("cl.exe", file, "/IGame/Public", "/FIGame/Definitions.h"),
                    });
                }
                var raw = Path.Combine(root, "raw.json");
                File.WriteAllText(raw, entries.ToString());
                var files = Path.Combine(root, "db", "files.json");
                Directory.CreateDirectory(Path.GetDirectoryName(files));
                var fixups = Path.Combine(root, "db", CompileDatabase.FixupsFile).Replace('\\', '/');
                const string pch = "C:/UE/Engine/SharedPCH.Engine.h";
                CompileDatabase.Sanitize(raw, new[] { new CompileDatabase.DatabaseVariant(files, _ => true) },
                    f => f.EndsWith("NoPch.cpp") ? null : pch, fixups);

                var byFile = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(files))
                    .ToDictionary(e => Path.GetFileName((string)e["file"]), e => e["arguments"].Select(a => (string)a).ToList());
                System.Collections.Generic.List<string> Forced(string name) => byFile[name].Where(a => a.StartsWith("/FI")).ToList();
                Assert.Equal(new[] { "/FIGame/Definitions.h", "/FI" + pch, "/FI" + fixups }, Forced("A.cpp"));
                Assert.Equal(new[] { "/FIGame/Definitions.h", "/FI" + fixups }, Forced("NoPch.cpp"));
                Assert.Equal(new[] { "/FIGame/Definitions.h", "/FI" + pch }, Forced("C.c"));

                // Unity entry: options, shared flags, PCH, fixups, then the unity source.
                CompileDatabase.BuildUnity(files, Path.Combine(root, "db", "unity.json"), Path.Combine(root, "db", "unity"), null);
                var unit = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(root, "db", "unity.json")))
                    .Select(e => e["arguments"].Select(a => (string)a).ToList())
                    .Single(a => a.Contains("/FI" + pch) && a.Contains("/FI" + fixups));
                int n = unit.Count;
                Assert.StartsWith("@", unit[n - 4]);
                Assert.Equal("/FI" + pch, unit[n - 3]);
                Assert.Equal("/FI" + fixups, unit[n - 2]);
                Assert.EndsWith(".cpp", unit[n - 1]);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Theory]
        [InlineData(19, 4)]
        [InlineData(3, 2)]
        [InlineData(0, int.MaxValue)]
        public void IndexingThreadsFitTheAvailableMemory(double availableGb, int threads) =>
            Assert.Equal(threads, ClangdClient.ByAvailableMemory(availableGb));

        [Fact]
        public void FullDatabaseKeepsTheProjectUnitsUnchanged()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-variants-" + Guid.NewGuid().ToString("N"));
            try
            {
                var entries = new Newtonsoft.Json.Linq.JArray();
                foreach (var (folder, count) in new[] { ("Game", 3), ("Engine", 5) })
                {
                    var dir = Path.Combine(root, folder, "Source", folder);
                    Directory.CreateDirectory(Path.Combine(dir, "Private"));
                    File.WriteAllText(Path.Combine(dir, folder + ".Build.cs"), "class X : ModuleRules { }");
                    for (int i = 0; i < count; i++)
                    {
                        var cpp = Path.Combine(dir, "Private", $"{folder}{i}.cpp").Replace('\\', '/');
                        File.WriteAllText(cpp, "int x;");
                        // Same options for project and engine: per-module grouping alone would mix them.
                        entries.Add(new Newtonsoft.Json.Linq.JObject
                        {
                            ["directory"] = root.Replace('\\', '/'),
                            ["file"] = cpp,
                            ["arguments"] = new Newtonsoft.Json.Linq.JArray("cl.exe", "/w", cpp, "/I" + folder),
                        });
                    }
                }
                var files = Path.Combine(root, "files.json");
                File.WriteAllText(files, entries.ToString());
                var output = Path.Combine(root, "db");
                Directory.CreateDirectory(output);
                // Exact mode: project sources one per translation unit, engine sources in unity units.
                var (project, engine) = CompileDatabase.WriteVariants(files, output, Path.Combine(output, "unity"), f => f.Contains("/Game/"), groupProject: false);
                Assert.Equal(3, project.Units);
                Assert.Equal(1, engine.Units);

                var projectDb = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(output, CompileDatabase.ProjectVariant)));
                var fullDb = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(output, CompileDatabase.FullVariant)));
                Assert.Equal(4, fullDb.Count);
                Assert.All(projectDb, u => Assert.DoesNotContain("/unity/", (string)u["file"]));
                // Every project unit is in the full database with the same file and arguments.
                foreach (var unit in projectDb)
                    Assert.Contains(fullDb, u => Newtonsoft.Json.Linq.JToken.DeepEquals(u, unit));
                var engineUnity = File.ReadAllText((string)fullDb.Single(u => ((string)u["file"]).Contains("/unity/"))["file"]);
                Assert.Contains("Engine0.cpp", engineUnity);
                Assert.DoesNotContain("Game0.cpp", engineUnity);

                // With project grouping on, project units are unity files distinct from the engine's (no name clash).
                var grouped = Path.Combine(root, "grouped");
                Directory.CreateDirectory(grouped);
                CompileDatabase.WriteVariants(files, grouped, Path.Combine(grouped, "unity"), f => f.Contains("/Game/"), groupProject: true);
                var groupedFull = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(grouped, CompileDatabase.FullVariant)));
                Assert.Equal(2, groupedFull.Select(u => (string)u["file"]).Distinct().Count());
                Assert.DoesNotContain("Engine0.cpp", File.ReadAllText((string)Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(Path.Combine(grouped, CompileDatabase.ProjectVariant)))[0]["file"]));
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
        public void GivesClangdRealPathsAndTheUserTheirOwn()
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

                var canonical = UnrealSense.Clang.ClangdClient.CanonicalPath(Path.Combine(view, "source", "actor.cpp"));
                Assert.Equal(Path.Combine(real, "Source", "Actor.cpp"), canonical, ignoreCase: true);
                Assert.EndsWith(@"Source\Actor.cpp", canonical);   // case as on disk
                // Paths coming back from clangd are given back under the folder the user works with.
                Assert.Equal(Path.Combine(view, "Source", "Other.h"), UnrealSense.Clang.ClangdClient.ToViewPath(Path.Combine(real, "Source", "Other.h")), ignoreCase: true);
            }
            finally
            {
                try { Directory.Delete(Path.Combine(root, "S")); } catch (IOException) { }
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void ReplacesIncludeDirectoriesWithHeaderMaps()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-hmap-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Public", "GameFramework"));
                File.WriteAllText(Path.Combine(root, "Public", "GameFramework", "Actor.h"), "");
                File.WriteAllText(Path.Combine(root, "Public", "Engine.h"), "");
                File.WriteAllText(Path.Combine(root, "Public", "Notes.txt"), "");
                Directory.CreateDirectory(Path.Combine(root, "Empty"));
                var maps = new HeaderMaps(Path.Combine(root, "maps"));
                var flags = maps.Rewrite(new[] { "/IPublic", "/IMissing", "/IEmpty", "/imsvcC:/VC/include", "/DX=1" }, root);

                // In place of each existing directory (search order kept), the directories as a fallback after the toolchain's.
                var dir = root.Replace('\\', '/');
                Assert.Equal(7, flags.Count);
                Assert.EndsWith("-Public.hmap", flags[0]);
                Assert.EndsWith("-Empty.hmap", flags[1]);
                Assert.Equal(new[] { "/imsvcC:/VC/include", "/DX=1", "/imsvc" + dir + "/Public", "/imsvc" + dir + "/Missing", "/imsvc" + dir + "/Empty" }, flags.Skip(2));

                // Lookup exactly as clang's HeaderMapImpl::lookupFilename does it.
                var map = File.ReadAllBytes(flags[0].Substring(2));
                string Lookup(string name)
                {
                    Assert.Equal(0x686D6170u, BitConverter.ToUInt32(map, 0));
                    uint strings = BitConverter.ToUInt32(map, 8), buckets = BitConverter.ToUInt32(map, 16);
                    string Str(uint offset) { int start = (int)(strings + offset), end = Array.IndexOf(map, (byte)0, start); return System.Text.Encoding.ASCII.GetString(map, start, end - start); }
                    for (uint bucket = HeaderMaps.Hash(name); ; bucket++)
                    {
                        int at = 24 + 12 * (int)(bucket & (buckets - 1));
                        uint key = BitConverter.ToUInt32(map, at);
                        if (key == 0) return null;
                        if (string.Equals(Str(key), name, StringComparison.OrdinalIgnoreCase))
                            return Str(BitConverter.ToUInt32(map, at + 4)) + Str(BitConverter.ToUInt32(map, at + 8));
                    }
                }
                Assert.Equal(dir + "/Public/GameFramework/Actor.h", Lookup("gameframework/actor.h"));
                Assert.Equal(dir + "/Public/Engine.h", Lookup("Engine.h"));
                Assert.Null(Lookup("Notes.txt"));
                Assert.Null(Lookup("Actor.h"));

                // A header added later is picked up by Refresh.
                maps.SaveManifest();
                File.WriteAllText(Path.Combine(root, "Public", "New.h"), "");
                Assert.Equal(1, HeaderMaps.Refresh(Path.Combine(root, "maps")));
                map = File.ReadAllBytes(flags[0].Substring(2));
                Assert.Equal(dir + "/Public/New.h", Lookup("New.h"));
                Assert.Equal(0, HeaderMaps.Refresh(Path.Combine(root, "maps")));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void PicksThePrecompiledHeaderUbtWouldUse()
        {
            var root = Path.Combine(Path.GetTempPath(), "us-pch-" + Guid.NewGuid().ToString("N"));
            try
            {
                string Module(string dir, string name, string body, params string[] headers)
                {
                    var path = Path.Combine(root, dir, name);
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, name + ".Build.cs"), $"public class {name} : ModuleRules {{ public {name}(ReadOnlyTargetRules T) : base(T) {{ {body} }} }}");
                    foreach (var h in headers) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, h))); File.WriteAllText(Path.Combine(path, h), "#pragma once"); }
                    Directory.CreateDirectory(Path.Combine(path, "Private"));
                    var cpp = Path.Combine(path, "Private", name + ".cpp");
                    File.WriteAllText(cpp, "");
                    return cpp;
                }
                Module("Engine/Source/Runtime", "Core", "PrivatePCHHeaderFile = \"Private/CorePrivatePCH.h\"; SharedPCHHeaderFile = \"Public/CoreSharedPCH.h\";",
                    "Private/CorePrivatePCH.h", "Public/CoreSharedPCH.h");
                var engine = Module("Engine/Source/Runtime", "Engine",
                    "PublicDependencyModuleNames.AddRange(new string[] { \"Core\" }); if (Target.bBuildEditor) { PrivateDependencyModuleNames.Add(\"UnrealEd\"); } " +
                    "PrivatePCHHeaderFile = \"Private/EnginePrivatePCH.h\"; SharedPCHHeaderFile = \"Public/EngineSharedPCH.h\";",
                    "Private/EnginePrivatePCH.h", "Public/EngineSharedPCH.h");
                Module("Engine/Source/Editor", "UnrealEd", "PublicDependencyModuleNames.Add(\"Engine\"); SharedPCHHeaderFile = \"Public/UnrealEdSharedPCH.h\";",
                    "Public/UnrealEdSharedPCH.h");
                var game = Module("Game/Source", "Game", "PublicDependencyModuleNames.AddRange(new string[] { \"Core\", \"Engine\" });");
                var editor = Module("Game/Source", "GameEditor", "PrivateDependencyModuleNames.AddRange(new string[] { \"Game\", \"UnrealEd\" });");
                var noPch = Module("Game/Source", "NoPch", "PCHUsage = ModuleRules.PCHUsageMode.NoPCHs; PublicDependencyModuleNames.Add(\"Engine\");");
                var own = Module("Game/Source", "Own", "PrivatePCHHeaderFile = \"Private/OwnPCH.h\"; PublicDependencyModuleNames.Add(\"Engine\");", "Private/OwnPCH.h");
                var generated = Path.Combine(root, "Game", "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game", "Module.Game.gen.cpp");

                var resolver = new PchResolver(new[] { Path.Combine(root, "Engine", "Source"), Path.Combine(root, "Game", "Source") });
                string Pch(string file) => Path.GetFileName(resolver.Find(file) ?? "(none)");
                Assert.Equal("EngineSharedPCH.h", Pch(game));        // Engine's private dependency on UnrealEd is not visible to Game
                Assert.Equal("UnrealEdSharedPCH.h", Pch(editor));    // the largest shared PCH among its dependencies
                Assert.Equal("(none)", Pch(noPch));
                Assert.Equal("OwnPCH.h", Pch(own));                  // a private PCH wins
                Assert.Equal("EnginePrivatePCH.h", Pch(engine));     // a shared-PCH provider builds with its private PCH
                Assert.Equal("EngineSharedPCH.h", Pch(generated));   // generated code belongs to the module named in its path
            }
            finally
            {
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
        public void MovesToolchainHeadersBehindClangIntrinsics()
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

        /// <summary>Opt-in (slow): UNREALSENSE_CLANGD_TESTS=1 runs clangd on the Gym project.</summary>
        [Fact]
        public void ClangdFindsOnlyTheRightOverload()
        {
            if (Environment.GetEnvironmentVariable("UNREALSENSE_CLANGD_TESTS") != "1") return;
            var uproject = @"C:\Unreal Project\Gym\Gym.uproject";
            var clangdPath = ClangdClient.FindClangd();
            if (!File.Exists(uproject) || clangdPath == null) return;

            var project = UnrealProject.Load(uproject);
            var db = CompileDatabase.GetOutputDirectory(project);
            if (!File.Exists(Path.Combine(db, "compile_commands.json"))) CompileDatabase.Generate(project, null);
            using var clangd = new ClangdClient();
            clangd.StartAsync(clangdPath, db, project.ProjectDirectory).GetAwaiter().GetResult();
            var header = @"C:\Unreal Project\Gym\Source\Gym\Public\Variant_Shooter\Weapons\ShooterWeapon.h";
            clangd.SyncDocumentAsync(header, File.ReadAllText(header)).GetAwaiter().GetResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (clangd.State != ClangdState.Indexing && sw.Elapsed.TotalSeconds < 20) System.Threading.Thread.Sleep(100);
            while (clangd.State == ClangdState.Indexing && sw.Elapsed.TotalMinutes < 20) System.Threading.Thread.Sleep(250);

            // AShooterWeapon::GetFirstPersonMesh (line 163) — AGymCharacter has a method with the same name.
            var refs = clangd.FindReferencesAsync(header, 162, 26, includeDeclaration: true).GetAwaiter().GetResult();
            var shooterCharacter = refs.Where(r => r.FilePath.EndsWith("ShooterCharacter.cpp")).ToList();
            Assert.Single(shooterCharacter);              // only Weapon->GetFirstPersonMesh(), not the character's own
            Assert.Equal(177, shooterCharacter[0].Line);
            Assert.Equal(9, shooterCharacter[0].Column);
            Assert.Contains(refs, r => r.FilePath.EndsWith("ShooterNPC.cpp"));
        }
    }
}
