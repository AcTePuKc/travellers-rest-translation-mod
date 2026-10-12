# Packages the profiler for a Nexus release.
#
# The profiler is self-contained: it builds to a single DLL that references only BepInEx
# and Unity assemblies, all of which the game already ships. BepInEx itself is bundled
# with Travellers Rest in the Steam build, so nothing has to be installed alongside it.
#
# This deliberately does NOT clear the whole dist folder, because the other two mods
# package into the same place and their output is not this script's to delete.

param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repoRoot "$OutputDir\profiler-package"
$packageRoot = Join-Path $stage "BepInEx\plugins\TravellersRestProfiler"
$zipPath = Join-Path $repoRoot "$OutputDir\TravellersRestProfiler-$Version.zip"

# The DLL is read from profiler\, which is committed. That is deliberate, and matches how the
# translation loader is released: the release workflow runs on a clean checkout with no game
# installed, so it cannot build this — the project references the game's own assemblies.
# The committed copy is what gets packaged; refresh it after a build if needed.
$binary = Join-Path $repoRoot "profiler\TravellersRestProfiler.dll"
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    throw ("Profiler binary not found at $binary. Build it with " +
           "dotnet build src\TravellersRestProfiler\TravellersRestProfiler.csproj -c Release, " +
           "then copy the output there.")
}
Write-Host "Packaging the committed binary"

# A build that starts copying BepInEx or Unity assemblies into its output would mean a
# reference lost its <Private>false</Private>, and shipping those would break other people's
# installs. Checked when an output folder exists, rather than trusted.
$buildOutput = Join-Path $repoRoot "src\TravellersRestProfiler\bin\Release\net472"
if (Test-Path -LiteralPath $buildOutput) {
    $extraDlls = Get-ChildItem -LiteralPath $buildOutput -File -Filter '*.dll' |
        Where-Object { $_.Name -ne 'TravellersRestProfiler.dll' }
    if ($extraDlls) {
        throw ("The build produced extra assemblies, which means a reference is missing " +
               "<Private>false</Private>: " + (($extraDlls.Name) -join ', '))
    }
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath $binary -Destination $packageRoot -Force

# The readme ships with the mod, because the report is the whole point of it and the
# in-game window may not draw at all on this game.
$readme = Join-Path $repoRoot "profiler\README.md"
if (Test-Path -LiteralPath $readme) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $packageRoot 'README.md') -Force
}
$changelog = Join-Path $repoRoot "profiler\changelog.txt"
if (Test-Path -LiteralPath $changelog) {
    Copy-Item -LiteralPath $changelog -Destination (Join-Path $packageRoot 'changelog.txt') -Force
}

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ""
Write-Host "Packaged $Version"
Get-ChildItem -LiteralPath $zipPath | Select-Object Name, Length | Format-Table -AutoSize

[pscustomobject]@{
    Version = $Version
    ZipPath = (Resolve-Path -LiteralPath $zipPath).Path
    Contents = 'TravellersRestProfiler.dll, README.md, changelog.txt'
}
