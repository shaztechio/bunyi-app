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

"""Builds release notes from the Conventional Commit history since the last tag.

    release-notes.py <current-tag> [previous-tag] [--tags PATTERN] [--path PATH ...]

Without a previous tag it finds the closest preceding tag matching --tags,
which defaults to `v*`, and falls back to the whole history when there is none.

--path narrows the history to one part of the tree. The two apps in this
repository release separately, so a release of one that listed the other's
commits would be describing work the download does not contain.

Subjects that are not conventional are kept under "Other changes" rather than
dropped. Everything before the convention was adopted looks like that, and a
release note that quietly omits half of what shipped is worse than an untidy one.
"""

import re
import subprocess
import sys

SUBJECT = re.compile(
    r"^(?P<type>[a-z]+)"
    r"(?:\((?P<scope>[^)]+)\))?"
    r"(?P<breaking>!)?"
    r": (?P<description>.+)$"
)

# Only the types worth their own heading. Everything else lands in "Other
# changes", which is where the noise belongs.
SECTIONS = [
    ("feat", "Features"),
    ("fix", "Fixes"),
    ("perf", "Performance"),
    ("docs", "Documentation"),
]
# The version bump is the release, not a change in it.
SKIP = re.compile(r"^Release v?\d+\.\d+\.\d+$")


def git(*args):
    result = subprocess.run(
        ["git", *args], capture_output=True, text=True, check=False
    )
    if result.returncode != 0:
        sys.stderr.write(f"error: git {' '.join(args)}: {result.stderr.strip()}\n")
        raise SystemExit(1)
    return result.stdout


def previous_tag(current, pattern="v*"):
    """The tag before `current`, or None when this is the first release."""
    tags = [t for t in git("tag", "--list", pattern, "--sort=-v:refname").split() if t]
    if current in tags:
        after = tags[tags.index(current) + 1 :]
        return after[0] if after else None
    return tags[0] if tags else None


def commits(current, previous, paths=()):
    span = f"{previous}..{current}" if previous else current
    # A null byte separates records: commit bodies contain blank lines, so any
    # newline-based delimiter eventually splits one in half.
    args = ["log", "--no-merges", "--format=%s%x1f%b%x00", span]
    if paths:
        args += ["--", *paths]
    raw = git(*args)
    for record in raw.split("\0"):
        record = record.strip("\n")
        if not record:
            continue
        subject, _, body = record.partition("\x1f")
        yield subject.strip(), body


# A trailer ends the footer as surely as a blank line does: "Co-Authored-By:"
# and friends are not part of the sentence being written.
TRAILER = re.compile(r"^[A-Z][A-Za-z]*(-[A-Za-z]+)*: ")


def breaking_note(body):
    """The whole BREAKING CHANGE footer, not just its first line.

    Commit footers wrap. Reading one line produced release notes that stopped
    mid-sentence — "every baseline must be rebuilt. The retry lives in the" —
    which shipped in 1.1.0. The instruction survived there by luck of where the
    wrap fell; a footer that wrapped two words earlier would have said nothing
    actionable at all.

    Collects until a blank line or the next trailer, and joins the wrapped lines
    back into one paragraph.
    """
    lines, collecting = [], False
    for line in body.splitlines():
        if line.startswith("BREAKING CHANGE:"):
            lines.append(line[len("BREAKING CHANGE:") :].strip())
            collecting = True
            continue
        if collecting:
            stripped = line.strip()
            if not stripped or TRAILER.match(line):
                break
            lines.append(stripped)
    return " ".join(part for part in lines if part).strip()


def classify(current, previous, paths=()):
    breaking, buckets, other = [], {t: [] for t, _ in SECTIONS}, []
    for subject, body in commits(current, previous, paths):
        if SKIP.match(subject):
            continue
        match = SUBJECT.match(subject)
        is_breaking = bool(match and match.group("breaking")) or "BREAKING CHANGE:" in body
        if is_breaking:
            breaking.append((subject, breaking_note(body)))
            continue
        if match and match.group("type") in buckets:
            buckets[match.group("type")].append(subject)
        else:
            other.append(subject)
    return breaking, buckets, other


def render(current, previous, paths=()):
    breaking, buckets, other = classify(current, previous, paths)
    out = []

    # First, and never in a list. A guest-side change means every existing user
    # must Rebuild, and a new binary alone is not enough. That is the one thing
    # in a release note that is expensive to miss.
    if breaking:
        out.append("## Breaking: you must Rebuild\n")
        out.append(
            "These change what is inside the guest. Installing this build is not "
            "enough — stop your VMs and choose **Rebuild** for each affected "
            "environment.\n"
        )
        for subject, note in breaking:
            out.append(f"- {subject}")
            if note:
                out.append(f"  {note}")
        out.append("")

    for key, heading in SECTIONS:
        if buckets[key]:
            out.append(f"## {heading}\n")
            out.extend(f"- {s}" for s in buckets[key])
            out.append("")

    if other:
        out.append("## Other changes\n")
        out.extend(f"- {s}" for s in other)
        out.append("")

    if not out:
        out.append("No changes recorded since the previous release.\n")

    if previous:
        out.append(f"**Full changes:** `{previous}...{current}`")
    return "\n".join(out).rstrip() + "\n"


def main(argv):
    args = argv[1:]
    pattern = "v*"
    paths = []

    positional = []
    i = 0
    while i < len(args):
        if args[i] == "--tags" and i + 1 < len(args):
            pattern = args[i + 1]
            i += 2
        elif args[i] == "--path" and i + 1 < len(args):
            paths.append(args[i + 1])
            i += 2
        else:
            positional.append(args[i])
            i += 1

    if not positional:
        sys.stderr.write(__doc__)
        return 64

    current = positional[0]
    previous = positional[1] if len(positional) > 1 else previous_tag(current, pattern)
    sys.stdout.write(render(current, previous, paths))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
