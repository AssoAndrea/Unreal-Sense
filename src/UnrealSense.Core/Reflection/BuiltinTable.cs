namespace UnrealSense.Reflection
{
    /// <summary>
    /// Built-in specifier table. Format per row: targets|Name|=|Documentation
    ///   targets: C=UCLASS I=UINTERFACE S=USTRUCT E=UENUM P=UPROPERTY F=UFUNCTION A=UPARAM M=UMETA D=UDELEGATE *=any
    ///   a leading 'm' marks a meta=(...) key; '=' in the third column marks specifiers that take a value.
    /// </summary>
    static class BuiltinTable
    {
        public const string Data = @"
# ---------------------------------------------------------------- UCLASS
C|Abstract||Class is abstract and can't be instantiated directly.
C|AdvancedClassDisplay||Shows all properties of the class in the advanced section of the Details panel.
C|AutoCollapseCategories|=|Lists categories that should be collapsed by default.
C|AutoExpandCategories|=|Lists categories that should be expanded by default.
CI|Blueprintable||Class can be used as a base class for creating Blueprints.
CSIE|BlueprintType||Type can be used for variables in Blueprints.
C|ClassGroup|=|Groups the class in the editor's Actor Browser / component list.
C|CollapseCategories||Properties are not grouped by category in the editor.
C|Config|=|Class can store data in an .ini file (e.g. Config=Game).
C|Const||Properties and functions of the class are const and exported as const.
CI|ConversionRoot||Root class for actor conversion in the Level Editor.
C|CustomConstructor||UHT will not generate a default constructor declaration.
C|DefaultConfig||Saves config properties to Default*.ini instead of the local config.
C|DefaultToInstanced||All instances of this class are considered instanced (sub-objects).
CI|DependsOn|=|Classes that must be compiled before this one.
C|Deprecated||Class is deprecated; objects of it won't be saved.
C|DontAutoCollapseCategories|=|Cancels AutoCollapseCategories for the listed categories.
C|DontCollapseCategories||Cancels CollapseCategories inherited from the parent.
C|EditInlineNew||Objects can be created from the Details panel (instanced property).
C|EditorConfig|=|Saves properties marked as config to a per-user editor config file.
C|GlobalUserConfig||Config properties are saved to the global user ini.
C|HideCategories|=|Lists categories to hide in the Details panel.
C|HideDropdown||Class won't appear in class picker dropdowns.
C|HideFunctions|=|Lists functions hidden from the user in the editor.
C|Intrinsic||Class is declared directly in C++ and has no UHT generated boilerplate.
CIS|MinimalAPI||Only type info is exported; functions can't be called from other modules.
C|NoExport||UHT does not generate a class declaration; it must be declared manually.
C|NonTransient||Negates a Transient specifier inherited from a parent class.
C|NotBlueprintable||Class can't be used as a Blueprint base. Negates Blueprintable.
C|NotEditInlineNew||Negates EditInlineNew inherited from a parent class.
C|NotPlaceable||Negates Placeable inherited from a parent class.
C|Optional||Objects of this class are only saved in optional packages (editor-only data).
C|PerObjectConfig||Config is stored per object instance, keyed by object name.
C|Placeable||Class can be placed in levels / created in the editor.
C|ProjectUserConfig||Config properties are saved to the project user ini.
C|ShowCategories|=|Cancels HideCategories for the listed categories.
C|ShowFunctions|=|Cancels HideFunctions for the listed functions.
C|SparseClassDataType|=|Struct type used to store sparse class data.
CS|Transient||Objects of this type are never saved to disk.
C|Within|=|Objects of this class can only be created with an Outer of the given class.
C|CustomFieldNotify||UHT won't generate FieldNotify code for this class.
C|MatchedSerializers||Class supports both text and binary serialization.
mC|BlueprintSpawnableComponent||Component can be added to a Blueprint actor.
mCF|BlueprintThreadSafe||Functions can be called from worker threads (e.g. animation update).
mC|ChildCannotTick||Blueprint subclasses of this actor/component can't tick.
mC|ChildCanTick||Blueprint subclasses can tick even if bCanEverTick is false.
mCF|DeprecatedNode||Node is deprecated and shows a warning when compiled.
mCFP|DeprecationMessage|=|Message shown for deprecated items.
m*|DisplayName|=|Name shown in the editor instead of the code name.
mC|DontUseGenericSpawnObject||Don't spawn this class with the generic Spawn Object node.
mC|ExposedAsyncProxy|=|Exposes a proxy object of this class in Async Task nodes.
mC|IgnoreCategoryKeywordsInSubclasses||Category keywords of this class are not inherited by subclasses.
mCI|IsBlueprintBase|=|States whether the class is (or isn't) an acceptable base for Blueprints.
mC|KismetHideOverrides|=|Blueprint events that can't be overridden.
mC|LoadBehavior|=|Load behavior for references to this class (e.g. LazyOnDemand).
mC|PrioritizeCategories|=|Categories shown at the top of the Details panel.
mC|ProhibitedInterfaces|=|Interfaces that are incompatible with this class.
mC|RestrictedToClasses|=|Restricts Blueprint function library nodes to the listed classes.
mCF|ShowWorldContextPin||Shows the World Context pin on nodes of this class.
mC|UsesHierarchy||Class uses hierarchical data (shows the hierarchy editing features).
m*|ToolTip|=|Overrides the tooltip generated from the code comment.
m*|ShortTooltip|=|Short tooltip used in some contexts (e.g. the parent class picker).
m*|DocumentationPolicy|=|Validation policy for tooltips and comments (Strict).
mCSEF|ScriptName|=|Name used when exposing to scripting languages (Python).
mCSE|BlueprintInternalUseOnly||Hidden from the user in Blueprint menus.
# ---------------------------------------------------------------- UINTERFACE
I|NotBlueprintable||Interface can't be implemented in Blueprints.
mI|CannotImplementInterfaceInBlueprint||Interface can't be implemented by a Blueprint (equivalent to NotBlueprintable).
mI|CannotGenerateMessageNodes||Don't generate generic Message nodes for this interface.
# ---------------------------------------------------------------- USTRUCT
S|Atomic||Struct is always serialized as a single unit.
S|Immutable||Struct is immutable (only valid in Object.h).
S|NoExport||UHT won't generate code for this struct.
mS|HasNativeBreak|=|Custom BlueprintCallable break function (Module.Class.Function).
mS|HasNativeMake|=|Custom BlueprintCallable make function (Module.Class.Function).
mS|HiddenByDefault||Pins of Make/Break nodes are hidden by default.
mS|DisableSplitPin||Pins of this struct type can't be split.
# ---------------------------------------------------------------- UENUM / UMETA
E|Flags||Enum is used as bit flags.
mE|Bitflags||Enum can be used as a bitmask in UPROPERTY(meta=(Bitmask, BitmaskEnum=...)).
mE|UseEnumValuesAsMaskValuesInEditor||Enum values are already mask values rather than bit indices.
mE|Experimental||Marks the enum as experimental.
M|DisplayName|=|Display name of the enum value in the editor.
M|Hidden||Enum value is hidden in the editor.
M|ToolTip|=|Tooltip of the enum value.
M|ScriptName|=|Name used for scripting.
M|Grouping|=|Group of the enum value in dropdowns.
# ---------------------------------------------------------------- UPROPERTY
P|AdvancedDisplay||Property is shown in the advanced (collapsed) section of the Details panel.
P|AssetRegistrySearchable||Property value is added to the asset registry as a searchable tag.
P|BlueprintAssignable||Multicast delegate can be bound in Blueprints.
PF|BlueprintAuthorityOnly||Only runs/binds on the network authority.
P|BlueprintCallable||Multicast delegate can be called from Blueprints.
P|BlueprintGetter|=|UFUNCTION used as the Blueprint getter (implies BlueprintReadOnly).
P|BlueprintReadOnly||Property can be read but not written by Blueprints.
P|BlueprintReadWrite||Property can be read and written by Blueprints.
P|BlueprintSetter|=|UFUNCTION used as the Blueprint setter (implies BlueprintReadWrite).
PF|Category|=|Category in the Details panel / Blueprint menus (use | for sub-categories).
P|Config||Value is loaded from / saved to the class config .ini.
P|Const||Property is const and exported as const.
P|DuplicateTransient||Value is reset to default on duplication (copy/paste, binary duplication).
P|EditAnywhere||Editable in the Details panel of archetypes and instances.
P|EditDefaultsOnly||Editable only on archetypes (class defaults), not on instances.
P|EditFixedSize||Array elements are editable but the array size can't be changed.
P|EditInline||Object property can be edited inline (deprecated: use Instanced).
P|EditInstanceOnly||Editable only on instances, not on archetypes.
P|Export||Object property is exported with its owner (copy/paste).
P|FieldNotify||Property broadcasts FieldNotify changes (MVVM / UMG ViewModels).
PF|Getter|=|Native getter accessor function (empty = Get<Name>).
P|GlobalConfig||Like Config but cannot be overridden in subclasses.
P|Instanced||Object property owns a unique sub-object per instance (implies EditInline + Export).
P|Interp||Property can be animated by Sequencer / Matinee tracks.
P|Localized||Value is localized (deprecated).
P|Native||Property is native: C++ code serializes it and exposes it to GC.
P|NoClear||Hides the Clear (and Browse) button for object references.
P|NoExport||Only for native classes: property is not exported to the generated header.
P|NonPIEDuplicateTransient||Reset to default on duplication except when duplicating for PIE.
P|NonPIETransient||Deprecated: use NonPIEDuplicateTransient.
P|NonTransactional||Changes are not included in undo/redo.
P|NotReplicated||Skips replication (struct members and service request parameters).
P|Ref||Value is copied out after a function call (function parameters only).
P|Replicated||Property is replicated over the network (needs DOREPLIFETIME in GetLifetimeReplicatedProps).
P|ReplicatedUsing|=|Replicated property with an OnRep notify UFUNCTION (ReplicatedUsing=OnRep_Name).
P|RepRetry||Retries replication of failed struct references (only for struct properties).
P|SaveGame||Included in SaveGame archives (ArIsSaveGame).
P|SerializeText||Native property serialized as text (ImportText/ExportText).
PF|Setter|=|Native setter accessor function (empty = Set<Name>).
P|SimpleDisplay||Visible in the Details panel without opening the advanced section.
P|SkipSerialization||Not serialized, but can still be exported to text.
P|TextExportTransient||Not exported to text format (copy/paste, T3D).
P|Transient||Not saved or loaded; zero-filled at load time.
P|VisibleAnywhere||Visible (read-only) in the Details panel of archetypes and instances.
P|VisibleDefaultsOnly||Visible only on archetypes.
P|VisibleInstanceOnly||Visible only on instances.
CISEPFAD|meta|=|Metadata: meta=(Key, Key=Value, ...)
mP|AllowAbstract|=|Subclass/SoftClass pickers show abstract classes.
mP|AllowedClasses|=|Comma separated classes allowed in asset pickers (FSoftObjectPath / object refs).
mP|AllowedTypes|=|Allowed primary asset types for FPrimaryAssetId properties.
mP|AllowPreserveRatio||Shows a ratio lock for FVector properties.
mP|AllowPrivateAccess||Allows BlueprintReadOnly/ReadWrite on private members.
mP|ArrayClamp|=|Clamps an integer index to the size of the named array.
mP|AssetBundles|=|Asset bundles this soft reference belongs to.
mP|BlueprintBaseOnly||Class pickers only show classes that can be Blueprint bases.
mP|Bitmask||Integer property is edited as a bitmask.
mP|BitmaskEnum|=|Enum (with meta Bitflags) used to name the bits of a Bitmask property.
mP|Categories|=|Restricts FGameplayTag(Container) pickers to the given tag root(s).
mP|ClampMax|=|Maximum value accepted for numeric properties.
mP|ClampMin|=|Minimum value accepted for numeric properties.
mP|ConfigHierarchyEditable||Property can be edited per config hierarchy level in Project Settings.
mP|ContentDir||FDirectoryPath picker uses a path relative to the Content folder.
mP|Delta|=|Step used by the spin box.
mP|DisplayAfter|=|Shows the property after the named property.
mP|DisplayPriority|=|Order of the property in its category (lower = higher).
mP|DisplayThumbnail|=|Shows (or hides) the asset thumbnail.
mP|EditCondition|=|Expression (e.g. bEnable or Mode == EMode::A) that enables editing.
mP|EditConditionHides||Hides the property instead of disabling it when EditCondition is false.
mP|EditFixedOrder||Array elements can't be reordered by dragging.
mP|ExactClass||Object pickers only show assets of the exact class (no subclasses).
mP|ExposeFunctionCategories|=|Function categories exposed when binding the property.
mP|ExposeOnSpawn||Shows the property as a pin on SpawnActor / Construct Object nodes.
mP|FilePathFilter|=|Filter for FFilePath pickers (e.g. ""uasset"").
mP|ForceInlineRow||TMap of structs is shown as a single row per entry.
mP|ForceUnits|=|Displays the value with fixed units (no conversion).
mP|GetByRef||Blueprint getter returns a reference.
mP|GetOptions|=|UFUNCTION returning TArray<FString>/FName used to populate a dropdown.
mP|HideAlphaChannel||Hides alpha in FColor/FLinearColor pickers.
mP|HideInDetailPanel||Hides a multicast delegate in the Details panel.
mP|HideViewOptions||Hides the view options of class pickers.
mP|InlineEditConditionToggle||Bool is shown inline as the toggle of the property using it as EditCondition.
mP|InvalidEnumValues|=|Enum values not allowed in the dropdown.
mP|LinearDeltaSensitivity|=|Mouse drag sensitivity of spin boxes.
mP|LongPackageName||FDirectoryPath is converted to a long package name.
mP|MakeEditWidget||FVector/FTransform shows a 3D widget in the viewport.
mP|MaxLength|=|Maximum text length of FString/FText.
mP|MetaClass|=|Base class for FSoftClassPath pickers.
mP|MultiLine||Text box is multi-line.
mP|MustImplement|=|Interface that classes in the picker must implement.
mP|NoElementDuplicate||Hides the duplicate button of array elements.
mP|NoResetToDefault||Hides the reset-to-default arrow.
mP|NoSpinbox||Numeric field without spin box drag.
mP|OnlyPlaceable||Class pickers only show placeable actors.
mP|PinHiddenByDefault||Struct member pin is hidden by default.
mP|ReadOnlyKeys||TMap keys can't be edited.
mP|RelativePath||FDirectoryPath picker uses a relative path.
mP|RelativeToGameDir||FDirectoryPath picker is relative to the game directory.
mP|RequiredAssetDataTags|=|Asset picker filter on asset registry tags.
mP|ShowInnerProperties||Shows inner properties of an object reference inline.
mP|ShowOnlyInnerProperties||Struct properties are shown without the struct header.
mP|ShowTreeView||Class picker shows a tree view.
mP|SliderExponent|=|Exponent applied to slider movement.
mP|TitleProperty|=|Property (or FText format) used as array element title.
mP|UIMax|=|Maximum of the slider range (does not clamp).
mP|UIMin|=|Minimum of the slider range (does not clamp).
mP|Units|=|Units of the value (cm, s, Degrees, kg...).
mP|ValidEnumValues|=|Enum values allowed in the dropdown.
mPF|DevelopmentOnly||Only available in development builds.
mP|IsBindableEvent||Delegate is shown as a bindable event in UMG.
mP|BindWidget||UMG: property is bound to a widget with the same name in the Widget Blueprint.
mP|BindWidgetOptional||UMG: optional BindWidget.
mP|BindWidgetAnim||UMG: property is bound to a widget animation with the same name.
mP|MultipleBindWidgetAnim||UMG: optional BindWidgetAnim.
# ---------------------------------------------------------------- UFUNCTION
F|BlueprintCallable||Function can be called from Blueprints.
F|BlueprintCosmetic||Cosmetic function; won't run on dedicated servers.
F|BlueprintGetter||Function is used as a Blueprint getter of a property.
F|BlueprintImplementableEvent||Function is implemented in Blueprints (no C++ body).
F|BlueprintNativeEvent||Function can be overridden in Blueprints and has a native Name_Implementation.
F|BlueprintPure||Function has no side effects; shown as a pure node (needs a return value).
F|BlueprintSetter||Function is used as a Blueprint setter of a property.
F|CallInEditor||Function can be called from the Details panel (button).
F|Client||RPC executed on the owning client (needs Name_Implementation).
F|CustomThunk||UHT won't generate the exec thunk; provide DEFINE_FUNCTION(execName).
F|Exec||Function can be called from the in-game console.
F|NetMulticast||RPC executed on the server and all clients (needs Name_Implementation).
F|Reliable||RPC is reliable.
F|SealedEvent||Function can't be overridden in subclasses (events only).
F|Server||RPC executed on the server (needs Name_Implementation).
F|ServiceRequest|=|Network service request RPC.
F|ServiceResponse|=|Network service response RPC.
F|Unreliable||RPC is unreliable.
F|WithValidation||RPC needs a Name_Validate function.
F|FieldNotify||Function is a FieldNotify (MVVM) getter.
mF|AdvancedDisplay|=|Parameters (or count) shown as advanced pins.
mF|ArrayParm|=|Parameters that are wildcard arrays.
mF|ArrayTypeDependentParams|=|Parameters whose type depends on ArrayParm.
mF|AutoCreateRefTerm|=|Reference parameters that get a default value when unconnected.
mF|BlueprintAutocast||Static function used as automatic cast between pin types.
mF|BlueprintInternalUseOnly||Hidden from Blueprint menus (used by custom nodes).
mF|BlueprintProtected||Function can only be called on 'self' in Blueprints.
mF|CallableWithoutWorldContext||Function can be called without a world context.
mF|CommutativeAssociativeBinaryOperator||Node gets an 'Add pin' button.
mF|CompactNodeTitle|=|Shows the node in compact form with this title.
mF|CustomStructureParam|=|Wildcard struct parameters.
mF|DefaultToSelf|=|Parameter defaulting to 'self'.
mF|DeprecatedFunction||Function is deprecated (compiler warning in Blueprints).
mF|DeterminesOutputType|=|Parameter whose class determines the output pin type.
mF|DynamicOutputParam|=|Output parameter whose type is determined dynamically.
mF|ExpandBoolAsExecs|=|Expands a bool parameter/return into exec pins.
mF|ExpandEnumAsExecs|=|Expands an enum parameter/return into exec pins.
mF|HidePin|=|Hides a parameter pin.
mF|HideSelfPin||Hides the self (target) pin.
mF|InternalUseParam|=|Parameters hidden from users.
mF|Keywords|=|Search keywords for the Blueprint action menu.
mF|Latent||Latent function (needs a FLatentActionInfo parameter).
mF|LatentInfo|=|Name of the FLatentActionInfo parameter.
mF|MapParam|=|Wildcard map parameters.
mF|MaterialParameterCollectionFunction||Function uses a material parameter collection.
mF|NativeBreakFunc||Function is a native break function for a struct.
mF|NativeMakeFunc||Function is a native make function for a struct.
mF|NotBlueprintThreadSafe||Function is not thread safe inside a BlueprintThreadSafe class.
mF|ReturnDisplayName|=|Display name of the return value pin.
mF|ScriptMethod|=|Exposes a static function as a script method of its first parameter.
mF|ScriptOperator|=|Exposes the function as a script operator.
mF|SetParam|=|Wildcard set parameters.
mF|UnsafeDuringActorConstruction||Function can't be called from Construction Scripts.
mF|Variadic||Function accepts a variable number of arguments (CustomThunk).
mF|WorldContext|=|Parameter used as world context (hidden pin).
# ---------------------------------------------------------------- UPARAM
A|ref||Non-const reference parameter is an input pin (not an output).
A|DisplayName|=|Display name of the pin.
A|Required||Parameter must be connected.
";
    }
}
