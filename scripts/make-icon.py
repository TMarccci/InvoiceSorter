"""Draws the Invoice Sorter icons (brand: indigo #6366F1 tile, white line glyph – like TMarccci/pulse).

    python scripts/make-icon.py            (needs Pillow)

Writes assets/invoicesorter.ico, assets/invoicesorter-256.png, assets/favicon-32.png and the server variants
assets/invoicesorter-server.ico, assets/invoicesorter-server-256.png.
"""
from pathlib import Path

from PIL import Image, ImageDraw

INDIGO = (99, 102, 241, 255)       # #6366F1
WHITE = (255, 255, 255, 255)
S = 1024                           # drawing size, scaled down for the icon sizes
ASSETS = Path(__file__).resolve().parent.parent / "assets"


def tile() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = 56
    d.rounded_rectangle((m, m, S - m, S - m), radius=210, fill=INDIGO)
    return img, d


def invoice_glyph(d: ImageDraw.ImageDraw) -> None:
    """A sheet with a folded corner, three text lines and a check mark."""
    w = 58
    x0, y0, x1, y1 = 300, 230, 724, 800
    fold = 130
    # Outline of the sheet with the folded top-right corner.
    pts = [(x0, y0), (x1 - fold, y0), (x1, y0 + fold), (x1, y1), (x0, y1), (x0, y0)]
    d.line(pts, fill=WHITE, width=w, joint="curve")
    for p in pts[:-1]:
        d.ellipse((p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2), fill=WHITE)
    # The fold.
    d.line([(x1 - fold, y0), (x1 - fold, y0 + fold), (x1, y0 + fold)], fill=WHITE, width=w // 2 + 8, joint="curve")
    # Text lines.
    for y, right in ((420, 600), (520, 640)):
        d.line([(390, y), (right, y)], fill=WHITE, width=46)
        d.ellipse((390 - 23, y - 23, 390 + 23, y + 23), fill=WHITE)
        d.ellipse((right - 23, y - 23, right + 23, y + 23), fill=WHITE)
    # Check mark at the bottom.
    check = [(400, 655), (470, 720), (610, 590)]
    d.line(check, fill=WHITE, width=54, joint="curve")
    for p in (check[0], check[-1]):
        d.ellipse((p[0] - 27, p[1] - 27, p[0] + 27, p[1] + 27), fill=WHITE)


def server_glyph(d: ImageDraw.ImageDraw) -> None:
    """Three stacked server units with a status dot and a line each."""
    for i in range(3):
        top = 270 + i * 175
        d.rounded_rectangle((280, top, 744, top + 135), radius=38, outline=WHITE, width=46)
        d.ellipse((345, top + 45, 391, top + 91), fill=WHITE)
        d.line([(450, top + 68), (650, top + 68)], fill=WHITE, width=40)
        for x in (450, 650):
            d.ellipse((x - 20, top + 48, x + 20, top + 88), fill=WHITE)


def save(img: Image.Image, name: str) -> None:
    ASSETS.mkdir(exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [img.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[-1].save(ASSETS / f"{name}.ico", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    img.resize((256, 256), Image.LANCZOS).save(ASSETS / f"{name}-256.png")
    print("written", ASSETS / f"{name}.ico")


if __name__ == "__main__":
    client, d = tile()
    invoice_glyph(d)
    save(client, "invoicesorter")
    client.resize((32, 32), Image.LANCZOS).save(ASSETS / "favicon-32.png")

    server, d = tile()
    server_glyph(d)
    save(server, "invoicesorter-server")
