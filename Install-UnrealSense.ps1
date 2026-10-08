# Installs or updates UnrealSense with the VSIX installer of the newest Visual Studio (2026 before 2022), so
# that instance is offered even when .vsix files are associated with an older Visual Studio.
# Usage: powershell -ExecutionPolicy Bypass -File Install-UnrealSense.ps1 [path\to\UnrealSense.vsix]
param([string]$Vsix)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-VsixVersion([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.GetEntry("extension.vsixmanifest")
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return [version]$manifest.PackageManifest.Metadata.Identity.Version
    }
    finally { $zip.Dispose() }
}

if (-not $Vsix) {
    # The highest version among the .vsix files next to this script (file dates are not reliable after copies).
    $candidates = Get-ChildItem $PSScriptRoot -Filter "UnrealSense*.vsix" | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Version = (Get-VsixVersion $_.FullName) }
    } | Sort-Object Version -Descending
    foreach ($c in $candidates) { Write-Host ("Package {0}  {1}" -f $c.Version, $c.Path) }
    $Vsix = $candidates | Select-Object -First 1 -ExpandProperty Path
}
if (-not $Vsix -or -not (Test-Path $Vsix)) { throw "UnrealSense .vsix not found next to this script; pass its path as the first argument." }
Write-Host ("Installing UnrealSense {0} from {1}" -f (Get-VsixVersion $Vsix), $Vsix)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$all = (& $vswhere -all -prerelease -format json | Out-String | ConvertFrom-Json)   # Windows PowerShell 5.1 returns the array as one object
$instances = @($all) | ForEach-Object { $_ } | Sort-Object { [version]$_.installationVersion } -Descending
foreach ($i in $instances) { Write-Host ("Found {0} {1} at {2}" -f $i.displayName, $i.installationVersion, $i.installationPath) }
$newest = $instances | Select-Object -First 1
if (-not $newest) { throw "No Visual Studio installation found." }

$installer = Join-Path $newest.installationPath "Common7\IDE\VSIXInstaller.exe"
Write-Host "Using $installer"
Start-Process $installer -ArgumentList "`"$Vsix`"" -Wait
