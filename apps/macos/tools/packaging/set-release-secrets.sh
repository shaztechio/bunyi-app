#!/bin/bash
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
#
# Loads the signing secrets used by .github/workflows/release.yml from a
# 1Password item exported as JSON, and sets them with gh.
#
#   ./apps/macos/tools/packaging/set-release-secrets.sh <export.json> [--repo owner/name] [--dry-run]
#
# python3 is required. It is not in a bare macOS, but it comes with the Xcode
# Command Line Tools, which anyone who can build this project already has.
#
# The export holds a signing key and an app-specific password. Keep it off
# shared storage and delete it when you are done; this says so again at the end.

set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
reader="$here/read-1password-export.py"

usage() {
  printf 'usage: %s <export.json> [--repo owner/name] [--dry-run]\n' "$0" >&2
  exit 64
}

export_file=""
repo=""
dry_run="no"
while [ $# -gt 0 ]; do
  case "$1" in
    --repo)
      repo="${2:-}"
      [ -n "$repo" ] || usage
      shift 2
      ;;
    --dry-run) dry_run="yes"; shift ;;
    -h|--help) usage ;;
    -*)
      printf 'error: unknown option %s\n' "$1" >&2
      usage
      ;;
    *)
      [ -z "$export_file" ] || usage
      export_file="$1"
      shift
      ;;
  esac
done

[ -n "$export_file" ] || usage
[ -f "$export_file" ] || { printf 'error: %s does not exist\n' "$export_file" >&2; exit 1; }
[ -f "$reader" ] || { printf 'error: %s is missing\n' "$reader" >&2; exit 1; }

command -v python3 >/dev/null 2>&1 || {
  printf 'error: python3 is not available. Install the Xcode Command Line Tools.\n' >&2
  exit 1
}
command -v gh >/dev/null 2>&1 || { printf 'error: gh is not installed\n' >&2; exit 1; }
gh auth status >/dev/null 2>&1 || {
  printf 'error: gh is not authenticated. Run: gh auth login\n' >&2
  exit 1
}

if [ -z "$repo" ]; then
  repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
fi

# Every secret the workflow reads. A missing one is a failure, not a warning: a
# half-configured release job fails minutes later, inside the signing step, with
# a far less obvious message.
required="APPLE_CERTIFICATE_P12 APPLE_CERTIFICATE_PASSWORD APPLE_SIGN_IDENTITY APPLE_ID APPLE_TEAM_ID APPLE_APP_PASSWORD"

# Everything is validated before anything is sent, so a bad export cannot leave
# the repository with some secrets updated and others stale.
# shellcheck disable=SC2086
summary="$(python3 "$reader" "$export_file" --check $required)"

printf 'Repository: %s\n' "$repo"
printf 'Found in the export:\n'
printf '%s\n' "$summary" | awk -F'\t' '{ printf "  %-28s %s characters\n", $1, $2 }'

# A Developer ID .p12 in base64 runs to thousands of characters. Anything much
# shorter is usually a certificate exported without its private key, which signs
# nothing and only fails once the workflow is already running.
p12_length="$(printf '%s\n' "$summary" | awk -F'\t' '$1 == "APPLE_CERTIFICATE_P12" { print $2 }')"
if [ "${p12_length:-0}" -lt 1000 ]; then
  printf '\nwarning: APPLE_CERTIFICATE_P12 is only %s characters.\n' "${p12_length:-0}" >&2
  printf 'A Developer ID .p12 exported with its private key is normally much larger.\n' >&2
  printf 'Check that you exported the certificate and its key from Keychain Access.\n' >&2
fi

if [ "$dry_run" = "yes" ]; then
  printf '\nDry run: nothing was sent.\n'
  exit 0
fi

printf '\nSetting secrets...\n'
for name in $required; do
  # The value goes in over stdin, never as an argument: a command line is
  # visible to every process on the machine through ps.
  python3 "$reader" "$export_file" --value "$name" | gh secret set "$name" --repo "$repo"
  printf '  set %s\n' "$name"
done

printf '\nDone. Confirm with:\n'
printf '  gh secret list --repo %s\n' "$repo"
printf '\nDelete %s now. It holds your signing key and an app-specific password.\n' "$export_file"
