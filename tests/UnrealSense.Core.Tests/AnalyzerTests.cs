using System.Collections.Generic;
using System.Linq;
using UnrealSense.Analysis;
using UnrealSense.Cpp;
using UnrealSense.Reflection;
using Xunit;

namespace UnrealSense.Tests
{
    public class AnalyzerTests
    {
        static List<Diagnostic> Analyze(string text, string path = @"C:\P\Source\Game\Public\Thing.h")
        {
            var file = HeaderParser.Parse(text, path);
            return HeaderAnalyzer.Analyze(file, new AnalysisContext { Catalog = SpecifierCatalog.CreateBuiltin() });
        }

        static string Apply(string text, CodeFix fix)
        {
            foreach (var edit in fix.Edits.Where(e => e.FilePath == null).OrderByDescending(e => e.Start))
                text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
            return text;
        }

        [Fact]
        public void MissingGeneratedIncludeWithFix()
        {
            const string text = "#pragma once\n#include \"CoreMinimal.h\"\n\nUCLASS()\nclass UThing : public UObject\n{\n\tGENERATED_BODY()\n};\n";
            var d = Assert.Single(Analyze(text), x => x.Id == HeaderAnalyzer.MissingGeneratedInclude);
            var fixedText = Apply(text, d.Fixes[0]);
            Assert.Contains("#include \"CoreMinimal.h\"\n#include \"Thing.generated.h\"", fixedText);
            Assert.DoesNotContain(Analyze(fixedText), x => x.Id == HeaderAnalyzer.MissingGeneratedInclude);
        }

        [Fact]
        public void GeneratedIncludeMustBeLast()
        {
            const string text = "#include \"Thing.generated.h\"\n#include \"Other.h\"\nUCLASS()\nclass UThing : public UObject\n{\n\tGENERATED_BODY()\n};\n";
            var d = Assert.Single(Analyze(text), x => x.Id == HeaderAnalyzer.GeneratedIncludeNotLast);
            var fixedText = Apply(text, d.Fixes[0]);
            Assert.StartsWith("#include \"Other.h\"\n#include \"Thing.generated.h\"", fixedText);
            Assert.DoesNotContain(Analyze(fixedText), x => x.Severity == DiagnosticSeverity.Error);
        }

        [Fact]
        public void MissingGeneratedBodyAndWrongPrefix()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass Thing : public AActor\n{\n\tint32 X;\n};\n";
            var diagnostics = Analyze(text);
            Assert.Contains(diagnostics, x => x.Id == HeaderAnalyzer.MissingGeneratedBody);
            Assert.Contains(diagnostics, x => x.Id == HeaderAnalyzer.WrongPrefix && x.Message.Contains("'A'"));
        }

        [Fact]
        public void PrivateBlueprintPropertyNeedsAllowPrivateAccess()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass AThing : public AActor\n{\n\tGENERATED_BODY()\n\tUPROPERTY(EditAnywhere, BlueprintReadWrite)\n\tint32 Value;\n};\n";
            var d = Assert.Single(Analyze(text), x => x.Id == HeaderAnalyzer.PrivateBlueprintAccess);
            var fixedText = Apply(text, d.Fixes[0]);
            Assert.Contains("UPROPERTY(EditAnywhere, BlueprintReadWrite, meta = (AllowPrivateAccess = \"true\"))", fixedText);
            Assert.DoesNotContain(Analyze(fixedText), x => x.Id == HeaderAnalyzer.PrivateBlueprintAccess);
        }

        [Fact]
        public void ConflictingSpecifiersWithRemoveFix()
        {
            const string text = "#include \"Thing.generated.h\"\nUSTRUCT()\nstruct FThing\n{\n\tGENERATED_BODY()\n\tUPROPERTY(EditAnywhere, VisibleAnywhere, BlueprintReadOnly, BlueprintReadWrite)\n\tint32 Value;\n};\n";
            var diagnostics = Analyze(text).Where(x => x.Id == HeaderAnalyzer.ConflictingSpecifiers).ToList();
            Assert.Equal(2, diagnostics.Count);
            var fixedText = Apply(text, diagnostics[0].Fixes[0]);
            Assert.Contains("UPROPERTY(EditAnywhere, BlueprintReadOnly, BlueprintReadWrite)", fixedText);
        }

        [Fact]
        public void BlueprintPureNeedsOutput()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass UThing : public UObject\n{\n\tGENERATED_BODY()\npublic:\n\tUFUNCTION(BlueprintPure)\n\tvoid NoOutput() const;\n\tUFUNCTION(BlueprintPure)\n\tvoid WithOutput(int32& Out) const;\n};\n";
            var d = Assert.Single(Analyze(text), x => x.Id == HeaderAnalyzer.PureWithoutOutput);
            Assert.Contains("BlueprintCallable", Apply(text, d.Fixes[0]));
        }

        [Fact]
        public void FunctionInStructIsAnError()
        {
            const string text = "#include \"Thing.generated.h\"\nUSTRUCT()\nstruct FThing\n{\n\tGENERATED_BODY()\n\tUFUNCTION()\n\tvoid Nope();\n};\n";
            Assert.Contains(Analyze(text), x => x.Id == HeaderAnalyzer.FunctionInStruct);
        }

        [Fact]
        public void RepNotifyMustExist()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass AThing : public AActor\n{\n\tGENERATED_BODY()\npublic:\n\tUPROPERTY(ReplicatedUsing = OnRep_Health)\n\tfloat Health;\n};\n";
            var diagnostics = Analyze(text);
            var d = Assert.Single(diagnostics, x => x.Id == HeaderAnalyzer.MissingRepNotify);
            Assert.Contains(diagnostics, x => x.Id == HeaderAnalyzer.MissingLifetimeProps);
            var fixedText = Apply(text, d.Fixes[0]);
            Assert.Contains("UFUNCTION()\n\tvoid OnRep_Health();", fixedText);
            Assert.DoesNotContain(Analyze(fixedText), x => x.Id == HeaderAnalyzer.MissingRepNotify);
        }

        [Fact]
        public void RawPointerSuggestsObjectPtr()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass AThing : public AActor\n{\n\tGENERATED_BODY()\n\tUPROPERTY()\n\tUStaticMeshComponent* Mesh;\n};\n";
            var d = Assert.Single(Analyze(text), x => x.Id == HeaderAnalyzer.RawObjectPointer);
            Assert.Contains("TObjectPtr<UStaticMeshComponent> Mesh;", Apply(text, d.Fixes[0]));
        }

        [Fact]
        public void SpecifierRequiringValue()
        {
            const string text = "#include \"Thing.generated.h\"\nUCLASS()\nclass AThing : public AActor\n{\n\tGENERATED_BODY()\n\tUPROPERTY(EditAnywhere, Category)\n\tint32 X;\n};\n";
            Assert.Contains(Analyze(text), x => x.Id == HeaderAnalyzer.MissingSpecifierValue);
        }

        [Fact]
        public void BuildsImplementationStub()
        {
            var file = HeaderParser.Parse("UCLASS()\nclass AThing : public AActor\n{\n\tGENERATED_BODY()\n\tUFUNCTION(Server, Reliable, WithValidation)\n\tvoid Fire(const FVector& At, int32 Count);\n};");
            var type = file.Types[0];
            var stub = HeaderAnalyzer.BuildStub(type, type.Functions[0], "Fire_Validate", validate: true);
            Assert.Contains("bool AThing::Fire_Validate(const FVector& At, int32 Count)", stub);
            Assert.Contains("return true;", stub);
        }
    }
}
