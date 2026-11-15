"""Draws Chirpy-Setup.exe's wizard pictures from the app icon (called by tools/build_chirpy_setup.ps1).

    python tools/chirpy_setup_images.py <voicetool.ico> <output folder>

Writes wizard-large.bmp (164x314, the Welcome and Finish pages' side picture), wizard-large-2x.bmp (328x628) and
wizard-small.bmp (55x55) / wizard-small-2x.bmp (110x110) for the top-right corner of the other pages. The colors are the app's own
(dark panel, violet accent). Nothing here is read from the network.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

BG, ACCENT, TEXT, MUTED = (23, 23, 28), (169, 112, 255), (240, 240, 245), (170, 170, 185)


def font(size, bold=False):
    for name in (("segoeuib.ttf" if bold else "segoeui.ttf"), "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def icon(path, size):
    ico = Image.open(path)
    ico.size = max(ico.info.get("sizes") or {ico.size}, key=lambda s: s[0])  # (the largest picture in the .ico)
    ico.load()
    return ico.convert("RGBA").resize((size, size), Image.LANCZOS)


def large(path, scale):
    w, h = 164 * scale, 314 * scale
    img = Image.new("RGB", (w, h), BG)
    draw = ImageDraw.Draw(img)
    draw.rectangle([0, 0, w, 6 * scale], fill=ACCENT)
    size = 64 * scale
    glyph = icon(path, size)
    img.paste(glyph, ((w - size) // 2, 70 * scale), glyph)
    title = font(26 * scale, bold=True)
    draw.text((w // 2, 160 * scale), "Chirpy", font=title, fill=TEXT, anchor="mm")
    small = font(10 * scale)
    for i, line in enumerate(("Talk anywhere and", "it types. Speech", "becomes text on", "this PC.")):
        draw.text((w // 2, (192 + 15 * i) * scale), line, font=small, fill=MUTED, anchor="mm")
    return img


def small(path, scale):
    side = 55 * scale
    img = Image.new("RGB", (side, side), BG)
    glyph = icon(path, 40 * scale)
    img.paste(glyph, ((side - 40 * scale) // 2,) * 2, glyph)
    return img


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    ico, out = sys.argv[1], Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    large(ico, 1).save(out / "wizard-large.bmp")
    large(ico, 2).save(out / "wizard-large-2x.bmp")
    small(ico, 1).save(out / "wizard-small.bmp")
    small(ico, 2).save(out / "wizard-small-2x.bmp")
    print(out)
