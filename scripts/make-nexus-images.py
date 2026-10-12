r"""Builds the promotional images for the Nexus page.

Design notes, kept here so the reasoning survives:

  * A pixel font (Press Start 2P) for the title, because Travellers Rest is pixel art and a
    clean modern font looks pasted on. It is used only for the title: that font is
    unreadable in long runs, so everything else is a normal UI font.
  * Amber, because it is the game's own accent colour (menus, buttons, the clock frame).
  * The report excerpt is the point of the image. A pretty screenshot on its own says nothing
    about why anyone would want this; a report naming a 13.9 s frame and its cause does.
  * Everything sits inside a dark panel with a border, so the image still reads when Nexus
    scales it down for a gallery thumbnail.

Run with the bundled Python, which has Pillow:

    & "$env:DSH_HOME\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe" `
        scripts\make-nexus-images.py
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

W, H = 1920, 1080

# Colours taken from the game's own UI.
AMBER = (230, 184, 92)
AMBER_DIM = (150, 120, 60)
PARCHMENT = (235, 228, 210)
GREY = (155, 155, 155)
GREEN = (150, 200, 140)

FONT_DIR = Path(r"C:\Windows\Fonts")
PIXEL_FONT = FONT_DIR / "PressStart2P-Regular.ttf"
UI_FONT = FONT_DIR / "segoeui.ttf"
MONO_FONT = FONT_DIR / "consola.ttf"
MONO_BOLD = FONT_DIR / "consolab.ttf"


def font(path: Path, size: int, fallback: str = "segoeui.ttf") -> ImageFont.FreeTypeFont:
    """Loads a font, falling back to something present rather than failing."""
    for candidate in (path, FONT_DIR / fallback):
        try:
            return ImageFont.truetype(str(candidate), size)
        except OSError:
            continue
    return ImageFont.load_default()


def fit_cover(image: Image.Image, width: int, height: int) -> Image.Image:
    """Scales and centre-crops so the frame is filled rather than letterboxed."""
    scale = max(width / image.width, height / image.height)
    resized = image.resize((int(image.width * scale), int(image.height * scale)), Image.LANCZOS)
    left = (resized.width - width) // 2
    top = (resized.height - height) // 2
    return resized.crop((left, top, left + width, top + height))


def build(game_shot: Path, hud_shot: Path, out_dir: Path) -> list[Path]:
    canvas = Image.new("RGB", (W, H), (12, 12, 14))

    if game_shot.exists():
        shot = fit_cover(Image.open(game_shot).convert("RGB"), W, H)
        # Darken enough for text to sit on top without fighting the art.
        canvas = shot
        shade = Image.new("RGB", (W, H), (10, 10, 12))
        canvas = Image.blend(canvas, shade, 0.82)

    draw = ImageDraw.Draw(canvas, "RGBA")

    # A soft vignette, drawn as translucent ellipse outlines from outside in. Pillow has no
    # gradient brush, so this approximates one and reads the same at this size. Each ring is
    # clamped so its top edge stays above its bottom edge: an earlier version let them cross
    # and Pillow refused to draw.
    for i in range(26):
        inset = 40 + i * 34
        top = inset - 120
        bottom = H - inset + 120
        left = inset
        right = W - inset
        if right - left < 8 or bottom - top < 8:
            break
        draw.ellipse((left, top, right, bottom), outline=(0, 0, 0, 6 + i * 2), width=38)

    # ---------------------------------------------------------------- title
    title_font = font(PIXEL_FONT, 40)
    sub_font = font(UI_FONT, 26)

    title_top = "TRAVELLERS REST"
    title_bot = "PERFORMANCE DIAGNOSTIC"

    def centred(text: str, fnt, y: int, fill, glow: bool = False) -> None:
        box = draw.textbbox((0, 0), text, font=fnt)
        x = (W - (box[2] - box[0])) // 2 - box[0]
        if glow:
            for dx, dy in ((-3, 0), (3, 0), (0, -3), (0, 3), (-2, -2), (2, 2), (-2, 2), (2, -2)):
                draw.text((x + dx, y + dy), text, font=fnt, fill=(230, 184, 92, 70))
        draw.text((x, y), text, font=fnt, fill=fill)

    centred(title_top, title_font, 74, AMBER, glow=True)
    centred(title_bot, title_font, 150, PARCHMENT, glow=True)
    centred("Measures what your game is actually doing", sub_font, 238, PARCHMENT)

    draw.line((360, 296, W - 360, 296), fill=AMBER_DIM, width=2)

    # ---------------------------------------------------------------- report panel
    px, py, pw, ph = 110, 388, 1120, 592
    draw.rectangle((px, py, px + pw, py + ph), fill=(16, 16, 18, 242), outline=AMBER_DIM, width=2)

    mono_bold = font(MONO_BOLD, 20)
    mono = font(MONO_FONT, 17)
    draw.text((px + 26, py + 20), "profiler-report-latest.txt", font=mono_bold, fill=AMBER)

    # Hand-picked lines from a real report. The file itself is long; these are the ones that
    # answer "what does this thing actually tell me".
    report = [
        ("Loading", GREY),
        ("  unload LoadingScene -> Gameplay", PARCHMENT),
        ("      duration 20.90 s     worst frame 13888.76 ms", PARCHMENT),
        ("      average frame 48.05 ms over 446 frames", PARCHMENT),
        ("", PARCHMENT),
        ("Stalls (frames of 2 s or more)", GREY),
        ("  13.89 s freeze, 16:32:18.881 to 16:32:32.770", AMBER),
        ("    what the game logged just before it:", GREY),
        ("      Sprite outline generation failed - could not read", PARCHMENT),
        ("      texture pixel data.   (431 times in one log)", PARCHMENT),
        ("", PARCHMENT),
        ("Memory", GREY),
        ("  Managed heap : 492.8 MB      Growth: 0 B over 43 s", PARCHMENT),
        ("", PARCHMENT),
        ("Mod list", GREY),
        ("  Travellers Rest Performance Diagnostic    0.1.0", GREEN),
        ("  Mod Config Menu                           1.0.2", PARCHMENT),
        ("  Travellers Rest Translation Loader        0.1.0", PARCHMENT),
    ]
    ty = py + 62
    for text, colour in report:
        if text:
            draw.text((px + 26, ty), text, font=mono, fill=colour)
        ty += 28

    # ---------------------------------------------------------------- status panel preview
    if hud_shot.exists():
        hud = Image.open(hud_shot).convert("RGB")
        # The crop is mostly empty game around the panel. Trim the blank top and right so the
        # panel fills the frame; an earlier attempt pasted the untrimmed crop and the preview
        # showed almost nothing but dark pixels.
        hud = hud.crop((0, 60, min(470, hud.width), min(210, hud.height)))

        hx, hy = 1296, 396
        draw.rectangle((hx - 14, hy - 14, hx + hud.width + 14, hy + hud.height + 14),
                       fill=(18, 18, 20, 255), outline=AMBER_DIM, width=2)
        canvas.paste(hud, (hx, hy))

        hud_font = font(UI_FONT, 19)
        draw.text((hx, hy + hud.height + 26), "and a status panel in the corner,", font=hud_font, fill=PARCHMENT)
        draw.text((hx, hy + hud.height + 54), "so a long wait is not mistaken for a hang", font=hud_font, fill=PARCHMENT)

    # ---------------------------------------------------------------- bullets
    bullet_font = font(UI_FONT, 20)
    bullets = [
        "Loading measured automatically, no key to press",
        "Freezes recorded with the game log that explains them",
        "Duplicate mods flagged",
        "Memory growth over a session",
    ]
    by = 686
    for text in bullets:
        draw.ellipse((1300, by + 9, 1307, by + 16), fill=AMBER)
        draw.text((1318, by), text, font=bullet_font, fill=PARCHMENT)
        by += 44

    # ---------------------------------------------------------------- footer
    foot = font(MONO_FONT, 16)
    draw.text((110, H - 70), "Requires BepInEx 5", font=foot, fill=GREY)
    label = "github.com/AcTePuKc"
    fw = draw.textbbox((0, 0), label, font=foot)[2]
    draw.text((W - 110 - fw, H - 70), label, font=foot, fill=GREY)

    # ---------------------------------------------------------------- save
    out_dir.mkdir(parents=True, exist_ok=True)
    written = []

    main = out_dir / "nexus-01-main.png"
    canvas.save(main, "PNG", optimize=True)
    written.append(main)

    thumb = out_dir / "nexus-02-thumbnail.png"
    canvas.resize((1280, 720), Image.LANCZOS).save(thumb, "PNG", optimize=True)
    written.append(thumb)

    return written


def main() -> int:
    repo = Path(__file__).resolve().parent.parent
    game_shot = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"C:\WebStuff\_analysis\shot-bl.png")
    hud_shot = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(r"C:\WebStuff\_analysis\shot-bl-crop.png")
    out_dir = Path(sys.argv[3]) if len(sys.argv) > 3 else repo / "profiler" / "screenshots"

    if not game_shot.exists():
        print(f"game screenshot not found: {game_shot}")
        print("capture one first, or pass a path as the first argument")
        return 1

    for path in build(game_shot, hud_shot, out_dir):
        print(f"{path}  {path.stat().st_size:,} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
