# Bug report: long freeze when loading a save

## Summary

Loading a save file freezes the game for **13 seconds**, caused by the game attempting
sprite outline generation on a texture that is not readable, **431 times in a row** in a
single load.

One frame of a 20.4 s save load accounts for 13.3 s of it. It is not slow loading spread
over time — it is a single frozen frame.

## Version and system

| | |
| --- | --- |
| Game version | v0.7.6.12.0 |
| Unity | 2022.3.62.7762112 |
| OS | Windows |
| GPU memory | 15,997 MB (`SystemInfo.graphicsMemorySize`) |
| Resolution | 1920x1080 |
| Quality | High |
| Mods | 2 installed, one of which is a read-only profiler that only counts. **Reproduced in the base game's own logs.** |

## Steps to reproduce

1. Start the game.
2. Load any save (the save used here is `SaveFile-12-10-2026-12-55-26.save`, 195 KB).
3. The loading screen appears as normal, then the game freezes for about 13 seconds
   before gameplay begins.

100% reproducible. The freeze happens once per save load.

## What the profiler measured

Frame times sampled from inside the game every frame:

| Phase | Duration |
| --- | --- |
| Title screen to main menu | 13–16 s |
| **Save load to gameplay** | **20.4 s** |
| — of which a **single frame** | **13.3 s** |
| — the remaining 422 frames | 47 ms average |
| Ordinary scene changes (mine, bathhouse, city) | 0.06–0.22 s |

So the problem is not scene loading in general. It is specific to loading a save, and
within that load it is one frame.

## Evidence from Player.log

The 13.3 s freeze is bracketed by a burst of identical errors:

```
13.26 s freeze, 14:42:44.243 to 14:42:57.503
    Sprite outline generation failed - could not read texture pixel data. Did you forget
    to make the texture readable?
    (this line appears 431 times in a single log file)
```

Immediately before the burst begins:

```
Unloading 7 Unused Serialized files (Serialized files now loaded: 0)
[PhysX] Initialized MultithreadedTaskDispatcher with 16 workers.
UnloadTime: 4.712900 ms
SceneReferences Awake
No players at the start of the scene. Spawning...
New Player added as player 1
BobAnimation SetSleep
Initializing MinePiece Pool...
Texture 'Cave_Tileset' is not readable, so Compress cannot work. You can make the
    texture readable in the Texture Import Settings.
    → then the 431 "Sprite outline generation failed" lines
```

Two textures are named as not readable, each once:

| Texture | Message |
| --- | --- |
| `Cave_Tileset` | is not readable, so Compress cannot work |
| `new_autumntileset_padding` | is not readable, so Compress cannot work |

## Why I think this is a real bug and not expected behaviour

1. **It is logged as a failure.** The message itself says `could not read texture pixel
   data` and `Did you forget to make the texture readable?`, which reads as something
   the code did not intend.
2. **The same operation is retried 431 times.** If the texture cannot be read, it cannot
   be read on the 431st attempt either. Whatever is calling this does not cache the
   negative result, so the cost is paid every time.
3. **It correlates exactly with the freeze.** The burst occupies the 13.3 s frame.
4. **The texture is named in the log.** `Cave_Tileset` is reported as not readable
   immediately before the burst starts, and the burst begins right after
   `Initializing MinePiece Pool...`, which suggests the outlines are being generated for
   mine tile sprites.
5. **The game recovers every time**, which is why the log contains 431 error lines rather
   than one crash. So this is a performance bug, not a crash — but the cost is a
   visible multi-second freeze on every save load.

## Possible fixes on your side

Either of these looks sufficient, and the first is probably a one-line change in the
editor:

1. **Mark the textures readable.** `Cave_Tileset` and `new_autumntileset_padding` are
   reported as not readable. Enabling **Read/Write Enabled** in their Texture Import
   Settings would make the pixel read succeed. Caveat: it doubles the texture's memory
   cost, so caching is the cheaper option if that matters.
2. **Cache the failure instead of retrying.** If a sprite outline cannot be generated
   because its texture is not readable, remember that per texture (or per sprite) and
   skip the attempt afterwards. That alone should remove 430 of the 431 attempts.
3. **Batch or defer the generation.** If the outlines are genuinely needed, generating
   them across frames instead of in one frame would turn a 13 s freeze into a longer but
   smooth load.

I do not know which code path does this, because the string is not present in the
decompiled `Assembly-CSharp` scripts nor findable in the shipped assemblies by text
search, so I cannot point at the method. The log lines above should identify it from
your side.

## Extra: a texture compression warning

Unrelated to the freeze, but visible on every load:

```
Texture 'Cave_Tileset' is not readable, so Compress cannot work.
Texture 'new_autumntileset_padding' is not readable, so Compress cannot work.
```

Same root cause as above, so fixing the readability may also silence these.

## How this was measured, if useful

A BepInEx profiler plugin, written for this investigation:

- Times every scene load separately from gameplay, using `SceneManager` events.
- Times real frames through a Harmony postfix on `WorldTime.Update`, because on this game
  a BepInEx plugin component never receives `Start`, `Update`, `LateUpdate` or `OnGUI`.
- Records every frame of 2 s or more and captures the last 40 lines of `Player.log` at
  that moment, which is how the 431-line burst was found.
- Reports memory: 492 MB managed and 2.72 GB Unity reserved after a save load, **with zero
  growth over 43 s of play**, so this is not a leak.

Happy to share the plugin or the full report if it would help.

## Attachments

- `Player.log` (61 KB) — full log containing all 431 lines
- Profiler report with frame timings and the captured context per freeze

The log is at:

```
%USERPROFILE%\AppData\LocalLow\Louqou\TravellersRest\Player.log
```
