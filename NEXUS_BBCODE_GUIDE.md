# Nexus mod-description style guide

Use this guide when creating or updating a Nexus Mods description for any mod in this repository.

The aim is a description that is easy to scan on desktop and mobile, works with Nexus's limited BBCode parser, and remains readable without decorative formatting.

## Preferred structure

Keep this order unless a mod has a strong reason to differ:

1. Centered title
2. One short `What it does` paragraph
3. `Highlights` flat bullet list
4. `Quick start`
5. `Requirements`
6. `Installation`
7. Feature-specific configuration sections
8. `Notes`, `Contributing`, and `Source and issues` when relevant

Start with the player's outcome. Do not open with implementation details, source paths, or a long explanation of every config setting.

## Use these formatting patterns

- Use `[b]Section title[/b]` for headings.
- A restrained centered title is fine: `[center][color=#e6b85c][b]MOD NAME[/b][/color][/center]`.
- Use one flat `[list]` for feature highlights and `[list=1]` for installation steps.
- Use `[b]SettingName[/b]` for config keys and paths instead of a full-width `[code]` block.
- Use a light color only to emphasize the first few words of a feature bullet, not entire paragraphs.
- Use ordinary paragraphs for explanations. Keep them short: one subject per paragraph.
- Use links in the form `[url=https://example.com]Visible label[/url]`; do not put quotes around the URL.

## Avoid these patterns

- Do not use `[code]` blocks for short paths, configuration keys, or one-line examples. They occupy the full content width and make the next line look detached.
- Do not rely on `[size]`, custom fonts, tables, columns, or nested lists. They are inconsistent in Nexus descriptions.
- Do not make a wall of colored text. Color is an accent, not the layout.
- Do not nest or cross BBCode blocks. A tag opened inside another tag must close before its parent closes.
- Do not use a spoiler unless it hides a genuinely optional long section. If used, all formatting inside it must begin and end inside that spoiler.
- Do not include private research material, local reference mods, local paths, API keys, or development-only instructions.

## Configuration writing

Explain settings in player terms first, then name the setting inline.

Good:

> Set `[b]RefreshLimitPerDay=0[/b]` for unlimited Refresh, or use a positive value to limit uses per in-game day.

Avoid:

> `[code]RefreshLimitPerDay=0[/code]`

For a feature with several settings, explain the defaults and the effect of changing them. Do not paste the whole generated BepInEx configuration file into the description.

## Recommended drafting workflow

1. Write or update the source `.bbcode` file in the repository.
2. Keep the prose compact and follow the preferred structure.
3. Paste it into Nexus's **BBCode view**, not only the rich-text editor.
4. Use Nexus preview and check the published page after saving.
5. Fix any parser-specific issue in the source `.bbcode` file so the repository remains the canonical copy.

Nexus has its own parser and has changed its validation rules in the past. Treat preview on the actual mod page as the final rendering test. See Nexus's [BBCode guidance](https://forums.nexusmods.com/topic/13458901-guidance-to-update-your-bbcode-december-2023/).

## Minimal starter template

```bbcode
[center][color=#e6b85c][b]MOD NAME[/b][/color][/center]

[b]What it does[/b]
One short paragraph describing the player-facing result.

[b]Highlights[/b]
[list]
[*]First important feature.[/*]
[*]Second important feature.[/*]
[/list]

[b]Requirements[/b]
[list]
[*]Game/platform requirement.[/*]
[*]Required framework or dependency.[/*]
[/list]

[b]Installation[/b]
[list=1]
[*]First installation step.[/*]
[*]Second installation step.[/*]
[/list]

[b]Source and issues[/b]
[url=https://github.com/OWNER/REPOSITORY]GitHub[/url]
```
