param(
    [string]$Version = "0.1.1",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# The DLL is read from employee-refresh\, which is committed. That is deliberate, and matches
# how the translation loader is released: the release workflow runs on a clean checkout with no
# game installed, so it cannot build this — the project references the game's own assemblies.
# The committed copy is what gets packaged; refresh it after a build if needed.
$pluginSource = Join-Path $repoRoot "employee-refresh\TravellersRestEmployeeRefresh.dll"
if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    throw ("EmployeeRefresh binary not found at $pluginSource. Build it with " +
           "dotnet build src\TravellersRestEmployeeRefresh\TravellersRestEmployeeRefresh.csproj " +
           "-c Release, then copy the output there.")
}

$translationsSource = Join-Path $repoRoot "employee-refresh\translations"
$packageRoot = Join-Path $repoRoot "$OutputDir\employee-refresh-package\BepInEx\plugins\EmployeeRefresh"
$translationsTarget = Join-Path $packageRoot "translations"
$zipPath = Join-Path $repoRoot "$OutputDir\EmployeeRefresh-$Version.zip"

if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    throw "EmployeeRefresh binary not found. Build src\TravellersRestEmployeeRefresh\TravellersRestEmployeeRefresh.csproj -c Release first."
}
$nameSources = Get-ChildItem -LiteralPath $translationsSource -Filter "employee-names.*.txt" -File
if ($nameSources.Count -eq 0) { throw "No employee name files found in: $translationsSource" }

# Stages and zips the EmployeeRefresh release.
#
# This script touches ONLY its own staging folder and its own zip. An earlier version deleted
# the entire dist folder, which meant packaging one mod destroyed the other mods' artefacts —
# in a repository that publishes three mods. That is the kind of mistake that silently ships
# the wrong file, so it is called out here rather than left to be rediscovered.

$distRoot = Join-Path $repoRoot $OutputDir
$stage = Join-Path $distRoot 'employee-refresh-package'
New-Item -ItemType Directory -Path $translationsTarget -Force | Out-Null
Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Force
Copy-Item -LiteralPath $nameSources.FullName -Destination $translationsTarget -Force
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stage -Recurse -Force

[pscustomobject]@{
    Languages = ($nameSources.Name -replace '^employee-names\.', '' -replace '\.txt$', '') -join ', '
    Version = $Version
    ZipPath = (Resolve-Path -LiteralPath $zipPath).Path
}
