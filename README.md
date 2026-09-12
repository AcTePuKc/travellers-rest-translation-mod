# Travellers Rest Translation Loader

Minimal, language-neutral BepInEx 5 plugin for testing translations in Travellers Rest.

## Installation for players and translators

1. Download the official [BepInEx 5.4.21 x64 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.21). Select `BepInEx_x64_5.4.21.0.zip`.
2. Extract the contents of the BepInEx ZIP into the folder that contains `TravellersRest.exe`:

   ```text
   ...\Travellers Rest\Windows\
   ```

3. Start Travellers Rest once, wait for the main menu, and close the game. This creates the BepInEx folders and config file.
4. Download the desired translation ZIP from this repository's [Releases](https://github.com/AcTePuKc/travellers-rest-translation-mod/releases) page.
5. Extract that ZIP into the same `Windows` folder. It already contains the required `BepInEx/plugins` and `BepInEx/config` paths.
6. Start the game again.

The generated config is:

```text
Travellers Rest\Windows\BepInEx\config\actepukc.travellersrest.translation.cfg
```

To select another language file, edit the config and change the filename:

```ini
TranslationFile = labels.bg.txt
```

For example, use `labels.de.txt` for German or `labels.fr.txt` for French. The filename must match a file in:

```text
Travellers Rest\Windows\BepInEx\plugins\TravellersRest Translation\translations\
```

The current loader applies matching entries regardless of which language is selected in the game's settings. The labels files use the game's English localization keys as identifiers, so the selected file replaces matching displayed text even if the game was previously set to another language.

The plugin reads the configured file from `translations/` and applies matching entries through the game's I2 Localization system. The source translation project remains separate from this mod project.

Each language should use its own file, for example `labels.bg.txt`, `labels.de.txt`, or `labels.example.txt`. The plugin contains no language-specific text.

## Local build

```powershell
.\scripts\build-and-install.ps1
```

The build copies the plugin and all files from `translations/` to:

```text
Travellers Rest\Windows\BepInEx\plugins\TravellersRest Translation\
```

For local testing, select a language file in the config, for example `TranslationFile = labels.example.txt`.

## Download a ready-to-use release

Releases contain a ready-to-install ZIP. No .NET SDK, compiler, game assemblies, or source build is required for end users.

Extract the ZIP into the game's `Windows` folder. The archive already contains the required `BepInEx/plugins` and `BepInEx/config` paths.

## GitHub Actions packaging

The `Build Translation Release` workflow packages one selected language file. Run it manually with a language code such as `bg`, `de`, or `fr`, and provide the release version. A version tag such as `v0.1.0` packages the default `bg` language and publishes a GitHub Release.

## Convert a Crowdin labels file

Crowdin exports can be converted without changing the source translation project:

```powershell
.\scripts\convert-labels.ps1 `
  -InputFile "C:\path\to\crowdin\labels.txt" `
  -LanguageCode bg `
  -OutputFile ".\translations\labels.bg.txt"
```

The converter keeps each entry on one physical line. Escaped sequences such as `\n` remain escaped and are decoded by the plugin at runtime. It also splits only at the first `=`, because translated text can contain additional equals signs. Older local exports that use ` / / ` for paragraph breaks are normalized to `\n\n`.

Duplicate keys are excluded from the generated file and written to a `.duplicates.txt` review file with all candidate lines. To select a candidate without editing the source export, create an overrides file next to the output, for example `translations/labels.bg.overrides.txt`, containing one selected `key=value` line:

```text
tutorialPopUp103=The selected translation goes here
```

The converter adds selected overrides back into the generated file and reports `Verified: false` only while duplicate keys remain unresolved. It also normalizes em dashes (`—`) to regular hyphens (`-`) and closes or repairs Unity rich-text tags in translated values. If a source file contains a `# Unique labels` marker after a prepended review report, everything before that marker is ignored.

## Convert a Crowdin XLIFF export

For a language export from Crowdin, prefer the language-specific `.xliff` file. It preserves the current target values and stores line breaks as real newlines:

```powershell
python .\scripts\convert-xliff-to-labels.py `
  --input "C:\path\to\I2Loc TravellersRest Localization_bg.xliff" `
  --output ".\translations\labels.bg.txt"
```

The XLIFF converter also excludes duplicate keys, writes all candidates to a `.duplicates.txt` review file, closes remaining unclosed Unity rich-text tags, and continues with the remaining entries. It uses the same optional `.overrides.txt` file next to the output to select a duplicate candidate. A result containing unresolved duplicates is reported as `Verified: false`.

Only XLIFF targets that differ from the English source are exported. Empty or untranslated entries are skipped. If a local draft has not been exported by Crowdin yet, it will not appear in the XLIFF and must be injected through the separate local CSV/TSV workflow first.

The XLSX converter is also available for workbooks that contain populated language columns:

```powershell
python .\scripts\convert-xlsx-to-labels.py `
  --input "C:\path\to\I2Loc TravellersRest Localization.xlsx" `
  --language Bulgarian `
  --output ".\translations\labels.bg.txt"
```
