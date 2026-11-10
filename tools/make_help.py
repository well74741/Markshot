# Builds help/index.html: Markshot's help page (one self-contained file: text, the toolbar icons and the logo
# embedded, the animated illustrations as SVG/SMIL), opened from the tray menu and the settings window's "?".
#   python tools/make_help.py
# Needs Pillow (the icons are scaled down and embedded). Edit the texts / scenes here, not the generated page.
import base64, io, math, os, re, sys
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = re.search(r'<Version>([^<]+)</Version>', open(os.path.join(ROOT, 'Markshot.csproj'), encoding='utf-8').read()).group(1)


def png_uri(path, size, white=False):
    image = Image.open(path).convert('RGBA')
    image.thumbnail((size, size), Image.LANCZOS)
    if white:
        # The icons are masks, some dark, some white: all white here (they sit on the dark toolbar / badges).
        image = Image.merge('RGBA', (*[Image.new('L', image.size, 255)] * 3, image.getchannel('A')))
    buffer = io.BytesIO()
    image.save(buffer, 'PNG', optimize=True)
    return 'data:image/png;base64,' + base64.b64encode(buffer.getvalue()).decode()


ICONS = {name: png_uri(os.path.join(ROOT, 'Assets', 'Icons', name + '.png'), 40, white=True) for name in [
    'rect', 'arrow', 'pen', 'text', 'number', 'mosaic', 'eraser', 'undo', 'ocr', 'translate', 'scroll', 'pin', 'save',
    'copy', 'close', 'capture', 'screen_record', 'edit', 'history', 'mouse_left', 'mouse_wheel', 'mouse_right', 'record', 'pause',
    'mic', 'speaker', 'keys', 'countdown', 'new_canvas', 'open', 'rotation', 'cutout', 'spotlight', 'polyline']}
LOGO = png_uri(os.path.join(ROOT, 'Assets', 'markshot.png'), 96)

# ---------------------------------------------------------------------------------------------------- SMIL helpers

def _frames(frames, dur):
    frames = sorted(frames, key=lambda f: f[0])
    if frames[0][0] > 0:
        frames.insert(0, (0, frames[0][1]))
    if frames[-1][0] < dur:
        frames.append((dur, frames[-1][1]))
    times = []
    for t, _ in frames:
        times.append('0' if t <= 0 else '1' if t >= dur else f'{t / dur:.4f}')
    return ';'.join(times), ';'.join(str(v) for _, v in frames)


def anim(attr, frames, dur):
    """An attribute through the loop: frames = [(second, value), ...]; held before the first and after the last."""
    times, values = _frames(frames, dur)
    return f'<animate attributeName="{attr}" dur="{dur}s" repeatCount="indefinite" keyTimes="{times}" values="{values}"/>'


def move(frames, dur):
    """translate(x, y) through the loop: frames = [(second, (x, y)), ...]."""
    times, values = _frames([(t, f'{x} {y}') for t, (x, y) in frames], dur)
    return f'<animateTransform attributeName="transform" type="translate" dur="{dur}s" repeatCount="indefinite" keyTimes="{times}" values="{values}"/>'


def transform(kind, frames, dur):
    times, values = _frames(frames, dur)
    return f'<animateTransform attributeName="transform" type="{kind}" dur="{dur}s" repeatCount="indefinite" keyTimes="{times}" values="{values}"/>'


def show(windows, dur, fade=0.2):
    """Opacity: 1 during the given (from, to) windows, fading in / out over `fade` seconds."""
    frames = [(0, 0)]
    for start, end in windows:
        frames += [(max(0, start - fade), 0), (start, 1), (end, 1), (min(dur, end + fade), 0)]
    return anim('opacity', frames, dur)


# --------------------------------------------------------------------------------------------------- scene pieces

FONT = "font-family=\"'Microsoft YaHei UI','Microsoft YaHei','PingFang SC','Segoe UI',sans-serif\""
ACCENT = '#4C8DFF'
RED = '#F24E4E'


def defs(p):
    return f'''<defs>
  <linearGradient id="{p}bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#dfe8f5"/><stop offset="1" stop-color="#eef0f7"/></linearGradient>
  <linearGradient id="{p}img" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#8ec0ff"/><stop offset="1" stop-color="#b7a6ff"/></linearGradient>
  <filter id="{p}sh" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="3" stdDeviation="5" flood-color="#000" flood-opacity=".22"/></filter>
</defs>'''


def desktop(p):
    return f'<rect width="640" height="360" fill="url(#{p}bg)"/>'


def text_lines(x, y, widths, gap=13, color='#c9ced8', height=6):
    return ''.join(f'<rect x="{x}" y="{y + i * gap}" width="{w}" height="{height}" rx="3" fill="{color}"/>' for i, w in enumerate(widths))


def window_a(p):
    """A document window (left)."""
    return f'''<g filter="url(#{p}sh)">
  <rect x="40" y="36" width="330" height="250" rx="8" fill="#fff"/>
  <path d="M40 44a8 8 0 0 1 8-8h314a8 8 0 0 1 8 8v14H40z" fill="#f1f3f6"/>
  <circle cx="54" cy="47" r="3.5" fill="#ff6159"/><circle cx="66" cy="47" r="3.5" fill="#ffbd2e"/><circle cx="78" cy="47" r="3.5" fill="#28c941"/>
  <rect x="60" y="74" width="150" height="10" rx="3" fill="#2b2f36"/>
  {text_lines(60, 96, [270, 250, 262, 190])}
  <rect x="60" y="150" width="120" height="70" rx="6" fill="url(#{p}img)"/>
  <path d="M68 210l26-26 18 18 12-12 20 20z" fill="#fff" opacity=".7"/><circle cx="160" cy="166" r="7" fill="#fff" opacity=".8"/>
  {text_lines(196, 154, [150, 136, 150, 98, 128])}
  <rect x="60" y="236" width="74" height="22" rx="5" fill="{ACCENT}"/><rect x="74" y="244" width="46" height="6" rx="3" fill="#fff"/>
  <rect x="142" y="236" width="74" height="22" rx="5" fill="#eef1f6"/><rect x="156" y="244" width="46" height="6" rx="3" fill="#b7bdc8"/>
</g>'''


def window_b(p):
    """A chat window (right, in front)."""
    return f'''<g filter="url(#{p}sh)">
  <rect x="330" y="110" width="270" height="200" rx="8" fill="#fff"/>
  <path d="M330 118a8 8 0 0 1 8-8h254a8 8 0 0 1 8 8v14H330z" fill="#f1f3f6"/>
  <rect x="344" y="118" width="60" height="6" rx="3" fill="#9aa1ad"/>
  <circle cx="356" cy="152" r="10" fill="#ffd38a"/><rect x="372" y="142" width="130" height="22" rx="8" fill="#eef1f6"/>
  <circle cx="584" cy="190" r="10" fill="#9fd6a8"/><rect x="430" y="180" width="140" height="22" rx="8" fill="#d6e7ff"/>
  <circle cx="356" cy="228" r="10" fill="#ffd38a"/><rect x="372" y="218" width="96" height="22" rx="8" fill="#eef1f6"/>
  <rect x="344" y="276" width="242" height="22" rx="6" fill="#f4f5f8" stroke="#e3e6ec"/>
</g>'''


def cursor(frames, dur, extra=''):
    return f'''<g>{move(frames, dur)}{extra}
  <path d="M0 0v17l4.6-4.2 2.9 6.8 2.8-1.2-2.9-6.6h6.3z" fill="#fff" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/></g>'''


def keycap(x, y, label, windows, dur, width=None):
    width = width or 14 + 9 * len(label)
    return f'''<g opacity="0">{show(windows, dur)}
  <rect x="{x}" y="{y}" width="{width}" height="28" rx="6" fill="#2a2d33" stroke="#555a63"/>
  <rect x="{x}" y="{y + 23}" width="{width}" height="5" rx="2" fill="#16181b"/>
  <text x="{x + width / 2}" y="{y + 18}" text-anchor="middle" font-size="13" font-weight="600" fill="#fff" {FONT}>{label}</text></g>'''


def badge(x, y, label, windows, dur, icon=None):
    width = 26 + 13 * len(label) + (18 if icon else 0)
    image = f'<image class="ico" href="{ICONS[icon]}" x="{x + 9}" y="{y + 5}" width="16" height="16"/>' if icon else ''
    return f'''<g opacity="0">{show(windows, dur)}
  <rect x="{x}" y="{y}" width="{width}" height="26" rx="13" fill="#1e1e1e" opacity=".92"/>{image}
  <text x="{x + width / 2 + (9 if icon else 0)}" y="{y + 17}" text-anchor="middle" font-size="12" fill="#fff" {FONT}>{label}</text></g>'''


def toolbar(x, y, icons, highlight=None, dur=None, opacity_windows=None):
    """The capture toolbar: Markshot's own icons on its dark rounded plate. highlight = [(second, index or None)]."""
    width = len(icons) * 22 + 10
    body = [f'<rect x="{x}" y="{y}" width="{width}" height="28" rx="8" fill="#1e1e1e" opacity=".95"/>']
    if highlight:
        frames = [(t, (x + 3 + i * 22, y + 3) if i is not None else (x + 3, y + 3)) for t, i in highlight]
        visible = [(t, 1 if i is not None else 0) for t, i in highlight]
        # jumps: each change held until the next one
        frames2, visible2 = [], []
        for k, (t, value) in enumerate(frames):
            if k:
                frames2.append((t - 0.01, frames[k - 1][1]))
                visible2.append((t - 0.01, visible[k - 1][1]))
            frames2.append((t, value))
            visible2.append((t, visible[k][1]))
        body.append(f'<g>{move(frames2, dur)}<rect width="22" height="22" rx="5" fill="{ACCENT}" opacity="0">{anim("opacity", visible2, dur)}</rect></g>')
    for i, name in enumerate(icons):
        if name == '|':
            body.append(f'<rect x="{x + 15 + i * 22}" y="{y + 7}" width="1" height="14" fill="#4a4a4a"/>')
        else:
            body.append(f'<image class="ico" href="{ICONS[name]}" x="{x + 8 + i * 22}" y="{y + 6}" width="16" height="16"/>')
    fade = show(opacity_windows, dur) if opacity_windows else ''
    return f'<g{" opacity=\"0\"" if opacity_windows else ""}>{fade}{"".join(body)}</g>'


def ripple(x, y, at, dur):
    return f'''<circle cx="{x}" cy="{y}" r="0" fill="none" stroke="{ACCENT}" stroke-width="2">{anim("r", [(at, 0), (at + 0.45, 16)], dur)}{anim("opacity", [(at - 0.01, 0), (at, 1), (at + 0.45, 0)], dur)}</circle>'''


def float_button(p, x, y):
    return f'''<g filter="url(#{p}sh)"><circle cx="{x}" cy="{y}" r="19" fill="#fff"/><image href="{LOGO}" x="{x - 13}" y="{y - 13}" width="26" height="26"/></g>'''


