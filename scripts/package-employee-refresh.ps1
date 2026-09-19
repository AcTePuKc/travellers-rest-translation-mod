param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginSource = Join-Path $repoRoot "src\TravellersRestEmployeeRefresh\bin\Release\net472\TravellersRestEmployeeRefresh.dll"
$translationsSource = Join-Path $repoRoot "employee-refresh\translations"
$packageRoot = Join-Path $repoRoot "$OutputDir\BepInEx\plugins\EmployeeRefresh"
$translationsTarget = Join-Path $packageRoot "translations"
$zipPath = Join-Path $repoRoot "$OutputDir\EmployeeRefresh-$Version.zip"

if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    throw "EmployeeRefresh binary not found. Build src\TravellersRestEmployeeRefresh\TravellersRestEmployeeRefresh.csproj -c Release first."
}
$nameSources = Get-ChildItem -LiteralPath $translationsSource -Filter "employee-names.*.txt" -File
if ($nameSources.Count -eq 0) { throw "No employee name files found in: $translationsSource" }

$distRoot = Join-Path $repoRoot $OutputDir
if (Test-Path -LiteralPath $distRoot) { Remove-Item -LiteralPath $distRoot -Recurse -Force }
New-Item -ItemType Directory -Path $translationsTarget -Force | Out-Null
Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Force
Copy-Item -LiteralPath $nameSources.FullName -Destination $translationsTarget -Force
Compress-Archive -Path (Join-Path $repoRoot "$OutputDir\BepInEx") -DestinationPath $zipPath -Force

[pscustomobject]@{
    Languages = ($nameSources.Name -replace '^employee-names\.', '' -replace '\.txt$', '') -join ', '
    Version = $Version
    ZipPath = (Resolve-Path -LiteralPath $zipPath).Path
}
