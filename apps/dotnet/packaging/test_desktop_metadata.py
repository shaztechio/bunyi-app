#!/usr/bin/env python3
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

"""Check generated package metadata and the actual Store artwork it embeds."""

from pathlib import Path
import struct
import tempfile
import unittest
import xml.etree.ElementTree as ET

from desktop_metadata import APP_ID, STORE, generate


class DesktopMetadataTests(unittest.TestCase):
    def test_desktop_and_appstream_reuse_store_text_without_windows_requirements(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            generate(output)
            desktop = (output / f"{APP_ID}.desktop").read_text(encoding="utf-8")
            component = ET.parse(output / f"{APP_ID}.metainfo.xml").getroot()
            manifest = ET.parse(STORE / "AppxManifest.xml").getroot()
            ns = {"m": "http://schemas.microsoft.com/appx/manifest/foundation/windows10"}
            name = manifest.findtext("m:Properties/m:DisplayName", namespaces=ns)
            self.assertIn(f"Name={name}\n", desktop)
            self.assertEqual(component.findtext("name"), name)
            self.assertEqual(component.findtext("launchable"), f"{APP_ID}.desktop")
            self.assertIn("Exec=bunyi-desktop\n", desktop)
            self.assertIn("Terminal=false\n", desktop)
            for term in (STORE / "StoreListing/text/search-terms.txt").read_text().splitlines():
                self.assertIn(term + ";", desktop)
            appstream = ET.tostring(component, encoding="unicode")
            self.assertIn((STORE / "StoreListing/text/features.txt").read_text().strip(), appstream)
            self.assertNotIn("Windows Store build", appstream)
            self.assertNotIn("style instruction", appstream)

    def test_ico_frames_and_linux_icons_are_identical_to_store_pngs(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            generate(output)
            ico = (output / "bunyi.ico").read_bytes()
            self.assertEqual(struct.unpack_from("<HHH", ico), (0, 1, 5))
            for index, size in enumerate((16, 24, 32, 48, 256)):
                width, height, _, _, planes, bits, length, offset = struct.unpack_from("<BBBBHHII", ico, 6 + index * 16)
                self.assertEqual((width, height, planes, bits), (size % 256, size % 256, 1, 32))
                original = (STORE / f"Assets/Square44x44Logo.targetsize-{size}.png").read_bytes()
                self.assertEqual(ico[offset:offset + length], original)
                self.assertEqual((output / f"icons/hicolor/{size}x{size}/apps/{APP_ID}.png").read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
