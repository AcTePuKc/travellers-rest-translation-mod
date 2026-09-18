param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$LanguageCode,
    [string]$Version = "0.1.0",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginSource = Join-Path $repoRoot "plugin\TravellersRestTranslationMod.dll"
$translationSource = Join-Path $repoRoot "translations\labels.$LanguageCode.txt"
$employeeNamesSource = Join-Path $repoRoot "translations\employee-names.$LanguageCode.txt"
$packageRoot = Join-Path $repoRoot "$OutputDir\BepInEx\plugins\TravellersRest Translation"
$translationTarget = Join-Path $packageRoot "translations\labels.$LanguageCode.txt"
$employeeNamesTarget = Join-Path $packageRoot "translations\employee-names.$LanguageCode.txt"
$configRoot = Join-Path $repoRoot "$OutputDir\BepInEx\config"
$zipPath = Join-Path $repoRoot "$OutputDir\TravellersRestTranslationMod-$Version-$LanguageCode.zip"

if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    throw "Plugin binary not found: $pluginSource"
}
if (-not (Test-Path -LiteralPath $translationSource -PathType Leaf)) {
    throw "Translation file not found: $translationSource"
}

$distRoot = Split-Path -Parent $packageRoot
if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}

New-Item -ItemType Directory -Path (Split-Path -Parent $translationTarget), $configRoot -Force | Out-Null
Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Force
Copy-Item -LiteralPath $translationSource -Destination $translationTarget -Force
if (Test-Path -LiteralPath $employeeNamesSource -PathType Leaf) {
    Copy-Item -LiteralPath $employeeNamesSource -Destination $employeeNamesTarget -Force
}

$config = @"
## Settings file for Travellers Rest Translation Loader $Version
## Plugin GUID: actepukc.travellersrest.translation

[General]

## Apply translations from the selected language file.
# Setting type: Boolean
# Default value: true
EnableTranslationOverrides = true

## Translation file inside the plugin translations folder.
# Setting type: String
TranslationFile = labels.$LanguageCode.txt

## Optional localized first-name and surname pools for generated staff.
# Setting type: String
EmployeeNamesFile = employee-names.$LanguageCode.txt

[UI]

## Read hForHours and mForMins from the game's currently selected language.
# Setting type: Boolean
# Default value: false
UseBuiltInDayStatsTimeUnits = false

[Debug]

## Write localization terms requested by the game to a runtime dump.
# Setting type: Boolean
# Default value: false
DumpObservedTerms = false

## Runtime dump filename inside the plugin translations folder.
# Setting type: String
# Default value: runtime-labels.txt
DumpFile = runtime-labels.txt
"@
[IO.File]::WriteAllText((Join-Path $configRoot "actepukc.travellersrest.translation.cfg"), $config, [Text.UTF8Encoding]::new($false))

Compress-Archive -Path (Join-Path $repoRoot "$OutputDir\BepInEx") -DestinationPath $zipPath -Force

[pscustomobject]@{
    LanguageCode = $LanguageCode
    Version = $Version
    ZipPath = (Resolve-Path -LiteralPath $zipPath).Path
    PluginBinary = (Resolve-Path -LiteralPath $pluginSource).Path
    TranslationFile = (Resolve-Path -LiteralPath $translationSource).Path
}
