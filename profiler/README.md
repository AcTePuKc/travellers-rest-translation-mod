# Travellers Rest Profiler

A BepInEx 5 plugin that measures what the game is actually doing, so performance work
can be based on numbers instead of guesses. It changes no gameplay and writes nothing
into saves.

The report is written to:

```
BepInEx/TravellersRestProfiler/profiler-report-latest.txt
```

## The constraint that shaped this plugin

On this game, a BepInEx plugin component gets its `Awake` and then nothing else. Unity
never calls `Start`, `Update`, `LateUpdate` or `OnGUI` on it, `FindObjectOfType` does not
find it afterwards, and coroutines started on objects created during `Awake` never tick
either. All of that was verified on the running game, not assumed.

The only Unity callbacks observed to fire reliably are `SceneManager.sceneLoaded` and
`SceneManager.sceneUnloaded`. They are what this plugin runs on. The consequences,
stated plainly:

* **Loading is measured.** Wall-clock time from scene unload until the next scene is
  ready — which is where the real problem in this game turned out to be.
* **It is not ticked per frame**, so there is no frame-by-frame sampling, no per-frame
  allocation tracking and no per-mod cost attribution. Those need a Harmony patch on a
  method the game calls every frame, which is the technique the other mods in this
  repository use. That is a deliberate next step, not an oversight.
* Where a frame figure cannot exist, the report says so rather than printing `0`. A
  counter that cannot be non-zero is not evidence, and an earlier version of this plugin
  drew a wrong conclusion by treating one as if it were.

## What it measures

| Measurement | How |
| --- | --- |
| **Loading windows** | Every scene load: wall time and memory added |
| Installed mods | BepInEx's `Chainloader.PluginInfos`: name, version, GUID |
| Mods installed twice | The same GUID loaded more than once |
| Harmony attributes per mod | Patch methods declared in the assembly. A **static** property of the code, not a runtime cost |
| Memory | Managed heap, Unity reserved, Unity allocated, Mono used |
| **Memory growth** | Baseline taken 45 s after the game becomes interactive, then compared |
| Frame timing | Sampled automatically for the duration of each load |

## Use

**There is nothing to do.** Start the game and load a save. Loading is measured from the
moment the plugin loads, frame sampling switches on by itself for the duration of each
load, and the report is rewritten every 20 seconds.

That is deliberate: a measurement must not depend on a UI that might not draw.

### A useful session

1. Start the game and load a save.
2. Play normally for two or three minutes.
3. Read `BepInEx/TravellersRestProfiler/profiler-report-latest.txt`.

That is the whole procedure. No key press, no window, no configuration.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `ReportIntervalSeconds` | `20` | Rewrite the report this often. `0` disables the timed rewrite |
| `StallThresholdMs` | `2000` | A frame at least this long is recorded as a freeze, with the game log from just before it |
| `ShowHud` | `true` | Show the status panel. `false` makes the mod completely invisible |
| `HudMargin` | `0` | Distance of the panel from the screen edge in pixels. `0` picks a value from your screen height |
| `HudOnLeft` | `false` | Put the panel on the left instead of the right |

`HudMargin` and `HudOnLeft` exist because the panel sits over whatever is in that corner. If
it covers something you need, move it rather than turning it off.

**There are no keys and no in-game window.** An earlier version had both, and they were
removed deliberately: on this game Unity never calls `OnGUI` on a BepInEx plugin
component, so the window could not be relied on to draw. What remains is measurement that
starts by itself and a report that writes itself.

## Measured results

Startup sequence, across many runs on the developer's machine:

| Window | Duration |
| --- | --- |
| Title screen to menu | **13–17 s** |
| Save to gameplay | **20–22 s** |
| Ordinary scene changes | 0.06–0.22 s |

The cost is concentrated in loading a save, and within that load **one single frame
accounts for most of it** — up to 14.4 s of a 21.9 s load. That is a freeze, not slow
loading.

A load window longer than 120 s is reported as `[SUSPECT]`, because that is a window which
stayed open while the game sat idle and not a load. One such window read 240 s and would
have been reported as a four-minute load had it not been flagged.

### The cause of the freeze

The profiler records every frame at or above the stall threshold together with the game's
own log lines from just before it. That is what made the freeze explainable:

```
14.37 s freeze, 16:29:47.335 to 16:30:01.701  (during a load)
  what the game logged just before it:
    Sprite outline generation failed - could not read texture pixel data.
    Sprite outline generation failed - could not read texture pixel data.
    ... (431 times in a single log)
```

The game attempts to generate sprite outlines from textures that are **not readable**,
which is a Unity import setting. Each attempt fails and costs time, and during one save
load it happens **431 times**. The game recovers each time — which is why the log
contains 431 lines instead of one crash — but it pays the cost on every attempt.

Related lines in the same log confirm the same root cause:

```
Texture 'Cave_Tileset' is not readable, so Compress cannot work.
Texture 'new_autumntileset_padding' is not readable, so Compress cannot work.
```

**This is the game's own asset pipeline, not a mod.** Two mods were installed, one of
which is this profiler and measures rather than acts. Nothing here is reachable by
patching a plugin.

