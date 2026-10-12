# Builds the translation loader and installs it into a Travellers Rest folder.
#
# The game folder comes from -GameDir, or the TRAVELLERS_REST_DIR environment variable, or
# the usual Steam location, so a clone of this repository is not tied to one machine.

param(
    [string]$GameDir = '',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $GameDir) { $GameDir = $env:TRAVELLERS_REST_DIR }
if (-not $GameDir) {
    $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Travellers Rest\Windows'
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'TravellersRest.exe'))) {
    throw ("TravellersRest.exe not found in '$GameDir'. Pass -GameDir, or set the " +
           "TRAVELLERS_REST_DIR environment variable.")
}

dotnet build (Join-Path $repoRoot 'TravellersRestTranslationMod.csproj') -c $Configuration -p:GameDir=$GameDir
