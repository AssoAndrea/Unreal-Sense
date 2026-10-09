using System;

namespace UnrealSense.Extension
{
    /// <summary>Mirrors the symbols of VSCommandTable.vsct.</summary>
    internal static class PackageGuids
    {
        public const string UnrealSensePackageString = "9a0e5c1d-3b7f-4d6a-8e2c-5f1b7a9d3c11";
        public const string UnrealSenseCmdSetString = "e4b2c8a6-1d3f-4a5b-9c7e-0f2d4b6a8c22";
        public static readonly Guid UnrealSensePackage = new Guid(UnrealSensePackageString);
        public static readonly Guid UnrealSenseCmdSet = new Guid(UnrealSenseCmdSetString);
    }

    internal static class PackageIds
    {
        public const int ShowUnrealExplorer = 0x0100;
        public const int FindBlueprintUsages = 0x0101;
        public const int GoToUnrealCounterpart = 0x0102;
        public const int GenerateProjectFiles = 0x0103;
        public const int OpenInUnrealEditor = 0x0104;
        public const int RefreshIndex = 0x0105;
        public const int FindSymbol = 0x0106;
        public const int FindFile = 0x0107;
        public const int FindUsages = 0x0108;
        public const int ToggleVisualAssist = 0x010B;
        public const int ToggleVsIndexing = 0x010C;
        public const int EnableOpenAssetsInEditor = 0x010E;
        public const int NewUnrealClass = 0x010F;
    }
}
