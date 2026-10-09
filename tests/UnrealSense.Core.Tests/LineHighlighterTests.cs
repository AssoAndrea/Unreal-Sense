using System.Collections.Generic;
using System.Linq;
using UnrealSense.Cpp;
using Xunit;

namespace UnrealSense.Tests
{
    public class LineHighlighterTests
    {
        /// <summary>Text of each highlighted span with its kind.</summary>
        static List<(string Text, HighlightKind Kind)> Spans(string line, System.Func<string, bool> isKnownType = null) =>
            LineHighlighter.Highlight(line, isKnownType).Select(s => (line.Substring(s.Start, s.Length), s.Kind)).ToList();

        static HighlightKind KindOf(string line, string text) => Spans(line).First(s => s.Text == text).Kind;

        [Fact]
        public void KeywordsTypesTemplateCalls()
        {
            var line = "const auto* Settings = GetDefault<UMyMenuSettings>();";
            Assert.Equal(HighlightKind.Keyword, KindOf(line, "const"));
            Assert.Equal(HighlightKind.Keyword, KindOf(line, "auto"));
            Assert.Equal(HighlightKind.Function, KindOf(line, "GetDefault"));
            Assert.Equal(HighlightKind.Type, KindOf(line, "UMyMenuSettings"));
            Assert.DoesNotContain(Spans(line), s => s.Text == "Settings");
        }

        [Fact]
        public void MacrosStringsMembers()
        {
            var line = "const FName Dev = FName(TEXT(\"Prefix_\") + Settings->FilterData.DevName.ToString());";
            Assert.Equal(HighlightKind.Type, KindOf(line, "FName"));
            Assert.Equal(HighlightKind.Macro, KindOf(line, "TEXT"));
            Assert.Equal(HighlightKind.String, KindOf(line, "\"Prefix_\""));
            Assert.Equal(HighlightKind.Member, KindOf(line, "FilterData"));
            Assert.Equal(HighlightKind.Member, KindOf(line, "DevName"));
            Assert.Equal(HighlightKind.Function, KindOf(line, "ToString"));
        }

        [Fact]
        public void ScopesNumbersComments()
        {
            var line = "int32 X = FMath::Max(UE::Foo, 42); // clamp";
            Assert.Equal(HighlightKind.Type, KindOf(line, "int32"));
            Assert.Equal(HighlightKind.Type, KindOf(line, "FMath"));
            Assert.Equal(HighlightKind.Function, KindOf(line, "Max"));
            Assert.Equal(HighlightKind.Namespace, KindOf(line, "UE"));
            Assert.Equal(HighlightKind.Number, KindOf(line, "42"));
            Assert.Equal(HighlightKind.Comment, KindOf(line, "// clamp"));
            Assert.Equal(HighlightKind.Comment, KindOf("Foo(); /* a */ Bar();", "/* a */"));
        }

        [Fact]
        public void Directives()
        {
            Assert.Equal(new[] { ("#include", HighlightKind.Preprocessor), ("\"Foo/Bar.h\"", HighlightKind.String) }, Spans("#include \"Foo/Bar.h\""));
            Assert.Equal(new[] { ("#include", HighlightKind.Preprocessor), ("<Engine/Engine.h>", HighlightKind.String) }, Spans("#include <Engine/Engine.h>"));
            Assert.Equal(HighlightKind.Macro, KindOf("#if WITH_EDITOR", "WITH_EDITOR"));
        }

        [Fact]
        public void UnrealTypeConvention()
        {
            Assert.True(LineHighlighter.LooksLikeUnrealType("UObject"));
            Assert.True(LineHighlighter.LooksLikeUnrealType("TArray"));
            Assert.True(LineHighlighter.LooksLikeUnrealType("SButton"));
            Assert.False(LineHighlighter.LooksLikeUnrealType("IsValid"));
            Assert.False(LineHighlighter.LooksLikeUnrealType("TEXT"));
            Assert.False(LineHighlighter.LooksLikeUnrealType("Update"));
            Assert.Equal(HighlightKind.Type, Spans("Widget Foo;", t => t == "Widget").Single().Kind);
            Assert.Equal(HighlightKind.Macro, KindOf("UPROPERTY(EditAnywhere)", "UPROPERTY"));
            Assert.Empty(Spans(""));
        }
    }
}