def thumbnail(p, x, y, at, dur, content):
    """A copied picture on the floating button, then sliding away (as Markshot does)."""
    return f'''<g opacity="0">{anim("opacity", [(at - 0.01, 0), (at, 1), (at + 1.1, 1), (at + 1.5, 0)], dur)}
  <g>{move([(at, (0, 0)), (at + 1.1, (0, 0)), (at + 1.5, (30, 0))], dur)}
  <g filter="url(#{p}sh)"><rect x="{x - 19}" y="{y - 19}" width="38" height="38" rx="9" fill="#fff"/></g>
  <svg x="{x - 16}" y="{y - 16}" width="32" height="32" viewBox="{content}" preserveAspectRatio="xMidYMid slice"><use href="#{p}page"/></svg></g></g>'''


def svg(p, body, label):
    return f'<svg class="demo" viewBox="0 0 640 360" role="img" aria-label="{label}" xmlns="http://www.w3.org/2000/svg">{defs(p)}{body}</svg>'


def dim(p, frames_x, frames_y, frames_w, frames_h, dur, windows, fixed=None):
    """The darkened screen with the area (animated) left bright, and its blue frame."""
    if fixed:
        x, y, w, h = fixed
        hole = f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="#000"/>'
        frame = f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="none" stroke="{ACCENT}" stroke-width="2.5"/>'
    else:
        a = anim('x', frames_x, dur) + anim('y', frames_y, dur) + anim('width', frames_w, dur) + anim('height', frames_h, dur)
        hole = f'<rect fill="#000">{a}</rect>'
        frame = f'<rect fill="none" stroke="{ACCENT}" stroke-width="2.5">{a}</rect>'
    return f'''<mask id="{p}hole"><rect width="640" height="360" fill="#fff"/>{hole}</mask>
<g opacity="0">{show(windows, dur, 0.3)}<rect width="640" height="360" fill="#000" opacity=".45" mask="url(#{p}hole)"/>{frame}</g>'''


# --------------------------------------------------------------------------------------------------------- scenes

def scene_capture():
    p, d = 'cap', 9
    x0, y0, x1, y1 = 56, 66, 300, 228
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>' + window_b(p) + float_button(p, 600, 320)
    body += dim(p, [(0, x0)], [(0, y0)], [(1.4, 0), (2.8, x1 - x0)], [(1.4, 0), (2.8, y1 - y0)], d, [(1.1, 5.2)])
    body += f'''<g opacity="0">{show([(1.5, 5.2)], d)}<rect x="{x0}" y="{y0 - 24}" width="74" height="19" rx="4" fill="#1e1e1e" opacity=".9"/>
  <text x="{x0 + 37}" y="{y0 - 10}" text-anchor="middle" font-size="11" fill="#fff" {FONT}>244 × 162</text></g>'''
    bar = ['rect', 'arrow', 'pen', 'text', 'number', 'mosaic', 'eraser', '|', 'ocr', 'pin', 'save', 'copy']
    bx = x1 - (len(bar) * 22 + 10)
    body += toolbar(bx, y1 + 8, bar, opacity_windows=[(3.0, 5.2)], dur=d)
    copy_x, copy_y = bx + 8 + 11 * 22 + 8, y1 + 8 + 14
    body += ripple(copy_x, copy_y, 4.7, d)
    body += keycap(560, 24, 'F1', [(0.5, 1.5)], d)
    body += thumbnail(p, 600, 320, 5.4, d, f'{x0} {y0} {x1 - x0} {y1 - y0}')
    body += cursor([(0, (420, 250)), (1.1, (x0, y0)), (1.4, (x0, y0)), (2.8, (x1, y1)), (3.5, (x1, y1)), (4.4, (copy_x, copy_y)), (5.4, (copy_x, copy_y)), (7.2, (470, 260))], d)
    return svg(p, body, '按 F1，拖出区域，点复制')


def scene_detect():
    p, d = 'det', 9
    wb = (330, 110, 270, 200)
    wa = (40, 36, 330, 250)
    im = (60, 150, 120, 70)
    seq = [(0, wb), (1.6, wb), (1.9, wa), (3.2, wa), (3.5, im), (4.8, im), (5.1, wa), (8.6, wa)]
    fx = [(t, r[0]) for t, r in seq]
    fy = [(t, r[1]) for t, r in seq]
    fw = [(t, r[2]) for t, r in seq]
    fh = [(t, r[3]) for t, r in seq]
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>' + window_b(p)
    body += dim(p, fx, fy, fw, fh, d, [(0.2, 8.4)])
    body += badge(270, 322, '滚轮 ↓ 进入元素', [(3.0, 4.2)], d, 'mouse_wheel')
    body += badge(270, 322, '滚轮 ↑ 回到窗口', [(4.6, 5.6)], d, 'mouse_wheel')
    body += badge(270, 322, '单击 = 截取这个范围', [(6.0, 7.6)], d, 'mouse_left')
    body += ripple(130, 182, 6.2, d)
    body += cursor([(0, (480, 230)), (1.4, (470, 200)), (2.0, (250, 190)), (2.6, (130, 182)), (8.6, (130, 182))], d)
    return svg(p, body, '悬停自动框住窗口和元素，滚轮切换范围')


def scene_annotate():
    p, d = 'ann', 11
    sx, sy, sw, sh = 50, 60, 300, 210
    body = desktop(p) + window_a(p) + window_b(p) + dim(p, None, None, None, None, d, [(0, d)], fixed=(sx, sy, sw, sh))
    bar = ['rect', 'arrow', 'pen', 'text', 'number', 'mosaic', 'eraser', '|', 'ocr', 'pin', 'save', 'copy']
    bx, by = sx + sw - (len(bar) * 22 + 10), sy + sh + 8
    body += toolbar(bx, by, bar, highlight=[(0, None), (0.4, 0), (2.5, 1), (4.7, 4), (6.3, 3), (8.8, None)], dur=d)
    fade = anim('opacity', [(0, 1), (9.6, 1), (10.4, 0), (10.9, 0), (11, 1)], d)
    rect = f'<rect x="56" y="70" fill="none" stroke="{RED}" stroke-width="3" rx="2">{anim("width", [(0.8, 0), (2.0, 162)], d)}{anim("height", [(0.8, 0), (2.0, 22)], d)}</rect>'
    # The arrow grows from where the drag starts; its head rides on the end, turned along the line.
    (x1, y1), (x2, y2) = (290, 236), (222, 96)
    full = math.hypot(x2 - x1, y2 - y1)
    ux, uy = (x2 - x1) / full, (y2 - y1) / full
    sx_, sy_ = x2 - ux * 12, y2 - uy * 12  # the line stops under the head
    shaft = full - 12
    arrow = (f'<path d="M{x1} {y1} L{sx_:.1f} {sy_:.1f}" fill="none" stroke="{RED}" stroke-width="4" stroke-dasharray="{shaft + 2:.1f}" stroke-dashoffset="{shaft + 2:.1f}">'
             f'{anim("stroke-dashoffset", [(2.9, round(shaft + 2, 1)), (3.8, 0)], d)}</path>'
             f'<g opacity="0">{anim("opacity", [(2.89, 0), (2.9, 1)], d)}'
             f'<polygon points="0,0 -17,-8.5 -17,8.5" fill="{RED}">'
             f'<animateMotion dur="{d}s" repeatCount="indefinite" rotate="auto" path="M{x1} {y1} L{x2} {y2}" calcMode="linear" '
             f'keyPoints="0;0;1;1" keyTimes="0;{2.9 / d:.4f};{3.8 / d:.4f};1"/></polygon></g>')
    number = (f'<g opacity="0">{anim("opacity", [(5.1, 0), (5.15, 1)], d)}<circle cx="246" cy="81" fill="{RED}">{anim("r", [(5.1, 0), (5.35, 12), (5.5, 10)], d)}</circle>'
              f'<text x="246" y="86" text-anchor="middle" font-size="13" font-weight="700" fill="#fff" {FONT}>1</text></g>')
    text = (f'<clipPath id="{p}type"><rect x="190" y="226" height="30">{anim("width", [(6.8, 0), (8.2, 160)], d)}</rect></clipPath>'
            f'<g clip-path="url(#{p}type)"><text x="196" y="246" font-size="15" font-weight="600" fill="{RED}" {FONT}>标题再大一点</text></g>')
    body += f'<g>{fade}{rect}{arrow}{number}{text}</g>'
    body += cursor([(0, (330, 250)), (0.8, (56, 70)), (2.0, (218, 92)), (2.6, (300, 240)), (2.9, (290, 236)), (3.8, (222, 96)), (4.6, (246, 81)),
                    (5.1, (246, 81)), (6.2, (194, 236)), (6.8, (194, 236)), (8.2, (300, 240)), (10, (330, 250))], d)
    return svg(p, body, '画矩形、箭头、序号和文字')


