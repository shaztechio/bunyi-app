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
import json
import re
import subprocess
import sys
import tempfile
import unittest

from build_site import README_START, README_END, build, render, select_versions, update_readme

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

    def test_readme_preserves_prose_and_is_idempotent(self):
        original = (ROOT / "README.md").read_text(encoding="utf-8")
        prefix = original.split(README_START)[0]
        suffix = original.split(README_END)[1]
        with tempfile.TemporaryDirectory() as temp:
            readme = Path(temp) / "README.md"
            readme.write_text(original, encoding="utf-8")
            versions = {"macos": "7.8.0", "dotnet": "9.10.0"}
            self.assertTrue(update_readme(readme, versions))
            result = readme.read_text(encoding="utf-8")
            self.assertEqual(result.split(README_START)[0], prefix)
            self.assertEqual(result.split(README_END)[1], suffix)
            block = result.split(README_START)[1].split(README_END)[0]
            self.assertIn("[Bunyi 7.8.0]", block)
            self.assertIn("/tag/v7.8.0)", block)
            self.assertEqual(block.count("[Bunyi 9.10.0]"), 2)
            self.assertEqual(block.count("/tag/dotnet-v9.10.0)"), 2)
            self.assertNotIn("/latest", block)
            before = readme.read_bytes()
            self.assertFalse(update_readme(readme, versions))
            self.assertEqual(readme.read_bytes(), before)
            update_readme(readme, {**versions, "dotnet": "9.11.0"})
            self.assertIn("/tag/v7.8.0)", readme.read_text(encoding="utf-8"))

    def test_bad_readme_markers_fail_without_overwriting(self):
        with tempfile.TemporaryDirectory() as temp:
            readme = Path(temp) / "README.md"
            for original in ("No markers", README_START, README_END,
                             README_END + README_START,
                             README_START * 2 + README_END,
                             README_START + README_END * 2):
                with self.subTest(original=original):
                    readme.write_text(original, encoding="utf-8")
                    before = readme.read_bytes()
                    with self.assertRaises(ValueError):
                        update_readme(readme, {"macos": "1.0.0", "dotnet": "2.0.0"})
                    self.assertEqual(readme.read_bytes(), before)

    def test_cli_syncs_site_and_readme_from_same_snapshot(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            readme = temp / "README.md"
            readme.write_text(README_START + "\nold\n" + README_END, encoding="utf-8")
            snapshot = temp / "releases.json"
            releases = [release("macos", "7.8.0"), release("dotnet", "9.10.0")]
            incomplete = release("dotnet", "10.0.0")
            incomplete["assets"].pop()
            snapshot.write_text(json.dumps([releases, [incomplete]]), encoding="utf-8")
            command = [sys.executable, str(ROOT / "tools/build_site.py"),
                       "--releases-json", str(snapshot), "--readme", str(readme)]
            result = subprocess.run(command + ["--source", str(ROOT / "docs"),
                                               "--output", str(temp / "site")],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            for output in (readme, temp / "site/index.html"):
                text = output.read_text(encoding="utf-8")
                for tag in ("v7.8.0", "dotnet-v9.10.0"):
                    self.assertIn("/tag/" + tag, text)
                self.assertNotIn("10.0.0", text)
            before = readme.read_bytes()
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(readme.read_bytes(), before)
            snapshot.write_text("[]", encoding="utf-8")
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(readme.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
