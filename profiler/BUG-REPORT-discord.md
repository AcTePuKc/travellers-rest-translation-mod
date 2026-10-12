# Discord message — copy from below

Short version for pasting into the Discord. The full report with all numbers and the
evidence files is in `BUG-REPORT-load-freeze.md`.

---

**Loading a save freezes the game for ~13 seconds, and I think I found why**

v0.7.6.12.0, Windows, 1920x1080. 100% reproducible: load any save and the game freezes for
about 13 seconds during the load.

I wrote a profiler to measure it rather than guess. Frame times from inside the game:

- title screen → main menu: 13–16 s
- **save → gameplay: 20.4 s, of which one single frame is 13.3 s**
- the other 422 frames average 47 ms
- ordinary scene changes (mine, bathhouse, city): 0.06–0.22 s

So it is not scene loading in general. It is one frozen frame while loading a save.

**What is happening in that frame**

`Player.log` contains this line **431 times**, and the burst lines up exactly with the
13.3 second frame:

```
Sprite outline generation failed - could not read texture pixel data.
Did you forget to make the texture readable?
```

And immediately before the burst starts:

```
Initializing MinePiece Pool...
Texture 'Cave_Tileset' is not readable, so Compress cannot work.
You can make the texture readable in the Texture Import Settings.
```

So it looks like sprite outlines are being generated for mine tile sprites, on a texture
that is not marked readable, and the failure is retried 431 times in a single load.

**Why I think it is a bug worth fixing**

- The log itself says `Did you forget to make the texture readable?`
- If the texture cannot be read, it cannot be read on attempt 431 either — the negative
  result does not appear to be cached
- The burst occupies the freeze
- It happens on **every** save load, not occasionally

**Possible fixes**

1. Mark `Cave_Tileset` readable in its Texture Import Settings (one checkbox, though it
   costs memory)
2. Or cache the failure: remember that this sprite/texture has no readable pixels and skip
   the attempt afterwards — that removes 430 of the 431 attempts
3. Or spread the generation across frames instead of doing it all in one

I could not find which code path does this (the message is not in the decompiled scripts
and I could not find it in the assemblies by text search), so I cannot point at the
method — but the log lines above should make it findable from your side.

Two textures are named as not readable: `Cave_Tileset` and `new_autumntileset_padding`.

**Also, for what it is worth**

- 492 MB managed and 2.72 GB Unity reserved after a load, with **zero growth over 43 s of
  play** — so this is not a leak, the memory is just loaded content.
- Happy to share the profiler plugin or the full `Player.log` if useful. Both are attached.

---

# Notes for you, not for the Discord

**What to attach:** `profiler/evidence/Player.log` (62 KB). That is the file containing
all 431 lines and is the strongest single piece of evidence.

**Optional:** `profiler/evidence/profiler-report.txt` (19 KB) shows the frame timings and
the captured context, but the log alone is probably enough and less to read.

**Two claims I deliberately kept out**, because I cannot back them:

- I do not claim the mine is required. The burst starts after `Initializing MinePiece
  Pool...`, but I have not proven that a different save, or a save in a different
  location, behaves the same.
- I do not claim the freeze is exactly 13 s for everyone. It is 13.3 s on this machine
  across runs; another machine may differ, but the cause should be the same.

**If they ask how it was measured**, the short answer is: a small BepInEx plugin that
times frames through a Harmony patch on `WorldTime.Update` and captures the last 40 lines
of `Player.log` whenever a frame exceeds 2 seconds. The plugin measures and changes
nothing about the game.
