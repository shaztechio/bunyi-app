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

"""Writes a background reference into a .DS_Store's `icvp` record.

    set-dmg-background.py <layout.DS_Store> <bookmark.bin>

capture-dmg-background.sh produces the bookmark against a live volume and
calls this. Doing it by hand is not useful: a reference captured anywhere
else names the wrong volume.

The .DS_Store is a Bud1 buddy-allocator file holding a B-tree. Records are
rewritten inside their existing block, so the file's size and every other
record are left exactly as they were.
"""
import plistlib
import struct
import sys

FIXED = {b"long": 4, b"shor": 4, b"type": 4, b"bool": 1, b"comp": 8, b"dutc": 8}


def read_record(data, pos):
    (name_len,) = struct.unpack_from(">I", data, pos)
    pos += 4
    name = data[pos:pos + name_len * 2].decode("utf-16-be")
    pos += name_len * 2
    struct_id = bytes(data[pos:pos + 4])
    struct_type = bytes(data[pos + 4:pos + 8])
    pos += 8
    if struct_type in FIXED:
        size = FIXED[struct_type]
    elif struct_type in (b"blob", b"ustr"):
        (n,) = struct.unpack_from(">I", data, pos)
        size = 4 + (n if struct_type == b"blob" else n * 2)
    else:
        raise ValueError(f"unknown struct type {struct_type!r}")
    return (name, struct_id, struct_type, bytes(data[pos:pos + size])), pos + size


def write_record(record):
    name, struct_id, struct_type, value = record
    encoded = name.encode("utf-16-be")
    return struct.pack(">I", len(encoded) // 2) + encoded + struct_id + struct_type + value


def find_dsdb(data, alloc, addresses):
    """Offset of the DSDB header, which holds the tree-wide record count."""
    (block_count,) = struct.unpack_from(">I", data, alloc)
    padded = ((block_count + 255) // 256) * 256
    pos = alloc + 8 + padded * 4
    (dir_count,) = struct.unpack_from(">I", data, pos)
    pos += 4
    for _ in range(dir_count):
        n = data[pos]
        pos += 1
        name = data[pos:pos + n].decode("ascii")
        pos += n
        (block_id,) = struct.unpack_from(">I", data, pos)
        pos += 4
        if name == "DSDB":
            return (addresses[block_id] & ~0x1F) + 4
    raise SystemExit("no DSDB directory entry")


def main(layout_path, bookmark_path):
    data = bytearray(open(layout_path, "rb").read())
    bookmark = open(bookmark_path, "rb").read()
    # Either format: a version-2 AliasRecord (what Finder stores, and what a
    # DMG known to display its background was found to use) or bookmark data.
    is_alias = bookmark[:4] == b"\x00\x00\x00\x00" and struct.unpack_from(">H", bookmark, 6)[0] == 2
    if not (is_alias or bookmark[:4] == b"book"):
        raise SystemExit("that file is neither an AliasRecord nor bookmark data")

    alloc = struct.unpack_from(">I", data, 8)[0] + 4
    count = struct.unpack_from(">I", data, alloc)[0]
    addresses = struct.unpack_from(">%dI" % count, data, alloc + 8)

    # Walk every block; the tree is one leaf in a layout this small, but
    # finding icvp by search beats assuming where it lives.
    for block_id, addr in enumerate(addresses):
        off, size = (addr & ~0x1F) + 4, 1 << (addr & 0x1F)
        if off + 8 > len(data):
            continue
        try:
            _, n = struct.unpack_from(">II", data, off)
            if not 0 < n < 64:
                continue
            records, pos = [], off + 8
            for _ in range(n):
                record, pos = read_record(data, pos)
                records.append(record)
        except Exception:
            continue
        if not any(r[1] == b"icvp" for r in records):
            continue

        end = pos
        for i, (name, sid, stype, val) in enumerate(records):
            if sid != b"icvp":
                continue
            plist = plistlib.loads(val[4:])
            before = len(plist.get("backgroundImageAlias", b""))
            plist["backgroundImageAlias"] = bookmark
            plist["backgroundType"] = 2  # 2 = picture
            blob = plistlib.dumps(plist, fmt=plistlib.FMT_BINARY)
            records[i] = (name, sid, stype, struct.pack(">I", len(blob)) + blob)
            print(f"backgroundImageAlias: {before} -> {len(bookmark)} bytes")

        rebuilt = b"".join(write_record(r) for r in records)
        if 8 + len(rebuilt) > size:
            # Bookmark data is several times the size of the old alias, and the
            # block is a fixed power-of-two allocation. pBB0/pBBk hold Finder's
            # window-browser state — sidebar and back/forward history — which a
            # disk image window has no use for; the icon positions (Iloc), view
            # options (icvp), and window frame (bwsp) are what matter. Growing
            # the allocator instead would mean rewriting the whole file.
            dropped = [r[1].decode() for r in records if r[1] in (b"pBB0", b"pBBk")]
            records = [r for r in records if r[1] not in (b"pBB0", b"pBBk")]
            rebuilt = b"".join(write_record(r) for r in records)
            print(f"dropped {', '.join(dropped)} to fit the bookmark")
        if 8 + len(rebuilt) > size:
            raise SystemExit(
                f"records need {8 + len(rebuilt)} bytes but the block holds {size}"
            )
        original_len = end - (off + 8)
        data[off + 8:off + 8 + len(rebuilt)] = rebuilt
        if len(rebuilt) < original_len:
            tail = off + 8 + len(rebuilt)
            data[tail:tail + original_len - len(rebuilt)] = b"\x00" * (
                original_len - len(rebuilt)
            )

        # The node counts its own records, and the DSDB header counts them for
        # the whole tree. Dropping a record without updating both leaves a file
        # that reads fine until something walks past the last real record into
        # the zeroed slack and fails — taking the entire layout with it, not
        # just the background.
        if len(records) != n:
            struct.pack_into(">I", data, off + 4, len(records))
            dsdb_off = find_dsdb(data, alloc, addresses)
            (total,) = struct.unpack_from(">I", data, dsdb_off + 8)
            struct.pack_into(">I", data, dsdb_off + 8, total - (n - len(records)))
            print(f"record count {n} -> {len(records)}, tree total {total} -> "
                  f"{total - (n - len(records))}")

        open(layout_path, "wb").write(bytes(data))
        print(f"wrote {layout_path} ({len(data)} bytes, size unchanged)")
        return

    raise SystemExit("no icvp record found in any block")


if __name__ == "__main__":
    main(*sys.argv[1:3])
