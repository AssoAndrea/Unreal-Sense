using System;
using System.Collections.Generic;
using System.IO;
using UnrealSense.Project;

namespace UnrealSense.Navigation
{
    public static class GoToRoots
    {
        static readonly string[] CodeFiles = { ".h", ".hpp", ".inl", ".cpp", ".c", ".cs", ".ini", ".uplugin", ".usf", ".ush", ".json" };
        static readonly string[] AllSymbols = { ".h", ".hpp", ".inl", ".cpp", ".c" };
        static readonly string[] HeaderSymbols = { ".h", ".hpp", ".inl" };

        /// <summary>
        /// Project code is fully indexed; for the engine only headers are scanned for symbols (their .cpp files
        /// mostly repeat the declarations) and ThirdParty folders are listed but not parsed.
        /// </summary>
        public static List<IndexRoot> For(UnrealProject project, bool includeEngine = true)
        {
            var roots = new List<IndexRoot>
            {
                new IndexRoot { Directory = project.SourceDirectory, FileExtensions = CodeFiles, SymbolExtensions = AllSymbols },
                new IndexRoot { Directory = Path.Combine(project.ProjectDirectory, "Plugins"), FileExtensions = CodeFiles, SymbolExtensions = AllSymbols, NoSymbolFolders = new[] { "ThirdParty" } },
                new IndexRoot { Directory = project.ConfigDirectory, FileExtensions = new[] { ".ini" } },
            };
            var engine = project.Engine;
            if (includeEngine && engine != null)
            {
                roots.Add(new IndexRoot { Directory = Path.Combine(engine.EngineDirectory, "Source"), IsEngine = true, FileExtensions = CodeFiles, SymbolExtensions = HeaderSymbols, NoSymbolFolders = new[] { "ThirdParty" } });
                roots.Add(new IndexRoot { Directory = Path.Combine(engine.EngineDirectory, "Plugins"), IsEngine = true, FileExtensions = CodeFiles, SymbolExtensions = HeaderSymbols, NoSymbolFolders = new[] { "ThirdParty" } });
                roots.Add(new IndexRoot { Directory = Path.Combine(engine.EngineDirectory, "Shaders"), IsEngine = true, FileExtensions = new[] { ".usf", ".ush" } });
                roots.Add(new IndexRoot { Directory = Path.Combine(engine.EngineDirectory, "Config"), IsEngine = true, FileExtensions = new[] { ".ini" } });
            }
            return roots;
        }

        public static string CachePath(UnrealProject project)
        {
            uint hash = 2166136261;
            foreach (var c in project.UProjectPath.ToUpperInvariant())
                hash = (hash ^ c) * 16777619;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnrealSense", "Cache", $"{project.Name}-{hash:x8}", "goto.bin");
        }
    }
}
