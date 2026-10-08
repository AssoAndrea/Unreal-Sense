using System;
using System.Collections.Generic;
using System.Linq;

namespace UnrealSense.Cpp
{
    public enum ReflectedKind { Class, Struct, Enum, Interface }

    public enum AccessLevel { Public, Protected, Private }

    /// <summary>One entry inside a reflection macro: "EditAnywhere", "Category=\"Ammo\"", "ClampMin = 0".</summary>
    public sealed class Specifier
    {
        public string Key { get; set; }
        /// <summary>Value with surrounding quotes removed, or null when the specifier is a flag.</summary>
        public string Value { get; set; }
        public int KeyStart { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public int ValueStart { get; set; } = -1;
        public int ValueEnd { get; set; } = -1;
        public bool IsMeta { get; set; }

        public int KeyEnd => KeyStart + (Key?.Length ?? 0);

        public override string ToString() => Value == null ? Key : $"{Key}={Value}";
    }

    /// <summary>UCLASS(...), UPROPERTY(...), UFUNCTION(...)...</summary>
    public sealed class ReflectionMacro
    {
        public string Name { get; set; }
        public int Start { get; set; }
        public int OpenParen { get; set; }
        public int CloseParen { get; set; }
        public int MetaOpenParen { get; set; } = -1;
        public int MetaCloseParen { get; set; } = -1;
        public List<Specifier> Specifiers { get; } = new List<Specifier>();
        public List<Specifier> Meta { get; } = new List<Specifier>();

        public int End => CloseParen + 1;

        public bool Has(string key) => Specifiers.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        public Specifier Get(string key) => Specifiers.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        public Specifier GetMeta(string key) => Meta.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        public bool HasMeta(string key) => GetMeta(key) != null;

        public bool IsMetaTrue(string key)
        {
            var meta = GetMeta(key);
            return meta != null && (meta.Value == null || !string.Equals(meta.Value, "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class IncludeDirective
    {
        public string Path { get; set; }
        public bool IsAngled { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public int End => Start + Length;
    }

    public abstract class ReflectedMember
    {
        public ReflectionMacro Macro { get; set; }
        public string Name { get; set; }
        public int NameStart { get; set; } = -1;
        public int NameEnd => NameStart + (Name?.Length ?? 0);
        public AccessLevel Access { get; set; }
        public int DeclarationStart { get; set; }
        public int DeclarationEnd { get; set; }
        public ReflectedType Owner { get; set; }
    }

    public sealed class FunctionParameter
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string DefaultValue { get; set; }
        public bool IsNonConstReference => Type != null && Type.TrimEnd().EndsWith("&") && !Type.TrimStart().StartsWith("const ");
    }

    public sealed class ReflectedFunction : ReflectedMember
    {
        public string ReturnType { get; set; }
        public int ReturnTypeStart { get; set; }
        public List<FunctionParameter> Parameters { get; } = new List<FunctionParameter>();
        public bool IsConst { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsStatic { get; set; }
        public bool IsOverride { get; set; }
        public bool HasInlineBody { get; set; }

        public bool ReturnsVoid => ReturnType == null || ReturnType.Trim() == "void";

        public override string ToString() => $"{ReturnType} {Name}({string.Join(", ", Parameters.Select(p => p.Type + " " + p.Name))}){(IsConst ? " const" : "")}";
    }

    public sealed class ReflectedProperty : ReflectedMember
    {
        public string Type { get; set; }
        public int TypeStart { get; set; }
        public int TypeEnd { get; set; }
        public override string ToString() => $"{Type} {Name}";
    }

    public sealed class ReflectedType
    {
        public ReflectedKind Kind { get; set; }
        public ReflectionMacro Macro { get; set; }
        public string Keyword { get; set; }
        public string Name { get; set; }
        public int NameStart { get; set; }
        public int NameEnd => NameStart + (Name?.Length ?? 0);
        public string ApiMacro { get; set; }
        public List<string> BaseTypes { get; } = new List<string>();
        public int BodyOpen { get; set; } = -1;
        public int BodyClose { get; set; } = -1;
        public string GeneratedBodyMacro { get; set; }
        public int GeneratedBodyStart { get; set; } = -1;
        public List<ReflectedFunction> Functions { get; } = new List<ReflectedFunction>();
        public List<ReflectedProperty> Properties { get; } = new List<ReflectedProperty>();
        public List<string> EnumValues { get; } = new List<string>();

        /// <summary>The "IFoo" class that follows UINTERFACE() class UFoo; it shares UFoo's macro.</summary>
        public bool IsNativeInterfaceClass { get; set; }

        /// <summary>Name as UHT exposes it: AShooterCharacter → ShooterCharacter, FFoo → Foo. Enums keep their name.</summary>
        public string ReflectedName => GetReflectedName(Name, Kind);

        public string BaseType => BaseTypes.FirstOrDefault();

        public static string GetReflectedName(string cppName, ReflectedKind kind)
        {
            if (string.IsNullOrEmpty(cppName) || kind == ReflectedKind.Enum || cppName.Length < 2) return cppName;
            char p = cppName[0];
            bool hasPrefix = (p == 'A' || p == 'U' || p == 'F' || p == 'I' || p == 'S' || p == 'T') && char.IsUpper(cppName[1]);
            return hasPrefix ? cppName.Substring(1) : cppName;
        }

        public override string ToString() => $"{Kind} {Name}";
    }

    public sealed class DelegateDeclaration
    {
        public string Macro { get; set; }
        public string Name { get; set; }
        public int Start { get; set; }
        public bool IsDynamic => Macro.Contains("_DYNAMIC_");
        public bool IsMulticast => Macro.Contains("MULTICAST") || Macro.Contains("SPARSE");
    }
}
