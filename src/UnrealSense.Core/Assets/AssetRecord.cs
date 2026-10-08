using System;
using System.Collections.Generic;

namespace UnrealSense.Assets
{
    /// <summary>A native (C++) reflected symbol as seen from an asset: "/Script/Module.Class[:Member]".</summary>
    public sealed class NativeSymbolRef : IEquatable<NativeSymbolRef>
    {
        public NativeSymbolRef() { }

        public NativeSymbolRef(string module, string type, string member = null)
        {
            Module = module;
            Type = type;
            Member = member;
        }

        /// <summary>Script package without the "/Script/" prefix, e.g. "Gym".</summary>
        public string Module { get; set; }

        /// <summary>Reflected type name without C++ prefix, e.g. "ShooterCharacter".</summary>
        public string Type { get; set; }

        /// <summary>Function / property name, or null for type references.</summary>
        public string Member { get; set; }

        public bool Equals(NativeSymbolRef other) =>
            other != null
            && string.Equals(Module, other.Module, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Member, other.Member, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => Equals(obj as NativeSymbolRef);

        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Module ?? "") * 31
            ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Type ?? "") * 17
            ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Member ?? "");

        public override string ToString() => Member == null ? $"/Script/{Module}.{Type}" : $"/Script/{Module}.{Type}:{Member}";
    }

    /// <summary>Everything we extract from one package header. Serialized to the on-disk cache.</summary>
    public sealed class AssetRecord
    {
        public string FilePath { get; set; }
        public string PackageName { get; set; }
        public long FileSize { get; set; }
        public DateTime LastWriteUtc { get; set; }

        /// <summary>Class of the main asset object: Blueprint, WidgetBlueprint, AnimBlueprint, World, DataAsset...</summary>
        public string AssetClass { get; set; }

        /// <summary>Native parent class of a Blueprint, when it derives from C++ directly.</summary>
        public NativeSymbolRef NativeParent { get; set; }

        /// <summary>Package name of the Blueprint parent when it derives from another Blueprint.</summary>
        public string BlueprintParent { get; set; }

        public List<NativeSymbolRef> ClassReferences { get; set; } = new List<NativeSymbolRef>();
        public List<NativeSymbolRef> FunctionCalls { get; set; } = new List<NativeSymbolRef>();
        public List<NativeSymbolRef> ImplementedEvents { get; set; } = new List<NativeSymbolRef>();

        /// <summary>Name map; kept only for assets that touch project code (used for property heuristics).</summary>
        public List<string> Names { get; set; }

        public string Error { get; set; }

        public string AssetName
        {
            get
            {
                var slash = PackageName?.LastIndexOf('/') ?? -1;
                return slash >= 0 ? PackageName.Substring(slash + 1) : PackageName;
            }
        }

        public bool IsBlueprint => AssetClass != null && AssetClass.EndsWith("Blueprint", StringComparison.Ordinal);
        public bool IsLevel => AssetClass == "World";
    }
}
