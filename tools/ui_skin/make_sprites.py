"""HairBrush UI skin generator: brushed steel + gold, sampled from HairBrush_Logo.png.

Writes the 9-slice sprites UITheme loads from Assets/Resources/UI_GFX (same names and sizes as
before; button sprites are sliced with an 8px border) plus two new ones:
HB_9sliceGold.png (primary action) and HB_PanelSteel.png (panels).

usage: python -I make_sprites.py <out_dir> [<preview_png>]
"""
import sys, os
import numpy as np
from PIL import Image

OUT = sys.argv[1]
PREVIEW = sys.argv[2] if len(sys.argv) > 2 else None

def lerp_profile(stops, n):
    """stops: [(t, (r,g,b)), ...] sorted by t in 0..1 -> (n,3) float array."""
    ts = np.array([s[0] for s in stops], float)
    cs = np.array([s[1] for s in stops], float)
    x = np.linspace(0, 1, n)
    return np.stack([np.interp(x, ts, cs[:, i]) for i in range(3)], axis=1)

def rounded_alpha(w, h, r):
    a = np.ones((h, w), float)
    if r <= 0: return a
    yy, xx = np.mgrid[0:h, 0:w]
    for cx, cy in ((r - .5, r - .5), (w - r - .5, r - .5), (r - .5, h - r - .5), (w - r - .5, h - r - .5)):
        in_corner = ((xx < r) if cx < w / 2 else (xx > w - r - 1)) & ((yy < r) if cy < h / 2 else (yy > h - r - 1))
        d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
        a = np.where(in_corner, np.clip(r + .5 - d, 0, 1), a)
    return a

def brushed(w, h, amount, seed):
    """Fine horizontal brushing: per-row brightness jitter, smoothed a little."""
    rng = np.random.default_rng(seed)
    rows = rng.normal(0, 1, h)
    rows = np.convolve(rows, [.25, .5, .25], mode="same")
    return 1 + amount * rows[:, None] * np.ones((1, w))

def chrome_tile(w, h, stops, outline, rim_top, rim_bottom, radius, side_lift=1.18, brush=.025, seed=1):
    body = lerp_profile(stops, h - 4)                    # rows 2..h-3
    img = np.zeros((h, w, 3), float)
    img[2:h - 2] = body[:, None, :]
    img[1, :] = rim_top
    img[h - 2, :] = rim_bottom
    img[0, :] = outline; img[h - 1, :] = outline
    img[2:h - 2] *= brushed(w, h - 4, brush, seed)[:, :, None]
    # side bevel: 1px lighter column inside the outline, outline at the very edge
    img[2:h - 2, 1] = np.clip(img[2:h - 2, 1] * side_lift, 0, 255)
    img[2:h - 2, w - 2] = np.clip(img[2:h - 2, w - 2] * side_lift * .9, 0, 255)
    img[:, 0] = outline; img[:, w - 1] = outline
    alpha = rounded_alpha(w, h, radius)
    return np.dstack([np.clip(img, 0, 255), alpha * 255]).astype(np.uint8)

def rounded_rect_sdf(w, h, r):
    """Signed distance (pixels) to a w x h rounded rectangle of corner radius r; < 0 inside."""
    yy, xx = np.mgrid[0:h, 0:w]
    px, py = xx + .5 - w / 2, yy + .5 - h / 2
    qx, qy = np.abs(px) - (w / 2 - r), np.abs(py) - (h / 2 - r)
    outside = np.sqrt(np.maximum(qx, 0) ** 2 + np.maximum(qy, 0) ** 2)
    inside = np.minimum(np.maximum(qx, qy), 0)
    return outside + inside - r

# Buttons are drawn at this fraction of the logo-chrome contrast. 1.0 was the first pass; the
# brief after seeing it in the app was "about 30% where it is now 80%", so .375.
BUTTON_CONTRAST = .375
# Corner radius in sprite pixels (= canvas units when sliced). The slice border is 8 to hold it.
BUTTON_RADIUS = 5

def soft_button(w, h, stops, outline, rim_top, rim_bottom, radius, contrast=BUTTON_CONTRAST, brush=.012, seed=1):
    """A rounded button: the chrome profile squeezed toward its own average by `contrast`, a 1px
    outline and a 1px rim that follow the rounded corners, antialiased edge."""
    body = lerp_profile(stops, h)
    mean = body.mean(axis=0)
    body = mean + (body - mean) * contrast
    rim_t = mean + (np.array(rim_top, float) - mean) * min(1, contrast * 1.6)
    rim_b = mean + (np.array(rim_bottom, float) - mean) * min(1, contrast * 1.6)

    img = np.repeat(body[:, None, :], w, axis=1)
    img *= brushed(w, h, brush, seed)[:, :, None]

    d = rounded_rect_sdf(w, h, radius)
    y_frac = (np.arange(h)[:, None] + .5) / h * np.ones((1, w))
    rim = np.where(y_frac[:, :, None] < .5, rim_t, rim_b)
    rim_band = (d > -2) & (d <= -1)
    img = np.where(rim_band[:, :, None], rim, img)
    out_band = d > -1
    img = np.where(out_band[:, :, None], np.array(outline, float), img)

    alpha = np.clip(.5 - d, 0, 1)
    return np.dstack([np.clip(img, 0, 255), alpha * 255]).astype(np.uint8)

