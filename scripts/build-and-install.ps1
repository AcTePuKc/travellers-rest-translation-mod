param(
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\Travellers Rest\Windows",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repoRoot "TravellersRestTranslationMod.csproj") -c $Configuration -p:GameDir=$GameDir
