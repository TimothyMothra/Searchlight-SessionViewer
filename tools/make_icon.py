"""
Generates the Copilot Sessions Tray app icon.

Design: a modern rounded tile with a diagonal indigo->cyan gradient, a bold
four-point AI "spark" (upper-right) and descending "session list" bars
(lower-left). Rendered per-size so it stays crisp: at 16/20 px the list bars
are dropped and the spark is centered & enlarged for legibility in the tray.

Output: Assets\app.ico (multi-resolution 16..256), preview PNGs, and a 1024px
packaging/listing master. Use --master-only to leave existing shell icons alone.
"""
import argparse
import math
import os
from PIL import Image, ImageDraw

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "Searchlight", "Assets")
OUT_DIR = os.path.abspath(OUT_DIR)
os.makedirs(OUT_DIR, exist_ok=True)

SS = 8  # supersample factor for antialiasing

# Gradient endpoints (top-left -> bottom-right).
C_TL = (91, 108, 255)    # indigo / periwinkle  #5B6CFF
C_BR = (23, 195, 230)    # cyan                 #17C3E6
WHITE = (255, 255, 255)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def diagonal_gradient(size):
    """A diagonal (TL->BR) linear gradient image, size x size, RGB."""
    img = Image.new("RGB", (size, size))
    px = img.load()
    maxd = (size - 1) * 2.0
    for y in range(size):
        for x in range(size):
            t = (x + y) / maxd
            px[x, y] = lerp(C_TL, C_BR, t)
    return img


def star4(cx, cy, r_tip, r_in, rot=0.0):
    """Vertices of a sharp concave 4-point star centered at (cx, cy)."""
    pts = []
    for i in range(4):
        a_tip = rot + i * (math.pi / 2)
        pts.append((cx + r_tip * math.cos(a_tip), cy + r_tip * math.sin(a_tip)))
        a_in = a_tip + math.pi / 4
        pts.append((cx + r_in * math.cos(a_in), cy + r_in * math.sin(a_in)))
    return pts


def draw_tile(size):
    """Render the icon at the given logical size, returns an RGBA image."""
    # ASSUMPTION: a 2048px drawing surface retains antialiasing for the 1024px
    # master without allocating an 8192px surface; existing small renders stay identical.
    S = size * min(SS, max(1, 2048 // size))
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    pad = int(S * 0.06)
    radius = int(S * 0.235)
    box = [pad, pad, S - pad, S - pad]

    # Rounded-tile mask.
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)

    # Gradient fill clipped to the tile.
    grad = diagonal_gradient(S).convert("RGBA")
    img.paste(grad, (0, 0), mask)

    # Soft top highlight for depth: a translucent white vertical fade
    # (brightest at the top edge, fading to nothing by ~65% height) so there
    # is no hard seam. Clipped to the tile.
    hi = Image.new("L", (S, S), 0)
    hpx = hi.load()
    fade_end = S * 0.65
    for y in range(S):
        t = 1.0 - (y / fade_end)
        v = int(max(0.0, t) * 46) if y < fade_end else 0
        if v:
            for x in range(S):
                hpx[x, y] = v
    hi = Image.composite(hi, Image.new("L", (S, S), 0), mask)
    img.alpha_composite(Image.merge("RGBA", (
        Image.new("L", (S, S), 255), Image.new("L", (S, S), 255),
        Image.new("L", (S, S), 255), hi)))

    small = size <= 20

    if small:
        # Tray-legible: one centered, bold spark (no companion spark).
        cx, cy = S * 0.5, S * 0.5
        _spark(draw, cx, cy, S * 0.40, S * 0.13, alpha=255)
    else:
        # Session "list" bars, lower-left, descending width (rounded).
        bar_h = S * 0.072
        bar_x = S * 0.20
        gap = S * 0.128
        widths = [0.46, 0.36, 0.26]
        # Raised ~a few px (0.04) so the bottom node dot clears the
        # bottom-left rounded corner instead of overhanging it.
        y0 = S * (0.545 - 0.04)
        for i, w in enumerate(widths):
            y = y0 + i * gap
            draw.rounded_rectangle(
                [bar_x, y, bar_x + S * w, y + bar_h],
                radius=bar_h / 2,
                fill=(255, 255, 255, 235))
            # leading "node" dot for each row (a session marker).
            dot_r = bar_h * 0.62
            dcx = bar_x - S * 0.052
            dcy = y + bar_h / 2
            draw.ellipse([dcx - dot_r, dcy - dot_r, dcx + dot_r, dcy + dot_r],
                         fill=(255, 255, 255, 235))

        # Big four-point AI spark, upper-right (companion spark removed).
        _spark(draw, S * 0.66, S * 0.345, S * 0.235, S * 0.072, alpha=255)

    return img.resize((size, size), Image.LANCZOS)


def _spark(draw, cx, cy, r_tip, r_in, alpha=255):
    draw.polygon(star4(cx, cy, r_tip, r_in), fill=(255, 255, 255, alpha))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--master-only", action="store_true")
    args = parser.parse_args()
    if not args.master_only:
        sizes = [16, 20, 24, 32, 48, 64, 128, 256]
        frames = [draw_tile(s) for s in sizes]
        ico_path = os.path.join(OUT_DIR, "app.ico")
        frames[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
        print("wrote", ico_path)
        for s in (256, 48, 32, 16):
            p = os.path.join(OUT_DIR, f"app_{s}.png")
            draw_tile(s).save(p)
            print("wrote", p)

    p = os.path.join(OUT_DIR, "app_1024.png")
    draw_tile(1024).save(p)
    print("wrote", p)
