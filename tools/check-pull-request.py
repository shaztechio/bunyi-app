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

"""Checks that a pull request title is a Conventional Commit.

    check-pull-request.py "<title>"      check one title
    check-pull-request.py                read it from $GITHUB_EVENT_PATH
    check-pull-request.py --self-test    run the cases below

The rule is /AGENTS.md, "How changes land": `<type>[(scope)][!]: <summary>`,
lowercase after the colon, no trailing full stop, imperative mood.

It matters because PRs squash. The title *becomes* the commit subject on main,
and then a line that `tools/release-notes.py` groups by type — so a title that
skips the convention is not untidy for a day, it is wrong in the release notes
forever, and the release is the last place anyone thinks to look.

Deliberately stricter than `release-notes.py`'s parser. That one takes any
lowercase word as a type and files unknown ones under "Other changes", which is
right for reading history it cannot change; a gate on new titles should accept
only the types the project uses. Anything this accepts, that parser accepts.
"""

import json
import os
import re
import sys

# /AGENTS.md lists these. `release-notes.py` gives headings to only four and
# files the rest under "Other changes" — a choice about presentation, not a
# reason to reject a truthful `ci:` or `chore:` title.
TYPES = [
    "feat", "fix", "docs", "refactor", "perf",
    "test", "build", "ci", "chore", "revert",
]

TITLE = re.compile(
    r"^(?P<type>" + "|".join(TYPES) + r")"
    r"(?:\((?P<scope>[^)\s][^)]*)\))?"
    r"(?P<breaking>!)?"
    r": (?P<description>.+)$"
)

# Verbs that end the way a non-imperative would. Without these, "embed the
# icon" and "process the manifest" are rejected for looking like "added" and
# "adds" — and a checker that blocks correct titles gets switched off, which
# leaves it enforcing nothing at all.
IMPERATIVE_EXCEPTIONS = {
    "embed", "feed", "need", "speed", "seed", "breed", "shed", "spread",
    "read", "exceed", "proceed", "succeed", "heed", "bleed",
    "process", "focus", "bypass", "dismiss", "express", "pass", "discuss",
    "address", "compress", "cross", "press", "toss", "guess",
}


def problems(title):
    """Every reason the title is not acceptable, in reading order."""
    if not title or not title.strip():
        return ["the title is empty"]

    found = []

    if title != title.strip():
        found.append("the title has leading or trailing whitespace")

    title = title.strip()
    match = TITLE.match(title)

    if not match:
        # Say which half is wrong. "Does not match the convention" sends
        # someone off to re-read a regex; naming the missing piece does not.
        if ":" not in title:
            found.append(
                "there is no `type: ` prefix — expected one of "
                + ", ".join(TYPES)
            )
            return found

        prefix = title.split(":", 1)[0]
        bare = prefix.split("(", 1)[0].rstrip("!")

        if bare.lower() in TYPES and bare not in TYPES:
            found.append("the type `" + bare + "` must be lowercase")
        elif bare.lower() not in TYPES:
            found.append(
                "`" + bare + "` is not a type — expected one of "
                + ", ".join(TYPES)
            )
        else:
            found.append(
                "the type and the summary must be separated by a colon and a "
                "single space, and the summary cannot be empty"
            )
        return found

    description = match.group("description")

    if description != description.strip():
        found.append("the summary has extra spaces around it")

    description = description.strip()

    if not description:
        found.append("the summary is empty")
        return found

    if description[:1].isupper():
        found.append(
            "the summary starts with a capital: `" + description.split()[0] + "`"
        )

    if description.endswith("."):
        found.append("the summary ends with a full stop")

    return found