def scale(stops, k, add=(0, 0, 0)):
    return [(t, tuple(min(255, c * k + a) for c, a in zip(col, add))) for t, col in stops]

# --- palettes ------------------------------------------------------------------------------
# The wordmark's chrome, top to bottom (sampled): steel blue -> bright band -> sharp horizon to
# dark navy -> back up to a light rim. Buttons use a gunmetal version so white text stays legible.
GUNMETAL = [
    (0.00, (78, 96, 118)),
    (0.36, (104, 118, 134)),
    (0.46, (96, 108, 122)),
    (0.50, (30, 40, 54)),      # the horizon
    (0.74, (40, 54, 72)),
    (0.95, (70, 88, 108)),
    (1.00, (84, 104, 124)),
]
OUTLINE = (6, 12, 20)

# The brush's gold (sampled 139,104,21 -> 238,193,55), with the same chrome horizon.
GOLD = [
    (0.00, (196, 146, 44)),
    (0.36, (246, 206, 96)),
    (0.46, (232, 186, 66)),
    (0.50, (176, 124, 30)),    # a softer horizon than the steel: dark text sits across it
    (0.74, (198, 146, 40)),
    (0.95, (226, 178, 60)),
    (1.00, (236, 192, 78)),
]
GOLD_OUTLINE = (36, 22, 4)

# Full-brightness logo chrome, toned down so a label on top still reads (UV range handle).
KNOB = scale([
    (0.00, (131, 164, 199)),
    (0.40, (201, 208, 216)),
    (0.50, (137, 152, 170)),
    (0.53, (49, 70, 93)),
    (0.80, (101, 133, 162)),
    (1.00, (164, 178, 189)),
], .68)

def save(name, arr):
    Image.fromarray(arr, "RGBA").save(os.path.join(OUT, name))
    return arr

sheet = []

# Buttons: 256x128, slice border 6 (existing meta). Corner radius 3 fits inside the border.
sheet.append(save("HB_9sliceSolid.png", soft_button(256, 128, GUNMETAL, OUTLINE, (150, 172, 196), (100, 124, 150), BUTTON_RADIUS)))
sheet.append(save("HB_9sliceSolidHov.png", soft_button(256, 128, scale(GUNMETAL, 1.28, (4, 8, 14)), OUTLINE, (196, 214, 232), (130, 156, 182), BUTTON_RADIUS, seed=2)))
# Pressed: a flash of the brush's gold.
sheet.append(save("HB_9sliceSolidClick.png", soft_button(256, 128, GOLD, GOLD_OUTLINE, (255, 238, 170), (240, 200, 110), BUTTON_RADIUS, seed=3)))
# Primary action (new): gold at rest.
sheet.append(save("HB_9sliceGold.png", soft_button(256, 128, GOLD, GOLD_OUTLINE, (255, 238, 170), (240, 200, 110), BUTTON_RADIUS, seed=4)))
sheet.append(save("HB_9sliceGoldHov.png", soft_button(256, 128, scale(GOLD, 1.1, (6, 6, 0)), GOLD_OUTLINE, (255, 246, 200), (250, 214, 130), BUTTON_RADIUS, seed=7)))

# Input fields (FineEdge, 128x128, border 5): a dark inset well with a steel edge.
w = h = 128
field = np.zeros((h, w, 3), float)
field[:] = lerp_profile([(0, (12, 16, 22)), (.12, (20, 25, 32)), (1, (22, 27, 34))], h)[:, None, :]
field[0, :] = OUTLINE; field[h - 1, :] = (92, 112, 136)          # inset: dark top, lit bottom
field[:, 0] = OUTLINE; field[:, w - 1] = (60, 76, 96)
field[1, 1:w - 1] = (8, 10, 14)
sheet.append(save("HB_9slice_FineEdge.png", np.dstack([field, rounded_alpha(w, h, 3) * 255]).astype(np.uint8)))

# Slider handles (FineGlow, 128x128, border 5): a small chrome knob.
sheet.append(save("HB_9slice_FineGlow.png", chrome_tile(128, 128, KNOB, OUTLINE, (220, 230, 240), (150, 170, 190), 4, side_lift=1.25, seed=5)))

# Divider (256x128, border x=20): hairline groove, dark over light, fading out at the ends.
div = np.zeros((128, 256, 4), float)
fade = np.clip(np.minimum(np.arange(256), 255 - np.arange(256)) / 20.0, 0, 1)
div[62:64, :, :3] = (4, 8, 12); div[62:64, :, 3] = 255 * fade
div[64, :, :3] = (128, 150, 176); div[64, :, 3] = 235 * fade
sheet.append(save("Divider.png", div.astype(np.uint8)))

