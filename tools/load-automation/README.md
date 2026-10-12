# Load-save automation

Development tooling, not part of the mod. It exists so the profiler can be tested without
a person launching the game by hand every time.

| File | Purpose |
| --- | --- |
| `record-input.ahk` | Records clicks and keys into a recipe. Press **F10** to start and stop |
| `play-input.ahk` | Replays a recipe, with `--speed`, `--delay` and `--window` |
| `load-save-modded.txt` | A recording that loads a save, used by the test scripts |

## The mod needs none of this

The profiler measures by itself and writes its report by itself. AutoHotkey is needed only
to replay a save load during automated testing. Nothing here ships with the mod, and a
player never needs any of it.

## Why a recording instead of coordinates in the script

The game's menus cannot be driven reliably by a fixed coordinate:

- **Mod Config Menu adds a Mods entry to the main menu**, which shifts every other button.
  On this machine, with Mods present, the Play button sits at `920,640`; without it, Play
  is the topmost button and sits around 45 px lower. A recorded coordinate then lands on
  the wrong control and the replay silently does nothing — which is worse than failing,
  because the run appears to succeed.
- **Windows display scaling adds another coordinate space.** The desktop here is physically
  1920x1080 while `SM_CXSCREEN` reports 1280x720, so a coordinate that looks right can
  land somewhere else entirely.
- **Whatever the game draws as a cursor is not the hardware cursor**, so screenshots cannot
  be used to work out where a click went.

Recording the real sequence sidesteps all three. It is also why `play-input.ahk` activates
the game window first: an unfocused Unity game silently drops clicks.

Two approaches were tried and abandoned, and the reasons are worth keeping:

- **Image searching for the Play button** could not find it reliably, even though the same
  template was cropped directly from a screenshot of that button.
- **Keyboard navigation of the main menu** does not work; the game ignores Enter and Space
  there.

## Requirement: at least two save files

Both recordings need **at least two saves** to exist in the game.

Clicking Play loads whichever save the game picks, and the two menus pick different ones:
the menu with Mod Config Menu loads the **newest** save, and the menu without it loads the
**second newest**. Which save it is does not matter for the measurement — only that a
second one exists to be loaded. With a single save the replay appears to do nothing.

The test script checks for this before running and warns when fewer than two are found.

## The two recordings, and why there are two

| File | For | Recorded Play position |
| --- | --- | --- |
| `load-save-modded.txt` | The game **with** Mod Config Menu | `972,644` |
| `load-save-nomcm.txt` | The game **without** it | `965,722` |

The difference is about **78 px**. Mod Config Menu adds a Mods entry that shifts every other
button down, so a recording made with it misses every control without it. That failure is
silent — the replay clicks nothing, and the run then measures an idle main menu while
looking successful.

`scripts/test-profiler.ps1` and the comparison scripts detect which menu is present and
choose the matching recording automatically, so normally neither has to be passed by hand.

## Three AutoHotkey traps worth knowing

All three cost real debugging time here, and the first two fail in the same unhelpful way:
the script starts, shows its tray icon, and then does nothing at all.

1. **The files must have CRLF line endings.** With LF-only endings, AutoHotkey v2 on Windows
   reads the file, starts the process, and silently never runs the script. No error, no
   output. If you edit these files with a tool that writes LF, convert them back.

2. **`FileAppend` requires a filename.** There is no default target; omitting it is a
   runtime error rather than a style problem.

3. **`SplitPath` does not behave as its parameter names suggest.** Measured on 2.0.26 with
   `C:\dir\name.txt`:

   ```
   SplitPath(path, , &dir, &name)        -> dir = "C:\dir",  name = "txt"
   SplitPath(path, , &dir, &name, &ext)  -> name = "txt",    ext = "name"
   ```

   The fourth parameter receives only the extension, and the fifth receives the name
   *without* its extension — the opposite of the obvious reading. Building a path from the
   fourth parameter yields `...\txt`, which is not a folder that exists, and the next
   `FileAppend` fails with "the system cannot find the path specified".

   The recorder therefore resolves only the folder with `SplitPath` and leaves the filename
   exactly as given.

## The recording is specific to a setup

`load-save-modded.txt` was recorded on this machine with **Mod Config Menu plus the
translation mod** installed, at **1920x1080 fullscreen**. On another machine, or with a
different set of mods, the recorded points will be wrong.

If a replay does nothing, that is the likely reason. Re-record:

1. Start the game and wait for the main menu.
2. Run the recorder:

   ```powershell
   & 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe' .\record-input.ahk .\load-save.txt
   ```

3. Press **F10**, click through to load a save exactly as you normally would, then press
   **F10** to stop.

**Stop once the save is selected.** Do not record the Escape you press afterwards to leave
the game: an earlier recording included it, and the replay then interrupted the load it was
supposed to measure, turning a 21 s load into a 135 s one.

Then point the test script at the new file with `-Recipe`.

## Checking whether a replay actually worked

A replay that clicks nothing produces a run that looks successful and measures an idle main
menu. If the timings look implausibly good — no freeze at all — check the game's own log for
evidence that a save was loaded:

```powershell
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Louqou\TravellersRest\Player.log" `
  -Pattern 'StartEnteringGame', 'Sprite outline generation failed'
```

`StartEnteringGame` present means a save loaded. About 431 `Sprite outline generation
failed` lines means the freeze this project was investigating happened.
