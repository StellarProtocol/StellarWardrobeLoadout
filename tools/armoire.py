"""The Wardrobe icon: approved design "D — Armoire" (2026-11-13) — a closed two-door wardrobe with a centre split, two
handles and short feet; from 18 px up an overhanging crown and a second top rail.

Origin: Mod\\_scratch\\icons-wardrobe\\round2 (geometry.py "main/D" + draw_icons.py, see its README). The 16 / 24 / 32 /
64 / 128 px paths below are copied VERBATIM from round2; 18 / 30 / 36 / 48 (the header tile's other UI-scale sizes)
were drawn natively here following the same rules. Every size is its OWN drawing in pixel coordinates — no coordinate
transforms, no master scaled down, no supersampling: Cairo strokes each path straight onto an N x N surface (round
caps + joins, ANTIALIAS_BEST), then RGB is forced to pure white so alpha alone carries the edge coverage.

Pixel-grid phase: EVEN stroke widths use integer centres, ODD widths half-pixel centres, so straight edges land on
pixel boundaries. Stroke ~2/24 of the box, rounded up (16/18/24: 2, 30/32/36: 3, 48: 4, 64: 6, 128: 11).

Needs the Cairo DLL that ships with conda base (Library/bin/cairo-2.dll) and Pillow.
"""
from pathlib import Path
import ctypes as ct
import os
import sys
from PIL import Image

STROKES = {16: 2, 18: 2, 24: 2, 30: 3, 32: 3, 36: 3, 48: 4, 64: 6, 128: 11}

# Per element: one SVG-style path (M/L/Z only). Order: body, crown (+ rail), split, left handle, right handle, feet.
DRAWINGS = {
    # round2 16: no second rail — it would close up the door interiors at this size.
    16: ["M 2 2 L 14 2 L 14 12 L 2 12 Z",
         "M 8 2 L 8 12",
         "M 5 6 L 5 8", "M 11 6 L 11 8",
         "M 3 12 L 3 14 M 13 12 L 13 14"],
    # new (2 px): 24's layout on 18 — rail 1 px clear of the crown, handles 1 px clear of wall and split.
    18: ["M 3 2 L 3 14 L 15 14 L 15 2",
         "M 2 2 L 16 2 M 3 5 L 15 5",
         "M 9 5 L 9 14",
         "M 6 9 L 6 10", "M 12 9 L 12 10",
         "M 4 14 L 4 16 M 14 14 L 14 16"],
    24: ["M 4 2 L 4 20 L 20 20 L 20 2",
         "M 3 2 L 21 2 M 4 5 L 20 5",
         "M 12 5 L 12 20",
         "M 9 10 L 9 12", "M 15 10 L 15 12",
         "M 5 20 L 5 22 M 19 20 L 19 22"],
    # new (3 px): 32's layout on 30 — split right of centre like 32, handles 1 px off the split.
    30: ["M 5.5 3.5 L 5.5 24.5 L 24.5 24.5 L 24.5 3.5",
         "M 3.5 3.5 L 26.5 3.5 M 5.5 8.5 L 24.5 8.5",
         "M 15.5 8.5 L 15.5 24.5",
         "M 11.5 15.5 L 11.5 17.5", "M 19.5 15.5 L 19.5 17.5",
         "M 7.5 24.5 L 7.5 26.5 M 22.5 24.5 L 22.5 26.5"],
    32: ["M 5.5 3.5 L 5.5 26.5 L 26.5 26.5 L 26.5 3.5",
         "M 3.5 3.5 L 28.5 3.5 M 5.5 8.5 L 26.5 8.5",
         "M 16.5 8.5 L 16.5 26.5",
         "M 12.5 15.5 L 12.5 18.5", "M 20.5 15.5 L 20.5 18.5",
         "M 7.5 26.5 L 7.5 28.5 M 24.5 26.5 L 24.5 28.5"],
    # new (3 px): 32's layout on 36 — 2 px clear at top and bottom, handles centred on the door height.
    36: ["M 6.5 3.5 L 6.5 30.5 L 29.5 30.5 L 29.5 3.5",
         "M 4.5 3.5 L 31.5 3.5 M 6.5 9.5 L 29.5 9.5",
         "M 18.5 9.5 L 18.5 30.5",
         "M 14.5 18.5 L 14.5 21.5", "M 22.5 18.5 L 22.5 21.5",
         "M 8.5 30.5 L 8.5 32.5 M 27.5 30.5 L 27.5 32.5"],
    # new (4 px): 64's layout on 48 — even stroke, so the split sits exactly on the centre line.
    48: ["M 8 4 L 8 39 L 40 39 L 40 4",
         "M 5 4 L 43 4 M 8 12 L 40 12",
         "M 24 12 L 24 39",
         "M 18 22 L 18 26", "M 30 22 L 30 26",
         "M 10 39 L 10 44 M 38 39 L 38 44"],
    64: ["M 11 6 L 11 52 L 53 52 L 53 6",
         "M 7 6 L 57 6 M 11 16 L 53 16",
         "M 32 16 L 32 52",
         "M 24 29 L 24 35", "M 40 29 L 40 35",
         "M 14 52 L 14 58 M 50 52 L 50 58"],
    128: ["M 21.5 13.5 L 21.5 104.5 L 106.5 104.5 L 106.5 13.5",
          "M 13.5 13.5 L 114.5 13.5 M 21.5 32.5 L 106.5 32.5",
          "M 64.5 32.5 L 64.5 104.5",
          "M 48.5 59.5 L 48.5 71.5", "M 80.5 59.5 L 80.5 71.5",
          "M 27.5 104.5 L 27.5 116.5 M 100.5 104.5 L 100.5 116.5"],
}

