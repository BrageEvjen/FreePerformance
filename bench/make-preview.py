from PIL import Image, ImageDraw, ImageFont
import os, sys

SRC = r"E:RimWorldModsParallelTickbenchdataportraitsscene_Creak.png"  # from: run-bench.ps1 -Portrait "scene=sleep|camsize=4|count=4|supersize=4"
OUT = sys.argv[1] if len(sys.argv) > 1 else "preview.png"
W, H = 1280, 720

src = Image.open(SRC).convert("RGB")
w, h = src.size
# 2x crop around the centre (rendered at 4x, so still sharp at 1280x720)
SHIFT = 440  # source px: moves the scene right by SHIFT/2 output px
bg = src.crop((w // 4 - SHIFT, h // 4, w * 3 // 4 - SHIFT, h * 3 // 4)).resize((W, H), Image.LANCZOS)

# Dark panel on the left for text, fading into the picture.
shade = Image.new("L", (W, H), 0)
d = ImageDraw.Draw(shade)
for x in range(W):
    a = 236 if x < 650 else int(236 * (780 - x) / 130) if x < 780 else 0
    d.line([(x, 0), (x, H)], fill=a)
img = Image.composite(Image.new("RGB", (W, H), (18, 16, 14)), bg, shade)
draw = ImageDraw.Draw(img)

F = r"C:\Windows\Fonts"
def font(name, size, bold=False):
    f = ImageFont.truetype(os.path.join(F, name), size)
    if bold:
        try:
            f.set_variation_by_name("Bold")
        except Exception:
            pass
    return f

title = font("bahnschrift.ttf", 66, True)
sub = font("segoeuisl.ttf", 30)
big = font("bahnschrift.ttf", 54, True)
desc = font("segoeui.ttf", 21)
small = font("segoeui.ttf", 19)
smallb = font("segoeuib.ttf", 19)

BEIGE = (236, 226, 200)
ORANGE = (232, 168, 72)
RED = (222, 98, 84)
GREY = (175, 168, 155)

x0 = 48
draw.text((x0, 38), "FREE PERFORMANCE", font=title, fill=BEIGE)
draw.text((x0, 116), "Same game. Less wasted work.", font=sub, fill=ORANGE)

# Three headline numbers
rows = [
    ("+14–23%", "faster game speed (TPS)", "in a late-game colony, measured in real play"),
    ("21", "optimizations", "each skips work that provably does nothing"),
    ("0", "differences from vanilla", "checked in test mode, also with 60 mods loaded"),
]
y = 190
for num, line1, line2 in rows:
    draw.text((x0, y), num, font=big, fill=ORANGE)
    nx = x0 + max(draw.textlength("+14–23%", font=big), 0) + 22
    draw.text((nx, y + 6), line1, font=font("segoeuib.ttf", 23), fill=BEIGE)
    draw.text((nx, y + 36), line2, font=small, fill=GREY)
    y += 96

# Footer
draw.text((x0, H - 88), "Works with Combat Extended, VEF, HAR, Performance Optimizer", font=small, fill=BEIGE)
draw.text((x0, H - 60), "Saves nothing to your save  ·  Open source", font=small, fill=GREY)

# Example on the right: the sleeping colonist
px, py = 575 + SHIFT // 2, 172
draw.ellipse([px - 38, py - 38, px + 38, py + 38], outline=ORANGE, width=4)
bx, by = 880, 410
draw.line([(px + 22, py + 32), (bx + 30, by - 4)], fill=ORANGE, width=3)
lines = ["Example: a colonist asleep in bed", "Path follower", "Weapon cooldowns", "Stance tracker"]
bw = 330
bh = 30 + 30 * (len(lines) - 1) + 14
draw.rounded_rectangle([bx - 14, by - 8, bx + bw, by + bh], radius=10, fill=(24, 22, 20), outline=ORANGE, width=2)
draw.text((bx, by - 2), lines[0], font=smallb, fill=BEIGE)
yy = by + 30
for t in lines[1:]:
    draw.line([(bx + 2, yy + 7), (bx + 14, yy + 19)], fill=RED, width=3)
    draw.line([(bx + 14, yy + 7), (bx + 2, yy + 19)], fill=RED, width=3)
    draw.text((bx + 26, yy), t + " – skipped", font=small, fill=BEIGE)
    yy += 30

img.save(OUT, optimize=True)
print(OUT, os.path.getsize(OUT))
