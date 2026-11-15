"""Draws the Basalt UI application icon (basalt columns) into every ICO size."""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ASSETS = os.path.join(ROOT, "src", "BasaltUI", "Assets")
DESIGN = os.path.join(ROOT, "design")
os.makedirs(ASSETS, exist_ok=True)

S = 1024
COL_BG_TOP = (24, 27, 33)
COL_BG_BOT = (13, 15, 19)
TEAL = (64, 214, 196)
TEAL_DIM = (42, 152, 141)
CREAM = (232, 236, 240)


def rounded(d, box, r, fill):
    d.rounded_rectangle(box, radius=r, fill=fill)


def gradient(size, top, bottom):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        t = y / (size - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        for x in range(size):
            px[x, y] = c
    return img


def draw(scale=1):
    size = S * scale
    img = gradient(size, COL_BG_TOP, COL_BG_BOT).convert("RGBA")
    d = ImageDraw.Draw(img, "RGBA")

    radius = int(0.205 * size)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)

    d = ImageDraw.Draw(img, "RGBA")
    inset = int(0.09 * size)
    rounded(d, [inset, inset, size - inset, size - inset], int(0.15 * size), fill=(31, 35, 43, 255))
    rounded(d, [inset, inset, size - inset, inset + int(0.035 * size)], int(0.15 * size),
            fill=(90, 98, 112, 130))

    # basalt columns: five vertical bars of different heights, standing on a common base
    base = int(size * 0.80)
    n = 5
    gap = int(size * 0.028)
    area_l, area_r = int(size * 0.20), int(size * 0.80)
    total_w = area_r - area_l
    bar_w = (total_w - gap * (n - 1)) // n
    tops = [0.56, 0.34, 0.20, 0.42, 0.62]
    for i, frac in enumerate(tops):
        l = area_l + i * (bar_w + gap)
        top = int(size * frac)
        col = TEAL if i % 2 == 0 else TEAL_DIM
        rounded(d, [l, top, l + bar_w, base], int(bar_w * 0.22), fill=col + (255,))
        # a thin lit edge on the left of each column
        d.rounded_rectangle([l, top, l + max(2, bar_w // 7), base], radius=int(bar_w * 0.12),
                            fill=(255, 255, 255, 46))

    # the shared base slab
    rounded(d, [int(size * 0.17), base, int(size * 0.83), int(size * 0.865)], int(size * 0.02),
            fill=CREAM + (235,))

    out = img.copy()
    out.putalpha(mask)
    return out


big = draw()
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
big.save(os.path.join(ASSETS, "basalt.ico"), format="ICO", sizes=sizes)
big.resize((256, 256), Image.LANCZOS).save(os.path.join(DESIGN, "icon-preview.png"))
print("wrote", os.path.join(ASSETS, "basalt.ico"))
