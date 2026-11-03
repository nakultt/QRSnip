"""Generates the sample pictures used by PictureScanTests.

Usage:  python tests/QRSnip.Core.Tests/Samples/generate.py   (requires Pillow and qrcode)

The output is deterministic, so re-running it only changes files when this script changes.
"""

import random
from pathlib import Path

import qrcode
from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent


def code(text: str, box: int, border: int = 2) -> Image.Image:
    qr = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_M, box_size=box, border=border)
    qr.add_data(text)
    qr.make(fit=True)
    return qr.make_image(fill_color="black", back_color="white").convert("RGBA")


def paper(w: int, h: int, seed: int) -> Image.Image:
    """Off-white background with faint text lines, like a page or a slide."""
    rnd = random.Random(seed)
    img = Image.new("RGBA", (w, h), (236, 234, 228, 255))
    draw = ImageDraw.Draw(img)
    for y in range(40, h - 40, 28):
        x = 40
        while x < w - 80:
            length = rnd.randint(20, 90)
            draw.rectangle([x, y, min(x + length, w - 40), y + 6], fill=(170, 170, 175, 255))
            x += length + rnd.randint(8, 16)
    return img


def rotated():
    img = paper(800, 600, 1)
    qr = code("https://example.com/rotated", 7).rotate(30, expand=True, resample=Image.BICUBIC, fillcolor=(0, 0, 0, 0))
    img.alpha_composite(qr, (400 - qr.width // 2, 300 - qr.height // 2))
    img.convert("RGB").save(HERE / "rotated.png", optimize=True)


def small():
    img = paper(640, 480, 2)
    qr = code("small", 2)  # 2 px modules
    img.alpha_composite(qr, (420, 300))
    img.convert("RGB").save(HERE / "small.png", optimize=True)


def multiple():
    img = paper(900, 500, 3)
    for text, box, pos in [("one", 8, (40, 60)), ("two", 6, (380, 120)), ("https://example.com/three", 5, (620, 200))]:
        img.alpha_composite(code(text, box), pos)
    img.convert("RGB").save(HERE / "multiple.png", optimize=True)


def transparent():
    """Black modules on a fully transparent background, as exported by many QR generators."""
    img = code("transparent", 6, border=0)
    img.putalpha(img.convert("L").point(lambda v: 255 if v < 128 else 0))
    canvas = Image.new("RGBA", (img.width + 40, img.height + 40), (0, 0, 0, 0))
    canvas.alpha_composite(img, (20, 20))
    canvas.save(HERE / "transparent.png", optimize=True)


def photo():
    """A slightly tilted, blurred and noisy shot of a printed code, saved as a JPEG."""
    img = Image.new("RGB", (1600, 1200), (120, 112, 100))
    sheet = paper(900, 700, 4).convert("RGB")
    sheet.paste(code("https://example.com/photo", 12).convert("RGB"), (250, 120))
    sheet = sheet.rotate(-7, expand=True, resample=Image.BICUBIC, fillcolor=(120, 112, 100))
    img.paste(sheet, (300, 200))
    img = img.filter(ImageFilter.GaussianBlur(1.6))
    noise = Image.effect_noise(img.size, 18).convert("RGB")
    img = Image.blend(img, noise, 0.08)
    shade = Image.linear_gradient("L").resize(img.size).point(lambda v: 200 + v // 5)
    img = Image.composite(img, Image.new("RGB", img.size, (40, 36, 30)), shade)
    img.save(HERE / "photo.jpg", quality=82, optimize=True)


if __name__ == "__main__":
    rotated()
    small()
    multiple()
    transparent()
    photo()
    for f in sorted(HERE.iterdir()):
        if f.suffix in {".png", ".jpg"}:
            print(f"{f.name}: {f.stat().st_size // 1024} KB")
