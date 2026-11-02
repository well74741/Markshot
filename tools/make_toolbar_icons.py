"""Draws toolbar icons that are generated rather than hand-made: arrow.png and undo.png.

White on transparent, 64x64, drawn 8x supersampled; the mark stays inside the
6..58 box shared by the other icons in Assets/Icons, with the same stroke
weight (about 6 px at 64), so every toolbar icon reads at the same size.
Run:  python tools/make_toolbar_icons.py
"""
import math
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICONS = os.path.join(ROOT, "Assets", "Icons")
S = 8          # supersampling
SIZE = 64
WIDTH = 6.0    # stroke weight at 64 px


def canvas():
    image = Image.new("L", (SIZE * S, SIZE * S), 0)
    return image, ImageDraw.Draw(image)


def stroke(draw, points, width=WIDTH):
    pts = [(x * S, y * S) for x, y in points]
    draw.line(pts, fill=255, width=round(width * S), joint="curve")
    r = width * S / 2
    for x, y in pts:  # round caps and joins
        draw.ellipse([x - r, y - r, x + r, y + r], fill=255)


def save(mask, name):
    small = mask.resize((SIZE, SIZE), Image.LANCZOS)
    out = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    out.putalpha(small)
    out.save(os.path.join(ICONS, name))
    print("wrote", name, small.getbbox())


def arrow():
    mask, draw = canvas()
    tip = (56.5, 7.5)
    d = (1 / math.sqrt(2), -1 / math.sqrt(2))   # shaft direction (towards the tip)
    p = (d[1] * -1, d[0])                        # perpendicular
    head_len, head_half = 22, 11.5
    base = (tip[0] - d[0] * head_len, tip[1] - d[1] * head_len)
    stroke(draw, [(9, 55), (base[0] + d[0] * 2, base[1] + d[1] * 2)])
    left = (base[0] + p[0] * head_half, base[1] + p[1] * head_half)
    right = (base[0] - p[0] * head_half, base[1] - p[1] * head_half)
    draw.polygon([(tip[0] * S, tip[1] * S), (left[0] * S, left[1] * S), (right[0] * S, right[1] * S)], fill=255)
    # soften the head's base corners like the round strokes (the tip stays sharp)
    for x, y in (left, right):
        r = 1.6 * S
        draw.ellipse([x * S - r, y * S - r, x * S + r, y * S + r], fill=255)
    save(mask, "arrow.png")


def undo():
    # Left-pointing chevron, a line to the right, a U-turn down and back left.
    mask, draw = canvas()
    top, bottom = 20.0, 54.0
    radius = (bottom - top) / 2
    turn_x = 37.5
    stroke(draw, [(21, top - 11), (9, top), (21, top + 11)])
    stroke(draw, [(9, top), (turn_x, top)])
    arc = [(turn_x + radius * math.sin(a), top + radius - radius * math.cos(a))
           for a in [i * math.pi / 32 for i in range(33)]]
    stroke(draw, arc)
    stroke(draw, [(turn_x, bottom), (19, bottom)])
    save(mask, "undo.png")


def mosaic_brush():
    # Like mosaic.png (outlined frame, two filled quarter cells) but in a round frame.
    mask, draw = canvas()
    c, r = 32.0, 26.0
    ring = [(c + r * math.cos(a), c + r * math.sin(a)) for a in [i * 2 * math.pi / 96 for i in range(97)]]
    stroke(draw, ring)
    cells = Image.new("L", mask.size, 0)
    cd = ImageDraw.Draw(cells)
    inner = r - WIDTH / 2 - 2.5
    cd.ellipse([(c - inner) * S, (c - inner) * S, (c + inner) * S, (c + inner) * S], fill=255)
    quarters = Image.new("L", mask.size, 0)
    qd = ImageDraw.Draw(quarters)
    gap = 1.5
    qd.rectangle([(c + gap) * S, 0, SIZE * S, (c - gap) * S], fill=255)        # top right
    qd.rectangle([0, (c + gap) * S, (c - gap) * S, SIZE * S], fill=255)        # bottom left
    from PIL import ImageChops
    mask = ImageChops.lighter(mask, ImageChops.multiply(cells, quarters))
    save(mask, "mosaic_brush.png")


if __name__ == "__main__":
    arrow()
    undo()
    mosaic_brush()
