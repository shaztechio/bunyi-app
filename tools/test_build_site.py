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

"""Exercise release selection and the actual website template without networking."""

from pathlib import Path
import re
import tempfile
import unittest

from build_site import build, render, select_versions

ROOT = Path(__file__).resolve().parents[1]


def release(family, version, build_number=4):
    if family == "dotnet":
        archives = [f"Bunyi-{version}-{rid}{cuda}.{ext}"
                    for rid, ext in (("win-x64", "zip"), ("linux-x64", "tar.gz"))
                    for cuda in ("", "-cuda")]
        names = archives + [name + ".sha256" for name in archives]
    else:
        names = [f"Bunyi-{version}-{build_number}.{ext}" for ext in ("dmg", "zip", "sha256")]
    return {
        "tag_name": ("dotnet-v" if family == "dotnet" else "v") + version,
        "draft": False, "prerelease": False, "published_at": "2026-09-06T00:00:00Z",
        "assets": [{"name": name, "state": "uploaded", "size": 100} for name in names],
    }


class SiteBuildTests(unittest.TestCase):
    def test_numeric_order_and_independent_families_across_pages(self):
        pages = [[release("dotnet", "2.9.0"), release("macos", "1.8.0")],
                 [release("dotnet", "2.10.0"), release("dotnet", "1.99.0")]]
        self.assertEqual(select_versions(pages), {"dotnet": "2.10.0", "macos": "1.8.0"})

    def test_only_published_stable_recognized_tags(self):
        invalid = []
        for key, value in (("draft", True), ("prerelease", True), ("published_at", None),
                           ("tag_name", "dotnet-v9.0.0-rc.1"), ("tag_name", "other-v9.0.0")):
            candidate = release("dotnet", "9.0.0")
            candidate[key] = value
            invalid.append(candidate)
        valid = [release("dotnet", "1.2.0"), release("macos", "1.3.0")]
        self.assertEqual(select_versions(invalid + valid), {"dotnet": "1.2.0", "macos": "1.3.0"})

    def test_incomplete_new_release_keeps_existing_downloads(self):
        valid = [release("dotnet", "1.2.0"), release("macos", "1.3.0")]
        for asset_index in range(8):
            with self.subTest(asset=asset_index):
                candidate = release("dotnet", "2.0.0")
                candidate["assets"].pop(asset_index)
                self.assertEqual(select_versions([candidate] + valid)["dotnet"], "1.2.0")
        for field, value in (("state", "new"), ("size", 0)):
            candidate = release("dotnet", "2.0.0")
            candidate["assets"][0][field] = value
            self.assertEqual(select_versions([candidate] + valid)["dotnet"], "1.2.0")

    def test_macos_requires_matching_build_and_checksum(self):
        valid = [release("dotnet", "1.2.0"), release("macos", "1.3.0")]
        candidate = release("macos", "2.0.0")
        candidate["assets"][1]["name"] = "Bunyi-2.0.0-5.zip"
        self.assertEqual(select_versions([candidate] + valid)["macos"], "1.3.0")
        candidate = release("macos", "2.0.0")
        candidate["assets"].pop()
        self.assertEqual(select_versions([candidate] + valid)["macos"], "1.3.0")

    def test_missing_family_fails_instead_of_publishing_broken_links(self):
        for releases in ([], [release("dotnet", "1.2.0")], [release("macos", "1.3.0")]):
            with self.assertRaisesRegex(ValueError, "No complete stable release"):
                select_versions(releases)

    def test_actual_template_uses_new_versions_everywhere(self):
        template = (ROOT / "docs/index.html").read_text(encoding="utf-8")
        html = render(template, {"macos": "1.8.0", "dotnet": "2.10.0"})
        self.assertNotIn("{{", html)
        self.assertNotIn("1.2.0", html)
        dotnet_links = re.findall(r'href="[^"]*/tag/(dotnet-v[^"]+)"', html)
        mac_links = re.findall(r'href="[^"]*/tag/(v[^"]+)"', html)
        self.assertEqual(dotnet_links, ["dotnet-v2.10.0"] * 7)
        self.assertEqual(mac_links, ["v1.8.0"] * 3)
        for text in ("Windows 2.10.0", "Linux 2.10.0", "Windows and Linux 2.10.0",
                     "Bunyi 2.10.0 for Windows", "Bunyi 2.10.0 for Linux",
                     "Bunyi-2.10.0-linux-x64.tar.gz", "macOS 1.8.0", "Bunyi 1.8.0"):
            self.assertIn(text, html)

    def test_placeholder_errors_are_not_deployed(self):
        versions = {"macos": "1.8.0", "dotnet": "2.10.0"}
        for template in ("No placeholders", "{{MACOS_VERSION}} {{DOTNET_VERSION}} {{UNKNOWN}}"):
            with self.assertRaises(ValueError):
                render(template, versions)

    def test_build_preserves_assets_and_source(self):
        source = ROOT / "docs"
        before = (source / "index.html").read_bytes()
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "site"
            build(source, output, [release("macos", "1.8.0"), release("dotnet", "2.10.0")])
            self.assertEqual((source / "index.html").read_bytes(), before)
            self.assertEqual((output / "CNAME").read_bytes(), (source / "CNAME").read_bytes())
            self.assertTrue((output / ".nojekyll").is_file())
            self.assertEqual((output / "assets/icon-64.png").read_bytes(), (source / "assets/icon-64.png").read_bytes())
            self.assertEqual({p.name for p in output.iterdir()}, {"assets", "index.html", "CNAME", ".nojekyll"})
        with self.assertRaisesRegex(ValueError, "outside"):
            build(source, source, [])


if __name__ == "__main__":
    unittest.main()