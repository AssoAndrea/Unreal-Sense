using System;
using System.IO;

namespace UnrealSense.Templates
{
    /// <summary>What a new class is: decides the prefix, the macros and the template.</summary>
    public enum NewClassKind
    {
        /// <summary>UCLASS deriving from a UObject class (U or A prefix).</summary>
        Class,
        /// <summary>UINTERFACE: a UMyX/IMyX pair.</summary>
        Interface,
        /// <summary>USTRUCT (F prefix), optionally deriving from another struct.</summary>
        Struct,
        /// <summary>UENUM enum class (E prefix), header only.</summary>
        Enum,
        /// <summary>Plain C++ class, no reflection.</summary>
        Empty,
    }

    /// <summary>A class a new class can derive from: an engine, plugin or project type, or a "Common" entry.</summary>
    public sealed class ParentClassInfo
    {
        /// <summary>C++ name with prefix (AActor, UInterface, FTableRowBase); null for Empty/Enum and a struct without base.</summary>
        public string Name { get; set; }

        /// <summary>Label in the Common list ("Actor Component"); null in All Classes.</summary>
        public string DisplayName { get; set; }

        public NewClassKind Kind { get; set; }

        /// <summary>Direct C++ base (first one), used to walk the hierarchy.</summary>
        public string BaseName { get; set; }

        /// <summary>Full path of the header that declares it, when known.</summary>
        public string HeaderPath { get; set; }

        /// <summary>Path for #include ("GameFramework/Actor.h").</summary>
        public string IncludePath { get; set; }

        /// <summary>Module that declares it: the new class's module must depend on it.</summary>
        public string ModuleName { get; set; }

        public string PluginName { get; set; }
        public bool PluginEnabledByDefault { get; set; }
        public bool IsEngine { get; set; }
        public bool IsAbstract { get; set; }

        /// <summary>The header is in the module's Private folder: only that module can include it, and not from Public.</summary>
        public bool IsPrivateHeader { get; set; }

        public string Text => DisplayName ?? Name;

        public override string ToString() => Text;

        /// <summary>
        /// Path to #include a header: relative to the Public/Classes/Private folder that holds it (those are on the
        /// include path), else to the module folder. <paramref name="moduleDirectory"/> may be null.
        /// </summary>
        public static string ComputeIncludePath(string headerPath, string moduleDirectory, out bool isPrivate)
        {
            isPrivate = false;
            if (string.IsNullOrEmpty(moduleDirectory))
                return Path.GetFileName(headerPath);
            var root = moduleDirectory.TrimEnd('\\', '/') + "\\";
            var full = headerPath.Replace('/', '\\');
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(headerPath);
            var relative = full.Substring(root.Length);
            int slash = relative.IndexOf('\\');
            if (slash > 0)
            {
                var first = relative.Substring(0, slash);
                if (first.Equals("Public", StringComparison.OrdinalIgnoreCase) || first.Equals("Classes", StringComparison.OrdinalIgnoreCase))
                    relative = relative.Substring(slash + 1);
                else if (first.Equals("Private", StringComparison.OrdinalIgnoreCase))
                {
                    isPrivate = true;
                    relative = relative.Substring(slash + 1);
                }
            }
            return relative.Replace('\\', '/');
        }
    }
}
