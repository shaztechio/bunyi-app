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

"""Package a self-contained Linux CPU publish into DEB and RPM installers."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

PACKAGING = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGING))
from desktop_metadata import APP_ID, generate, metadata

DOTNET = PACKAGING.parent
DEB_DEPENDS = (
    "libc6 (>= 2.35), libgcc-s1, libstdc++6, libgomp1, zlib1g, "
    "libicu72 | libicu74 | libicu76 | libicu78, libssl3t64 | libssl3, "
    "libgssapi-krb5-2, ca-certificates, tzdata, libx11-6, libice6, libsm6, "
    "libxi6, libxcursor1, libxrandr2, libxext6, libxrender1, libgl1, "
    "libfontconfig1, libasound2t64 | libasound2, libpulse0, libdbus-1-3, xdg-utils"
)
RPM_DEPENDS = (
    "glibc >= 2.35, libgcc, libstdc++, libgomp, zlib, libicu, openssl-libs, "
    "krb5-libs, ca-certificates, tzdata, libX11, libICE, libSM, libXi, "
    "libXcursor, libXrandr, libXext, libXrender, mesa-libGL, fontconfig, "
    "alsa-lib, pulseaudio-libs, dbus-libs, xdg-utils"
)


def verify_publish(publish, version):
    for name in ("Bunyi.App", "Bunyi.App.dll", "libcoreclr.so", "libonnxruntime.so",
                 "Bunyi.App.runtimeconfig.json"):
        if not (publish / name).is_file():
            raise ValueError(f"Missing self-contained Linux app file: {name}")
    deps = json.loads((publish / "Bunyi.App.deps.json").read_text())
    if not deps["runtimeTarget"]["name"].endswith("/linux-x64") or any(
            name.startswith("Microsoft.ML.OnnxRuntime.Gpu/") for name in deps["libraries"]):
        raise ValueError("Only the self-contained linux-x64 CPU desktop build can be installed")
    if f"Bunyi.App/{version}" not in deps["libraries"]:
        raise ValueError(f"Published app does not match version {version}")
    defaults = json.loads((publish / "bunyi.defaults.json").read_text())
    if defaults != {"schemaVersion": 1, "modelDownloadSource": "mirror"}:
        raise ValueError("Published app must contain the shared mirror default")


def stage(publish, root, generated):
    destination = root / "usr/lib/bunyi"
    shutil.copytree(publish, destination)
    # Nothing ever writes to the original publish folder or to user app data.
    generate(generated)
    for source, target in (
        (generated / f"{APP_ID}.desktop", root / f"usr/share/applications/{APP_ID}.desktop"),
        (generated / f"{APP_ID}.metainfo.xml", root / f"usr/share/metainfo/{APP_ID}.metainfo.xml"),
        (DOTNET.parents[1] / "LICENSE", root / "usr/share/doc/bunyi/copyright"),
    ):
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    shutil.copytree(generated / "icons", root / "usr/share/icons")
    launcher = root / "usr/bin/bunyi-desktop"
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher.write_text('#!/bin/sh\nexec /usr/lib/bunyi/Bunyi.App "$@"\n')
    for path in root.rglob("*"):
        path.chmod(0o755 if path.is_dir() else 0o644)
    launcher.chmod(0o755)
    (destination / "Bunyi.App").chmod(0o755)


def build(publish, output, formats, version=None):
    publish, output = Path(publish).resolve(), Path(output).resolve()
    if output == publish or publish in output.parents:
        raise ValueError("Output directory must be outside the publish directory")
    version = version or ET.parse(DOTNET / "Directory.Build.props").findtext(".//VersionPrefix")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version or ""):
        raise ValueError("Expected a major.minor.patch version")
    verify_publish(publish, version)
    for tool in ((["dpkg-deb"] if "deb" in formats else []) +
                 (["rpmbuild"] if "rpm" in formats else [])):
        if not shutil.which(tool):
            raise ValueError(f"Install {tool} before packaging")
    output.mkdir(parents=True, exist_ok=True)
    # Retain unique staging output for diagnosis, never recursively remove a
    # supplied directory. RPM paths with spaces need spec quoting throughout.
    work = Path(tempfile.mkdtemp(prefix="bunyi-packages-"))
    root = work / "root"
    stage(publish, root, work / "metadata")
    product = metadata()
    results = []
    if "deb" in formats:
        control = root / "DEBIAN/control"
        control.parent.mkdir()
        installed_kb = sum(p.stat().st_size for p in root.rglob("*") if p.is_file()) // 1024
        control.write_text(
            f"Package: bunyi\nVersion: {version}\nArchitecture: amd64\n"
            "Section: sound\nPriority: optional\n"
            f"Maintainer: {product['publisher']} <hello@bunyi.app>\nHomepage: https://bunyi.app/\n"
            f"Installed-Size: {installed_kb}\nDepends: {DEB_DEPENDS}\n"
            f"Description: {product['summary']}\n {product['paragraphs'][1]}\n",
            encoding="utf-8")
        target = output / f"bunyi_{version}_amd64.deb"
        subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(root), str(target)], check=True)
        # DEBIAN metadata is outside the RPM file list; it is not installed.
        results.append(target)
    if "rpm" in formats:
        top = work / "rpm"
        for name in ("BUILD", "RPMS", "SOURCES", "SPECS", "SRPMS"):
            (top / name).mkdir(parents=True)
        spec = top / "SPECS/bunyi.spec"
        spec.write_text(f'''Name: bunyi
Version: {version}
Release: 1
Summary: {product['summary']}
License: Apache-2.0
URL: https://bunyi.app/
Vendor: {product['publisher']}
BuildArch: x86_64
Requires: {RPM_DEPENDS}
# .NET includes optional diagnostic libraries; automatic dependency scanning
# would require their optional libraries too. Runtime needs are tested in CI.
AutoReqProv: no
%global debug_package %{{nil}}
%global __os_install_post %{{nil}}

%description
{product['paragraphs'][1]}

%install
mkdir -p "%{{buildroot}}"
cp -a "{root}/usr" "%{{buildroot}}/"

%files
%defattr(-,root,root,-)
/usr/lib/bunyi
/usr/bin/bunyi-desktop
/usr/share/applications/{APP_ID}.desktop
/usr/share/metainfo/{APP_ID}.metainfo.xml
/usr/share/icons/hicolor/*/apps/{APP_ID}.png
%license /usr/share/doc/bunyi/copyright
''', encoding="utf-8")
        subprocess.run(["rpmbuild", "--define", f"_topdir {top}", "-bb", str(spec)], check=True)
        target = output / f"bunyi-{version}-1.x86_64.rpm"
        shutil.copyfile(top / f"RPMS/x86_64/{target.name}", target)
        results.append(target)
    for target in results:
        with target.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        Path(str(target) + ".sha256").write_text(f"{digest}  {target.name}\n")
    print(f"Staging retained at {work}")
    return results


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-directory", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=DOTNET / "dist")
    parser.add_argument("--formats", nargs="+", choices=("deb", "rpm"), default=["deb", "rpm"])
    parser.add_argument("--version", help="Override the source version; published binary must match (upgrade fixtures)")
    args = parser.parse_args()
    for result in build(args.publish_directory, args.output, args.formats, args.version):
        print(result)
