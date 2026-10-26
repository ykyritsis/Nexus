"""Draws the Nexus app icon (src/Nexus/Assets/nexus.ico and nexus.png).
usage: python tools/make-icon.py
A dark rounded tile with a glowing ring, like a controller's Guide button.
"""
import os

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "Nexus", "Assets")
os.makedirs(OUT, exist_ok=True)

W = 1024
ACCENT = (90, 200, 250, 255)

img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
tile = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(tile)
for y in range(W):  # dark vertical gradient
    t = y / W
    c = (int(30 - 12 * t), int(34 - 12 * t), int(44 - 14 * t), 255)
    d.line([(0, y), (W, y)], fill=c)
mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, W - 1), radius=int(W * 0.22), fill=255)
img.paste(tile, (0, 0), mask)

ring = Image.new("RGBA", (W, W), (0, 0, 0, 0))
rd = ImageDraw.Draw(ring)
r, lw = W * 0.29, int(W * 0.085)
box = (W / 2 - r, W / 2 - r, W / 2 + r, W / 2 + r)
rd.arc(box, 0, 360, fill=ACCENT, width=lw)
glow = ring.filter(ImageFilter.GaussianBlur(W * 0.04))
img = Image.alpha_composite(img, glow)
img = Image.alpha_composite(img, glow)
img = Image.alpha_composite(img, ring)
core = Image.new("RGBA", (W, W), (0, 0, 0, 0))
cr = W * 0.1
ImageDraw.Draw(core).ellipse((W / 2 - cr, W / 2 - cr, W / 2 + cr, W / 2 + cr), fill=(235, 245, 255, 255))
img = Image.alpha_composite(img, core)

img.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "nexus.png"))
img.save(os.path.join(OUT, "nexus.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon written to", os.path.abspath(OUT))
