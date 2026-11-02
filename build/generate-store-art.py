"""Renders Microsoft Store listing art (box art, poster art, app tile icon) from the app logo.

Usage:  python build/generate-store-art.py      (requires Pillow)
"""

import importlib.util
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "store"

spec = importlib.util.spec_from_file_location("assets", Path(__file__).with_name("generate-assets.py"))
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)

TOP, BOTTOM = (236, 242, 251), (205, 219, 242)


def background(width: int, height: int) -> Image.Image:
    img = Image.new("RGBA", (width, height))
    draw = ImageDraw.Draw(img)
    for y in range(height):
        t = y / (height - 1)
        draw.line([(0, y), (width, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    return img


def centered(canvas: Image.Image, mark: Image.Image, top: int | None = None):
    x = (canvas.width - mark.width) // 2
    y = (canvas.height - mark.height) // 2 if top is None else top
    canvas.alpha_composite(mark, (x, y))


def main():
    OUT.mkdir(parents=True, exist_ok=True)

    # 1:1 box art (Store recommends 2160x2160).
    box = background(2160, 2160)
    centered(box, assets.logo(1200))
    box.convert("RGB").save(OUT / "boxart-2160x2160.png", optimize=True)

    # 2:3 poster art (Store recommends 1440x2160).
    poster = background(1440, 2160)
    centered(poster, assets.logo(860), top=480)
    draw = ImageDraw.Draw(poster)
    title = ImageFont.truetype("seguisb.ttf", 150)
    tagline = ImageFont.truetype("segoeui.ttf", 64)
    for text, font, y, color in (("QRSnip", title, 1440, (24, 32, 48)),
                                 ("Scan any QR code on your screen", tagline, 1650, (70, 84, 110))):
        w = draw.textlength(text, font=font)
        draw.text(((poster.width - w) / 2, y), text, font=font, fill=color)
    poster.convert("RGB").save(OUT / "poster-1440x2160.png", optimize=True)

    # 1:1 app tile icon, transparent (300x300).
    assets.logo(300).save(OUT / "tile-icon-300x300.png", optimize=True)


if __name__ == "__main__":
    main()
