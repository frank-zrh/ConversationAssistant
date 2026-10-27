"""Export the original SVG app mark to Windows assets.

Requires Python 3.10+ and Pillow, as used by generate_demo_gif.py.
Run with --check to validate existing exports without modifying them.
The SVG master uses rounded rectangles, polygons, and a vertical gradient.
"""
from __future__ import annotations

import argparse
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageColor, ImageDraw

ASSETS = Path(__file__).resolve().parents[1] / "App" / "Assets"
NS = {"svg": "http://www.w3.org/2000/svg"}
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
EXPORTS = {
    "AppIcon.png": ((256, 256), 256),
    "Square44x44Logo.scale-200.png": ((88, 88), 88),
    "Square44x44Logo.targetsize-24_altform-unplated.png": ((24, 24), 24),
    "Square44x44Logo.targetsize-48_altform-lightunplated.png": ((48, 48), 48),
    "Square150x150Logo.scale-200.png": ((300, 300), 228),
    "Wide310x150Logo.scale-200.png": ((620, 300), 200),
    "SplashScreen.scale-200.png": ((1240, 600), 240),
    "StoreLogo.png": ((50, 50), 50),
    "LockScreenLogo.scale-200.png": ((48, 48), 48),
}


def render_master(monochrome: bool = False) -> Image.Image:
    root = ET.parse(ASSETS / "AppIcon.svg").getroot()
    edge = 1024
    scale = edge / 100
    image = Image.new("RGBA", (edge, edge))
    for element in root:
        kind = element.tag.rsplit("}", 1)[-1]
        if kind not in ("rect", "polygon"):
            continue
        fill = element.attrib["fill"]
        if monochrome and fill in ("url(#tile)", "#73DEEF"):
            continue
        mask = Image.new("L", image.size)
        draw = ImageDraw.Draw(mask)
        if kind == "rect":
            x, y, width, height, radius = (
                float(element.get(key, "0")) * scale
                for key in ("x", "y", "width", "height", "rx")
            )
            draw.rounded_rectangle((x, y, x + width, y + height), radius, fill=255)
        else:
            values = [float(value) * scale for value in element.attrib["points"].replace(",", " ").split()]
            draw.polygon(list(zip(values[::2], values[1::2])), fill=255)
        if fill.startswith("url("):
            gradient_id = fill[5:-1]
            gradient = root.find(f"svg:defs/svg:linearGradient[@id='{gradient_id}']", NS)
            if gradient is None:
                raise ValueError(f"Missing gradient: {gradient_id}")
            stops = gradient.findall("svg:stop", NS)
            top, bottom = (ImageColor.getrgb(stop.attrib["stop-color"]) for stop in stops)
            paint = Image.new("RGBA", image.size)
            pixels = ImageDraw.Draw(paint)
            for row in range(edge):
                fraction = max(0, min(1, (row - y) / height))
                color = tuple(round(a + (b - a) * fraction) for a, b in zip(top, bottom))
                pixels.line((0, row, edge, row), fill=(*color, 255))
        else:
            paint = Image.new("RGBA", image.size, "white" if monochrome else fill)
        image.alpha_composite(Image.composite(paint, Image.new("RGBA", image.size), mask))
    return image


def generate() -> None:
    master = render_master()
    for name, (size, mark_size) in EXPORTS.items():
        source = render_master(monochrome=True) if name.startswith("LockScreen") else master
        mark = source.resize((mark_size, mark_size), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", size)
        canvas.alpha_composite(mark, ((size[0] - mark_size) // 2, (size[1] - mark_size) // 2))
        canvas.save(ASSETS / name, optimize=True)
    master.save(ASSETS / "AppIcon.ico", format="ICO", sizes=[(size, size) for size in ICO_SIZES])


def validate() -> None:
    for name, (size, _) in EXPORTS.items():
        with Image.open(ASSETS / name) as image:
            image.verify()
        with Image.open(ASSETS / name) as image:
            if image.size != size or image.mode != "RGBA":
                raise ValueError(f"Invalid dimensions or pixel format: {name}")
            if image.getbbox() is None or image.getpixel((0, 0))[3] != 0:
                raise ValueError(f"Expected a visible mark with transparent corners: {name}")
            if name.startswith("LockScreen") and any(
                pixel[:3] != (255, 255, 255)
                for y in range(size[1]) for x in range(size[0])
                if (pixel := image.getpixel((x, y)))[3] > 0
            ):
                raise ValueError("Lock-screen mark must be monochrome white")
    with Image.open(ASSETS / "AppIcon.ico") as icon:
        if icon.ico.sizes() != {(size, size) for size in ICO_SIZES}:
            raise ValueError("ICO is missing a required Windows size")
        for size in ICO_SIZES:
            if icon.ico.getimage((size, size)).getbbox() is None:
                raise ValueError(f"Empty ICO frame: {size}")
    print(f"Validated {len(EXPORTS)} PNG assets and {len(ICO_SIZES)} ICO sizes.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Validate existing assets without writing files")
    args = parser.parse_args()
    if not args.check:
        generate()
    validate()


if __name__ == "__main__":
    main()