def mood_notes(title):
    """Non-blocking notes about the imperative rule.

    A warning rather than an error, on the evidence. English cannot be told
    apart from a regex here: this repository already contains
    `fix(site): downloads for every platform`, where "downloads" is a noun,
    and an earlier version of this script rejected it. Blocking a correct
    title is worse than missing an incorrect one — the fix people reach
    for when a gate is wrong is to delete the gate, and then the rules that
    *can* be checked mechanically stop being checked too.

    So the structural rules above are the gate, and this is a note beside it
    for a human to judge.
    """
    if not title or not title.strip():
        return []

    match = TITLE.match(title.strip())
    if not match:
        return []

    description = match.group("description").strip()
    if not description:
        return []

    first = re.split(r"[^A-Za-z'-]", description, maxsplit=1)[0].lower()

    if not first or first in IMPERATIVE_EXCEPTIONS:
        return []

    if first.endswith("ed"):
        return ["`" + first + "` may be past tense — the rule asks for "
                "imperative mood: add, not added"]

    if first.endswith("s") and not first.endswith("ss"):
        return ["`" + first + "` may be third person — the rule asks for "
                "imperative mood: add, not adds. Ignore this if it is a noun."]

    return []


def title_from_event():
    """The pull request title from the workflow event payload."""
    path = os.environ.get("GITHUB_EVENT_PATH")
    if not path or not os.path.exists(path):
        return None

    with open(path, encoding="utf-8") as handle:
        event = json.load(handle)

    return (event.get("pull_request") or {}).get("title")


# The checker has no test project to live in, so it carries its own cases and
# CI runs them. A gate that is wrong is worse than no gate: it blocks correct
# work, and the fix people reach for is to remove the gate.
CASES = [
    ("feat(macos): add a help button to the main window", True),
    ("fix: stop the release check failing on a good build", True),
    ("docs(dotnet): the app has three modes; stop shipping help", True),
    ("feat(dotnet)!: change where models are stored", True),
    ("perf(dotnet): publish ReadyToRun, for 30% off time to first frame", True),
    ("ci: enforce the conventional-commit title rule", True),
    ("refactor(core): embed the icon rather than copying it", True),
    ("fix: process the manifest before the download starts", True),
    ("fix(dotnet): release the previous model before downloading the next", True),
    ("Add a help button", False),
    ("feature(macos): add a button", False),
    ("Feat(macos): add a button", False),
    ("feat(macos):add a button", False),
    ("feat(macos): Add a button", False),
    ("feat(macos): add a button.", False),
    ("feat(macos): added a button", True),   # noted, not blocked
    ("feat(macos): adds a button", True),    # noted, not blocked
    ("fix(site): downloads for every platform", True),  # a noun, and real
    ("feat(macos): ", False),
    ("", False),
]


def self_test():
    failures = 0

    for title, acceptable in CASES:
        found = problems(title)
        if bool(found) != acceptable:
            continue

        failures += 1
        if acceptable:
            print("FAIL rejected a good title: " + repr(title) + " -> " + str(found))
        else:
            print("FAIL accepted a bad title: " + repr(title))

    # The mood check has cases of its own, since it no longer shows up in
    # whether a title is accepted.
    noted = [
        ("feat(macos): added a button", True),
        ("feat(macos): adds a button", True),
        ("feat(macos): add a button", False),
        ("refactor(core): embed the icon", False),
        ("fix(site): downloads for every platform", True),
    ]

    for title, expected in noted:
        if bool(mood_notes(title)) == expected:
            continue
        failures += 1
        print("FAIL mood note wrong for " + repr(title))

    total = len(CASES) + len(noted)
    print(str(total - failures) + "/" + str(total) + " cases pass")
    return 1 if failures else 0


def main(argv):
    if "--self-test" in argv:
        return self_test()

    title = argv[1] if len(argv) > 1 else title_from_event()

    if title is None:
        print("no title given, and no pull request in the event payload",
              file=sys.stderr)
        return 2

    found = problems(title)

    # The annotation form, so the reason appears on the pull request itself
    # rather than only in a log nobody opens when a check goes red.
    for note in mood_notes(title):
        print("::warning title=Pull request title::" + note)

    if not found:
        print("Title is a Conventional Commit: " + title)
        return 0

    for reason in found:
        print("::error title=Pull request title::" + reason)

    print()
    print("  title: " + title)
    print("  rule:  <type>[(scope)][!]: <summary>")
    print("         lowercase after the colon, no trailing full stop,")
    print("         imperative mood. See /AGENTS.md, \"How changes land\".")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
