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

"""Generate desktop integration from the existing Windows Store listing."""

import argparse
import json
from pathlib import Path
import shutil
import struct
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
STORE = ROOT / "windows"
APP_ID = "app.bunyi.Bunyi"
NS = {"m": "http://schemas.microsoft.com/appx/manifest/foundation/windows10"}


def metadata():
    manifest = ET.parse(STORE / "AppxManifest.xml")
    props = manifest.find("m:Properties", NS)
    text = STORE / "StoreListing/text"
    paragraphs = (text / "description.txt").read_text(encoding="utf-8").strip().split("\n\n")
    return {
        "name": props.findtext("m:DisplayName", namespaces=NS),
        "publisher": props.findtext("m:PublisherDisplayName", namespaces=NS),
        "description": props.findtext("m:Description", namespaces=NS),
        "summary": paragraphs[0],
        "paragraphs": paragraphs[:2] + [(text / "features.txt").read_text(encoding="utf-8").strip()],
        "keywords": (text / "search-terms.txt").read_text(encoding="utf-8").splitlines(),
    }


def generate(output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    data = metadata()
    (output / "metadata.json").write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")

    # ICO can contain PNG frames verbatim. No resampling or alternate artwork:
    # these are the exact target-size logos prepared for the Windows Store.
    sizes = (16, 24, 32, 48, 256)
    frames = [(STORE / f"Assets/Square44x44Logo.targetsize-{size}.png").read_bytes()
              for size in sizes]
    offset = 6 + 16 * len(frames)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    for size, frame in zip(sizes, frames):
        directory.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0,
                                     1, 32, len(frame), offset))
        offset += len(frame)
        target = output / f"icons/hicolor/{size}x{size}/apps"
        target.mkdir(parents=True, exist_ok=True)
        (target / f"{APP_ID}.png").write_bytes(frame)
    (output / "bunyi.ico").write_bytes(directory + b"".join(frames))
    # The Store's largest square tile is 600px; retain its actual dimensions.
    large = output / "icons/hicolor/600x600/apps"
    large.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(STORE / "Assets/Square150x150Logo.scale-400.png", large / f"{APP_ID}.png")

    desktop = ("[Desktop Entry]\nType=Application\n"
               f"Name={data['name']}\nComment={data['description']}\n"
               f"Exec=bunyi-desktop\nIcon={APP_ID}\nTerminal=false\n"
               "Categories=AudioVideo;Audio;\n"
               f"Keywords={';'.join(data['keywords'])};\n")
    (output / f"{APP_ID}.desktop").write_text(desktop, encoding="utf-8", newline="\n")
    component = ET.Element("component", type="desktop-application")
    for tag, value in (("id", APP_ID), ("metadata_license", "CC0-1.0"),
                       ("project_license", "Apache-2.0"), ("name", data["name"]),
                       ("summary", data["summary"].rstrip("."))):
        ET.SubElement(component, tag).text = value
    developer = ET.SubElement(component, "developer", id="app.bunyi")
    ET.SubElement(developer, "name").text = data["publisher"]
    ET.SubElement(component, "launchable", type="desktop-id").text = f"{APP_ID}.desktop"
    ET.SubElement(component, "url", type="homepage").text = "https://bunyi.app/"
    ET.SubElement(component, "url", type="bugtracker").text = "https://github.com/shaztechio/bunyi-app/issues"
    description = ET.SubElement(component, "description")
    for paragraph in data["paragraphs"]:
        ET.SubElement(description, "p").text = paragraph
    ET.indent(component)
    ET.ElementTree(component).write(output / f"{APP_ID}.metainfo.xml", encoding="utf-8", xml_declaration=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    generate(parser.parse_args().output)
