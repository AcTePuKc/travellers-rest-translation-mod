# EmployeeRefresh

`EmployeeRefresh` is a separate utility mod. It does not depend on the Bulgarian Translation Mod and the two mods do not share or overwrite each other's employee-name files.

## Quick start

1. Install the release ZIP into Travellers Rest's `Windows` folder.
2. Open the Staff menu and choose a profession tab.
3. Click **Refresh** or press `F6` to generate a new set of candidates.
4. Optionally install [Mod Config Menu](https://www.nexusmods.com/travellersrest/mods/95) to change the name file, hotkey, button spacing, and window layout in-game.

EmployeeRefresh includes Bulgarian and Italian name pools. Change `EmployeeNamesFile` in Mod Config Menu or the BepInEx config to switch between them. The selected file is reloaded immediately and any open staff list refreshes automatically.

## Name pools and localization

Place the language file in this mod's `translations` folder and select it in:

```ini
[General]
EmployeeNamesFile = employee-names.bg.txt
```

The file uses separate first-name sections and either gendered surname sections or one shared surname section. `[Surnames]` is useful for languages whose surnames do not change with gender (for example Italian); when present, it takes priority over the gendered surname sections.

```text
[MaleFirstNames]
[FemaleFirstNames]
[MaleSurnames]
[FemaleSurnames]
# or, instead of both sections above:
[Surnames]

[UI]
RefreshButton=Refresh
```

Keep first names and surnames short so they fit the staff cards and dialogs. The recommended maximum is 9 characters per name part; shorter is better. EmployeeRefresh warns about longer entries but does not silently cut them off.

The employee-name loader is intentionally owned only by EmployeeRefresh. This prevents the translation DLL and the utility DLL from patching the same game method and producing unpredictable results.

To create a new name pool, copy one of the bundled `employee-names.*.txt` files, rename it, and select the new filename through `EmployeeNamesFile`. You can contribute it through a [pull request](https://github.com/AcTePuKc/travellers-rest-translation-mod/pulls) or publish a separate pool on Nexus.

## Refreshing candidates

The game exposes its own candidate generator as `StaffManager.CreateRandomOptionsWorkers()`. EmployeeRefresh calls that original method when the configured hotkey is pressed, then refreshes the open hire-staff panel. The default hotkey is `F6`:

```ini
[General]
RefreshHotkey = F6
```

Leave the value empty to disable the hotkey. The button text is loaded from the same selected employee-name file, so each language can provide its own label in `[UI]`:

```text
[UI]
RefreshButton=Обнови
```

If this entry is missing, EmployeeRefresh tries the game's `Refresh` key and finally uses the config fallback.

The visible button is cloned from the game's own staff-button template. `ButtonLabel` is only the final fallback:

```ini
[General]
ShowButton = true
ButtonLabel = Refresh
```

`ButtonGap` controls the spacing from the game's original button in that same local UI layout. Start around `20`; very large values can move the button outside the visible UI area.

The staff hiring window can also be enlarged without changing the game font:

```ini
[Window]
HeightMultiplier = 1.25
GrowUpward = true
VerticalOffset = 40
```

The value is a percentage multiplier: `1.25` means 125% of the original height. `VerticalOffset` moves the whole hiring window upward in UI units; increase it if the bottom controls are too close to the screen edge.
