# Builds UnrealSense and installs it cleanly into the Visual Studio experimental instance.
# A plain VSIXInstaller upgrade leaves VS's MEF cache pointing at the previous install folder
# ("package did not load correctly"), so this uninstalls, clears the caches and rebuilds the configuration.
param(
    [string]$Solution = "C:\Unreal Project\Gym\Gym.slnx",
    [switch]$NoBuild,
    [switch]$NoLaunch
)
$ErrorActionPreference = "Stop"
$extensionId = "UnrealSense.c6a3e5b2-4f0e-4b8e-9f1d-2a7c9e1b5d40"
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -property installationPath
$instanceId = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -property instanceId
$ide = Join-Path $vs "Common7\IDE"
$vsix = Join-Path $PSScriptRoot "src\UnrealSense.Vsix\bin\Debug\net48\UnrealSense.vsix"

if (-not $NoBuild) {
    # The packaged manifest is not reliably regenerated when only the version changes: rebuild it every time.
    Get-ChildItem (Join-Path $PSScriptRoot "src\UnrealSense.Vsix\obj") -Recurse -Filter "extension.vsixmanifest" -ErrorAction SilentlyContinue | Remove-Item -Force
    & (Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe") (Join-Path $PSScriptRoot "src\UnrealSense.Vsix\UnrealSense.Vsix.csproj") /restore /t:Rebuild /p:Configuration=Debug /v:minimal /nologo /clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$exp = Get-Process devenv -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -match "sperimentale|Experimental" }
if ($exp) {
    Write-Host "Waiting for the experimental instance to close (pid $($exp.Id -join ', '))..."
    $exp | Wait-Process
}

Write-Host "Uninstalling previous version..."
Start-Process (Join-Path $ide "VSIXInstaller.exe") -ArgumentList "/quiet", "/rootSuffix:Exp", "/uninstall:$extensionId" -Wait

$local = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\18.0_${instanceId}Exp"
foreach ($cache in "ComponentModelCache", "MEFCacheBackup") {
    $path = Join-Path $local $cache
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Host "Installing $vsix..."
$install = Start-Process (Join-Path $ide "VSIXInstaller.exe") -ArgumentList "/quiet", "/rootSuffix:Exp", "`"$vsix`"" -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "VSIXInstaller failed with exit code $($install.ExitCode)" }

Write-Host "Rebuilding the VS configuration..."
Start-Process (Join-Path $ide "devenv.exe") -ArgumentList "/rootsuffix", "Exp", "/updateconfiguration" -Wait

if (-not $NoLaunch) {
    Start-Process (Join-Path $ide "devenv.exe") -ArgumentList "/rootsuffix", "Exp", "`"$Solution`""
    Write-Host "Launched the experimental instance on $Solution"
}
