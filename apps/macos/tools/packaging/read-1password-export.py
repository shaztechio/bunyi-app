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

"""Reads named fields out of a 1Password item exported as JSON.

    read-1password-export.py FILE --check NAME...   validate; print name<TAB>length
    read-1password-export.py FILE --value NAME      write one value to stdout

A separate file rather than a heredoc inside the shell script. Quoting a Python
program through several layers of shell is how that script ended up with a
syntax error nobody could see.
"""

import json
import re
import sys


def load(path):
    try:
        raw = open(path, encoding="utf-8").read()
    except OSError as error:
        sys.stderr.write(f"error: could not read {path}: {error}\n")
        raise SystemExit(1)
    try:
        return json.loads(raw)
    except json.JSONDecodeError as error:
        # A hand-edited export can pick up a trailing comma before a closing
        # bracket. Recoverable, but said out loud: quietly rewriting a file full
        # of secrets is not something to do behind the user's back.
        sys.stderr.write(f"warning: {path} is not valid JSON ({error}).\n")
        sys.stderr.write("warning: retrying with trailing commas removed.\n")
        try:
            return json.loads(re.sub(r",(\s*[}\]])", r"\1", raw))
        except json.JSONDecodeError as retry_error:
            sys.stderr.write(f"error: {path} could not be parsed: {retry_error}\n")
            raise SystemExit(1)


def collect_fields(item):
    """Flattens 1Password's sections/fields into {field title: value}."""
    found = {}
    for section in item.get("details", {}).get("sections", []) or []:
        for field in section.get("fields", []) or []:
            name = field.get("t")
            if name:
                found[name] = field.get("v")
    return found


def check(fields, names):
    problems = []
    for name in names:
        if name not in fields:
            problems.append(f"{name}: not present in the export")
            continue
        value = str(fields[name] or "").strip()
        if not value:
            problems.append(f"{name}: empty")
        elif value == "REDACTED":
            problems.append(f"{name}: still says REDACTED, so this is the sample file")
    if problems:
        sys.stderr.write("error: the export cannot be used:\n")
        for problem in problems:
            sys.stderr.write(f"  {problem}\n")
        return 1

    # Only names and lengths are ever printed. A length catches the usual
    # mistake of exporting a certificate without its private key, and reveals
    # nothing about the value itself.
    for name in names:
        sys.stdout.write(f"{name}\t{len(str(fields[name]))}\n")
    return 0


def main(argv):
    if len(argv) < 4:
        sys.stderr.write(__doc__)
        return 64
    path, mode, names = argv[1], argv[2], argv[3:]
    fields = collect_fields(load(path))

    if mode == "--check":
        return check(fields, names)
    if mode == "--value":
        if len(names) != 1:
            sys.stderr.write("error: --value takes exactly one field name\n")
            return 64
        if names[0] not in fields:
            sys.stderr.write(f"error: no field named {names[0]}\n")
            return 1
        # No trailing newline: this is piped straight into `gh secret set`, and a
        # stray newline would become part of the stored secret.
        sys.stdout.write(str(fields[names[0]]))
        return 0

    sys.stderr.write(f"error: unknown mode {mode}\n")
    return 64


if __name__ == "__main__":
    sys.exit(main(sys.argv))
