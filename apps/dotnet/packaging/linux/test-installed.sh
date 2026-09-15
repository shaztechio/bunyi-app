#!/usr/bin/env bash
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

# Only inside disposable containers. The package manager must resolve native
# dependencies on a clean OS, without a preinstalled SDK or desktop stack.
set -euo pipefail
packages="$(realpath "$1")"
format="$2"
previous="${3:-}"
if [ ! -f /.dockerenv ]; then
  echo 'Run this test inside a disposable Docker container.' >&2
  exit 1
fi
(cd "$packages" && sha256sum -c *."$format".sha256)
if [ "$format" = deb ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y --no-install-recommends python3 xvfb xauth dbus-x11 desktop-file-utils appstream
  if [ -n "$previous" ]; then apt-get install -y "$previous"/*.deb; fi
else
  dnf install -y python3 xorg-x11-server-Xvfb xorg-x11-xauth dbus-x11 desktop-file-utils appstream
  if [ -n "$previous" ]; then dnf install -y "$previous"/*.rpm; fi
fi
useradd -m bunyi-smoke
mkdir -p /home/bunyi-smoke/.local/share/Bunyi/Models /home/bunyi-smoke/.config/Bunyi
printf 'retain models' > /home/bunyi-smoke/.local/share/Bunyi/Models/sentinel
printf 'retain settings' > /home/bunyi-smoke/.config/Bunyi/sentinel
chown -R bunyi-smoke:bunyi-smoke /home/bunyi-smoke
if [ "$format" = deb ]; then
  apt-get install -y "$packages"/*.deb
  installed_version="$(dpkg-query -W -f='${Version}' bunyi)"
  expected_version="$(dpkg-deb -f "$packages"/*.deb Version)"
else
  dnf install -y "$packages"/*.rpm
  installed_version="$(rpm -q --qf '%{VERSION}' bunyi)"
  expected_version="$(rpm -qp --qf '%{VERSION}' "$packages"/*.rpm)"
fi
test "$installed_version" = "$expected_version"
export EXPECTED_VERSION="$expected_version"
desktop-file-validate /usr/share/applications/app.bunyi.Bunyi.desktop
appstreamcli validate --no-net /usr/share/metainfo/app.bunyi.Bunyi.metainfo.xml
test -x /usr/bin/bunyi-desktop
test ! -e /usr/bin/bunyi

python3 - <<'PY'
import ctypes
import json
import os
from pathlib import Path
root = Path('/usr/lib/bunyi')
assert 'Bunyi.App/' + os.environ['EXPECTED_VERSION'] in json.loads((root / 'Bunyi.App.deps.json').read_text())['libraries']
assert json.loads((root / 'bunyi.defaults.json').read_text()) == {
    'schemaVersion': 1, 'modelDownloadSource': 'mirror'}
# Include the inference/transcription libraries that startup alone won't load.
for name in ('libonnxruntime.so', 'libSkiaSharp.so', 'libHarfBuzzSharp.so', 'libminiaudio.so'):
    ctypes.CDLL(str(root / name))
whisper = list((root / 'runtimes/linux-x64').rglob('libwhisper.so'))
assert whisper, 'Whisper native library is missing'
# Whisper runtime contains alternative CPU instruction sets; the default must load.
native = (root / 'runtimes/linux-x64/libwhisper.so')
folder = native.parent if native.exists() else whisper[0].parent
# Match Whisper.net's dependency-first loading: its packaged filenames don't
# carry the .so.0 SONAME suffix used by the ELF references.
handles = [ctypes.CDLL(str(folder / name), mode=ctypes.RTLD_GLOBAL) for name in (
    'libggml-base-whisper.so', 'libggml-cpu-whisper.so', 'libggml-whisper.so', 'libwhisper.so')]
PY

# Run as an ordinary desktop user. A timeout means the app stayed open; a
# startup crash returns immediately. Check the app's own first-frame log too.
set +e
runuser -u bunyi-smoke -- env HOME=/home/bunyi-smoke timeout 15s \
  xvfb-run -a dbus-run-session -- /usr/bin/bunyi-desktop
code=$?
set -e
test "$code" -eq 124
grep -ri 'startup.*frame' /home/bunyi-smoke/.local/share/Bunyi/Logs

# Package-manager reinstall repairs owned files without changing user data.
printf 'damaged payload' > /usr/share/doc/bunyi/copyright
if [ "$format" = deb ]; then
  apt-get install -y --reinstall "$packages"/*.deb
else
  dnf reinstall -y "$packages"/*.rpm
fi
grep -q 'Apache License' /usr/share/doc/bunyi/copyright
if [ "$format" = deb ]; then
  apt-get purge -y bunyi
else
  dnf remove -y bunyi
fi
test ! -e /usr/lib/bunyi/Bunyi.App
test ! -e /usr/bin/bunyi-desktop
test ! -e /usr/share/applications/app.bunyi.Bunyi.desktop
grep -q 'retain models' /home/bunyi-smoke/.local/share/Bunyi/Models/sentinel
grep -q 'retain settings' /home/bunyi-smoke/.config/Bunyi/sentinel
echo 'Native dependencies, desktop startup, repair, uninstall and retained data passed.'
