param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$LanguageCode,
    [string]$Version = "0.1.0",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# Stages and zips the translation-loader release.
#
# This script touches ONLY its own staging folder and its own zip. An earlier version deleted
# the entire dist folder, which meant packaging one mod destroyed the other mods' artefacts —
# in a repository that publishes three mods. Called out so it is not reintroduced.
$stage = Join-Path $repoRoot "$OutputDir\translation-loader-package"
$packageRoot = Join-Path $stage "BepInEx\plugins\TravellersRest Translation"

$pluginSource = Join-Path $repoRoot "plugin\TravellersRestTranslationMod.dll"
$translationSource = Join-Path $repoRoot "translations\labels.$LanguageCode.txt"
$categoryLabelsSource = Join-Path $repoRoot "translations\category-labels.$LanguageCode.txt"
$translationTarget = Join-Path $packageRoot "translations\labels.$LanguageCode.txt"
$categoryLabelsTarget = Join-Path $packageRoot "translations\category-labels.$LanguageCode.txt"
$configRoot = Join-Path $stage "BepInEx\config"
$zipPath = Join-Path $repoRoot "$OutputDir\TravellersRestTranslationMod-$Version-$LanguageCode.zip"

if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) {
    throw "Plugin binary not found: $pluginSource"
}
if (-not (Test-Path -LiteralPath $translationSource -PathType Leaf)) {
    throw "Translation file not found: $translationSource"
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path (Split-Path -Parent $translationTarget), $configRoot -Force | Out-Null
Copy-Item -LiteralPath $pluginSource -Destination $packageRoot -Force
Copy-Item -LiteralPath $translationSource -Destination $translationTarget -Force
if (Test-Path -LiteralPath $categoryLabelsSource -PathType Leaf) {
    Copy-Item -LiteralPath $categoryLabelsSource -Destination $categoryLabelsTarget -Force
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

## Optional fixed labels for employee category tab tooltips.
# Setting type: String
CategoryLabelsFile = category-labels.$LanguageCode.txt

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

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stage -Recurse -Force

[pscustomobject]@{
    LanguageCode = $LanguageCode
    Version = $Version
    ZipPath = (Resolve-Path -LiteralPath $zipPath).Path
    PluginBinary = (Resolve-Path -LiteralPath $pluginSource).Path
    TranslationFile = (Resolve-Path -LiteralPath $translationSource).Path
}
