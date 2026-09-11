// Copyright 2026 Shazron Abdullah and Bunyi contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

import { writeFile } from "node:fs/promises";

const roots = ["customvoice", "voicedesign", "voiceclone",
  "onnx/customvoice", "onnx/voicedesign", "onnx/voiceclone"];
const published = {};
for (const root of roots) {
  const response = await fetch("https://models.bunyi.app/" + root + "/manifest.sha256",
    { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(root + ": manifest HTTP " + response.status);
  const text = await response.text();
  let count = 0;
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim() || line.startsWith("#")) continue;
    const match = /^([a-fA-F0-9]{64})\s+\*?(.+)$/.exec(line);
    if (!match) throw new Error(root + ": malformed checksum entry");
    const [, hash, path] = match;
    if (!/^[a-zA-Z0-9_.\/-]+$/.test(path) ||
        path.split("/").some(part => !part || part === "." || part === ".."))
      throw new Error(root + ": unsafe object path");
    const key = root + "/" + path;
    if (key in published) throw new Error("Duplicate key: " + key);
    published[key] = hash.toLowerCase();
    count++;
  }
  if (!count) throw new Error(root + ": empty manifest");
  published[root + "/manifest.sha256"] = null;
  published[root + "/manifest.txt"] = null;
  console.log(root + ": " + count + " published files");
}
const sorted = Object.fromEntries(Object.entries(published).sort(([a], [b]) => a.localeCompare(b, "en")));
await writeFile(new URL("../src/published-files.json", import.meta.url), JSON.stringify(sorted, null, 2) + "\n");

