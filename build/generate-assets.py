"""Renders the QRSnip logo and every MSIX / Store asset size from code.

Usage:  python build/generate-assets.py      (requires Pillow)
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "QRSnip" / "Assets"
DOCS = ROOT / "docs" / "images"

SS = 4  # supersampling factor

# Windows 11 Fluent palette: light from the top, soft depth, no backplate.
CARD_TOP, CARD_BOTTOM, CARD_EDGE = (255, 255, 255), (226, 232, 241), (176, 188, 206)
INK_TOP, INK_BOTTOM = (46, 64, 99), (16, 28, 52)
BRACKET_TOP, BRACKET_BOTTOM = (91, 206, 255), (0, 95, 210)
SCAN_LEFT, SCAN_RIGHT = (120, 240, 255), (60, 170, 255)


def vgradient(w: int, h: int, top, bottom) -> Image.Image:
    """Vertical gradient (Fluent icons are lit from above)."""
    col = Image.new("RGBA", (1, 256))
    for y in range(256):
        t = y / 255
        col.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return col.resize((w, h), Image.BICUBIC)


def dgradient(w: int, h: int, start, end) -> Image.Image:
    """Diagonal gradient, top-left to bottom-right."""
    small = Image.new("RGBA", (64, 64))
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126
            small.putpixel((x, y), tuple(round(a + (b - a) * t) for a, b in zip(start, end)) + (255,))
    return small.resize((w, h), Image.BICUBIC)


def fill(canvas: Image.Image, mask: Image.Image, paint: Image.Image):
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    layer.paste(paint, (0, 0), mask)
    canvas.alpha_composite(layer)


def shadow(canvas: Image.Image, mask: Image.Image, big: int, dy: float, blur: float, alpha: int):
    soft = mask.filter(ImageFilter.GaussianBlur(big * blur))
    soft = ImageChops.multiply(soft, Image.new("L", soft.size, alpha))
    layer = Image.new("RGBA", canvas.size, (8, 20, 45, 0))
    layer.putalpha(soft)
    canvas.alpha_composite(layer, (0, round(big * dy)))


def logo(size: int) -> Image.Image:
    big = size * SS
    u = lambda v: v * big  # noqa: E731 - unit to pixels
    simple = size <= 24
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    def rect_mask(boxes, radius):
        m = Image.new("L", (big, big), 0)
        d = ImageDraw.Draw(m)
        for x0, y0, x1, y1 in boxes:
            d.rounded_rectangle((u(x0), u(y0), u(x1), u(y1)), radius=u(radius), fill=255)
        return m

    # 1. The "screen" card.
    card = (0.17, 0.14, 0.83, 0.80) if not simple else (0.14, 0.12, 0.86, 0.84)
    card_mask = rect_mask([card], 0.09)
    shadow(canvas, card_mask, big, 0.025, 0.035, 90)
    edge = rect_mask([card], 0.09)
    fill(canvas, edge, vgradient(big, big, CARD_EDGE, CARD_EDGE))
    inset = 0.012 if not simple else 0.03
    inner = rect_mask([(card[0] + inset, card[1] + inset, card[2] - inset, card[3] - inset)], 0.08)
    fill(canvas, inner, vgradient(big, big, CARD_TOP, CARD_BOTTOM))

    # 2. QR code printed on the card.
    cx0, cy0, cx1, cy1 = card
    pad = 0.1 if not simple else 0.11
    qx0, qy0, qx1 = cx0 + pad, cy0 + pad, cx1 - pad
    q = qx1 - qx0
    f = q * (0.36 if not simple else 0.42)  # finder size
    ink = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(ink)

    def finder(x, y):
        ring = f * 0.2
        d.rounded_rectangle((u(x), u(y), u(x + f), u(y + f)), radius=u(f * 0.22), fill=255)
        d.rounded_rectangle((u(x + ring), u(y + ring), u(x + f - ring), u(y + f - ring)), radius=u(f * 0.1), fill=0)
        if not simple:
            c = f * 0.36
            d.rounded_rectangle((u(x + c), u(y + c), u(x + f - c), u(y + f - c)), radius=u(f * 0.06), fill=255)

    finder(qx0, qy0)
    finder(qx1 - f, qy0)
    finder(qx0, qy0 + q - f)
    if not simple:
        # Data modules: a 3x3 checker in the remaining corner.
        step = f / 3
        m = step * 0.86
        bx, by = qx1 - f, qy0 + q - f
        for gx, gy in ((0, 0), (2, 0), (1, 1), (0, 2), (2, 2)):
            x, y = bx + gx * step, by + gy * step
            d.rounded_rectangle((u(x), u(y), u(x + m), u(y + m)), radius=u(m * 0.25), fill=255)
    fill(canvas, ink, vgradient(big, big, INK_TOP, INK_BOTTOM))

    # 3. Scan brackets in front, extending past the card.
    t, l = (0.085, 0.25) if not simple else (0.11, 0.3)
    x0, y0, x1, y1 = (0.06, 0.04, 0.94, 0.92) if not simple else (0.02, 0.02, 0.98, 0.98)
    boxes = []
    for left in (True, False):
        for top in (True, False):
            hx = x0 if left else x1 - l
            vy = y0 if top else y1 - l
            hy = y0 if top else y1 - t
            vx = x0 if left else x1 - t
            boxes += [(hx, hy, hx + l, hy + t), (vx, vy, vx + t, vy + l)]
    brackets = rect_mask(boxes, t / 2)
    shadow(canvas, brackets, big, 0.015, 0.02, 70)
    fill(canvas, brackets, dgradient(big, big, BRACKET_TOP, BRACKET_BOTTOM))

    # Top highlight on the brackets for a little gloss.
    gloss = ImageChops.multiply(brackets, vgradient(big, big, (90,) * 3, (0,) * 3).convert("L"))
    hl = Image.new("RGBA", (big, big), (255, 255, 255, 0))
    hl.putalpha(gloss)
    canvas.alpha_composite(hl)

    # 4. Scan line with a soft glow.
    if not simple:
        line = rect_mask([(0.12, 0.455, 0.88, 0.49)], 0.0175)
        glow = line.filter(ImageFilter.GaussianBlur(big * 0.025))
        glow_layer = Image.new("RGBA", (big, big), SCAN_LEFT + (0,))
        glow_layer.putalpha(ImageChops.multiply(glow, Image.new("L", glow.size, 200)))
        canvas.alpha_composite(glow_layer)
        fill(canvas, line, dgradient(big, big, SCAN_LEFT, SCAN_RIGHT))

    return canvas.resize((size, size), Image.LANCZOS)


def tile(width: int, height: int, mark: float) -> Image.Image:
    """Transparent tile with the logo centred (Windows draws the accent plate)."""
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    side = round(min(width, height) * mark)
    img.alpha_composite(logo(side), ((width - side) // 2, (height - side) // 2))
    return img


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)
    for old in ASSETS.glob("*.png"):
        old.unlink()

    scales = (100, 125, 150, 200, 400)
    for scale in scales:
        f = scale / 100
        logo(round(44 * f)).save(ASSETS / f"Square44x44Logo.scale-{scale}.png")
        tile(round(150 * f), round(150 * f), 0.5).save(ASSETS / f"Square150x150Logo.scale-{scale}.png")
        tile(round(310 * f), round(150 * f), 0.5).save(ASSETS / f"Wide310x150Logo.scale-{scale}.png")
        tile(round(71 * f), round(71 * f), 0.6).save(ASSETS / f"SmallTile.scale-{scale}.png")
        tile(round(310 * f), round(310 * f), 0.45).save(ASSETS / f"LargeTile.scale-{scale}.png")
        logo(round(50 * f)).save(ASSETS / f"StoreLogo.scale-{scale}.png")
        tile(round(620 * f), round(300 * f), 0.45).save(ASSETS / f"SplashScreen.scale-{scale}.png")

    for target in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        icon = logo(target)
        icon.save(ASSETS / f"Square44x44Logo.targetsize-{target}.png")
        icon.save(ASSETS / f"Square44x44Logo.targetsize-{target}_altform-unplated.png")
        icon.save(ASSETS / f"Square44x44Logo.targetsize-{target}_altform-lightunplated.png")

    # Unqualified copy for use inside the app UI.
    logo(256).save(ASSETS / "AppLogo.png")

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    logo(256).save(ASSETS / "AppIcon.ico", sizes=[(s, s) for s in sizes])

    logo(300).save(DOCS / "logo-300.png")  # Store listing / README
    logo(1024).save(DOCS / "logo-1024.png")


if __name__ == "__main__":
    main()