# Panels (new, 128x256, border L4 T40 R4 B8): opaque, the colour baked in. A gunmetal sheen
# in the top border - the logo's chrome, very dark - then a quiet steel body.
w, h = 128, 256
top = lerp_profile([(0, (70, 84, 102)), (.55, (52, 62, 76)), (.68, (22, 28, 38)), (1, (36, 41, 48))], 38)
body = lerp_profile([(0, (36, 41, 48)), (1, (25, 28, 33))], h - 40)
panel = np.zeros((h, w, 3), float)
panel[0, :] = OUTLINE
panel[1, :] = (128, 150, 176)                                    # bright top rim
panel[2:40] = top[:, None, :]
panel[40:] = body[:, None, :]
panel[2:] *= brushed(w, h - 2, .012, 6)[:, :, None]
panel[1:, 1] = np.clip(panel[1:, 1] * 1.6, 0, 255)                # lit left edge
panel[1:, w - 2] = np.clip(panel[1:, w - 2] * 1.3, 0, 255)
panel[:, 0] = OUTLINE; panel[:, w - 1] = OUTLINE; panel[h - 1, :] = OUTLINE
sheet.append(save("HB_PanelSteel.png", np.dstack([np.clip(panel, 0, 255), rounded_alpha(w, h, 4) * 255]).astype(np.uint8)))

if PREVIEW:
    # Mock-up: each sprite drawn the way Unity slices it, at button/panel sizes, on the app's bg.
    def slice9(arr, bw, bh, border):
        l, t, r, b = border
        h0, w0 = arr.shape[:2]
        src = Image.fromarray(arr, "RGBA")
        out = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
        xs = [(0, l, 0, l), (l, w0 - r, l, bw - r), (w0 - r, w0, bw - r, bw)]
        ys = [(0, t, 0, t), (t, h0 - b, t, bh - b), (h0 - b, h0, bh - b, bh)]
        for sx0, sx1, dx0, dx1 in xs:
            for sy0, sy1, dy0, dy1 in ys:
                if dx1 > dx0 and dy1 > dy0:
                    out.paste(src.crop((sx0, sy0, sx1, sy1)).resize((dx1 - dx0, dy1 - dy0), Image.BILINEAR), (dx0, dy0))
        return out
    from PIL import ImageDraw, ImageFont
    try: font = ImageFont.truetype("arialbd.ttf", 13)
    except Exception: font = ImageFont.load_default()
    names = ["HB_9sliceSolid.png", "HB_9sliceSolidHov.png", "HB_9sliceSolidClick.png", "HB_9sliceGold.png",
             "HB_9slice_FineEdge.png", "HB_9slice_FineGlow.png", "HB_PanelSteel.png", "Divider.png"]
    arrs = {n: np.array(Image.open(os.path.join(OUT, n)).convert("RGBA")) for n in names}
    canvas = Image.new("RGBA", (760, 420), (10, 14, 20, 255))
    canvas.alpha_composite(slice9(arrs["HB_PanelSteel.png"], 330, 400, (4, 40, 4, 8)), (10, 10))
    d = ImageDraw.Draw(canvas)
    d.text((40, 22), "HAIRBRUSH - BETA", fill=(240, 196, 80), font=font)
    y = 64
    for label, n in (("SAVE PROJ", "HB_9sliceSolid.png"), ("HOVER", "HB_9sliceSolidHov.png"),
                     ("PRESSED", "HB_9sliceSolidClick.png")):
        canvas.alpha_composite(slice9(arrs[n], 290, 30, (8, 8, 8, 8)), (30, y))
        d.text((175 - d.textlength(label, font=font) / 2, y + 7), label, fill=(240, 250, 255), font=font)
        y += 40
    canvas.alpha_composite(slice9(arrs["HB_9sliceGold.png"], 290, 30, (8, 8, 8, 8)), (30, y))
    d.text((175 - d.textlength("START GROOMING", font=font) / 2, y + 7), "START GROOMING", fill=(30, 20, 4), font=font)
    y += 46
    canvas.alpha_composite(slice9(arrs["Divider.png"], 290, 10, (20, 0, 20, 0)), (30, y)); y += 20
    d.text((30, y + 2), "Length: 0.085", fill=(235, 240, 245), font=font); y += 22
    d.rectangle((30, y + 6, 320, y + 10), fill=(31, 36, 41))
    d.rectangle((30, y + 6, 220, y + 10), fill=(101, 133, 162))
    canvas.alpha_composite(slice9(arrs["HB_9slice_FineGlow.png"], 14, 20, (5, 5, 5, 5)), (214, y - 2)); y += 30
    canvas.alpha_composite(slice9(arrs["HB_9slice_FineEdge.png"], 90, 24, (5, 5, 5, 5)), (30, y))
    d.text((42, y + 4), "1234", fill=(235, 240, 245), font=font)
    # raw tiles on the right
    x, y = 360, 10
    for n in names:
        t = Image.fromarray(arrs[n], "RGBA"); t.thumbnail((180, 90))
        canvas.alpha_composite(t, (x, y)); d.text((x, y + t.size[1] + 2), n, fill=(150, 160, 170))
        x += 200
        if x > 600: x, y = 360, y + 130
    canvas.convert("RGB").resize((1520, 840), Image.LANCZOS).save(PREVIEW)
