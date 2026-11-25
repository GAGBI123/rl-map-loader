"""Finds the blue "Restart to update" button in the header of the app window on a screenshot.

    find_button.py <screenshot.png> <window x> <window y> <window width>   ->  prints "x y" (screen coordinates)
"""
import sys

from PIL import Image

img = Image.open(sys.argv[1]).convert("RGB")
wx, wy, ww = (int(v) for v in sys.argv[2:5])
xs, ys = [], []
# right half of the header row (the accent gradient runs from #2D7FF9 to #4F6BFF)
for y in range(wy + 6, min(wy + 80, img.height)):
    for x in range(wx + ww // 2, min(wx + ww, img.width)):
        r, g, b = img.getpixel((x, y))
        if b >= 225 and 30 <= r <= 100 and 95 <= g <= 145:
            xs.append(x)
            ys.append(y)
if len(xs) < 100:
    sys.exit(1)
print((min(xs) + max(xs)) // 2, (min(ys) + max(ys)) // 2)