_cairo = None


def _lib():
    global _cairo
    if _cairo is None:
        bin_dir = Path(sys.prefix) / "Library" / "bin"
        os.add_dll_directory(str(bin_dir))
        _cairo = ct.CDLL(str(bin_dir / "cairo-2.dll"))
    return _cairo


def _fn(name, result, *args):
    f = getattr(_lib(), "cairo_" + name)
    f.restype, f.argtypes = result, list(args)
    return f


def render_png(n, png_path):
    """Stroke the N px drawing natively with Cairo and save it as pure-white + alpha."""
    P, D, I = ct.c_void_p, ct.c_double, ct.c_int
    surface = _fn("image_surface_create", P, I, I, I)(0, n, n)   # ARGB32, transparent
    cr = _fn("create", P, P)(surface)
    _fn("set_source_rgba", None, P, D, D, D, D)(cr, 1, 1, 1, 1)
    _fn("set_line_width", None, P, D)(cr, STROKES[n])
    _fn("set_line_cap", None, P, I)(cr, 1)    # ROUND
    _fn("set_line_join", None, P, I)(cr, 1)   # ROUND
    _fn("set_antialias", None, P, I)(cr, 6)   # BEST: native-resolution coverage, no resampling
    new_path, move = _fn("new_path", None, P), _fn("move_to", None, P, D, D)
    line, close = _fn("line_to", None, P, D, D), _fn("close_path", None, P)
    stroke = _fn("stroke", None, P)
    for data in DRAWINGS[n]:
        new_path(cr)
        words = iter(data.split())
        for cmd in words:
            if cmd == "M":
                move(cr, float(next(words)), float(next(words)))
            elif cmd == "L":
                line(cr, float(next(words)), float(next(words)))
            elif cmd == "Z":
                close(cr)
            else:
                raise ValueError(cmd)
        stroke(cr)
    status = _fn("surface_write_to_png", I, P, ct.c_char_p)(surface, str(png_path).encode("utf-8"))
    _fn("destroy", None, P)(cr)
    _fn("surface_destroy", None, P)(surface)
    if status:
        raise RuntimeError(f"Cairo PNG status {status}")
    # Pure-white RGB even under partial / zero alpha (the plugin and launcher tint it; safe filtering).
    with Image.open(png_path) as native:
        alpha = native.convert("RGBA").getchannel("A")
    out = Image.new("RGBA", (n, n), "white")
    out.putalpha(alpha)
    out.save(png_path)


def write_svg(n, svg_path):
    """The same geometry as an editable SVG (the reproducible source of the PNG)."""
    w = STROKES[n]
    paths = "\n".join(f'<path d="{d}" fill="none" stroke="#FFFFFF" stroke-width="{w}"/>' for d in DRAWINGS[n])
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{n}" height="{n}" viewBox="0 0 {n} {n}" '
           f'stroke-linecap="round" stroke-linejoin="round">\n'
           f'<title>Wardrobe D / Armoire — {n}px independently hinted</title>\n{paths}\n</svg>\n')
    Path(svg_path).write_text(svg, encoding="utf-8")
