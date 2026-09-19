# Travellers Rest Mods

A small collection of independent BepInEx 5 mods for Travellers Rest. Each mod has its own DLL, configuration, release ZIP, and install folder; installing one never requires installing the others.

| Mod | Purpose | Install folder |
| --- | --- | --- |
| Translation Loader | Loads a selected `labels.<language>.txt` file, provides optional QA dumps, category-label overrides, and a Day Stats time-unit workaround. | `BepInEx/plugins/TravellersRest Translation/` |
| EmployeeRefresh | Refreshes available staff candidates, loads optional local employee name pools, and resizes the hire-staff window. | `BepInEx/plugins/EmployeeRefresh/` |

Private research material is kept outside the published project and is ignored by Git.

## Translation Loader

### Installation for players and translators

1. Download the official [BepInEx 5.4.21 x64 release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.21). Select `BepInEx_x64_5.4.21.0.zip`.
2. Extract the contents of the BepInEx ZIP into the folder that contains `TravellersRest.exe`:

   ```text
   ...\Travellers Rest\Windows\
   ```

3. Start Travellers Rest once, wait for the main menu, and close the game. This creates the BepInEx folders and config file.
4. Download the desired translation ZIP from this repository's [Releases](https://github.com/AcTePuKc/travellers-rest-translation-mod/releases) page.
5. Extract that ZIP into the same `Windows` folder. It already contains the required `BepInEx/plugins` and `BepInEx/config` paths.
6. Start the game again.

## EmployeeRefresh

`EmployeeRefresh` is a separate utility mod. It is independent from the Translation Loader, and the Translation Loader does not load or modify employee names.

It provides:

- a Refresh button and configurable `F6` hotkey for new staff candidates;
- optional, language-specific employee first-name and surname pools;
- a configurable taller hiring window, without reducing the game font size.

Install its own release ZIP into the game's `Windows` folder. Do not extract an EmployeeRefresh ZIP over the Translation Loader ZIP or vice versa; both archives contain their own correct plugin path and may safely coexist.

EmployeeRefresh keeps its language-specific name pools in:

```text
Travellers Rest\Windows\BepInEx\plugins\EmployeeRefresh\translations\
```

Use `MaleFirstNames` and `FemaleFirstNames`, plus either gendered `MaleSurnames` / `FemaleSurnames` or one shared `Surnames` section. Keep each first name and surname short—preferably no more than 8–9 characters, with shorter entries fitting best in the UI. The mod warns about longer entries instead of silently truncating them. See [employee-refresh/README.md](employee-refresh/README.md) for the file format.

The EmployeeRefresh config is:

```text
Travellers Rest\Windows\BepInEx\config\actepukc.travellersrest.employee-refresh.cfg
```

Use `EmployeeNamesFile` to select the name pool, `RefreshHotkey` to change or disable the hotkey, `ButtonGap` to position the Refresh button, and the `[Window]` settings to change the hiring-window size. Full file-format and configuration details are in [employee-refresh/README.md](employee-refresh/README.md).

## Translation Loader configuration

The Translation Loader config is:

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

### Day Stats time units and language testing

The Day Stats service-time formatter in the game does not use the existing `hForHours` and `mForMins` localization keys. The mod provides an optional workaround for this.

For the Bulgarian translation, keep the Bulgarian labels and use the Bulgarian time-unit entries:

```ini
[General]
EnableTranslationOverrides = true

[UI]
UseBuiltInDayStatsTimeUnits = false
```

To view the game's currently selected language without the Bulgarian translation overrides, use the built-in localization values instead:

```ini
[General]
EnableTranslationOverrides = false

[UI]
UseBuiltInDayStatsTimeUnits = true
```

The second mode is useful for testing another language. It disables the mod's translated labels and lets the game provide its own `hForHours` and `mForMins` values.

The plugin reads the configured file from `translations/` and applies matching entries through the game's I2 Localization system. The source translation project remains separate from this mod project.

Each language should use its own file, for example `labels.bg.txt`, `labels.de.txt`, or `labels.example.txt`. The plugin contains no language-specific text.

## Local builds

### Translation Loader

```powershell
.\scripts\build-and-install.ps1
```

The build copies the plugin and all files from `translations/` to:

```text
Travellers Rest\Windows\BepInEx\plugins\TravellersRest Translation\
```

For local testing, select a language file in the config, for example `TranslationFile = labels.example.txt`.

