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

"""Builds the AliasRecord Finder uses to find a disk image's background.

    make-background-alias.py <mounted-background-file> <output.bin>

make-dmg.sh runs this against the mounted read-write image, before it is
converted to the compressed image that ships.

Why an alias and not bookmark data: a DMG known to display its background
was inspected, and Finder had stored a version-2 AliasRecord. Bookmark data
in the same field did not draw, so this reproduces the format that is known
to work rather than the one that ought to.

Why it must run against the live volume: an alias identifies the volume by
name AND creation date. A record copied from another project keeps that
project's creation date, so it never matches, and Finder falls back to the
plain background colour without reporting anything.
"""
import os
import struct
import sys

# Seconds between the Mac (1904) and Unix (1970) epochs.
MAC_EPOCH_OFFSET = 2082844800


def pascal(text, size, encoding="mac-roman"):
    raw = text.encode(encoding)
    if len(raw) > size:
        raise SystemExit(f"{text!r} does not fit in {size} bytes")
    return bytes([len(raw)]) + raw + b"\x00" * (size - len(raw))


def tagged(tag, data):
    out = struct.pack(">hH", tag, len(data)) + data
    return out + (b"\x00" if len(data) & 1 else b"")


def main(target, output):
    target = os.path.realpath(target)
    if not os.path.isfile(target):
        raise SystemExit(f"{target} does not exist")

    parts = target.split(os.sep)
    if len(parts) < 4 or parts[1] != "Volumes":
        raise SystemExit(f"{target} is not on a mounted volume under /Volumes")
    volume_name = parts[2]
    volume_path = os.sep.join(parts[:3])
    parent_name = parts[-2]
    file_name = parts[-1]

    volume_created = int(os.stat(volume_path).st_birthtime) + MAC_EPOCH_OFFSET
    file_created = int(os.stat(target).st_birthtime) + MAC_EPOCH_OFFSET

    header = bytearray(150)
    struct.pack_into(">IHHH", header, 0, 0, 0, 2, 0)      # user type, size, version, kind
    header[10:38] = pascal(volume_name, 27)
    struct.pack_into(">I", header, 38, volume_created)
    header[42:44] = b"BD"                                  # filesystem signature
    struct.pack_into(">H", header, 44, 1)                  # disk type: fixed
    struct.pack_into(">I", header, 46, 0xFFFFFFFF)         # parent directory id: unknown
    header[50:114] = pascal(file_name, 63)
    struct.pack_into(">I", header, 114, 0xFFFFFFFF)        # file number: unknown
    struct.pack_into(">I", header, 118, file_created)
    header[122:126] = b"TIFF"
    header[126:130] = b"8BIM"
    struct.pack_into(">hh", header, 130, -1, -1)           # nlvl from, to
    struct.pack_into(">I", header, 134, 3586)              # volume attributes
    struct.pack_into(">H", header, 138, 25461)             # filesystem id

    # The path tags are what Finder actually resolves once the volume matches.
    relative = os.sep + os.sep.join(parts[3:])
    body = b"".join([
        tagged(0, parent_name.encode("mac-roman")),
        tagged(2, ("/:" + ":".join(parts[1:])).encode("mac-roman")),
        tagged(14, struct.pack(">H", len(file_name)) + file_name.encode("utf-16-be")),
        tagged(15, struct.pack(">H", len(volume_name)) + volume_name.encode("utf-16-be")),
        tagged(18, relative.encode("mac-roman")),
        tagged(19, volume_path.encode("mac-roman")),
        struct.pack(">hH", -1, 0),
    ])

    alias = bytearray(bytes(header) + body)
    struct.pack_into(">H", alias, 4, len(alias))

    with open(output, "wb") as handle:
        handle.write(bytes(alias))

    print(f"alias: {len(alias)} bytes for {target}")
    print(f"  volume {volume_name!r} created {volume_created}")


if __name__ == "__main__":
    main(*sys.argv[1:3])
