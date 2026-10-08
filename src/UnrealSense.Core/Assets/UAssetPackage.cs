using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnrealSense.Assets
{
    /// <summary>An entry of a package import table (FObjectImport).</summary>
    public sealed class ObjectImport
    {
        public string ClassPackage { get; internal set; }
        public string ClassName { get; internal set; }
        public int OuterIndex { get; internal set; }
        public string ObjectName { get; internal set; }
        public string PackageName { get; internal set; }
    }

    /// <summary>An entry of a package export table (FObjectExport). Only the fields we need are kept.</summary>
    public sealed class ObjectExport
    {
        public int ClassIndex { get; internal set; }
        public int SuperIndex { get; internal set; }
        public int TemplateIndex { get; internal set; }
        public int OuterIndex { get; internal set; }
        public string ObjectName { get; internal set; }
    }

    /// <summary>
    /// Header-only view of an editor (uncooked) .uasset/.umap package: name map, imports and exports.
    /// The layout mirrors FPackageFileSummary / FObjectImport / FObjectExport serialization in
    /// Engine/Source/Runtime/CoreUObject/Private/UObject/{PackageFileSummary,ObjectResource}.cpp.
    /// </summary>
    public sealed class UAssetPackage
    {
        public const uint PackageFileTag = 0x9E2A83C1;
        const uint PKG_UnversionedProperties = 0x00002000;
        const uint PKG_FilterEditorOnly = 0x80000000;

        // EUnrealEngineObjectUE4Version values used by the reader.
        const int VER_UE4_LOAD_FOR_EDITOR_GAME = 365;
        const int VER_UE4_SERIALIZE_TEXT_IN_PACKAGES = 459;
        const int VER_UE4_COOKED_ASSETS_IN_EDITOR_SUPPORT = 485;
        const int VER_UE4_NAME_HASHES_SERIALIZED = 504;
        const int VER_UE4_PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS = 507;
        const int VER_UE4_TEMPLATEINDEX_IN_COOKED_EXPORTS = 508;
        const int VER_UE4_64BIT_EXPORTMAP_SERIALSIZES = 511;
        const int VER_UE4_ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID = 516;
        const int VER_UE4_NON_OUTER_PACKAGE_IMPORT = 520;
        const int VER_UE4_LATEST = 522;

        // EUnrealEngineObjectUE5Version values used by the reader.
        const int VER_UE5_OPTIONAL_RESOURCES = 1003;
        const int VER_UE5_REMOVE_OBJECT_EXPORT_PACKAGE_GUID = 1005;
        const int VER_UE5_TRACK_OBJECT_EXPORT_IS_INHERITED = 1006;
        const int VER_UE5_ADD_SOFTOBJECTPATH_LIST = 1008;
        const int VER_UE5_SCRIPT_SERIALIZATION_OFFSET = 1010;
        const int VER_UE5_PACKAGE_SAVED_HASH = 1016;
        const int VER_UE5_LATEST = 1018;

        public string FilePath { get; private set; }
        public int FileVersionUE4 { get; private set; }
        public int FileVersionUE5 { get; private set; }
        public uint PackageFlags { get; private set; }
        public IReadOnlyList<string> Names { get; private set; }
        public IReadOnlyList<ObjectImport> Imports { get; private set; }
        public IReadOnlyList<ObjectExport> Exports { get; private set; }

        public bool IsCooked => (PackageFlags & PKG_FilterEditorOnly) != 0;

        public static UAssetPackage Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
            {
                var package = Read(stream);
                package.FilePath = path;
                return package;
            }
        }

        public static UAssetPackage Read(Stream stream)
        {
            var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (r.ReadUInt32() != PackageFileTag)
                throw new InvalidDataException("Not an Unreal package (bad tag) or byte-swapped package.");

            int legacyFileVersion = r.ReadInt32();
            if (legacyFileVersion >= 0)
                throw new InvalidDataException("UE3 packages are not supported.");
            if (legacyFileVersion < -9)
                throw new InvalidDataException($"Unsupported package legacy file version {legacyFileVersion}.");

            if (legacyFileVersion != -4)
                r.ReadInt32(); // LegacyUE3Version

            int ue4 = r.ReadInt32();
            int ue5 = legacyFileVersion <= -8 ? r.ReadInt32() : 0;
            int licensee = r.ReadInt32();
            if (ue4 == 0 && ue5 == 0 && licensee == 0)
            {
                // Unversioned package: assume the latest layout we know.
                ue4 = VER_UE4_LATEST;
                ue5 = VER_UE5_LATEST;
            }

            if (ue5 >= VER_UE5_PACKAGE_SAVED_HASH)
            {
                r.ReadBytes(20); // SavedHash (FIoHash)
                r.ReadInt32();   // TotalHeaderSize
            }

            if (legacyFileVersion <= -2)
                SkipCustomVersions(r, legacyFileVersion);

            if (ue5 < VER_UE5_PACKAGE_SAVED_HASH)
                r.ReadInt32(); // TotalHeaderSize

            ReadFString(r); // PackageName (deprecated FolderName)
            uint packageFlags = r.ReadUInt32();
            bool filterEditorOnly = (packageFlags & PKG_FilterEditorOnly) != 0;

            int nameCount = r.ReadInt32();
            int nameOffset = r.ReadInt32();
            if (ue5 >= VER_UE5_ADD_SOFTOBJECTPATH_LIST)
            {
                r.ReadInt32(); // SoftObjectPathsCount
                r.ReadInt32(); // SoftObjectPathsOffset
            }
            if (!filterEditorOnly && ue4 >= VER_UE4_ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID)
                ReadFString(r); // LocalizationId
            if (ue4 >= VER_UE4_SERIALIZE_TEXT_IN_PACKAGES)
            {
                r.ReadInt32(); // GatherableTextDataCount
                r.ReadInt32(); // GatherableTextDataOffset
            }
            int exportCount = r.ReadInt32();
            int exportOffset = r.ReadInt32();
            int importCount = r.ReadInt32();
            int importOffset = r.ReadInt32();

            long length = stream.Length;
            Validate(nameCount, nameOffset, length, "name");
            Validate(importCount, importOffset, length, "import");
            Validate(exportCount, exportOffset, length, "export");

            var names = new string[nameCount];
            stream.Position = nameOffset;
            for (int i = 0; i < nameCount; i++)
            {
                names[i] = ReadFString(r);
                if (ue4 >= VER_UE4_NAME_HASHES_SERIALIZED)
                    r.ReadUInt32(); // NonCasePreservingHash + CasePreservingHash (2 x uint16)
            }

            string ReadName()
            {
                int index = r.ReadInt32();
                int number = r.ReadInt32();
                if (index < 0 || index >= names.Length)
                    throw new InvalidDataException($"Name index {index} out of range.");
                return number > 0 ? names[index] + "_" + (number - 1) : names[index];
            }

            var imports = new ObjectImport[importCount];
            stream.Position = importOffset;
            for (int i = 0; i < importCount; i++)
            {
                var import = new ObjectImport
                {
                    ClassPackage = ReadName(),
                    ClassName = ReadName(),
                    OuterIndex = r.ReadInt32(),
                    ObjectName = ReadName(),
                };
                if (ue4 >= VER_UE4_NON_OUTER_PACKAGE_IMPORT)
                    import.PackageName = ReadName();
                if (ue5 >= VER_UE5_OPTIONAL_RESOURCES)
                    r.ReadInt32(); // bImportOptional
                imports[i] = import;
            }

            bool unversionedProperties = (packageFlags & PKG_UnversionedProperties) != 0;
            var exports = new ObjectExport[exportCount];
            stream.Position = exportOffset;
            for (int i = 0; i < exportCount; i++)
            {
                var export = new ObjectExport
                {
                    ClassIndex = r.ReadInt32(),
                    SuperIndex = r.ReadInt32(),
                };
                if (ue4 >= VER_UE4_TEMPLATEINDEX_IN_COOKED_EXPORTS)
                    export.TemplateIndex = r.ReadInt32();
                export.OuterIndex = r.ReadInt32();
                export.ObjectName = ReadName();
                r.ReadUInt32(); // ObjectFlags
                if (ue4 < VER_UE4_64BIT_EXPORTMAP_SERIALSIZES)
                {
                    r.ReadInt32(); r.ReadInt32();
                }
                else
                {
                    r.ReadInt64(); r.ReadInt64();
                }
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); // bForcedExport, bNotForClient, bNotForServer
                if (ue5 < VER_UE5_REMOVE_OBJECT_EXPORT_PACKAGE_GUID)
                    r.ReadBytes(16);
                if (ue5 >= VER_UE5_TRACK_OBJECT_EXPORT_IS_INHERITED)
                    r.ReadInt32(); // bIsInheritedInstance
                r.ReadUInt32(); // PackageFlags
                if (ue4 >= VER_UE4_LOAD_FOR_EDITOR_GAME)
                    r.ReadInt32(); // bNotAlwaysLoadedForEditorGame
                if (ue4 >= VER_UE4_COOKED_ASSETS_IN_EDITOR_SUPPORT)
                    r.ReadInt32(); // bIsAsset
                if (ue5 >= VER_UE5_OPTIONAL_RESOURCES)
                    r.ReadInt32(); // bGeneratePublicHash
                if (ue4 >= VER_UE4_PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
                {
                    r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                }
                if (!unversionedProperties && ue5 >= VER_UE5_SCRIPT_SERIALIZATION_OFFSET)
                {
                    r.ReadInt64(); r.ReadInt64(); // ScriptSerializationStart/EndOffset
                }
                exports[i] = export;
            }

            return new UAssetPackage
            {
                FileVersionUE4 = ue4,
                FileVersionUE5 = ue5,
                PackageFlags = packageFlags,
                Names = names,
                Imports = imports,
                Exports = exports,
            };
        }

        /// <summary>Returns the import for a negative FPackageIndex, or null.</summary>
        public ObjectImport GetImport(int packageIndex)
        {
            if (packageIndex >= 0) return null;
            int i = -packageIndex - 1;
            return i < Imports.Count ? Imports[i] : null;
        }

        /// <summary>Returns the export for a positive FPackageIndex, or null.</summary>
        public ObjectExport GetExport(int packageIndex)
        {
            if (packageIndex <= 0) return null;
            int i = packageIndex - 1;
            return i < Exports.Count ? Exports[i] : null;
        }

        /// <summary>Object name for any FPackageIndex (import or export).</summary>
        public string GetObjectName(int packageIndex)
        {
            if (packageIndex < 0) return GetImport(packageIndex)?.ObjectName;
            if (packageIndex > 0) return GetExport(packageIndex)?.ObjectName;
            return null;
        }

        /// <summary>
        /// Full path of an import, e.g. "/Script/Gym.ShooterWeapon:Fire" or "/Game/BP/BP_Base.BP_Base_C".
        /// </summary>
        public string GetImportPath(int packageIndex)
        {
            var chain = new List<string>();
            var current = GetImport(packageIndex);
            int guard = 0;
            while (current != null && guard++ < 64)
            {
                chain.Add(current.ObjectName);
                if (current.OuterIndex == 0) break;
                current = GetImport(current.OuterIndex);
            }
            chain.Reverse();
            if (chain.Count == 0) return null;
            var sb = new StringBuilder(chain[0]);
            for (int i = 1; i < chain.Count; i++)
                sb.Append(i == 1 ? '.' : ':').Append(chain[i]);
            return sb.ToString();
        }

        /// <summary>Package name ("/Script/Gym", "/Game/...") that ultimately owns an import.</summary>
        public string GetImportPackage(int packageIndex)
        {
            var current = GetImport(packageIndex);
            int guard = 0;
            while (current != null && current.OuterIndex != 0 && guard++ < 64)
                current = GetImport(current.OuterIndex);
            return current?.ObjectName;
        }

        static void Validate(int count, int offset, long length, string what)
        {
            if (count < 0 || count > 10_000_000 || (count > 0 && (offset <= 0 || offset >= length)))
                throw new InvalidDataException($"Corrupt {what} table (count={count}, offset={offset}).");
        }

        static void SkipCustomVersions(BinaryReader r, int legacyFileVersion)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > 100_000) throw new InvalidDataException("Corrupt custom version container.");
            for (int i = 0; i < count; i++)
            {
                if (legacyFileVersion == -2)
                {
                    r.ReadInt32(); r.ReadInt32(); // ECustomVersionSerializationFormat::Enums
                }
                else if (legacyFileVersion >= -5)
                {
                    r.ReadBytes(16); r.ReadInt32(); ReadFString(r); // Guids
                }
                else
                {
                    r.ReadBytes(16); r.ReadInt32(); // Optimized
                }
            }
        }

        static string ReadFString(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length == 0) return string.Empty;
            if (length > 0)
            {
                if (length > 1 << 20) throw new InvalidDataException("String too long.");
                var bytes = r.ReadBytes(length);
                return Encoding.GetEncoding(28591).GetString(bytes, 0, Math.Max(0, length - 1));
            }
            if (length < -(1 << 20)) throw new InvalidDataException("String too long.");
            var wide = r.ReadBytes(-length * 2);
            return Encoding.Unicode.GetString(wide, 0, Math.Max(0, wide.Length - 2));
        }
    }
}
