using System.Linq;
using UnrealSense.Cpp;
using Xunit;

namespace UnrealSense.Tests
{
    public class HeaderParserTests
    {
        const string Header = @"#pragma once
#include ""CoreMinimal.h""
#include ""GameFramework/Actor.h""
#include ""MyActor.generated.h""

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHealthChanged, float, NewHealth);
DECLARE_MULTICAST_DELEGATE(FOnNativeOnly);

UENUM(BlueprintType)
enum class EMyMode : uint8
{
    First UMETA(DisplayName = ""First mode""),
    Second
};

USTRUCT(BlueprintType)
struct FMyData
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = ""Data"")
    TMap<FName, TArray<int32>> Values;
};

/** Doc */
UCLASS(Blueprintable, meta = (DisplayName = ""My Actor""))
class MYGAME_API AMyActor final : public AActor, public IMyInterface
{
    GENERATED_BODY()

    UPROPERTY(VisibleAnywhere, meta = (AllowPrivateAccess = ""true""))
    TObjectPtr<USceneComponent> Root;

    int32 NotReflected = 0;

public:
    UPROPERTY(EditAnywhere, Category = ""Stats|Health"", meta = (ClampMin = 0, ClampMax = ""100""))
    float Health = 100.f;

    UPROPERTY(BlueprintAssignable)
    FOnHealthChanged OnHealthChanged;

    uint8 bFlag : 1;

    UFUNCTION(BlueprintCallable, Category = ""Stats"")
    void Heal(float Amount, UPARAM(ref) TArray<int32>& Out, const FVector& Where = FVector::ZeroVector);

    UFUNCTION(BlueprintPure)
    int32 GetValue() const { return 42; }

protected:
    UFUNCTION(BlueprintNativeEvent)
    void OnHit(AActor* Other);

    virtual void BeginPlay() override;
};
";

        [Fact]
        public void ParsesTypesAndHeads()
        {
            var file = HeaderParser.Parse(Header, @"C:\P\Source\MyGame\Public\MyActor.h");

            Assert.Equal(3, file.Includes.Count);
            Assert.Equal("MyActor.generated.h", file.GeneratedInclude.Path);
            Assert.Equal(3, file.Types.Count);

            var enumType = file.Types[0];
            Assert.Equal(ReflectedKind.Enum, enumType.Kind);
            Assert.Equal("EMyMode", enumType.Name);
            Assert.Equal(new[] { "First", "Second" }, enumType.EnumValues);

            var actor = file.Types[2];
            Assert.Equal("AMyActor", actor.Name);
            Assert.Equal("MyActor", actor.ReflectedName);
            Assert.Equal("MYGAME_API", actor.ApiMacro);
            Assert.Equal(new[] { "AActor", "IMyInterface" }, actor.BaseTypes);
            Assert.Equal("GENERATED_BODY", actor.GeneratedBodyMacro);
            Assert.Equal("My Actor", actor.Macro.GetMeta("DisplayName").Value);
        }

        [Fact]
        public void ParsesMembersWithAccess()
        {
            var actor = HeaderParser.Parse(Header).Types.Single(t => t.Name == "AMyActor");

            Assert.Equal(new[] { "Root", "Health", "OnHealthChanged" }, actor.Properties.Select(p => p.Name));
            Assert.Equal(AccessLevel.Private, actor.Properties[0].Access);
            Assert.Equal(AccessLevel.Public, actor.Properties[1].Access);
            Assert.Equal("float", actor.Properties[1].Type);
            Assert.Equal("Stats|Health", actor.Properties[1].Macro.Get("Category").Value);
            Assert.Equal("100", actor.Properties[1].Macro.GetMeta("ClampMax").Value);

            Assert.Equal(new[] { "Heal", "GetValue", "OnHit" }, actor.Functions.Select(f => f.Name));
            var heal = actor.Functions[0];
            Assert.Equal("void", heal.ReturnType);
            Assert.Equal(3, heal.Parameters.Count);
            Assert.Equal("TArray<int32>&", heal.Parameters[1].Type);
            Assert.Equal("Out", heal.Parameters[1].Name);
            Assert.Equal("FVector::ZeroVector", heal.Parameters[2].DefaultValue);

            var getValue = actor.Functions[1];
            Assert.True(getValue.IsConst);
            Assert.True(getValue.HasInlineBody);
            Assert.Equal(AccessLevel.Protected, actor.Functions[2].Access);
        }

        [Fact]
        public void ParsesStructMembersAsPublicAndTemplates()
        {
            var data = HeaderParser.Parse(Header).Types.Single(t => t.Name == "FMyData");
            var values = Assert.Single(data.Properties);
            Assert.Equal("Values", values.Name);
            Assert.Equal("TMap<FName, TArray<int32>>", values.Type);
            Assert.Equal(AccessLevel.Public, values.Access);
        }

        [Fact]
        public void ParsesDelegates()
        {
            var file = HeaderParser.Parse(Header);
            Assert.Equal(new[] { "FOnHealthChanged", "FOnNativeOnly" }, file.Delegates.Select(d => d.Name));
            Assert.True(file.Delegates[0].IsDynamic && file.Delegates[0].IsMulticast);
            Assert.False(file.Delegates[1].IsDynamic);
        }

        [Fact]
        public void ParsesInterfacePair()
        {
            const string text = @"
UINTERFACE(MinimalAPI, Blueprintable)
class UInteractable : public UInterface { GENERATED_BODY() };

class AForward;

class MYGAME_API IInteractable
{
    GENERATED_BODY()
public:
    UFUNCTION(BlueprintCallable, BlueprintNativeEvent)
    void Interact(AActor* Instigator);
};";
            var file = HeaderParser.Parse(text);
            Assert.Equal(2, file.Types.Count);
            var native = file.Types[1];
            Assert.True(native.IsNativeInterfaceClass);
            Assert.Equal("Interactable", native.ReflectedName);
            Assert.Equal("Interact", Assert.Single(native.Functions).Name);
        }

        [Fact]
        public void ToleratesIncompleteBuffer()
        {
            const string text = "UCLASS()\nclass AFoo : public AActor\n{\n GENERATED_BODY()\n UPROPERTY(EditAnywhere, Cat";
            var file = HeaderParser.Parse(text);
            Assert.Equal("AFoo", Assert.Single(file.Types).Name);
        }
    }
}
