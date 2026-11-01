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

"""Measure energy boundaries, not speech boundaries, from Bunyi CLI JSON logs.

Usage: python tools/srt-silence-probe.py EXPERIMENT_FOLDER
Reads *-hello.log / *-sentence.log and their generated PCM16 mono WAV files.
Does not trim or modify audio. JSON on stdout is suitable for a research report.
"""

import array
import hashlib
import json
import math
from pathlib import Path
import sys
import wave


def measure(log):
    events = [json.loads(line) for line in log.read_text(encoding="utf-8-sig").splitlines()
              if line.startswith("{")]
    result = next(event for event in reversed(events) if event.get("type") in ("result", "error"))
    if not result.get("ok"):
        raise ValueError(f"Generation failed: {log}")
    path = Path(result["outputPath"])
    with wave.open(str(path), "rb") as wav:
        if (wav.getsampwidth(), wav.getnchannels(), wav.getframerate()) != (2, 1, 24000):
            raise ValueError(f"Expected PCM16 mono 24 kHz: {path}")
        samples = array.array("h", wav.readframes(wav.getnframes()))
    if sys.byteorder != "little":
        samples.byteswap()
    window = 240  # Non-overlapping 10 ms windows; includes a partial final window.
    rms = [math.sqrt(sum((v / 32768) ** 2 for v in samples[i:i + window]) /
                     len(samples[i:i + window])) for i in range(0, len(samples), window)]
    boundaries = {}
    for db in (-50, -40):
        active = [i for i, energy in enumerate(rms) if energy >= 10 ** (db / 20)]
        boundaries[str(db)] = None if not active else {
            "leadingMs": round(active[0] * window / 24, 3),
            "trailingMs": round((len(samples) - min(len(samples), (active[-1] + 1) * window)) / 24, 3),
        }
    return {
        "trial": log.stem,
        "text": result["metadata"]["text"],
        "durationSeconds": len(samples) / 24000,
        "elapsedSeconds": result["elapsedSeconds"],
        "modelSource": result["modelSource"],
        "appVersion": result["metadata"]["appVersion"],
        "wav": path.name,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "energyBoundariesDbfs": boundaries,
    }


if __name__ == "__main__":
    folder = Path(sys.argv[1])
    logs = sorted([*folder.glob("*-hello.log"), *folder.glob("*-sentence.log")])
    if len(logs) != 6:
        raise ValueError(f"Expected six trial logs, found {len(logs)}")
    print(json.dumps([measure(log) for log in logs], indent=2))
