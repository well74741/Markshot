"""The default logo in colour: tools/logo_white.ico (the white pixel art, made by make_icon.py) coloured with a
diagonal gradient, no background, every size of the icon on its own. Writes Assets/markshot.ico, markshot_dark.ico
(the same: it reads on light and dark title bars) and markshot.png.

    python tools/colorize_logo.py
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
STOPS = [(0.0, (0x2E, 0xC8, 0xFF)), (0.33, (0x5B, 0x6C, 0xFF)), (0.62, (0xB4, 0x55, 0xFF)), (0.82, (0xFF, 0x5C, 0x8A)), (1.0, (0xFF, 0xB1, 0x3D))]


def gradient(t):
    for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
        if t <= t1:
            k = (t - t0) / (t1 - t0) if t1 > t0 else 0
            return tuple(c0[i] + (c1[i] - c0[i]) * k for i in range(3))
    return STOPS[-1][1]


def colour(image):
    image = image.convert('RGBA')
    w, h = image.size
    px = image.load()
    span = max(1, w + h - 2)
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            shade = (0.299 * r + 0.587 * g + 0.114 * b) / 255  # the white art's own light and shadow stay
            c = gradient((x + y) / span)
            px[x, y] = tuple(int(round(v * (0.25 + 0.75 * shade))) for v in c) + (a,)
    return image


source = Image.open(ROOT / 'tools' / 'logo_white.ico')
sizes = sorted(source.ico.sizes())
frames = [colour(source.ico.getimage(size)) for size in sizes]
largest = frames[-1]
for name in ('markshot.ico', 'markshot_dark.ico'):
    largest.save(ROOT / 'Assets' / name, format='ICO', sizes=sizes, append_images=frames[:-1])
largest.save(ROOT / 'Assets' / 'markshot.png')
print('sizes', sizes)