### EmployeeRefresh

```powershell
dotnet build .\src\TravellersRestEmployeeRefresh\TravellersRestEmployeeRefresh.csproj -c Release
```

The project stages its DLL and every file from `employee-refresh/translations/` in the EmployeeRefresh plugin folder. To create a standalone ZIP with all bundled name pools after building:

```powershell
.\scripts\package-employee-refresh.ps1 -Version 0.1.0
```

## Dump runtime localization terms

Debug dumps are disabled by default. Enable them only while investigating a missing or unusual string, then disable them again before normal use.

To inspect the exact localization keys requested by the running game, enable this in `BepInEx/config/actepukc.travellersrest.translation.cfg`:

```ini
[Debug]
DumpObservedTerms = true
DumpFile = runtime-labels.txt
```

Start the game, reproduce the dialogue or screen you want to inspect, then close the game. The plugin writes the observed terms and the text returned to the game to `BepInEx/plugins/TravellersRest Translation/translations/runtime-labels.txt`. The dump is regenerated on each start while the option is enabled.

The optional full Dialogue System database dump can be enabled with:

```ini
[Debug]
DumpDialogueDatabase = true
```

It writes `runtime-dialogue-database.txt` next to `runtime-labels.txt` and is also disabled by default.

## Download a ready-to-use release

Releases contain a ready-to-install ZIP. No .NET SDK, compiler, game assemblies, or source build is required for end users.

Extract the ZIP into the game's `Windows` folder. The archive already contains the required `BepInEx/plugins` and `BepInEx/config` paths.

## Translation Loader GitHub Actions packaging

The `Build Translation Release` workflow packages one selected language file. Run it manually with a language code such as `bg`, `de`, or `fr`, and provide the release version. A version tag such as `v0.1.0` packages the default `bg` language and publishes a GitHub Release.

EmployeeRefresh is packaged separately with `scripts/package-employee-refresh.ps1`; it must never be bundled into a translation release because users may want either mod on its own.

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

### Downloading the language file from Crowdin

The recommended workflow is to export the language directly from Crowdin:

1. Open the desired language in the Crowdin editor. For Bulgarian, the language link is [Bulgarian in the Travellers Rest project](https://crowdin.com/editor/travellers-rest/12/en-bg).
2. Click the file name `I2Loc TravellersRest Localization.xlsx`.
3. When the file is open, select `File` → `Save as XLIFF`.
4. Save the language-specific XLIFF outside this repository, for example:

   ```text
   C:\Temp\I2Loc TravellersRest Localization_nl.xliff
   ```

The downloaded file is for local conversion only. Do not commit the XLIFF, the workbook, or the generated test dump to this repository.

Prefer the language-specific `.xliff` file over XLSX for this workflow. XLIFF preserves the current target values and stores line breaks as real newlines:

```powershell
python .\scripts\convert-xliff-to-labels.py `
  --input "C:\path\to\I2Loc TravellersRest Localization_bg.xliff" `
  --output ".\translations\labels.bg.txt"
```

The XLIFF converter also excludes duplicate keys, writes all candidates to a `.duplicates.txt` review file, closes remaining unclosed Unity rich-text tags, and continues with the remaining entries. It uses the same optional `.overrides.txt` file next to the output to select a duplicate candidate. A result containing unresolved duplicates is reported as `Verified: false`.

Only non-empty XLIFF targets that differ from the English source are exported. The converter does not require a target to be `final`: it also accepts targets whose XLIFF state is `translated` or `needs-translation`. The command prints a state summary so you can see which kinds of records were present in the download. A target that is identical to the English source is intentionally skipped.

For example, the generated Dutch file can be selected in the installed config without changing the Bulgarian file:

```ini
TranslationFile = labels.nl.txt
```

Copy `labels.nl.txt` into:

```text
Travellers Rest\Windows\BepInEx\plugins\TravellersRest Translation\translations\
```

If a local draft has not been exported by Crowdin yet, it will not appear in the XLIFF. In that case, use the separate local CSV/TSV injection workflow first, then export XLIFF again.

The XLSX converter is also available for workbooks that contain populated language columns:

```powershell
python .\scripts\convert-xlsx-to-labels.py `
  --input "C:\path\to\I2Loc TravellersRest Localization.xlsx" `
  --language Bulgarian `
  --output ".\translations\labels.bg.txt"
```