The generator could not be located in the decompiled scripts or in any shipped assembly
by its message text, so the responsible method is not identifiable by string search. A
fix would mean patching whatever calls Unity's pixel read on an unreadable texture — a
change to the game's rendering behaviour, not a mod.

## What it deliberately does not do

* **Per-mod CPU and allocation attribution.** Dynamic patching of every plugin method is
  unreliable, and a wrong attribution is worse than an honest gap.
* **Frame-by-frame sampling through a plugin component.** This game never ticks one, so
  the per-frame heartbeat comes from a Harmony patch on `WorldTime.Update` instead.
* **Deep memory profiling through Unity's Profiler API.** Only the counters that work
  without it are read.


## Watching it work

A small status panel appears in the top-right corner of the screen while the game runs:

```
Travellers Rest profiler
Loading a scene (2 finished so far)
elapsed 00:57
loads 2   freezes 2
```

It exists because a long wait is otherwise indistinguishable from a hang. Turn it off with
`ShowHud = false` in the config, and the mod becomes completely invisible.

`status.txt`, next to the report, carries the same information for anything that wants to
read it.

### How the panel is drawn

This is worth recording, because getting here took several wrong turns.

The game never calls `OnGUI` on the **BepInEx plugin component** — it is created during
`Awake`, and a component created that early never receives GUI callbacks here. That was
originally mistaken for the game never calling `OnGUI` at all, and an interactive window was
removed on that basis.

The truth came out of an error message: drawing from a per-frame Harmony patch produced
*"You can only call GUI functions from inside OnGUI"*, which proved the game does render a
GUI — just not for a component created too early. So the panel is drawn by a small component
created **lazily, from inside a frame**, once the game is running. Created that way, it
receives `OnGUI` normally.

## Installation

**Requirement: BepInEx 5.** Travellers Rest does not ship with it — the player installs
BepInEx 5.4.x into the game folder first, which every other mod for this game also
requires. BepInEx is not bundled here and should not be, because it belongs to its own
project and players usually already have it.

1. Install BepInEx 5.4.x into the Travellers Rest folder if it is not already there.
2. Extract this archive into the same game folder, so that the DLL lands at:

   ```
   BepInEx/plugins/TravellersRestProfiler/TravellersRestProfiler.dll
   ```

3. Start the game. That is the whole installation.

**Nothing else is required.** The mod builds to a single DLL that references only BepInEx
and Unity assemblies. The game provides the Unity ones, BepInEx provides the rest, and
`package-profiler.ps1` fails the build if that ever stops being true — so there is no
hidden second download.

Uninstalling is deleting the `BepInEx/plugins/TravellersRestProfiler/` folder. The mod
writes nothing outside its own report folder and touches no saves.

## Building

```powershell
dotnet build .\src\TravellersRestProfiler\TravellersRestProfiler.csproj -c Release
```

The DLL is produced at `src/TravellersRestProfiler/bin/Release/net472/TravellersRestProfiler.dll`.
Copy it into `BepInEx/plugins/TravellersRestProfiler/`.

## Testing, unattended

Verifying this plugin used to mean asking someone to launch the game, wait, and report
back. That is slow and it produced several rounds of "the plugin looks dead but we do not
know why". The repository now automates the whole cycle.

```powershell
# build, deploy, launch, wait for the main menu, replay saved inputs, read the report, stop
.\scripts\test-profiler.ps1 -Replay -MenuWait 100 -Seconds 70

# just check the build and startup path
.\scripts\test-profiler.ps1 -Seconds 100

# re-read the report from the last run without launching anything
.\scripts\test-profiler.ps1 -InspectReport 'F:\...\BepInEx\TravellersRestProfiler\profiler-report-latest.txt'
```

It exits non-zero if any check fails and prints the report when something is wrong.

| Check | Why it exists |
| --- | --- |
| builds with no errors | Nothing is deployed from a failed build |
| no game process before deploy | A running game locks the DLL and silently deploys nothing |
| deployed DLL matches the build | Catches exactly that silent failure |
| reports cleared before the run | A stale report otherwise looks like a fresh success |
| runtime initialised, heartbeat live | The failures that actually happened here were silent |
| per-frame hook attached | Without it there is no frame timing at all |
| a load was observed | The measurement this plugin exists for |
| frame timing captured | `measured frames (N` must be non-zero |
| report written with no key press | The report must not depend on the UI |

### Recording a new input sequence

The game's menus cannot be driven by guessing coordinates. Record the real sequence once
and replay it forever.

```powershell
# start the recorder, then press F10 in the game to start and stop
& 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe' .\scripts\record-input.ahk
```

Mouse clicks with coordinates, `Escape`, `Space`, `Enter`, `Tab`, arrows, `F9`, `F5`,
`F11`, `Backspace` and `Delete` are recorded, with the timing between them. The result
is a plain text recipe that `play-input.ahk` replays, optionally with `--speed` or
`--delay` to cover a cold start being slower than the recording.

The title screen takes roughly 75–95 seconds to become interactive, which is why
`-MenuWait` exists and why a full automated run takes about three minutes. That figure is
measured, not assumed.
