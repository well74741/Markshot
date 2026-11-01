"""Generates the Markshot logo: Assets/markshot.ico (all Windows sizes) and Assets/markshot.png.

Flat style on a transparent background: four white capture-corner brackets and
a bold white "M", filling the canvas. A thin dark outline keeps the white mark
readable on light backgrounds (Explorer, light taskbars) as well as dark ones.
Run with Pillow installed:  python tools/make_icon.py
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "Assets")
FONT = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "segoeuib.ttf")

OUTLINE = (20, 23, 29)   # #14171D
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def mark_mask(s, small):
    """White parts of the logo as an 'L' mask at s x s pixels."""
    mask = Image.new("L", (s, s), 0)
    draw = ImageDraw.Draw(mask)
    margin = s * (0.08 if small else 0.06)
    arm = s * 0.27
    width = max(1, round(s * (0.1 if small else 0.085)))
    for cx, cy, dx, dy in ((margin, margin, 1, 1), (s - margin, margin, -1, 1),
                           (margin, s - margin, 1, -1), (s - margin, s - margin, -1, -1)):
        draw.line([(cx, cy + dy * arm), (cx, cy), (cx + dx * arm, cy)], fill=255, width=width, joint="curve")
        r = width / 2
        for px, py in ((cx, cy + dy * arm), (cx + dx * arm, cy), (cx, cy)):
            draw.ellipse([px - r, py - r, px + r, py + r], fill=255)

    font = ImageFont.truetype(FONT, int(s * (0.66 if small else 0.62)))
    box = draw.textbbox((0, 0), "M", font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    draw.text(((s - tw) / 2 - box[0], (s - th) / 2 - box[1]), "M", font=font, fill=255)
    return mask


def render(size):
    small = size <= 24
    scale = 8
    s = size * scale
    white = mark_mask(s, small)
    # Outline: the white mark grown by ~1px at the final size (every size), in the dark colour.
    grow = 2 * scale + 1
    outline = white.filter(ImageFilter.MaxFilter(grow))
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    image.paste(Image.new("RGBA", (s, s), OUTLINE + (255,)), (0, 0), outline)
    image.paste(Image.new("RGBA", (s, s), (255, 255, 255, 255)), (0, 0), white)
    return image.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(ASSETS, exist_ok=True)
    images = {size: render(size) for size in SIZES}
    images[256].save(os.path.join(ASSETS, "markshot.png"))
    images[256].save(os.path.join(ASSETS, "markshot.ico"), format="ICO",
                     sizes=[(s, s) for s in SIZES], append_images=[images[s] for s in SIZES if s != 256])
    print("wrote", os.path.join(ASSETS, "markshot.ico"))


if __name__ == "__main__":
    main()
