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

"""Sync the site and README using complete, published stable GitHub releases."""

import argparse
import json
from pathlib import Path
import re
import shutil

TAG = re.compile(r"(?P<prefix>dotnet-v|v)(?P<version>[0-9]+\.[0-9]+\.[0-9]+)")
TOKENS = {"macos": "{{MACOS_VERSION}}", "dotnet": "{{DOTNET_VERSION}}"}
README_START = "<!-- release-downloads:start -->"
README_END = "<!-- release-downloads:end -->"
README_DOWNLOADS = """**Download:**

- **macOS:** [Bunyi {{MACOS_VERSION}}](https://github.com/shaztechio/bunyi-app/releases/tag/v{{MACOS_VERSION}}) — Apple Silicon, macOS 15 or later.
- **Windows:** [Bunyi {{DOTNET_VERSION}}](https://github.com/shaztechio/bunyi-app/releases/tag/dotnet-v{{DOTNET_VERSION}}) — x64, standard and NVIDIA CUDA builds.
- **Linux:** [Bunyi {{DOTNET_VERSION}}](https://github.com/shaztechio/bunyi-app/releases/tag/dotnet-v{{DOTNET_VERSION}}) — x64, standard and NVIDIA CUDA builds.
"""


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
    return versions


def update_readme(path, versions):
    path = Path(path)
    before = path.read_text(encoding="utf-8")
    if before.count(README_START) != 1 or before.count(README_END) != 1:
        raise ValueError("README must contain exactly one release-downloads block")
    start = before.index(README_START) + len(README_START)
    end = before.index(README_END)
    if end < start:
        raise ValueError("README release-downloads markers are out of order")
    after = before[:start] + "\n" + render(README_DOWNLOADS, versions) + before[end:]
    if after != before:
        path.write_text(after, encoding="utf-8", newline="\n")
    return after != before


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--releases-json", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=Path("docs"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--readme", type=Path, help="Update the marked downloads block in place")
    args = parser.parse_args()
    if not args.output and not args.readme:
        parser.error("at least one of --output or --readme is required")
    try:
        releases = json.loads(args.releases_json.read_text(encoding="utf-8-sig"))
        versions = (build(args.source, args.output, releases) if args.output
                    else select_versions(releases))
        if args.readme:
            update_readme(args.readme, versions)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Site build failed: {error}\n")
    print(f"Release references: macOS {versions['macos']}; Windows/Linux {versions['dotnet']}")


if __name__ == "__main__":
    main()
