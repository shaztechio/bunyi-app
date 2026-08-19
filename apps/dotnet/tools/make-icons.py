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
#
# Builds this app's icons from the macOS app's, so the two look like one
# product. Not run by the build: the results are committed, and this is here so
# they can be rebuilt when the macOS icon changes.
#
#   python -m venv venv && venv/bin/pip install Pillow
#   venv/bin/python tools/make-icons.py

import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(
    HERE, "..", "..", "macos", "Assets.xcassets", "AppIcon.appiconset")
ASSETS = os.path.join(HERE, "..", "src", "App", "Assets")

# Windows shows the icon at all of these: 16 in the title bar, 256 in Explorer's
# large view. Leaving a size out makes Windows scale a neighbouring one, which
# looks worse than any of them.
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# What the window itself is given. One large PNG rather than a set: Avalonia
# scales it per platform, and 256 is enough for a title bar and a taskbar on a
# high-DPI screen.
WINDOW_SIZE = 256


def main() -> None:
    os.makedirs(ASSETS, exist_ok=True)

    master = Image.open(os.path.join(SOURCE, "icon-512pt@2x.png")).convert("RGBA")
    print(f"source {master.size}")

    window = master.resize((WINDOW_SIZE, WINDOW_SIZE), Image.LANCZOS)
    window_path = os.path.join(ASSETS, "bunyi.png")
    window.save(window_path)
    print(f"wrote {window_path} {window.size}")

    # Each size resampled from the 1024 master rather than from the one above
    # it, so a 16-pixel icon is not four rounds of downscaling deep.
    frames = [master.resize((s, s), Image.LANCZOS) for s in ICO_SIZES]

    ico_path = os.path.join(ASSETS, "bunyi.ico")
    frames[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in ICO_SIZES])
    print(f"wrote {ico_path} with {len(ICO_SIZES)} sizes")


if __name__ == "__main__":
    main()
