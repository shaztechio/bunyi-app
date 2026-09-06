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

"""Render the site and README badges from complete, published stable releases."""

import argparse
from html import escape
import json
from pathlib import Path
import re
import shutil

TAG = re.compile(r"(?P<prefix>dotnet-v|v)(?P<version>[0-9]+\.[0-9]+\.[0-9]+)")
TOKENS = {"macos": "{{MACOS_VERSION}}", "dotnet": "{{DOTNET_VERSION}}"}
BADGES = {"macos": ("macOS", "macos"), "windows": ("Windows", "dotnet"),
          "linux": ("Linux", "dotnet")}


def complete_assets(release, family, version):
    names = {
        asset["name"] for asset in release.get("assets", [])
        if asset.get("state") == "uploaded" and asset.get("size", 0) > 0
    }
    if family == "dotnet":
        archives = {
            f"Bunyi-{version}-{rid}{cuda}.{extension}"
            for rid, extension in (("win-x64", "zip"), ("linux-x64", "tar.gz"))
            for cuda in ("", "-cuda")
        }
        return archives | {name + ".sha256" for name in archives} <= names
    # macOS includes an independent build number and one checksum file for
    # both archives. Require a complete set with the same version AND build.
    pattern = re.compile(rf"Bunyi-{re.escape(version)}-[0-9]+\.dmg")
    return any(
        {name[:-4] + ".zip", name[:-4] + ".sha256"} <= names
        for name in names if pattern.fullmatch(name)
    )


def select_versions(releases):
    # gh api --paginate --slurp returns one list per page.
    if releases and isinstance(releases[0], list):
        releases = [release for page in releases for release in page]
    selected = {}
    for release in releases:
        match = TAG.fullmatch(release.get("tag_name", ""))
        if not match or release.get("draft") or release.get("prerelease"):
            continue
        if not release.get("published_at"):
            continue
        family = "dotnet" if match["prefix"] == "dotnet-v" else "macos"
        version = match["version"]
        if not complete_assets(release, family, version):
            continue
        # Publishing a maintenance patch to an older line must not demote a
        # newer stable version. GitHub's global 'latest' mixes the two apps.
        order = tuple(map(int, version.split(".")))
        if family not in selected or order > selected[family][0]:
            selected[family] = (order, version)
    missing = TOKENS.keys() - selected.keys()
    if missing:
        raise ValueError("No complete stable release for: " + ", ".join(sorted(missing)))
    return {family: value[1] for family, value in selected.items()}


def render(template, versions):
    for family, token in TOKENS.items():
        if token not in template:
            raise ValueError("Missing release placeholder: " + token)
        template = template.replace(token, versions[family])
    if "{{" in template:
        raise ValueError("Unresolved template placeholder")
    return template


def release_badge(label, version):
    # Static SVGs served by Pages: no third-party badge service or browser API.
    # Size each panel for its text, including future multi-digit versions.
    left = len(label) * 8 + 20
    right = len(version) * 8 + 20
    width = left + right
    label, version = escape(label, quote=True), escape(version, quote=True)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="24" role="img" aria-label="{label}: {version}">
  <title>{label}: {version}</title>
  <rect width="{width}" height="24" rx="4" fill="#24292f"/>
  <path d="M {left} 0 H {width - 4} Q {width} 0 {width} 4 V 20 Q {width} 24 {width - 4} 24 H {left} Z" fill="#176b45"/>
  <g fill="#fff" text-anchor="middle" font-family="Verdana,DejaVu Sans,sans-serif" font-size="12">
    <text x="{left / 2}" y="16">{label}</text>
    <text x="{left + right / 2}" y="16">{version}</text>
  </g>
</svg>
'''


def build(source, output, releases):
    source, output = Path(source).resolve(), Path(output).resolve()
    if output == source or source in output.parents:
        raise ValueError("Build output must be outside the docs source directory")
    versions = select_versions(releases)
    html = render((source / "index.html").read_text(encoding="utf-8"), versions)
    output.mkdir(parents=True, exist_ok=True)
    # Only public site assets ship; build tools, credentials and release API
    # responses stay outside the Pages artifact.
    shutil.copytree(source / "assets", output / "assets", dirs_exist_ok=True)
    for name in ("CNAME", ".nojekyll"):
        shutil.copyfile(source / name, output / name)
    (output / "index.html").write_text(html, encoding="utf-8", newline="\n")
    badges = output / "releases"
    badges.mkdir(exist_ok=True)
    for name, (label, family) in BADGES.items():
        (badges / f"{name}.svg").write_text(
            release_badge(label, versions[family]), encoding="utf-8", newline="\n")
    return versions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--releases-json", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=Path("docs"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        releases = json.loads(args.releases_json.read_text(encoding="utf-8-sig"))
        versions = build(args.source, args.output, releases)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Site build failed: {error}\n")
    print(f"Site built: macOS {versions['macos']}; Windows/Linux {versions['dotnet']}")


if __name__ == "__main__":
    main()
