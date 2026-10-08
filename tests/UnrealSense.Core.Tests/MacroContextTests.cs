using UnrealSense.Cpp;
using UnrealSense.Reflection;
using Xunit;

namespace UnrealSense.Tests
{
    public class MacroContextTests
    {
        /// <summary>'|' marks the caret.</summary>
        static MacroContext At(string textWithCaret)
        {
            int caret = textWithCaret.IndexOf('|');
            return MacroContext.Find(textWithCaret.Remove(caret, 1), caret);
        }

        [Fact]
        public void KeyContextWithPrefix()
        {
            var c = At("class A {\n UPROPERTY(EditAnywhere, Blue|");
            Assert.NotNull(c);
            Assert.Equal("UPROPERTY", c.MacroName);
            Assert.Equal(SpecifierTarget.Property, c.Target);
            Assert.False(c.InMeta);
            Assert.False(c.IsValue);
            Assert.Equal(4, c.PrefixLength);
            Assert.Contains("EditAnywhere", c.UsedKeys);
        }

        [Fact]
        public void EmptyKeyContextAfterParen()
        {
            var c = At("UFUNCTION(|)");
            Assert.NotNull(c);
            Assert.Equal("UFUNCTION", c.MacroName);
            Assert.Equal(0, c.PrefixLength);
        }

        [Fact]
        public void MetaContext()
        {
            var c = At("UPROPERTY(EditAnywhere, meta = (ClampMin = 0, Ui|))");
            Assert.NotNull(c);
            Assert.True(c.InMeta);
            Assert.Equal("UPROPERTY", c.MacroName);
            Assert.Contains("ClampMin", c.UsedKeys);
            Assert.Equal(2, c.PrefixLength);
        }

        [Fact]
        public void ValueContextInString()
        {
            var c = At("UPROPERTY(EditAnywhere, Category = \"Comb|");
            Assert.NotNull(c);
            Assert.True(c.IsValue);
            Assert.True(c.InString);
            Assert.Equal("Category", c.Key);
            Assert.Equal(4, c.PrefixLength);
        }

        [Fact]
        public void ValueContextIdentifier()
        {
            var c = At("UPROPERTY(ReplicatedUsing = OnRep_|)");
            Assert.NotNull(c);
            Assert.True(c.IsValue);
            Assert.Equal("ReplicatedUsing", c.Key);
            Assert.Equal("OnRep_".Length, c.PrefixLength);
        }

        [Fact]
        public void NoContextOutsideMacros()
        {
            Assert.Null(At("void Foo(int |)"));
            Assert.Null(At("UPROPERTY(EditAnywhere)\n float |"));
            Assert.Null(At("UPROPERTY(EditAnywhere |"));
            Assert.Null(At("// UPROPERTY(|"));
        }
    }
}
