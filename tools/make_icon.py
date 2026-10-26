"""Draws the ReelCap icon: a rounded dark tile with a red record dot inside a ring."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(sys.argv[1] if len(sys.argv) > 1 else "src")
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Tile with a vertical gradient.
tile = Image.new("RGBA", (S, S))
td = ImageDraw.Draw(tile)
for y in range(S):
    t = y / S
    td.line([(0, y), (S, y)], fill=(int(36 - 14 * t), int(38 - 14 * t), int(52 - 18 * t), 255))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=255)
img.paste(tile, (0, 0), mask)

c = S // 2
d.ellipse([c - 300, c - 300, c + 300, c + 300], outline=(255, 255, 255, 60), width=34)
d.ellipse([c - 200, c - 200, c + 200, c + 200], fill=(255, 72, 92, 255))
d.ellipse([c - 120, c - 160, c + 20, c - 60], fill=(255, 160, 170, 90))

img.resize((256, 256), Image.LANCZOS).save(out / "icon.png")
img.save(out / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", out / "icon.ico")