def scene_pin():
    p, d = 'pin', 11
    x, y, w, h = 56, 66, 244, 162
    cx, cy = x + w / 2, y + h / 2
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>' + window_b(p)
    body += dim(p, None, None, None, None, d, [(0, 1.2)], fixed=(x, y, w, h))
    bar = ['rect', 'arrow', 'pen', 'text', 'number', 'mosaic', 'eraser', '|', 'ocr', 'pin', 'save', 'copy']
    bx = x + w - (len(bar) * 22 + 10)
    body += toolbar(bx, y + h + 8, bar, opacity_windows=[(0, 1.2)], dur=d)
    pin_x, pin_y = bx + 8 + 9 * 22 + 8, y + h + 22
    body += ripple(pin_x, pin_y, 0.9, d)
    # the pin: the area's picture, with a shadow; dragged, zoomed, faded, turned, then F
    picture = f'<svg x="{x}" y="{y}" width="{w}" height="{h}" viewBox="{x} {y} {w} {h}"><use href="#{p}page"/></svg>'
    pin = (f'<g filter="url(#{p}sh)"><rect x="{x}" y="{y}" width="{w}" height="{h}" fill="#fff"/></g>{picture}'
           f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="none" stroke="{ACCENT}" stroke-width="1"/>')
    rotate = transform('rotate', [(6.4, f'0 {cx} {cy}'), (6.8, f'-90 {cx} {cy}'), (8.2, f'-90 {cx} {cy}'), (8.6, f'0 {cx} {cy}')], d)
    scale = transform('scale', [(3.6, 1), (4.3, 1.25), (8.2, 1.25), (8.6, 1)], d)
    body += (f'<g opacity="0">{anim("opacity", [(1.15, 0), (1.25, 1), (5.0, 1), (5.6, 0.55), (8.2, 0.55), (8.6, 1), (10.2, 1), (10.6, 0)], d)}'
             f'<g>{move([(1.6, (0, 0)), (2.8, (250, 50)), (11, (250, 50))], d)}'
             f'<g transform="translate({cx} {cy})"><g>{scale}<g transform="translate({-cx} {-cy})"><g>{rotate}{pin}</g></g></g></g></g></g>')
    body += badge(250, 322, '拖动移动', [(1.6, 2.9)], d)
    body += badge(250, 322, '滚轮缩放', [(3.4, 4.5)], d, 'mouse_wheel')
    body += badge(240, 322, 'Ctrl + 滚轮 透明度', [(4.8, 5.9)], d, 'mouse_wheel')
    body += keycap(560, 24, 'E', [(6.2, 7.2)], d)
    body += keycap(560, 24, 'F', [(7.9, 9.0)], d)
    body += cursor([(0, (320, 260)), (0.7, (pin_x, pin_y)), (1.3, (pin_x, pin_y)), (1.6, (150, 140)), (2.8, (400, 190)), (11, (400, 190))], d)
    return svg(p, body, '贴图：拖动、缩放、透明度、旋转、F 复原')


def page_rows(x, y, count, width):
    """A long page: headings, paragraphs, pictures."""
    out, at = [], y
    for k in range(count):
        if k % 3 == 0:
            out.append(f'<rect x="{x}" y="{at}" width="{width * 0.45:.0f}" height="9" rx="3" fill="#2b2f36"/>')
            at += 18
        if k % 4 == 1:
            out.append(f'<rect x="{x}" y="{at}" width="{width * 0.5:.0f}" height="46" rx="5" fill="#{["9ec5ff", "ffd38a", "9fd6a8", "c9b8ff"][k % 4]}"/>')
            out.append(text_lines(x + width * 0.55, at + 4, [width * 0.4, width * 0.35, width * 0.42]))
            at += 58
        else:
            out.append(text_lines(x, at, [width * f for f in (0.95, 0.9, 0.97, 0.6)]))
            at += 62
    return ''.join(out)


def scene_long():
    p, d = 'long', 10
    vx, vy, vw, vh = 52, 62, 270, 220
    steps = [(0, 0)]
    t, offset = 1.0, 0
    while t < 7.2:
        steps += [(t, offset), (t + 0.35, offset + 70)]
        offset += 70
        t += 0.75
    page = f'<g id="{p}page">{page_rows(vx + 14, vy + 12, 26, vw - 28)}</g>'
    body = desktop(p) + f'''<g filter="url(#{p}sh)"><rect x="40" y="30" width="294" height="270" rx="8" fill="#fff"/></g>
<path d="M40 38a8 8 0 0 1 8-8h278a8 8 0 0 1 8 8v14H40z" fill="#f1f3f6"/>
<clipPath id="{p}view"><rect x="{vx}" y="{vy}" width="{vw}" height="{vh}"/></clipPath>
<g clip-path="url(#{p}view)"><g>{move([(t_, (0, -o)) for t_, o in steps], d)}{page}</g></g>'''
    body += dim(p, None, None, None, None, d, [(0, d)], fixed=(vx, vy, vw, vh))
    body += toolbar(vx + vw - 142, vy + vh + 8, ['scroll', '|', 'ocr', 'edit', 'copy', 'save', 'close'], dur=d)
    # the preview: what is stitched so far, growing; scaled so the whole long picture fits the card in the end
    full = vh + offset
    k = 244 / full
    grow = [(t_, (vh + o) * k) for t_, o in steps]
    width = vw * k
    left = 384 + (172 - width) / 2
    body += f'''<g filter="url(#{p}sh)"><rect x="370" y="24" width="200" height="312" rx="10" fill="#fff"/></g>
<text x="384" y="46" font-size="12" font-weight="600" fill="#2b2f36" {FONT}>长图预览</text>
<clipPath id="{p}grown"><rect x="{left}" y="56" width="{width}">{anim("height", grow, d)}</rect></clipPath>
<g clip-path="url(#{p}grown)"><rect x="{left}" y="56" width="{width}" height="{full * k}" fill="#fff"/>
<g transform="translate({left} 56) scale({k:.4f}) translate({-vx} {-vy})"><use href="#{p}page"/></g></g>
<rect x="{left}" y="56" width="{width}" fill="none" stroke="{ACCENT}" stroke-width="1">{anim("height", grow, d)}</rect>
<circle cx="390" cy="318" r="4" fill="#2fbf71"/><text x="400" y="322" font-size="11" fill="#5d6470" {FONT}>正在拼接 · 来回滚动不会重复</text>'''
    body += badge(110, 322, '在框里滚动', [(0.9, 7.6)], d, 'mouse_wheel')
    body += cursor([(0, (250, 300)), (0.8, (200, 170)), (d, (200, 170))], d)
    return svg(p, body, '长截图：在框里滚动，自动拼接')


def scene_translate():
    p, d = 'tr', 9
    x, y, w, h = 70, 60, 300, 200
    english = f'''<text x="{x + 20}" y="{y + 34}" font-size="17" font-weight="700" fill="#1d2027" {FONT}>Export settings</text>
<text x="{x + 20}" y="{y + 72}" font-size="13" fill="#4a505b" {FONT}>File name</text><rect x="{x + 120}" y="{y + 58}" width="160" height="20" rx="4" fill="#f4f5f8" stroke="#dfe2e8"/>
<text x="{x + 20}" y="{y + 104}" font-size="13" fill="#4a505b" {FONT}>Format</text><rect x="{x + 120}" y="{y + 90}" width="160" height="20" rx="4" fill="#f4f5f8" stroke="#dfe2e8"/>
<text x="{x + 20}" y="{y + 136}" font-size="13" fill="#4a505b" {FONT}>Keep metadata</text>
<rect x="{x + 196}" y="{y + 160}" width="84" height="26" rx="6" fill="{ACCENT}"/><text x="{x + 238}" y="{y + 177}" text-anchor="middle" font-size="13" fill="#fff" {FONT}>Export</text>'''
    chinese = f'''<rect x="{x + 14}" y="{y + 14}" width="160" height="28" fill="#fff"/><text x="{x + 20}" y="{y + 34}" font-size="17" font-weight="700" fill="#1d2027" {FONT}>导出设置</text>
<rect x="{x + 14}" y="{y + 58}" width="100" height="20" fill="#fff"/><text x="{x + 20}" y="{y + 72}" font-size="13" fill="#4a505b" {FONT}>文件名</text>
<rect x="{x + 14}" y="{y + 90}" width="100" height="20" fill="#fff"/><text x="{x + 20}" y="{y + 104}" font-size="13" fill="#4a505b" {FONT}>格式</text>
<rect x="{x + 14}" y="{y + 122}" width="120" height="20" fill="#fff"/><text x="{x + 20}" y="{y + 136}" font-size="13" fill="#4a505b" {FONT}>保留元数据</text>
<rect x="{x + 198}" y="{y + 162}" width="80" height="22" fill="{ACCENT}"/><text x="{x + 238}" y="{y + 177}" text-anchor="middle" font-size="13" fill="#fff" {FONT}>导出</text>'''
    body = desktop(p) + f'<g filter="url(#{p}sh)"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="#fff"/></g>' + english
    body += f'<g opacity="0">{anim("opacity", [(2.3, 0), (2.9, 1), (6.8, 1), (7.3, 0)], d)}{chinese}</g>'
    body += dim(p, None, None, None, None, d, [(0, d)], fixed=(x - 6, y - 6, w + 12, h + 12))
    bar = ['rect', 'arrow', 'text', 'mosaic', '|', 'ocr', 'translate', 'pin', 'copy']
    bx = x + w + 6 - (len(bar) * 22 + 10)
    body += toolbar(bx, y + h + 14, bar, highlight=[(0, None), (1.6, 6), (7.0, None)], dur=d)
    body += f'''<g opacity="0">{anim("opacity", [(2.6, 0), (2.9, 1), (6.8, 1), (7.1, 0)], d)}
  <rect x="{x + w - 122}" y="{y - 38}" width="128" height="24" rx="12" fill="#1e1e1e"/>
  <text x="{x + w - 64}" y="{y - 22}" text-anchor="middle" font-size="11.5" fill="#fff" {FONT}>English → 中文  ✕</text></g>'''
    body += ripple(bx + 8 + 6 * 22 + 8, y + h + 28, 1.6, d)
    body += cursor([(0, (440, 300)), (1.4, (bx + 8 + 6 * 22 + 8, y + h + 28)), (2.2, (bx + 8 + 6 * 22 + 8, y + h + 28)), (3.0, (470, 230)), (d, (470, 230))], d)
    return svg(p, body, '原位翻译：选区里的文字原地换成译文')


def scene_winmouse():
    p, d = 'wm', 9
    x0, y0, x1, y1 = 196, 146, 362, 228
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>' + float_button(p, 600, 320)
    a = anim('width', [(1.0, 0), (2.4, x1 - x0)], d) + anim('height', [(1.0, 0), (2.4, y1 - y0)], d)
    body += f'''<g opacity="0">{anim("opacity", [(0.95, 0), (1.0, 1), (2.9, 1), (3.0, 0)], d)}
  <rect x="{x0}" y="{y0}" rx="8" fill="{ACCENT}" fill-opacity=".1" stroke="{ACCENT}" stroke-width="2">{a}</rect></g>'''
    body += keycap(500, 24, 'Win', [(0.4, 2.9)], d, 46)
    body += badge(452, 62, '+ 左键拖动', [(0.6, 2.9)], d, 'mouse_left')
    body += thumbnail(p, 600, 320, 3.1, d, f'{x0} {y0} {x1 - x0} {y1 - y0}')
    # the text recognition result
    body += f'''<g opacity="0">{anim("opacity", [(3.3, 0), (3.7, 1), (7.8, 1), (8.2, 0)], d)}
  <g filter="url(#{p}sh)"><rect x="392" y="70" width="216" height="168" rx="10" fill="#fff"/></g>
  <text x="408" y="94" font-size="12" font-weight="600" fill="#2b2f36" {FONT}>文字识别</text>
  {text_lines(408, 108, [180, 164, 176, 120, 150], 14, '#9aa1ad')}
  <rect x="408" y="202" width="62" height="22" rx="5" fill="{ACCENT}"/><text x="439" y="217" text-anchor="middle" font-size="11" fill="#fff" {FONT}>复制</text>
  <rect x="476" y="202" width="76" height="22" rx="5" fill="#eef1f6"/><text x="514" y="217" text-anchor="middle" font-size="11" fill="#2b2f36" {FONT}>贴到屏幕</text></g>'''
    body += cursor([(0, (150, 300)), (1.0, (x0, y0)), (2.4, (x1, y1)), (d, (x1, y1))], d)
    return svg(p, body, 'Win + 鼠标：不进截图界面，直接划区域')


def scene_float():
    p, d = 'fl', 8
    bx, by = 560, 300
    icons = ['capture', 'screen_record', 'ocr', 'edit', 'history']
    row_w = len(icons) * 30 + 32
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>'
    clip = f'<clipPath id="{p}row"><rect y="{by - 20}" height="40" rx="20">{anim("x", [(1.0, bx), (1.35, bx - row_w), (4.2, bx - row_w), (4.55, bx)], d)}{anim("width", [(1.0, 0), (1.35, row_w + 20), (4.2, row_w + 20), (4.55, 0)], d)}</rect></clipPath>'
    row = ''.join(f'<image class="ico" href="{ICONS[name]}" x="{bx - row_w + 10 + i * 30}" y="{by - 9}" width="18" height="18"/>' for i, name in enumerate(icons))
    body += clip + f'<g clip-path="url(#{p}row)" filter="url(#{p}sh)"><rect x="{bx - row_w - 4}" y="{by - 20}" width="{row_w + 30}" height="40" rx="20" fill="#1e1e1e"/></g><g clip-path="url(#{p}row)">{row}</g>'
    body += float_button(p, bx, by)
    tip_x = bx - row_w + 10 + 2 * 30 + 9
    body += f'''<g opacity="0">{show([(2.4, 3.8)], d)}<rect x="{tip_x - 32}" y="{by - 56}" width="64" height="24" rx="6" fill="#1e1e1e"/>
  <text x="{tip_x}" y="{by - 40}" text-anchor="middle" font-size="11" fill="#fff" {FONT}>文字识别</text></g>'''
    body += thumbnail(p, bx, by, 5.6, d, '60 150 120 70')
    body += badge(150, 316, '任何程序复制图片，按钮上都会滑过缩略图', [(5.4, 7.2)], d)
    body += cursor([(0, (420, 200)), (0.9, (bx + 4, by + 4)), (1.8, (bx + 4, by + 4)), (2.3, (tip_x, by + 2)), (3.9, (tip_x, by + 2)), (4.6, (430, 210)), (d, (430, 210))], d)
    return svg(p, body, '悬浮按钮：单击截图，悬停展开更多')



def motion(path, frames, dur, rotate=''):
    """animateMotion along a path: frames = [(second, 0..1 along it)]."""
    times, points = _frames(frames, dur)
    extra = f' rotate="{rotate}"' if rotate else ''
    return f'<animateMotion dur="{dur}s" repeatCount="indefinite" path="{path}" calcMode="linear" keyPoints="{points}" keyTimes="{times}"{extra}/>'


def pointer_on(path, frames, dur):
    return f'''<g>{motion(path, frames, dur)}
  <path d="M0 0v17l4.6-4.2 2.9 6.8 2.8-1.2-2.9-6.6h6.3z" fill="#fff" stroke="#111" stroke-width="1.2" stroke-linejoin="round"/></g>'''


def scene_lasso():
    p, d = 'las', 8
    shape = 'M64 150 C58 118 138 104 196 128 C238 146 232 214 186 232 C140 250 70 238 64 196 C62 180 64 166 64 150 Z'
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>' + window_b(p)
    # dark while drawing; once let go, the shape is cut out of the darkness
    body += f'''<g opacity="0">{show([(0.4, 6.8)], d)}
  <mask id="{p}cut"><rect width="640" height="360" fill="#fff"/><path d="{shape}" fill="#000" opacity="0">{anim("opacity", [(3.3, 0), (3.5, 1)], d)}</path></mask>
  <rect width="640" height="360" fill="#000" opacity=".45" mask="url(#{p}cut)"/></g>
<path d="{shape}" fill="none" stroke="{ACCENT}" stroke-width="2.5" stroke-dasharray="560" stroke-dashoffset="560" opacity="0">
  {anim("stroke-dashoffset", [(0.9, 560), (3.2, 0)], d)}{show([(0.9, 4.6)], d)}</path>'''
    # the result: a pin of that shape (see-through outside it, the shadow follows the outline)
    body += f'''<clipPath id="{p}clip"><path d="{shape}"/></clipPath>
<g opacity="0">{anim("opacity", [(4.6, 0), (4.9, 1), (7.4, 1), (7.8, 0)], d)}
  <g>{move([(4.6, (0, 0)), (5.6, (330, -40)), (8, (330, -40))], d)}
  <g filter="url(#{p}sh)"><path d="{shape}" fill="#fff"/></g><g clip-path="url(#{p}clip)"><use href="#{p}page"/></g>
  <path d="{shape}" fill="none" stroke="{ACCENT}" stroke-width="1"/></g></g>'''
    body += badge(220, 322, '按住右键拖动 = 套索截图', [(0.6, 3.4)], d, 'mouse_right')
    body += badge(200, 322, '松开：沿路径抠出，外面透明，可直接贴图', [(3.6, 7.2)], d)
    body += pointer_on(shape, [(0, 0), (0.9, 0), (3.2, 1), (8, 1)], d)
    return svg(p, body, '套索截图：按住右键画出任意形状')


def scene_ocr():
    p, d = 'ocr', 10
    rows = [('产品', '单价', '数量'), ('键盘', '199', '2'), ('鼠标', '89', '3'), ('显示器', '1299', '1')]
    tx, ty = 64, 92
    grid = ''.join(f'<line x1="{tx}" y1="{ty + r * 28}" x2="{tx + 240}" y2="{ty + r * 28}" stroke="#c9ced8"/>' for r in range(5))
    grid += ''.join(f'<line x1="{x}" y1="{ty}" x2="{x}" y2="{ty + 112}" stroke="#c9ced8"/>' for x in (tx, tx + 100, tx + 170, tx + 240))
    grid += ''.join(f'<text x="{tx + [10, 110, 180][c] }" y="{ty + 19 + r * 28}" font-size="13" {"font-weight=\"700\"" if r == 0 else ""} fill="#2b2f36" {FONT}>{rows[r][c]}</text>'
                    for r in range(4) for c in range(3))
    doc = f'''<g filter="url(#{p}sh)"><rect x="40" y="36" width="290" height="250" rx="8" fill="#fff"/></g>
<path d="M40 44a8 8 0 0 1 8-8h274a8 8 0 0 1 8 8v14H40z" fill="#f1f3f6"/>
<rect x="64" y="70" width="120" height="9" rx="3" fill="#2b2f36"/>{grid}
<text x="64" y="236" font-size="15" fill="#2b2f36" {FONT}>面积 S = πr²，误差 ±0.5%</text>'''
    body = desktop(p) + doc
    body += dim(p, None, None, None, None, d, [(0, 2.0)], fixed=(54, 62, 262, 186))
    body += toolbar(316 - (8 * 22 + 10), 254, ['rect', 'arrow', 'text', 'mosaic', '|', 'ocr', 'pin', 'copy'], opacity_windows=[(0, 2.0)], dur=d)
    ocr_x = 316 - (8 * 22 + 10) + 8 + 5 * 22 + 8
    body += ripple(ocr_x, 268, 1.4, d)
    # the text recognition window: 原文 as a table (表格 on), then as text
    cells = ''.join(f'<rect x="{372 + c * 70}" y="{110 + r * 26}" width="70" height="26" fill="{"#f1f3f6" if r == 0 else "#fff"}" stroke="#d5d9e0"/>'
                    f'<text x="{380 + c * 70}" y="{128 + r * 26}" font-size="12" {"font-weight=\"700\"" if r == 0 else ""} fill="#2b2f36" {FONT}>{rows[r][c]}</text>'
                    for r in range(4) for c in range(3))
    text_view = ''.join(f'<text x="374" y="{126 + r * 22}" font-size="12" fill="#2b2f36" {FONT} xml:space="preserve">{"    ".join(rows[r])}</text>' for r in range(4))
    text_view += f'<text x="374" y="{126 + 4 * 22 + 6}" font-size="12" fill="#2b2f36" {FONT}>面积 S = πr²，误差 ±0.5%</text>'
    body += f'''<g opacity="0">{anim("opacity", [(1.7, 0), (2.1, 1), (9.4, 1), (9.8, 0)], d)}
  <g filter="url(#{p}sh)"><rect x="356" y="40" width="250" height="250" rx="10" fill="#fff"/></g>
  <text x="372" y="66" font-size="13" font-weight="700" fill="#2b2f36" {FONT}>文字识别</text>
  <text x="372" y="94" font-size="12" font-weight="600" fill="#2b2f36" {FONT}>原文</text>
  <rect x="500" y="80" width="40" height="20" rx="5" fill="{ACCENT}">{anim("opacity", [(6.4, 1), (6.5, 0)], d)}</rect>
  <text x="520" y="94" text-anchor="middle" font-size="11" fill="#fff" {FONT}>{anim("fill", [(6.4, "#fff"), (6.5, "#5d6470")], d)}表格</text>
  <text x="566" y="94" text-anchor="middle" font-size="11" fill="#5d6470" {FONT}>复制</text>
  <g>{anim("opacity", [(6.4, 1), (6.6, 0)], d)}{cells}</g>
  <g opacity="0">{anim("opacity", [(6.5, 0), (6.7, 1)], d)}{text_view}</g>
  <circle cx="378" cy="270" r="3.5" fill="#2fbf71"/><text x="388" y="274" font-size="11" fill="#5d6470" {FONT}>识别完成 · 本地识别，图片不上传</text></g>'''
    body += ripple(566, 90, 3.8, d) + ripple(520, 90, 6.3, d)
    body += badge(110, 322, '表格按行列识别，复制后粘贴到 Excel / Word 仍是表格', [(4.0, 6.0)], d)
    body += badge(150, 322, '关掉「表格」：文字保留排版，列用 Tab 分隔', [(6.6, 9.2)], d)
    body += cursor([(0, (300, 300)), (1.2, (ocr_x, 268)), (1.6, (ocr_x, 268)), (3.4, (566, 90)), (4.2, (566, 90)), (6.0, (520, 90)), (6.6, (520, 90)), (8.0, (470, 230)), (d, (470, 230))], d)
    return svg(p, body, '文字识别：表格按行列识别')


def scene_editor():
    p, d = 'ed', 11
    body = f'''<defs><pattern id="{p}chk" width="16" height="16" patternUnits="userSpaceOnUse"><rect width="16" height="16" fill="#2a2a2a"/>
  <rect width="8" height="8" fill="#333"/><rect x="8" y="8" width="8" height="8" fill="#333"/></pattern></defs>
<rect width="640" height="360" fill="#151515"/>
<rect width="640" height="30" fill="#1e1e1e"/><text x="16" y="20" font-size="12" fill="#c8c8c8" {FONT}>Markshot 图片编辑器</text>
<rect x="470" y="30" width="170" height="330" fill="#1b1b1b"/><line x1="470" y1="30" x2="470" y2="360" stroke="#2d2d2f"/>
{"".join(f'<text x="486" y="{62 + i * 26}" font-size="12" fill="{"#fff" if name == "调色" else "#9da3ab"}" {FONT}>{name}</text>' for i, name in enumerate(["尺寸", "旋转与镜像", "裁切与变形", "调色"]))}
<text x="486" y="180" font-size="11" fill="#9da3ab" {FONT}>饱和度</text><rect x="486" y="190" width="130" height="3" rx="1.5" fill="#3a3a3c"/>
<circle cx="600" cy="191.5" r="6" fill="{ACCENT}">{anim("cx", [(4.6, 600), (5.8, 500), (9.6, 500), (10.4, 600)], d)}</circle>
<text x="486" y="222" font-size="11" fill="#9da3ab" {FONT}>亮度</text><rect x="486" y="232" width="130" height="3" rx="1.5" fill="#3a3a3c"/><circle cx="551" cy="233.5" r="6" fill="#6e6e72"/>
<rect x="30" y="50" width="420" height="240" fill="url(#{p}chk)"/>'''
    picture_a = f'''<g id="{p}a"><rect x="60" y="80" width="170" height="120" rx="4" fill="url(#{p}img)"/>
  <path d="M70 190l46-46 32 32 22-22 50 36z" fill="#fff" opacity=".7"/><circle cx="196" cy="108" r="12" fill="#fff" opacity=".8"/></g>'''
    grey = f'<rect x="60" y="80" width="170" height="120" rx="4" fill="#888" style="mix-blend-mode:saturation" opacity="0">{anim("opacity", [(4.6, 0), (5.8, 1), (9.6, 1), (10.4, 0)], d)}</rect>'
    picture_b = f'''<g><rect x="0" y="0" width="140" height="100" rx="4" fill="#ffd38a"/><rect x="14" y="16" width="70" height="8" rx="3" fill="#7a5a22"/>
  {text_lines(14, 34, [100, 86, 104, 70], 12, '#b98d45')}</g>'''
    body += picture_a + grey
    # B dragged in from outside, then selected and turned
    sel = f'<rect x="-3" y="-3" width="146" height="106" fill="none" stroke="{ACCENT}" stroke-width="1.5" opacity="0">{show([(2.0, 4.0)], d)}</rect>'
    handles = ''.join(f'<rect x="{x - 4}" y="{y - 4}" width="8" height="8" fill="#fff" stroke="{ACCENT}" opacity="0">{show([(2.0, 4.0)], d)}</rect>' for x, y in [(-3, -3), (143, -3), (-3, 103), (143, 103)])
    body += f'''<g>{move([(0, (520, 380)), (0.6, (520, 380)), (1.8, (260, 150)), (d, (260, 150))], d)}
  <g>{transform("rotate", [(2.4, "0 70 50"), (3.4, "-8 70 50"), (9.6, "-8 70 50"), (10.4, "0 70 50")], d)}{picture_b}{sel}{handles}</g></g>'''
    # crop: the frame closes in, outside darkened
    crop = [(6.2, (40, 64, 400, 220)), (7.2, (56, 76, 360, 196)), (8.8, (56, 76, 360, 196))]
    cx_ = anim('x', [(t, r[0]) for t, r in crop], d) + anim('y', [(t, r[1]) for t, r in crop], d) + anim('width', [(t, r[2]) for t, r in crop], d) + anim('height', [(t, r[3]) for t, r in crop], d)
    body += f'''<g opacity="0">{show([(6.2, 8.8)], d)}<mask id="{p}crop"><rect width="640" height="360" fill="#fff"/><rect fill="#000">{cx_}</rect></mask>
  <rect x="30" y="50" width="420" height="240" fill="#000" opacity=".5" mask="url(#{p}crop)"/>
  <rect fill="none" stroke="#fff" stroke-width="1.5" stroke-dasharray="6 4">{cx_}</rect></g>'''
    bar = ['rect', 'arrow', 'pen', 'text', 'mosaic', '|', 'pin', 'save', 'copy']
    body += toolbar(240 - (len(bar) * 22 + 10) // 2, 312, bar)
    pin_x = 240 - (len(bar) * 22 + 10) // 2 + 8 + 6 * 22 + 8
    body += ripple(pin_x, 326, 9.3, d)
    body += badge(42, 58, '拖入图片拼版', [(0.6, 1.9)], d)
    body += badge(42, 58, '选中后旋转、缩放', [(2.2, 3.8)], d)
    body += badge(42, 58, '调色：饱和度、亮度、曲线…', [(4.4, 6.0)], d)
    body += badge(42, 58, '裁切 · 梯形校正 · 网格变形', [(6.3, 8.7)], d)
    body += cursor([(0, (560, 330)), (0.6, (560, 330)), (1.8, (300, 180)), (2.4, (400, 150)), (3.4, (400, 170)), (4.6, (600, 191)), (5.8, (500, 191)),
                    (6.2, (40, 64)), (7.2, (56, 76)), (8.8, (56, 76)), (9.3, (pin_x, 326)), (d, (pin_x, 326))], d)
    return svg(p, body, '图片编辑器：拼版、旋转、调色、裁切')


def scene_record():
    p, d = 'rec', 11
    fx, fy, fw, fh = 70, 60, 300, 200
    body = desktop(p) + f'<g id="{p}page">{window_a(p)}</g>'
    body += f'''<mask id="{p}hole"><rect width="640" height="360" fill="#fff"/><rect x="{fx}" y="{fy}" width="{fw}" height="{fh}" fill="#000"/></mask>
<rect width="640" height="360" fill="#000" opacity=".25" mask="url(#{p}hole)"/>
<rect x="{fx - 2}" y="{fy - 2}" width="{fw + 4}" height="{fh + 4}" fill="none" stroke-width="3" stroke="{ACCENT}">
  {anim("stroke", [(2.79, ACCENT), (2.8, RED), (6.2, RED), (6.21, ACCENT)], d)}</rect>'''
    # the bar: time · fps · record · pause · sound · mic · keys
    bx, by = fx, fy + fh + 12
    bar = f'''<rect x="{bx}" y="{by}" width="300" height="28" rx="8" fill="#1e1e1e" opacity=".95"/>
<text x="{bx + 70}" y="{by + 18}" font-size="11" fill="#c8c8c8" {FONT}>30 fps</text>
<circle cx="{bx + 140}" cy="{by + 14}" r="7" fill="{RED}">{anim("r", [(0.8, 7), (0.95, 5), (1.1, 7)], d)}</circle>
<rect x="{bx + 134}" y="{by + 8}" width="12" height="12" rx="2" fill="#fff" opacity="0">{show([(2.8, 6.2)], d, 0.01)}</rect>'''
    bar += ''.join(f'<image class="ico" href="{ICONS[name]}" x="{bx + 162 + i * 26}" y="{by + 6}" width="16" height="16"/>' for i, name in enumerate(['pause', 'speaker', 'mic', 'keys', 'close']))
    body += f'<g>{anim("opacity", [(6.4, 1), (6.6, 0), (10.6, 0), (10.8, 1)], d)}{bar}</g>'
    timer = ''
    for i in range(4):
        timer += f'<g opacity="0">{show([(2.8 + i, 3.8 + i if i < 3 else 6.2)], d, 0.01)}<text x="{bx + 12}" y="{by + 18}" font-size="13" font-weight="700" fill="#fff" {FONT}>00:0{i}</text></g>'
    timer += f'<g>{show([(0, 2.8)], d, 0.01)}<text x="{bx + 12}" y="{by + 18}" font-size="13" font-weight="700" fill="#fff" {FONT}>00:00</text></g>'
    body += f'<g>{anim("opacity", [(6.4, 1), (6.6, 0), (10.6, 0), (10.8, 1)], d)}{timer}</g>'
    # countdown
    for i, n in enumerate(['3', '2', '1']):
        body += f'<g opacity="0">{show([(1.2 + i * 0.5, 1.6 + i * 0.5)], d, 0.08)}<circle cx="{fx + fw / 2}" cy="{fy + fh / 2}" r="34" fill="#000" opacity=".55"/><text x="{fx + fw / 2}" y="{fy + fh / 2 + 14}" text-anchor="middle" font-size="40" font-weight="700" fill="#fff" {FONT}>{n}</text></g>'
    # 操作录制: the keys pressed, shown in the video
    body += f'''<g opacity="0">{show([(3.6, 5.2)], d)}<rect x="{fx + 10}" y="{fy + fh - 36}" width="92" height="26" rx="6" fill="#000" opacity=".7"/>
  <text x="{fx + 56}" y="{fy + fh - 18}" text-anchor="middle" font-size="12" font-weight="600" fill="#fff" {FONT}>Ctrl + C</text></g>'''
    # after stop: the preview, trimming, saving
    body += f'''<g opacity="0">{show([(6.6, 10.4)], d)}
  <rect x="{fx}" y="{fy + fh + 12}" width="300" height="56" rx="8" fill="#1e1e1e" opacity=".95"/>
  <rect x="{fx + 12}" y="{fy + fh + 24}" width="276" height="10" rx="5" fill="#3a3a3c"/>
  <rect y="{fy + fh + 24}" height="10" fill="{ACCENT}" opacity=".55">{anim("x", [(7.0, fx + 12), (7.8, fx + 52)], d)}{anim("width", [(7.0, 276), (7.8, 236), (8.4, 236), (9.0, 196)], d)}</rect>
  <polygon points="0,0 -6,-9 6,-9" fill="#fff">{transform("translate", [(7.0, f"{fx + 12} {fy + fh + 22}"), (7.8, f"{fx + 52} {fy + fh + 22}")], d)}</polygon>
  <polygon points="0,0 -6,-9 6,-9" fill="#fff">{transform("translate", [(8.4, f"{fx + 288} {fy + fh + 22}"), (9.0, f"{fx + 248} {fy + fh + 22}")], d)}</polygon>
  {"".join(f'<rect x="{fx + 150 + i * 48}" y="{fy + fh + 42}" width="42" height="20" rx="5" fill="{ACCENT if label == "GIF" else "#2e2e30"}"/><text x="{fx + 171 + i * 48}" y="{fy + fh + 56}" text-anchor="middle" font-size="11" fill="#fff" {FONT}>{label}</text>' for i, label in enumerate(["MP4", "GIF", "复制"]))}</g>'''
    body += ripple(bx + 140, by + 14, 0.8, d) + ripple(bx + 140, by + 14, 6.1, d) + ripple(fx + 219, fy + fh + 52, 9.6, d)
    body += badge(400, 60, '录屏框拖边移动、拖把手调大小', [(0, 1.0)], d)
    body += badge(420, 60, '倒计时后开始，框变红', [(1.2, 3.0)], d)
    body += badge(420, 60, '按下的键录进视频', [(3.4, 5.4)], d, 'keys')
    body += badge(400, 60, '停止后剪掉头尾，存成 GIF', [(6.6, 10.2)], d)
    body += cursor([(0, (300, 330)), (0.8, (bx + 140, by + 14)), (1.2, (bx + 140, by + 14)), (3.0, (150, 140)), (5.6, (220, 200)), (6.1, (bx + 140, by + 14)), (6.6, (bx + 140, by + 14)),
                    (7.0, (fx + 12, fy + fh + 22)), (7.8, (fx + 52, fy + fh + 22)), (8.4, (fx + 288, fy + fh + 22)), (9.0, (fx + 248, fy + fh + 22)), (9.6, (fx + 219, fy + fh + 52)), (d, (fx + 219, fy + fh + 52))], d)
    return svg(p, body, '录屏：选区域、录制、剪辑、存 GIF')


SCENES = {
    'capture': scene_capture(), 'detect': scene_detect(), 'annotate': scene_annotate(), 'pin': scene_pin(),
    'long': scene_long(), 'translate': scene_translate(), 'winmouse': scene_winmouse(), 'float': scene_float(),
    'lasso': scene_lasso(), 'ocr': scene_ocr(), 'editor': scene_editor(), 'record': scene_record(),
}


def figure(name, caption):
    return f'''<figure class="demo-card" data-demo="{name}">{SCENES[name]}
<figcaption><span>{caption}</span><button class="pause" type="button" title="暂停 / 继续">❚❚</button></figcaption></figure>'''


def kbd(text):
    return ' + '.join(f'<kbd>{part}</kbd>' for part in text.split(' + '))


# ----------------------------------------------------------------------------------------------------------- page

SECTIONS = []


def section(anchor, title, html):
    SECTIONS.append((anchor, title))
    return f'<section id="{anchor}"><h2>{title}</h2>{html}</section>'


content = []
WECHAT = png_uri(os.path.join(ROOT, 'Assets', 'donate_qr_wechat_light.png'), 300)


def star(text):
    """A feature Markshot has and most screenshot tools do not: bold, marked."""
    return f'<strong class="star">{text}</strong>'


content.append(section('intro', '认识 Markshot', f'''
<p class="lead">Markshot 是一款 Windows 截图工具：截图、批注、贴图、长截图、录屏、文字识别和翻译都在一个小程序里，常驻托盘，随叫随到。</p>
<div class="cards">
  <div class="card"><b>截图 + 批注</b><span>自动框住窗口和界面元素，矩形、箭头、序号、文字、马赛克、聚焦……画完直接复制。</span></div>
  <div class="card"><b>贴图</b><span>把截图钉在屏幕最上层，当参考、做对照；可缩放、旋转、调透明度、继续批注，图上的文字能直接选中复制。</span></div>
  <div class="card"><b>长截图</b><span>在框里滚动，网页、聊天记录、文档自动拼成一张长图，来回滚动不会重复。</span></div>
  <div class="card"><b>文字识别与翻译</b><span>本地识别，图片不离开电脑；能识别表格、符号和公式；原位翻译把选区里的文字直接换成译文。</span></div>
  <div class="card"><b>录屏</b><span>录制屏幕区域，导出 MP4 / GIF，可剪头尾、录声音、显示按键。</span></div>
  <div class="card"><b>图片编辑器</b><span>多张图片拼版、裁切、调色、抠色、变形，批注随时可改。</span></div>
</div>
<p>Markshot 启动后在任务栏右下角的托盘里（<img class="inline-logo" src="{LOGO}" alt="">图标），右键托盘图标可以打开所有功能、设置和本帮助。
文中 {star("加粗高亮")} 的是 Markshot 有特色、别的截图软件不常见的功能。</p>'''))

content.append(section('start', '快速上手', f'''
<ol class="steps">
  <li>按 {kbd('F1')}（或单击悬浮按钮）开始截图，屏幕会变暗。</li>
  <li>按住鼠标左键拖出要截的区域；也可以直接单击，截取鼠标下自动框住的窗口或元素。</li>
  <li>在下方工具栏里画上批注，然后按 {kbd('Enter')} 或点「复制」——截图就在剪贴板里了，到聊天窗口或文档里 {kbd('Ctrl + V')} 粘贴即可。</li>
</ol>
{figure('capture', '按 F1 → 拖出区域 → 点复制（或按 Enter）。复制后，悬浮按钮上会滑过这张图的缩略图。')}
<p class="tip">不想要这次截图？按 {kbd('Esc')} 或单击右键退出。{star("拖动中途按一下右键，就撤回这一次拖动")}——拖选区、画图形、移动元素都一样，不用松手再撤销。</p>'''))

content.append(section('capture', '截图', f'''
<h3>自动识别窗口和元素</h3>
<p>截图时不用精确拖动：鼠标经过的窗口、图片、文字段、按钮会被自动框住，单击就截取它。</p>
{figure('detect', '鼠标移到哪里框到哪里；滚轮在「元素 ↔ 窗口 ↔ 整个屏幕」之间切换范围，单击截取。')}
<ul>
  <li>{star("滚轮切换识别范围")}：向下进入更小的元素，向上回到窗口，再往上是整个屏幕；调到的档位会被记住。</li>
  <li>窗口被别的窗口挡住一部分时，框的是它的完整轮廓；带阴影边框的窗口（如微信）框的是窗口本身；Markshot 自己的设置、编辑器窗口也能框住。</li>
  <li>设置 → 保存与识别 → 截图识别：可以分别关掉元素识别、窗口识别，或调整灵敏度。</li>
</ul>
<h3>套索截图（任意形状）</h3>
{figure('lasso', '还没选区域时，按住右键拖出任意形状；松开后沿路径抠出，外面透明，贴图的阴影也沿着形状。')}
<p>{star("按住右键拖动 = 套索截图")}，按住 {kbd('Shift')} 画直线段。截出的图外面是透明的，复制到支持透明的软件或贴图时就是这个形状。</p>
<h3>调整选区</h3>
<ul>
  <li>拖动边框和八个手柄调整大小，拖动区域内部移动；方向键 1 像素微调，{kbd('Shift')} + 方向键 10 像素。</li>
  <li>{star("拖出选区时按住空格，可以移动正在画的选区")}（像 Photoshop 一样），松开空格继续调大小；按住 {kbd('Alt')} 以起点为中心，按住 {kbd('Shift')} 画正方形。</li>
  <li>{star("拖动中按右键撤回这次拖动")}，选区回到拖动前的样子。</li>
  <li>左上角的尺寸标签可以直接点击输入宽高；{kbd('R')} 载入最近用过的截图区域。</li>
  <li>{star("圆角截图")}：把选区四角内侧的小圆点往里拖，或在选好区域后滚动滚轮。复制、保存和贴图都会带上圆角（Win + 鼠标截图也会沿用）。</li>
  <li>放大镜显示像素颜色，按 {kbd('C')} 复制色值，{kbd('Shift')} 切换 RGB / HEX；单按 {kbd('Alt')} 显示贯通屏幕的十字辅助线。</li>
  <li>{kbd('Ctrl + M')} 切换截图里是否带鼠标指针。</li>
</ul>
<h3>截好之后</h3>
<table>
  <tr><th>操作</th><th>方法</th></tr>
  <tr><td>复制并退出</td><td>{kbd('Enter')} / {kbd('Ctrl + C')} / 双击选区 / 工具栏「复制」</td></tr>
  <tr><td>贴到屏幕上</td><td>{kbd('F3')} / 区域内单击鼠标中键 / 工具栏「贴图」</td></tr>
  <tr><td>另存为 / 快速保存</td><td>{kbd('Ctrl + S')} / {kbd('Ctrl + Shift + S')}</td></tr>
  <tr><td>文字识别 / 原位翻译</td><td>工具栏「文字识别」/「原位翻译」开关</td></tr>
  <tr><td>长截图、录屏</td><td>工具栏对应按钮（录制的就是选中的区域）</td></tr>
  <tr><td>退出</td><td>{kbd('Esc')} / 右键</td></tr>
</table>'''))

content.append(section('annotate', '批注', f'''
<p>选好区域后，下方工具栏就是批注工具：矩形 / 椭圆、箭头 / 直线 / 折线、画笔 / 荧光笔、文字、序号 / 聚焦、马赛克 / 模糊 / 水印、橡皮擦。</p>
{figure('annotate', '选工具 → 在区域里拖动。矩形框住重点，箭头指过去，序号标顺序，文字写说明。')}
<ul>
  <li>{star("右键撤回：画图形、移动、缩放、旋转时还没松手，按一下右键就撤回这一步")}；拿着工具时右键先放下工具，再右键才是退出。</li>
  <li>{star("画图形时按住空格，可以整体移动正在画的图形")}，松开继续画；按住 {kbd('Alt')} 从中心画，{kbd('Shift')} 画正方形 / 正圆 / 45° 线。</li>
  <li>{star("橡皮擦「抠除」")}：连同图片和所有批注一起擦成透明空洞，之后画的批注也不会盖住空洞；贴图上空洞真正透明，阴影沿着空洞的形状。</li>
  <li>{star("箭头、直线可以弯曲")}：选中后拖两端改方向，拖中间的点弯成曲线；序号拉出的箭头也能拖动箭头的方向。</li>
  <li>{star("聚焦")}：拖出一块区域，里面保持原样、外面变暗，突出重点；马赛克、模糊可以只打码一部分。</li>
  <li>工具的样式、粗细、颜色在工具栏下方的副栏里调；带 ▾ 的按钮里有同类的其他样式，{kbd('Tab')} 在它们之间切换。</li>
  <li>选中元素后滚动滚轮调粗细 / 字号，{kbd('Ctrl')} + 滚轮调透明度；{kbd('Delete')} 删除，{kbd('Ctrl + Z')} / {kbd('Ctrl + Y')} 撤销 / 重做（也可以按 {kbd('Z')} / {kbd('Shift + Z')}）。</li>
  <li>文字：选文字工具后在空白处双击开始输入，{kbd('Enter')} 完成，{kbd('Shift + Enter')} 换行。</li>
  <li>画笔按住 {kbd('Alt')} 出现吸色十字，单击吸取颜色；数位板画笔有压感。</li>
  <li>工具快捷键：{kbd('M')} 图形、{kbd('L')} 箭头、{kbd('B')} 画笔、{kbd('T')} 文字、{kbd('S')} 序号、{kbd('E')} 橡皮、{kbd('V')} 选择（设置里可改）。工具栏按钮可以长按拖动排序，设置 → 批注栏 里可以隐藏不常用的按钮。</li>
</ul>'''))

content.append(section('pin', '贴图', f'''
<p>贴图就是把图片钉在屏幕最上层：写文档时对照资料、比较两个版本、记住一串数字……</p>
{figure('pin', '截图后点「贴图」（或按 F3）。拖动移动，滚轮缩放，Ctrl + 滚轮调透明度，E 旋转，F 一步复原。')}
<ul>
  <li>{kbd('F3')}：把剪贴板里的图片（或文字）直接贴到屏幕上；把图片文件拖到悬浮按钮上也会贴在屏幕中央，GIF 动图会动。</li>
  <li>{star("贴图上的文字可以直接拖选复制")}（本地文字识别），双击选中整行。按住 {kbd('Alt')} 拖动则只移动贴图——这个键可以在 设置 → 快捷键 → 贴图窗口 里换成别的键；那里也可以关掉文字选择，关掉后在文字上拖动就是移动贴图。</li>
  <li>{kbd('空格')} 打开批注工具栏，在贴图上继续画；{kbd('Q')} / {kbd('W')} 水平 / 垂直翻转，{kbd('E')} 旋转，{star("F 一步复原")}：转过、翻过多少次都只用一个动画回到最初的大小和方向。</li>
  <li>{kbd('T')} 切换置顶，{kbd('L')} 锁定；右键菜单里有鼠标穿透（点击直接穿过贴图，{kbd('Ctrl + Alt + M')}）、原位翻译、另存为等。</li>
  <li>双击复制并关闭，{kbd('Esc')} 关闭，{kbd('Shift + Esc')} 关闭所有贴图；关错了按 {kbd('Ctrl + Alt + Z')} 恢复，或从托盘「贴图历史」找回（连批注一起恢复）。</li>
  <li>{kbd('Shift')} + 单击可以多选贴图，一起移动、一起关闭；拖动贴图没松手时按右键，贴图回到原位。</li>
</ul>'''))

content.append(section('long', '长截图', f'''
<p>网页、聊天记录、长文档一屏放不下时用长截图：截图选好区域后，点工具栏的「长截图」，然后在框里滚动。</p>
{figure('long', '框选要滚动的部分 → 点「长截图」→ 在框里滚动，右边的预览实时显示拼好的长图 → 复制 / 保存 / 编辑 / 文字识别。')}
<ul>
  <li>向下、向上都能拼接（聊天窗口从底部往上翻也可以）；{star("来回滚动不会重复拼接")}，滚轮一次滚很多也能接上。</li>
  <li>{star("框里有固定的侧栏、滚动条也能识别")}，结果里会自动去掉它们；固定的页头、底栏只保留一份。屏幕缩放（125%、150%）和平滑滚动的页面也能拼接。</li>
  <li>工具条上的 {star("文字识别")} 按钮直接识别整张长图里的文字（长图会分段识别，从头到尾都读得到）。</li>
  <li>滚得太快、两次画面之间没有重叠时，预览窗会提示「跟丢了」：往回滚一点再慢一些即可接上。</li>
  <li>{kbd('Tab')} 切换竖向 / 横向，{kbd('Enter')} 进入图片编辑器，{kbd('Esc')} 取消回到截图。</li>
</ul>'''))

content.append(section('ocr', '文字识别与翻译', f'''
<p>文字识别在本机运行（PP-OCRv5 模型），图片不会上传。截图工具栏、悬浮按钮、Win + 左键、长截图工具条都可以打开文字识别。</p>
{figure('ocr', '框住要识别的内容 → 点「文字识别」。表格按行列识别，打开「表格」以表格显示；复制后粘贴到 Excel / Word 仍是表格。')}
<ul>
  <li>{star("识别表格")}：有没有表格线都可以，同一行、同一列对齐的内容会还原成表格；「表格」开关切换表格样式和文字样式，复制时同时带表格格式。</li>
  <li>{star("保留排版")}：同一行的文字合成一行，缩进和段落空行保留；{star("分栏的文章按栏阅读")}（先读完左栏再读右栏），不会左右混在一起。</li>
  <li>{star("符号和公式")}：π、±、≤、≥、≠、≈、√、∑、°、℃、上标 ² 等常见符号都能识别；上下排的分数会写成 (a+b)/(2c) 这样的一行。复杂的多层公式目前只能识别出其中的文字。</li>
  <li>识别时会同时读取二维码、条形码，识别出的链接可以直接点开。</li>
  <li>{star("导出")}：文字识别窗口的「导出」把结果存成 Excel（表格逐格，数字可直接计算）、Markdown（表格转成 Markdown 表格）或文本。</li>
  <li>{star("公式识别（扩展模型）")}：在 设置 → 保存与识别 → 文字识别 → 扩展模型 里下载「公式识别」（约 120 MB，不随安装包）。装好后文字识别窗口出现「公式」按钮，把公式图片识别成 LaTeX，可粘贴到 Word、Markdown、论文里；再点一下回到文字结果。</li>
</ul>
<h3>原位翻译</h3>
{figure('translate', '打开工具栏的「原位翻译」开关，选区里的文字直接换成译文，位置、底色、颜色都保留；复制、贴图得到的就是译文图。')}
<ul>
  <li>{star("原位翻译")}：右上角的语言标签可以切换目标语言，点 ✕ 关闭翻译回到原文；贴图的右键菜单里也有「原位翻译」。</li>
  <li>「文字识别」窗口左边是原文（可编辑），右边是译文，两边都可以复制或贴到屏幕上。</li>
  <li>翻译需要在 设置 → 翻译 里填写一个 OpenAI 兼容的接口（DeepSeek、通义、Moonshot、Ollama 等）和 API Key；也可以下载 {star("离线翻译包")}，不联网也能翻译。</li>
</ul>'''))

content.append(section('winmouse', 'Win + 鼠标', f'''
<p>{star("不进入截图界面")}：按住 {kbd('Win')} 键直接用鼠标在屏幕上划一个区域。</p>
{figure('winmouse', '按住 Win，用左键拖出区域，松开即识别文字。中键划 = 贴图，右键划 = 复制。')}
<ul>
  <li>默认：左键 = 识别文字，中键 = 贴图，右键 = 复制；设置 → 快捷键 里可以给每个键换成别的动作或关闭。</li>
  <li>划的时候按住 {kbd('空格')} 可以移动区域，按右键取消；区域沿用截图的圆角设置。</li>
</ul>'''))

content.append(section('float', '悬浮按钮', f'''
{figure('float', '单击悬浮按钮截图；鼠标移上去展开截屏、录屏、文字识别、图片编辑器、贴图历史。')}
<ul>
  <li>单击的动作可以在设置里改（截图、文字识别、录屏……）；拖动按钮可以放到任何位置，会记住。</li>
  <li>{star("任何程序复制了图片，按钮上都会滑过一张缩略图")}，确认已经复制成功。</li>
  <li>把图片文件拖到按钮上，直接贴到屏幕中央。</li>
  <li>右键菜单：添加贴图、粘贴剪贴板、关闭 / 打开所有贴图、鼠标穿透、全屏时自动隐藏、设置。</li>
  <li>按钮图标可以换成自己的图片或 GIF（设置 → 样式 → 悬浮按钮）。</li>
</ul>'''))

content.append(section('record', '录屏', f'''
{figure('record', '截图工具栏的录屏按钮（录制选中的区域）或托盘「录屏」→ 开始 → 停止 → 剪掉头尾 → 保存为 MP4 / GIF 或直接复制。')}
<ul>
  <li>托盘菜单「录屏」、悬浮按钮或截图工具栏的录屏按钮打开；拖边框移动、拖把手调大小，框线画在区域外面，不会录进视频。</li>
  <li>工具栏可以选帧率、尺寸、倒计时，录系统声音和麦克风；{star("操作录制")}把按下的键盘 / 鼠标键显示在画面里，一起录进视频。</li>
  <li>停止后原地预览：拖动时间条剪掉头尾，调倍速，保存为 MP4 或 GIF，或 {star("直接复制视频文件")} 粘贴到聊天里。</li>
  <li>工具栏开始按钮左边的「截屏」可以随时截下录制区域。</li>
</ul>'''))

content.append(section('editor', '图片编辑器', f'''
{figure('editor', '拖入图片拼在一起，选中后旋转缩放，右侧调色，裁切后保存或贴到屏幕。')}
<ul>
  <li>托盘「图片编辑器…」、截图 / 贴图工具栏的编辑按钮、长截图的「编辑」打开；也可以设为图片的打开方式。</li>
  <li>{star("图片和批注都是可以再改的元素")}：多张图片拼版、排层、对齐，批注随时可改。</li>
  <li>裁切（矩形 / 手绘路径）、梯形校正、{star("网格变形")}、旋转镜像、调色和曲线、{star("抠色与替换颜色")}。</li>
  <li>保存为 PNG / JPEG / WebP / BMP / TIFF，或「贴到屏幕」。</li>
</ul>'''))

content.append(section('cases', '使用案例', f'''
<div class="cases">
  <div class="case"><h4>① 给同事指出界面问题</h4><p>{kbd('F1')} 框住出问题的窗口 → 矩形框住位置 → 箭头 + 序号标出步骤 → 文字写说明 → {kbd('Enter')} 复制，到聊天里 {kbd('Ctrl + V')}。</p></div>
  <div class="case"><h4>② 对照资料写文档</h4><p>截下参考资料后按 {kbd('F3')} 贴图，{kbd('Ctrl')} + 滚轮调到半透明，开「鼠标穿透」，贴图就像一层描图纸，下面的软件照常操作。</p></div>
  <div class="case"><h4>③ 保存一整段聊天记录</h4><p>框住聊天记录区域 → 长截图 → 从底部往上滚到开头 → 复制。往上翻、来回翻都不会拼重复。</p></div>
  <div class="case"><h4>④ 看懂外文软件</h4><p>框住英文界面，打开「原位翻译」：每个按钮、标签原地换成中文，贴图后还能边看边操作。</p></div>
  <div class="case"><h4>⑤ 把图片里的表格变成 Excel</h4><p>按住 {kbd('Win')} 用左键划过表格 → 文字识别窗口里以表格显示 → 复制 → 在 Excel 里粘贴，行列都在。</p></div>
  <div class="case"><h4>⑥ 打码后再发出去</h4><p>截图后用马赛克或模糊盖住手机号、头像；用「聚焦」把要看的地方留亮、周围变暗。</p></div>
  <div class="case"><h4>⑦ 录一段操作演示</h4><p>框住区域 → 录屏 → 打开「操作录制」显示按键 → 停止后剪掉头尾 → 保存为 GIF 发给别人。</p></div>
  <div class="case"><h4>⑧ 抠出一个不规则的图形</h4><p>按住右键沿着图形画一圈（套索截图），或截图后用橡皮擦「抠除」擦掉不要的部分 → 贴图，外面是透明的。</p></div>
  <div class="case"><h4>⑨ 复制论文里的公式和符号</h4><p>框住公式所在的段落 → 文字识别：π、±、√、∑ 等符号原样识别，分数写成一行，可以直接粘贴编辑。</p></div>
</div>'''))

rows = [
    ('截图', 'F1'), ('贴图（剪贴板里的图片 / 文字）', 'F3'), ('鼠标穿透', 'Ctrl + Alt + M'), ('恢复最近关闭的贴图', 'Ctrl + Alt + Z'),
    ('关闭所有贴图', 'Shift + Esc'), ('Win + 鼠标划区域', 'Win + 左键 / 中键 / 右键'),
]
capture_rows = [
    ('复制并退出', 'Enter'), ('另存为 / 快速保存', 'Ctrl + S'), ('贴图', 'F3'), ('全选当前屏幕', 'Ctrl + A'), ('撤销 / 重做', 'Ctrl + Z'),
    ('拖动中移动选区 / 图形', '空格'), ('撤回正在进行的拖动', '右键'), ('套索截图', '右键'),
    ('载入最近的截图区域', 'R'), ('复制像素颜色', 'C'), ('截图带鼠标', 'Ctrl + M'), ('退出', 'Esc'),
]
pin_rows = [
    ('批注', '空格'), ('旋转 / 水平翻转 / 垂直翻转', 'E'), ('复原', 'F'), ('置顶 / 锁定', 'T'), ('在文字上拖动贴图', 'Alt'), ('复制', 'Ctrl + C'), ('关闭', 'Esc'),
]


def table(items, extra=None):
    extra = extra or {}
    return '<table>' + ''.join(f'<tr><td>{name}</td><td>{extra.get(name) or kbd(keys)}</td></tr>' for name, keys in items) + '</table>'


content.append(section('keys', '快捷键速查', f'''
<div class="grid2">
  <div><h3>全局</h3>{table(rows)}</div>
  <div><h3>截图中</h3>{table(capture_rows, {"另存为 / 快速保存": kbd("Ctrl + S") + " / " + kbd("Ctrl + Shift + S"), "撤销 / 重做": kbd("Ctrl + Z") + " / " + kbd("Ctrl + Y"), "载入最近的截图区域": kbd("R") + " / " + kbd("Shift + R"), "拖动中移动选区 / 图形": "按住 " + kbd("空格"), "撤回正在进行的拖动": "拖动中按 " + kbd("右键"), "套索截图": "未选区域时按住 " + kbd("右键") + " 拖动"})}</div>
  <div><h3>贴图上</h3>{table(pin_rows, {"旋转 / 水平翻转 / 垂直翻转": kbd("E") + " / " + kbd("Q") + " / " + kbd("W"), "置顶 / 锁定": kbd("T") + " / " + kbd("L"), "关闭": kbd("Esc") + " / 双击（复制并关闭）", "在文字上拖动贴图": "按住 " + kbd("Alt") + "（可改）"})}</div>
  <div><h3>批注工具</h3>{table([("图形", "M"), ("箭头 / 直线", "L"), ("画笔", "B"), ("文字", "T"), ("序号", "S"), ("橡皮擦", "E"), ("选择", "V")])}</div>
</div>
<p class="tip">所有快捷键都可以在 设置 → 快捷键 里修改，全局快捷键还可以设成鼠标中键 / 侧键；全屏玩游戏时可以在托盘菜单里「禁用所有快捷键」。</p>'''))

content.append(section('faq', '常见问题', f'''
<details><summary>按 F1 没反应？</summary><p>F1 可能被别的软件占用了。打开 设置 → 快捷键，点输入框后按下新的组合键；托盘图标中键单击也能直接截图。</p></details>
<details><summary>长截图提示「跟丢了」或「无法拼接」？</summary><p>两次画面之间没有重叠时会跟丢：往回滚一点、再滚慢一些就能接上。框只框能滚动的部分效果最好；如果框里只有动画或视频在变，内容没有整体移动，就无法拼接。</p></details>
<details><summary>贴图上一拖就选中了文字，不想要？</summary><p>拖动时按住 {kbd('Alt')}；或在 设置 → 快捷键 → 贴图窗口 里关掉「贴图上的文字可直接拖选复制」，也可以把 Alt 换成别的键。</p></details>
<details><summary>能识别复杂公式吗？</summary><p>日常文字识别能读出常见符号和简单分数；多层的公式请下载「公式识别」扩展模型（设置 → 保存与识别），用文字识别窗口的「公式」按钮识别成 LaTeX。</p></details>
<details><summary>识别出的表格不对？</summary><p>表格按对齐的行列还原，合并单元格、斜线表头可能会拆错；关掉「表格」开关看文字，或复制后在 Excel 里稍作调整。</p></details>
<details><summary>翻译显示失败？</summary><p>翻译需要在 设置 → 翻译 填写接口地址和 API Key，或下载离线翻译包。文字识别不需要联网。</p></details>
<details><summary>第一次文字识别比较慢？</summary><p>第一次要加载识别模型（约 1–2 秒），之后就很快了；很长的长截图会分段识别，稍等几秒。</p></details>
<details><summary>截图里会有悬浮按钮吗？</summary><p>不会。截图时悬浮按钮自动隐藏（设置里可以改为一直显示）。</p></details>
<details><summary>设置改了没生效？</summary><p>设置里的修改要点右下角「保存」才生效；主题、悬浮按钮等是即时预览，不保存关闭时会还原。</p></details>
<details><summary>出了问题怎么反馈？</summary><p>托盘菜单「导出诊断信息」会把日志和配置（不含 API Key）打包到桌面，发给作者即可（联系方式见下方「关于」）。</p></details>'''))

content.append(section('about', '关于', f'''
<div class="about">
  <div>
    <p><b>Markshot</b> {VERSION}　·　作者 <b>guoqing.wu</b>（GitHub 账户 <a href="https://github.com/well74741" target="_blank" rel="noopener">well74741</a>）</p>
    <p>项目主页与下载：<a href="https://github.com/well74741/Markshot" target="_blank" rel="noopener">github.com/well74741/Markshot</a>（新版本、更新说明都在这里）。</p>
    <p>Copyright © 2026 guoqing.wu (well74741)。保留所有权利。</p>
    <p class="muted">Markshot 使用了以下开源组件，各自遵循其许可证：RapidOcrNet 与 PaddleOCR PP-OCRv5 模型（Apache-2.0）、ONNX Runtime（MIT）、SkiaSharp（MIT）、NAudio（MIT）、zxing-cpp 与 ZXing.Net（Apache-2.0）、FFmpeg（GPL v3，作为独立程序随附）。完整列表见安装目录里的 THIRD-PARTY-NOTICES.md。</p>
    <p>有建议、发现问题，或者想交流使用心得，欢迎扫码加作者微信。</p>
  </div>
  <figure class="qr">
    <img src="{WECHAT}" alt="作者微信二维码">
    <figcaption>扫码添加作者微信</figcaption>
  </figure>
</div>'''))


nav = ''.join(f'<a href="#{anchor}">{title}</a>' for anchor, title in SECTIONS)

CSS = '''
:root{--bg:#f5f6f8;--card:#fff;--text:#1d2027;--muted:#666d78;--line:#e4e7ec;--accent:#4C8DFF;--soft:#eef3ff;--kbd:#f1f3f6;--mark:#ffe27a}
@media (prefers-color-scheme:dark){:root{--bg:#151515;--card:#1e1e1e;--text:#e9e9ea;--muted:#9da3ab;--line:#2d2d2f;--soft:#1f2a3d;--kbd:#2a2b2e;--mark:#6b5512}}
*{box-sizing:border-box}html{scroll-behavior:smooth}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.75 "Microsoft YaHei UI","Microsoft YaHei","PingFang SC","Segoe UI",sans-serif}
header{position:sticky;top:0;z-index:5;display:flex;align-items:center;gap:12px;padding:12px 28px;background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
header img{width:30px;height:30px}header b{font-size:17px}header span{color:var(--muted);font-size:13px;margin-left:auto}
.layout{display:flex;max-width:1180px;margin:0 auto;gap:36px;padding:0 28px}
nav{position:sticky;top:70px;align-self:flex-start;width:170px;flex:none;padding:24px 0;display:flex;flex-direction:column}
nav a{color:var(--muted);text-decoration:none;padding:5px 12px;border-left:2px solid transparent;font-size:14px}
nav a:hover{color:var(--text)}nav a.on{color:var(--accent);border-left-color:var(--accent);font-weight:600}
main{flex:1;min-width:0;padding:16px 0 80px}
section{padding-top:22px}h2{font-size:24px;margin:26px 0 12px}h3{font-size:17px;margin:24px 0 8px}h4{margin:0 0 6px;font-size:15px}
.lead{font-size:16.5px}ul,ol{padding-left:22px}li{margin:4px 0}
kbd{display:inline-block;min-width:22px;padding:0 7px;border:1px solid var(--line);border-bottom-width:2px;border-radius:5px;background:var(--kbd);font:600 12.5px/20px "Segoe UI","Microsoft YaHei UI",sans-serif;text-align:center}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:12px;margin:16px 0}
.card,.case{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
.card b{display:block;margin-bottom:4px}.card span{color:var(--muted);font-size:14px}
.cases{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:12px}.case p{margin:0;color:var(--muted);font-size:14px}
.steps li{margin:8px 0}
.demo-card{margin:16px 0 18px;background:var(--card);border:1px solid var(--line);border-radius:14px;overflow:hidden}
.demo{display:block;width:100%;height:auto;cursor:pointer}
.ico{opacity:.92}
figcaption{display:flex;align-items:center;gap:12px;padding:10px 16px;color:var(--muted);font-size:13.5px;border-top:1px solid var(--line)}
figcaption span{flex:1}.pause{border:1px solid var(--line);background:var(--kbd);color:var(--text);border-radius:6px;width:30px;height:26px;cursor:pointer;font-size:11px}
table{border-collapse:collapse;width:100%;margin:8px 0;background:var(--card);border-radius:10px;overflow:hidden}
td,th{border-bottom:1px solid var(--line);padding:7px 12px;text-align:left;font-size:14px}th{color:var(--muted);font-weight:600}tr:last-child td{border-bottom:0}
.grid2{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:4px 20px}
.tip{background:var(--soft);border-radius:10px;padding:10px 14px;font-size:14px}
details{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px;margin:8px 0}summary{cursor:pointer;font-weight:600}details p{margin:8px 0 0;color:var(--muted)}
.inline-logo{width:16px;height:16px;vertical-align:-3px;margin:0 2px}
strong.star{font-weight:700;background:linear-gradient(transparent 60%,var(--mark) 0);padding:0 1px}
.about{display:flex;gap:28px;align-items:flex-start;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px 20px}
.about p{margin:6px 0}.about .muted{color:var(--muted);font-size:13px}.about a{color:var(--accent)}
.qr{margin:0;flex:none;text-align:center}.qr img{width:170px;height:170px;border-radius:10px;display:block;background:#fff;padding:8px}.qr figcaption{border:0;justify-content:center;padding:6px 0 0}
@media (max-width:820px){.about{flex-direction:column}}
footer{color:var(--muted);font-size:13px;text-align:center;padding:30px}footer a{color:var(--muted)}
@media (max-width:820px){nav{display:none}.layout{padding:0 16px}}
'''

JS = '''
// Click a picture (or ❚❚) to pause / continue it. ?t=3.5 shows every picture at that second (for checking them).
document.querySelectorAll('.demo-card').forEach(card => {
  const svg = card.querySelector('svg'), button = card.querySelector('.pause');
  const toggle = () => { if (svg.animationsPaused()) { svg.unpauseAnimations(); button.textContent = '❚❚'; } else { svg.pauseAnimations(); button.textContent = '▶'; } };
  svg.addEventListener('click', toggle); button.addEventListener('click', toggle);
});
const query = new URLSearchParams(location.search), at = query.get('t'), only = query.get('demo');
if (only) document.body.innerHTML = '<main style="padding:12px;max-width:900px">' + document.querySelector(`[data-demo="${only}"]`).outerHTML + '</main>';
if (at !== null) document.querySelectorAll('svg.demo').forEach(svg => { svg.pauseAnimations(); svg.setCurrentTime(parseFloat(at)); });
// The section being read is marked in the list on the left.
const links = [...document.querySelectorAll('nav a')];
const watch = new IntersectionObserver(entries => entries.forEach(entry => {
  if (entry.isIntersecting) links.forEach(a => a.classList.toggle('on', a.getAttribute('href') === '#' + entry.target.id));
}), { rootMargin: '-30% 0px -60% 0px' });
document.querySelectorAll('section').forEach(s => watch.observe(s));
'''

html = f'''<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Markshot 帮助</title><link rel="icon" href="{LOGO}"><style>{CSS}</style></head>
<body>
<header><img src="{LOGO}" alt=""><b>Markshot 帮助</b><span>适用于 Markshot {VERSION}</span></header>
<div class="layout"><nav>{nav}</nav><main>{''.join(content)}</main></div>
<footer>Markshot {VERSION} · 作者 guoqing.wu (well74741) · <a href="https://github.com/well74741/Markshot" target="_blank" rel="noopener">GitHub</a> · 托盘图标右键「帮助」或设置窗口右上角的「?」可再次打开本页</footer>
<script>{JS}</script>
</body></html>
'''

os.makedirs(os.path.join(ROOT, 'help'), exist_ok=True)
with open(os.path.join(ROOT, 'help', 'index.html'), 'w', encoding='utf-8', newline='\n') as file:
    file.write(html)
print('help/index.html', len(html) // 1024, 'KB')
