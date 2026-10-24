#!/usr/bin/env python3
"""Derive WinSight PNG/tray assets locally from the supplied ICO; requires Pillow only for regeneration."""
import hashlib
import json
import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance
from PIL.IcoImagePlugin import IcoImageFile

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "Resources/AppIcon/WinSight.ico"


def main():
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else ICON
    with Image.open(source) as image:
        if not isinstance(image, IcoImageFile):
            raise ValueError("Expected the supplied multi-resolution WinSight.ico")
        sizes = image.ico.sizes()
        master = image.ico.getimage(max(sizes)).convert("RGBA")

        def frame(size):
            if (size, size) in sizes:
                return image.ico.getimage((size, size)).convert("RGBA")
            return master.resize((size, size), Image.Resampling.LANCZOS)

        normal = frame(64)
        dimmed = ImageEnhance.Brightness(normal).enhance(0.4)
        paused = normal.copy()
        draw = ImageDraw.Draw(paused)
        draw.ellipse((39, 39, 63, 63), fill=(20, 28, 38, 255), outline=(255, 255, 255, 255), width=2)
        draw.rounded_rectangle((46, 45, 49, 57), radius=1, fill="white")
        draw.rounded_rectangle((54, 45, 57, 57), radius=1, fill="white")
        outputs = {
            "Resources/AppIcon/32x32.png": frame(32),
            "Resources/AppIcon/128x128.png": frame(128),
            "Resources/AppIcon/256x256.png": frame(256),
            "Resources/AppIcon/icon.png": frame(256),
            "docs/logo.png": frame(256),
            "Resources/AppIcon/tray.png": normal,
            "Resources/AppIcon/tray-dim.png": dimmed,
            "Resources/AppIcon/tray-paused.png": paused,
        }
        for name, asset in outputs.items():
            path = ROOT / name
            temporary = path.with_suffix(".tmp.png")
            asset.save(temporary)
            temporary.replace(path)
    if source.resolve() != ICON.resolve():
        shutil.copyfile(source, ICON)
    hashes = {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in outputs}
    receipt = {"input_path": "Resources/AppIcon/WinSight.ico",
               "input_sha256": hashlib.sha256(ICON.read_bytes()).hexdigest(),
               "ico_sizes": sorted(sizes), "assets_sha256": hashes}
    (ROOT / "docs/winsight-icon-assets.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(f"WinSight.ico preserved; generated {len(outputs)} PNG/tray assets")


if __name__ == "__main__":
    main()
