# Builds the Release VSIX and a private extension gallery (atom.xml + UnrealSense.vsix) in dist\gallery.
# Copy dist\gallery to a shared folder once, add it in Visual Studio under
#   Tools > Options > Environment > Extensions > Additional Extension Galleries  (URL: file:///S:/path/to/gallery/atom.xml)
# and from then on Visual Studio finds and installs new versions by itself: replace the two files to publish one.
param([string]$GalleryFolder)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -property installationPath
# The VSSDK does not always regenerate obj\...\extension.vsixmanifest when only the version changes, which ships
# a package Visual Studio sees as the previous version: force a clean package build.
Get-ChildItem (Join-Path $root "src\UnrealSense.Vsix\obj") -Recurse -Filter "extension.vsixmanifest" -ErrorAction SilentlyContinue | Remove-Item -Force
& (Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe") (Join-Path $root "src\UnrealSense.Vsix\UnrealSense.Vsix.csproj") /restore /t:Rebuild /p:Configuration=Release /v:minimal /nologo /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

[xml]$manifest = Get-Content (Join-Path $root "src\UnrealSense.Vsix\source.extension.vsixmanifest")
$identity = $manifest.PackageManifest.Metadata.Identity
$version = $identity.Version
# The package's Version constant stamps the index (a new version re-indexes from scratch): it must match.
$packageSource = Get-Content (Join-Path $root "src\UnrealSense.Vsix\UnrealSensePackage.cs") -Raw
if ($packageSource -notmatch "const string Version = `"$([regex]::Escape($version))`"") { throw "UnrealSensePackage.Version differs from source.extension.vsixmanifest ($version)." }
$vsix =Join-Path $root "src\UnrealSense.Vsix\bin\Release\net48\UnrealSense.vsix"

# Refuse to publish a package whose embedded version differs from the source manifest.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $reader = New-Object System.IO.StreamReader($zip.GetEntry("extension.vsixmanifest").Open())
    [xml]$packaged = $reader.ReadToEnd()
    $reader.Dispose()
}
finally { $zip.Dispose() }
$packagedVersion = $packaged.PackageManifest.Metadata.Identity.Version
if ($packagedVersion -ne $version) { throw "The built package says version $packagedVersion but source.extension.vsixmanifest says $version." }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -Filter "UnrealSense-*.vsix" | Remove-Item
Copy-Item $vsix (Join-Path $dist "UnrealSense-$version.vsix")
Copy-Item (Join-Path $root "Install-UnrealSense.ps1") $dist -Force

$gallery = if ($GalleryFolder) { $GalleryFolder } else { Join-Path $dist "gallery" }
New-Item -ItemType Directory -Force $gallery | Out-Null
Copy-Item $vsix (Join-Path $gallery "UnrealSense.vsix") -Force

$now = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$description = [System.Security.SecurityElement]::Escape($manifest.PackageManifest.Metadata.Description.'#text')
$feed = @"
<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title type="text">UnrealSense</title>
  <id>uuid:3f6d2a1e-7c4b-4e5a-9b0d-1c2e3f4a5b6c;id=1</id>
  <updated>$now</updated>
  <entry>
    <id>$($identity.Id)</id>
    <title type="text">UnrealSense</title>
    <summary type="text">$description</summary>
    <published>$now</published>
    <updated>$now</updated>
    <author><name>$($identity.Publisher)</name></author>
    <content type="application/octet-stream" src="UnrealSense.vsix" />
    <Vsix xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/developer/vsx-syndication-schema/2010">
      <Id>$($identity.Id)</Id>
      <Version>$version</Version>
      <References />
      <Rating xsi:nil="true" />
      <RatingCount xsi:nil="true" />
      <DownloadCount xsi:nil="true" />
    </Vsix>
  </entry>
</feed>
"@
Set-Content -Path (Join-Path $gallery "atom.xml") -Value $feed -Encoding UTF8
Write-Host "UnrealSense $version -> $dist\UnrealSense-$version.vsix"
Write-Host "Gallery: $gallery (atom.xml + UnrealSense.vsix)"
