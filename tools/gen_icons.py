"""Generate the Wardrobe window's glyph icons (Icons/<name>-<N>.png).

WHITE on transparent: the plugin tints each one at draw time (ImageElement.Tint, fw 2.30.0), so the glyphs follow
the theme (text / muted / red). House icon rule (Stellar-Design-Language §3): ONE pre-drawn, pixel-hinted picture per
on-screen size, never one master scaled down. Each glyph is drawn in a 24-unit design grid (the approved mockup's SVG
paths), mapped onto N pixels with every point snapped so a stroke's edges sit on whole pixels (odd strokes centre on
.5, even strokes on whole pixels); only diagonals and arcs get anti-aliased (8x supersample, box-filtered down).

Sizes: the glyph's size in canvas units x the UI scales the game runs at (canvas -> screen 0.75 at 1080p, 1.0 at
1440p, 1.25 / 1.5 / 2.0 above), plus 16 and 32. The plugin picks the bitmap nearest to units x live scale.

Run (conda python, from the plugin root):  python tools/gen_icons.py
"""
import os
import sys
from PIL import Image, ImageDraw

SS = 8
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Icons")
SCALES = [0.75, 1.0, 1.25, 1.5, 2.0]

ARMOIRE_UNITS = 24    # header-tile glyph; launcher tile = the 128 px drawing (wardrobe-icon.png)

# name -> glyph size in canvas units (what the window draws). Keep in sync with Plugin.Style.Icons.cs.
UNITS = {"pencil": 16, "refresh": 16, "check": 16, "close": 14}
GRIP_UNITS = (6, 14)   # w x h


def sizes_for(units):
    return sorted({int(round(units * s)) for s in SCALES} | {16, 32})


def stroke_px(n):
    """Whole-pixel stroke for an N px glyph (~2/24 of the box, never below 1)."""
    return 1 if n < 15 else 2 if n < 27 else 3 if n < 40 else 4


def snap(v, n, w):
    """24-unit design coordinate -> pixel coordinate on the N px grid, snapped so a w px stroke's edges are whole."""
    p = v * n / 24.0
    return (int(p) + 0.5) if w % 2 else float(round(p))


class Pen:
    def __init__(self, n):
        self.n = n
        self.w = stroke_px(n)
        self.img = Image.new("L", (n * SS, n * SS), 0)
        self.d = ImageDraw.Draw(self.img)

    def pt(self, x, y):
        return (snap(x, self.n, self.w) * SS, snap(y, self.n, self.w) * SS)

    def line(self, *pts, closed=False):
        p = [self.pt(x, y) for x, y in pts]
        if closed:
            p.append(p[0])
        self.d.line(p, fill=255, width=self.w * SS, joint="curve")
        r = self.w * SS / 2.0   # round caps
        for (x, y) in (p[0], p[-1]):
            self.d.ellipse([x - r, y - r, x + r, y + r], fill=255)

    def arc(self, cx, cy, r, start, end):
        x, y = self.pt(cx, cy)
        rr = r * self.n / 24.0 * SS
        self.d.arc([x - rr, y - rr, x + rr, y + rr], start, end, fill=255, width=self.w * SS)

    def done(self):
        alpha = self.img.resize((self.n, self.n), Image.BOX)
        out = Image.new("RGBA", (self.n, self.n), (255, 255, 255, 0))
        out.putalpha(alpha)
        return out


def pencil(n):
    p = Pen(n)
    p.line((4, 20), (5, 16), (16, 5), (19, 8), (8, 19), closed=True)
    return p.done()


def refresh(n):
    p = Pen(n)
    p.arc(12, 12, 8, 0, 315)                # ~7/8 of a circle, open at the top-right
    p.line((20, 4), (20, 9), (15, 9))       # arrow head
    return p.done()


def check(n):
    p = Pen(n)
    p.line((5, 12.5), (9.5, 17), (19, 7))
    return p.done()


def close(n):
    p = Pen(n)
    p.line((6, 6), (18, 18))
    p.line((18, 6), (6, 18))
    return p.done()


def grip(w, h):
    """Six square dots (2 columns x 3 rows), whole-pixel squares, no anti-aliasing at all."""
    d = max(1, int(round(h / 7.0)))
    img = Image.new("RGBA", (w, h), (255, 255, 255, 0))
    px = img.load()
    cols = [int(round(w * 0.25 - d / 2.0)), int(round(w * 0.75 - d / 2.0))]
    rows = [int(round(h * y / 14.0 - d / 2.0)) for y in (2, 7, 12)]
    for cx in cols:
        for cy in rows:
            for x in range(max(0, cx), min(w, cx + d)):
                for y in range(max(0, cy), min(h, cy + d)):
                    px[x, y] = (255, 255, 255, 255)
    return img


DRAW = {"pencil": pencil, "refresh": refresh, "check": check, "close": close}

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, units in UNITS.items():
        for n in sizes_for(units):
            DRAW[name](n).save(os.path.join(OUT, f"{name}-{n}.png"), optimize=True)
        print(name, sizes_for(units))
    gw, gh = GRIP_UNITS
    gsizes = sorted({int(round(gh * s)) for s in SCALES} | {16, 32})
    for h in gsizes:
        grip(max(3, int(round(h * gw / gh))), h).save(os.path.join(OUT, f"grip-{h}.png"), optimize=True)
    print("grip", gsizes)
    sys.dont_write_bytecode = True   # no tools/__pycache__ in the repo
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import armoire
    src = os.path.join(OUT, "src")
    os.makedirs(src, exist_ok=True)
    asizes = sizes_for(ARMOIRE_UNITS)
    for n in asizes:
        armoire.render_png(n, os.path.join(OUT, f"armoire-{n}.png"))
    for n in armoire.DRAWINGS:
        armoire.write_svg(n, os.path.join(src, f"armoire-{n}.svg"))
    armoire.render_png(128, os.path.join(OUT, "..", "wardrobe-icon.png"))
    print("armoire", asizes, "+ launcher 128, svg", sorted(armoire.DRAWINGS))
