using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnrealSense.Templates
{
    /// <summary>
    /// Class templates in the format of the engine's Engine/Content/Editor/Templates/*.template files (same %TOKENS%).
    /// The engine's own file wins when it exists, so new classes look like the ones the Unreal Editor creates for that
    /// engine version; these copies cover a missing engine and the kinds the engine has no template for
    /// (structs, enums, subsystems, anim instances, widgets).
    /// </summary>
    public static class BuiltinClassTemplates
    {
        // Indented with four spaces here, turned into tabs (Unreal's style) when loaded.
        static readonly Dictionary<string, string> Templates = new Dictionary<string, string>
        {
            ["ActorClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    // Sets default values for this actor's properties
    %PREFIXED_CLASS_NAME%();

protected:
    // Called when the game starts or when spawned
    virtual void BeginPlay() override;

public:
    // Called every frame
    virtual void Tick(float DeltaTime) override;
};
",
            ["ActorClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

// Sets default values
%PREFIXED_CLASS_NAME%::%PREFIXED_CLASS_NAME%()
{
    // Set this actor to call Tick() every frame.  You can turn this off to improve performance if you don't need it.
    PrimaryActorTick.bCanEverTick = true;
}

// Called when the game starts or when spawned
void %PREFIXED_CLASS_NAME%::BeginPlay()
{
    Super::BeginPlay();
}

// Called every frame
void %PREFIXED_CLASS_NAME%::Tick(float DeltaTime)
{
    Super::Tick(DeltaTime);
}
",
            ["PawnClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    // Sets default values for this pawn's properties
    %PREFIXED_CLASS_NAME%();

protected:
    // Called when the game starts or when spawned
    virtual void BeginPlay() override;

public:
    // Called every frame
    virtual void Tick(float DeltaTime) override;

    // Called to bind functionality to input
    virtual void SetupPlayerInputComponent(class UInputComponent* PlayerInputComponent) override;
};
",
            ["PawnClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

// Sets default values
%PREFIXED_CLASS_NAME%::%PREFIXED_CLASS_NAME%()
{
    // Set this pawn to call Tick() every frame.  You can turn this off to improve performance if you don't need it.
    PrimaryActorTick.bCanEverTick = true;
}

// Called when the game starts or when spawned
void %PREFIXED_CLASS_NAME%::BeginPlay()
{
    Super::BeginPlay();
}

// Called every frame
void %PREFIXED_CLASS_NAME%::Tick(float DeltaTime)
{
    Super::Tick(DeltaTime);
}

// Called to bind functionality to input
void %PREFIXED_CLASS_NAME%::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
{
    Super::SetupPlayerInputComponent(PlayerInputComponent);
}
",
            ["CharacterClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    // Sets default values for this character's properties
    %PREFIXED_CLASS_NAME%();

protected:
    // Called when the game starts or when spawned
    virtual void BeginPlay() override;

public:
    // Called every frame
    virtual void Tick(float DeltaTime) override;

    // Called to bind functionality to input
    virtual void SetupPlayerInputComponent(class UInputComponent* PlayerInputComponent) override;
};
",
            ["CharacterClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

// Sets default values
%PREFIXED_CLASS_NAME%::%PREFIXED_CLASS_NAME%()
{
    // Set this character to call Tick() every frame.  You can turn this off to improve performance if you don't need it.
    PrimaryActorTick.bCanEverTick = true;
}

// Called when the game starts or when spawned
void %PREFIXED_CLASS_NAME%::BeginPlay()
{
    Super::BeginPlay();
}

// Called every frame
void %PREFIXED_CLASS_NAME%::Tick(float DeltaTime)
{
    Super::Tick(DeltaTime);
}

// Called to bind functionality to input
void %PREFIXED_CLASS_NAME%::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
{
    Super::SetupPlayerInputComponent(PlayerInputComponent);
}
",
            ["ActorComponentClass.h"] =@"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

UCLASS(ClassGroup=(Custom), meta=(BlueprintSpawnableComponent))
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    // Sets default values for this component's properties
    %PREFIXED_CLASS_NAME%();

protected:
    // Called when the game starts
    virtual void BeginPlay() override;

public:
    // Called every frame
    virtual void TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;
};
",
            ["ActorComponentClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

// Sets default values for this component's properties
%PREFIXED_CLASS_NAME%::%PREFIXED_CLASS_NAME%()
{
    // Set this component to be initialized when the game starts, and to be ticked every frame.  You can turn these features
    // off to improve performance if you don't need them.
    PrimaryComponentTick.bCanEverTick = true;
}

// Called when the game starts
void %PREFIXED_CLASS_NAME%::BeginPlay()
{
    Super::BeginPlay();
}

// Called every frame
void %PREFIXED_CLASS_NAME%::TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction)
{
    Super::TickComponent(DeltaTime, TickType, ThisTickFunction);
}
",
            ["InterfaceClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
#include ""UObject/Interface.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

// This class does not need to be modified.
UINTERFACE(%UCLASS_SPECIFIER_LIST%)
class %PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()
};

/**
 *
 */
class %CLASS_MODULE_API_MACRO%I%UNPREFIXED_CLASS_NAME%
{
    GENERATED_BODY()

    // Add interface functions to this class. This is the class that will be inherited to implement this interface.
public:
};
",
            ["InterfaceClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

// Add default functionality here for any I%UNPREFIXED_CLASS_NAME% functions that are not pure virtual.
",
            ["EmptyClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""

/**
 *
 */
class %CLASS_MODULE_API_MACRO%%UNPREFIXED_CLASS_NAME%
{
public:
    %UNPREFIXED_CLASS_NAME%();
    ~%UNPREFIXED_CLASS_NAME%();
};
",
            ["EmptyClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

%UNPREFIXED_CLASS_NAME%::%UNPREFIXED_CLASS_NAME%()
{
}

%UNPREFIXED_CLASS_NAME%::~%UNPREFIXED_CLASS_NAME%()
{
}
",
            ["UObjectClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()
};
",
            ["UObjectClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%
",
            ["SubsystemClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    virtual void Deinitialize() override;
};
",
            ["SubsystemClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

void %PREFIXED_CLASS_NAME%::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
}

void %PREFIXED_CLASS_NAME%::Deinitialize()
{
    Super::Deinitialize();
}
",
            ["AnimInstanceClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

public:
    // Called when the animation instance is initialized
    virtual void NativeInitializeAnimation() override;

    // Called every frame to update the animation variables
    virtual void NativeUpdateAnimation(float DeltaSeconds) override;
};
",
            ["AnimInstanceClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

void %PREFIXED_CLASS_NAME%::NativeInitializeAnimation()
{
    Super::NativeInitializeAnimation();
}

void %PREFIXED_CLASS_NAME%::NativeUpdateAnimation(float DeltaSeconds)
{
    Super::NativeUpdateAnimation(DeltaSeconds);
}
",
            ["UserWidgetClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
UCLASS(%UCLASS_SPECIFIER_LIST%)
class %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME% : public %PREFIXED_BASE_CLASS_NAME%
{
    GENERATED_BODY()

protected:
    // Called after the widget is constructed and added to the viewport
    virtual void NativeConstruct() override;
};
",
            ["UserWidgetClass.cpp"] = @"%COPYRIGHT_LINE%

%MY_HEADER_INCLUDE_DIRECTIVE%

void %PREFIXED_CLASS_NAME%::NativeConstruct()
{
    Super::NativeConstruct();
}
",
            ["StructClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
%BASE_CLASS_INCLUDE_DIRECTIVE%
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
USTRUCT(BlueprintType)
struct %CLASS_MODULE_API_MACRO%%PREFIXED_CLASS_NAME%%BASE_CLASS_CLAUSE%
{
    GENERATED_BODY()
};
",
            ["EnumClass.h"] = @"%COPYRIGHT_LINE%

#pragma once

#include ""CoreMinimal.h""
#include ""%UNPREFIXED_CLASS_NAME%.generated.h""

/**
 *
 */
UENUM(BlueprintType)
enum class %PREFIXED_CLASS_NAME% : uint8
{
    None
};
",
        };

        /// <summary>
        /// Text of a template ("ActorClass.h"): the engine's file when <paramref name="engineTemplatesDirectory"/> has
        /// it, otherwise the built-in copy; null when neither exists (e.g. no .cpp for an enum).
        /// </summary>
        public static string Load(string name, string engineTemplatesDirectory)
        {
            if (engineTemplatesDirectory != null)
            {
                var file = Path.Combine(engineTemplatesDirectory, name + ".template");
                try
                {
                    if (File.Exists(file)) return File.ReadAllText(file);
                }
                catch (IOException) { }
                catch (System.UnauthorizedAccessException) { }
            }
            return Templates.TryGetValue(name, out var text) ? ToTabs(text) : null;
        }

        static string ToTabs(string text) =>
            Regex.Replace(text.Replace("\r\n", "\n"), @"^(?: {4})+", m => new string('\t', m.Length / 4), RegexOptions.Multiline);
    }
}
