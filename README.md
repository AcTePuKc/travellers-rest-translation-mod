# Travellers Rest Translation Loader

Minimal, language-neutral BepInEx 5 plugin for testing translations in Travellers Rest.

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

The generated config is:

```text
Travellers Rest\Windows\BepInEx\config\actepukc.travellersrest.translation.cfg
```

Select a language file in the config:

```ini
TranslationFile = labels.example.txt
```

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

The converter keeps each entry on one physical line. Escaped sequences such as `\n` remain escaped and are decoded by the plugin at runtime. It also splits only at the first `=`, because translated text can contain additional equals signs.

## Convert an I2Loc XLSX workbook

The workbook format stores line breaks as real newlines inside cells. Use the XLSX converter to turn them into the escaped form required by `labels.*.txt`:

```powershell
python .\scripts\convert-xlsx-to-labels.py `
  --input "C:\path\to\I2Loc TravellersRest Localization.xlsx" `
  --language Bulgarian `
  --output ".\translations\labels.bg.txt"
```

The language name must match a workbook column exactly. Empty language cells are skipped, so an unapproved language produces an empty output file until translations are present.
