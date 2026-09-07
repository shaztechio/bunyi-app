# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

"""Rebuild committed MSIX assets from Bunyi's existing 1024px master.

Requires Pillow. Run from any directory: python path/to/make-assets.py.
No artwork is generated or redesigned; every size is sampled from the master.
"""
from math import ceil
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
MASTER = HERE.parents[2] / "macos/Assets.xcassets/AppIcon.appiconset/icon-512pt@2x.png"


def main():
    assets = HERE / "Assets"
    assets.mkdir(exist_ok=True)
    with Image.open(MASTER) as source:
        master = source.convert("RGBA")
    for name, base in (("StoreLogo", 50), ("Square44x44Logo", 44),
                       ("Square150x150Logo", 150)):
        for scale in (100, 125, 150, 200, 400):
            size = ceil(base * scale / 100)
            master.resize((size, size), Image.Resampling.LANCZOS).save(
                assets / f"{name}.scale-{scale}.png")
    for size in (16, 24, 32, 48, 256):
        icon = master.resize((size, size), Image.Resampling.LANCZOS)
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            icon.save(assets / f"Square44x44Logo.targetsize-{size}{suffix}.png")
    # Partner Center listing artwork, not part of the installed package.
    listing = HERE / "StoreListing"
    listing.mkdir(exist_ok=True)
    master.resize((300, 300), Image.Resampling.LANCZOS).save(listing / "StoreLogo-300.png")
    print(f"Wrote {len(list(assets.glob('*.png')))} package icons and the 300px listing logo.")


if __name__ == "__main__":
    main()

