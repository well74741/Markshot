"""Generates the Markshot logo: Assets/markshot.ico (all Windows sizes) and Assets/markshot.png.

Flat style: dark rounded square, four white capture-corner brackets and a bold
white "M". Run with Pillow installed:  python tools/make_icon.py
Small sizes are drawn separately with whole-pixel strokes so the tray icon stays crisp.
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "Assets")
FONT = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "segoeuib.ttf")

BACKGROUND = (20, 23, 29, 255)   # #14171D
FOREGROUND = (255, 255, 255, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(size):
    """Draws at 8x and downsamples (large sizes) or draws on the pixel grid (small sizes)."""
    if size <= 24:
        return render_small(size)

    scale = 8
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=BACKGROUND)

    # Corner brackets.
    margin = s * 0.19
    arm = s * 0.17
    width = max(scale, round(s * 0.055))
    for cx, cy, dx, dy in ((margin, margin, 1, 1), (s - margin, margin, -1, 1),
                           (margin, s - margin, 1, -1), (s - margin, s - margin, -1, -1)):
        draw.line([(cx, cy + dy * arm), (cx, cy), (cx + dx * arm, cy)], fill=FOREGROUND, width=width, joint="curve")
        r = width / 2
        for px, py in ((cx, cy + dy * arm), (cx + dx * arm, cy)):
            draw.ellipse([px - r, py - r, px + r, py + r], fill=FOREGROUND)

    # Bold M, optically centred.
    font = ImageFont.truetype(FONT, int(s * 0.46))
    box = draw.textbbox((0, 0), "M", font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    draw.text(((s - tw) / 2 - box[0], (s - th) / 2 - box[1]), "M", font=font, fill=FOREGROUND)
    return image.resize((size, size), Image.LANCZOS)


def render_small(size):
    """16-24 px: whole-pixel brackets, the M drawn at native size."""
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=max(2, size // 5), fill=BACKGROUND)
    m = max(2, round(size * 0.16))
    arm = max(2, round(size * 0.2))
    for x0, y0, dx, dy in ((m, m, 1, 1), (size - 1 - m, m, -1, 1), (m, size - 1 - m, 1, -1),
                           (size - 1 - m, size - 1 - m, -1, -1)):
        draw.line([(x0, y0), (x0 + dx * (arm - 1), y0)], fill=FOREGROUND, width=1)
        draw.line([(x0, y0), (x0, y0 + dy * (arm - 1))], fill=FOREGROUND, width=1)

    font = ImageFont.truetype(FONT, round(size * 0.55))
    box = draw.textbbox((0, 0), "M", font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    draw.text((round((size - tw) / 2 - box[0]), round((size - th) / 2 - box[1])), "M", font=font, fill=FOREGROUND)
    return image


def main():
    os.makedirs(ASSETS, exist_ok=True)
    images = {size: render(size) for size in SIZES}
    images[256].save(os.path.join(ASSETS, "markshot.png"))
    images[256].save(os.path.join(ASSETS, "markshot.ico"), format="ICO",
                     sizes=[(s, s) for s in SIZES], append_images=[images[s] for s in SIZES if s != 256])
    print("wrote", os.path.join(ASSETS, "markshot.ico"))


if __name__ == "__main__":
    main()
