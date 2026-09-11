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

import assert from "node:assert/strict";
import { createHash, createHmac } from "node:crypto";
import test from "node:test";
import worker, { LINK_LIFETIME_SECONDS } from "../src/index.js";
import published from "../src/published-files.json" with { type: "json" };

const key = "onnx/customvoice/vocoder.onnx.data";
const origin = "https://bunyi-downloads.bunyi.workers.dev/";
function environment(overrides = {}) {
  return {
    R2_ACCOUNT_ID: "a22215ba113580f2c0f3fe4cb775335d", R2_BUCKET: "bunyi-models",
    R2_ACCESS_KEY_ID: "test-access-key", R2_SECRET_ACCESS_KEY: "test-secret",
    DOWNLOADS_ENABLED: "true",
    KILLSWITCH_STATE: { get: async () => ({ killed: false }) },
    ...overrides,
  };
}
function request(path = key, init = {}) { return new Request(origin + path, init); }
function verifySignature(location, method, secret) {
  const url = new URL(location);
  const signature = url.searchParams.get("X-Amz-Signature");
  url.searchParams.delete("X-Amz-Signature");
  const encode = value => encodeURIComponent(value).replace(/[!'()*]/g,
    char => "%" + char.charCodeAt(0).toString(16).toUpperCase());
  const query = [...url.searchParams].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
    .map(([k, v]) => encode(k) + "=" + encode(v)).join("&");
  const canonical = [method, url.pathname, query, "host:" + url.host + "\n", "host", "UNSIGNED-PAYLOAD"].join("\n");
  const credential = url.searchParams.get("X-Amz-Credential").split("/");
  const scope = credential.slice(1).join("/");
  const message = ["AWS4-HMAC-SHA256", url.searchParams.get("X-Amz-Date"), scope,
    createHash("sha256").update(canonical).digest("hex")].join("\n");
  let signingKey = "AWS4" + secret;
  for (const component of credential.slice(1))
    signingKey = createHmac("sha256", signingKey).update(component).digest();
  return createHmac("sha256", signingKey).update(message).digest("hex") === signature;
}

for (const method of ["GET", "HEAD"]) {
  test(method + " redirects to a valid method-specific S3 signature", async () => {
    const response = await worker.fetch(request(key, { method }), environment());
    assert.equal(response.status, 307);
    assert.equal(await response.text(), "");
    const location = response.headers.get("location");
    const target = new URL(location);
    assert.equal(target.origin, "https://a22215ba113580f2c0f3fe4cb775335d.r2.cloudflarestorage.com");
    assert.equal(target.pathname, "/bunyi-models/" + key);
    assert.equal(target.searchParams.get("X-Amz-Expires"), String(LINK_LIFETIME_SECONDS));
    assert.equal(target.searchParams.get("X-Amz-SignedHeaders"), "host");
    assert.ok(verifySignature(location, method, "test-secret"));
    assert.ok(!verifySignature(location, method === "GET" ? "HEAD" : "GET", "test-secret"));
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("cloudflare-cdn-cache-control"), "no-store");
  });
}

test("caller headers and query cannot alter scope, expiry or signed headers", async () => {
  const response = await worker.fetch(request(key + "?X-Amz-Expires=604800&bucket=private&response-content-type=text/html", {
    headers: { Range: "bytes=100-", "If-Range": '"etag"', Authorization: "Bearer unrelated", Cookie: "private=yes" },
  }), environment());
  const url = new URL(response.headers.get("location"));
  assert.equal(url.searchParams.get("X-Amz-Expires"), "300");
  assert.equal(url.searchParams.get("X-Amz-SignedHeaders"), "host");
  assert.equal(url.searchParams.get("bucket"), null);
  assert.equal(url.searchParams.get("response-content-type"), null);
  assert.ok(!url.href.includes("unrelated"));
  assert.ok(!url.href.includes("private"));
  assert.ok(verifySignature(url.href, "GET", "test-secret"));
});

for (const path of ["", "private.txt", "onnx/customvoice/", "onnx/customvoice/not-published.onnx",
  "onnx%2fcustomvoice/vocoder.onnx.data", "onnx/customvoice/%76ocoder.onnx.data",
  "onnx/customvoice/../../private.txt", "onnx/customvoice/manifest.sha256/extra"]) {
  test("unpublished path is rejected: " + path, async () => {
    const response = await worker.fetch(request(path), environment());
    assert.equal(response.status, 404);
    assert.equal(response.headers.get("location"), null);
  });
}
for (const method of ["POST", "PUT", "DELETE", "OPTIONS"]) {
  test("reject " + method, async () => {
    const response = await worker.fetch(request(key, { method }), environment());
    assert.equal(response.status, 405);
    assert.equal(response.headers.get("allow"), "GET, HEAD");
  });
}
test("only the production and test host over HTTPS may issue links", async () => {
  for (const origin of ["http://models.bunyi.app/", "https://evil.example/"]) {
    assert.equal((await worker.fetch(new Request(origin + key), environment())).status, 404);
  }
  assert.equal((await worker.fetch(new Request("https://models.bunyi.app/" + key), environment())).status, 307);
});
for (const state of [null, {}, [], { killed: true }, { killed: "false" }]) {
  test("fail closed for missing, killed or malformed state: " + JSON.stringify(state), async () => {
    const response = await worker.fetch(request(), environment({ KILLSWITCH_STATE: { get: async () => state } }));
    assert.equal(response.status, 503);
    assert.equal(response.headers.get("x-bunyi-download-status"), "paused");
    assert.equal(response.headers.get("retry-after"), "60");
    assert.equal(response.headers.get("location"), null);
  });
}
test("kill switch is read for every request and is never written", async () => {
  let killed = false, reads = 0;
  const env = environment({ KILLSWITCH_STATE: {
    get: async (name, options) => {
      reads++;
      assert.equal(name, "killswitch:state");
      assert.deepEqual(options, { type: "json", cacheTtl: 30 });
      return { killed };
    },
  } });
  assert.equal((await worker.fetch(request(), env)).status, 307);
  killed = true;
  assert.equal((await worker.fetch(request(), env)).status, 503);
  assert.equal(reads, 2);
});
test("local emergency pause does not depend on KV", async () => {
  for (const enabled of [undefined, "", "false"]) {
    const response = await worker.fetch(request(), environment({
      DOWNLOADS_ENABLED: enabled, KILLSWITCH_STATE: { get: () => assert.fail("must not read") },
    }));
    assert.equal(response.status, 503);
    assert.equal(response.headers.get("x-bunyi-download-status"), "paused");
  }
});
test("credentials, configuration and KV failures give generic errors", async () => {
  for (const overrides of [{ R2_ACCESS_KEY_ID: "" }, { R2_SECRET_ACCESS_KEY: "" },
    { R2_BUCKET: "another-bucket" }, { R2_ACCOUNT_ID: "invalid" }, { KILLSWITCH_STATE: undefined },
    { KILLSWITCH_STATE: { get: async () => { throw new Error("private exception"); } } }]) {
    const response = await worker.fetch(request(), environment(overrides));
    assert.equal(response.status, 503);
    assert.equal(response.headers.get("location"), null);
    assert.ok(!(await response.text()).includes("private exception"));
    assert.equal(response.headers.get("x-bunyi-download-status"), null);
  }
});
test("all published paths are canonical and cover the six model roots", async () => {
  const roots = new Set();
  for (const [key, hash] of Object.entries(published)) {
    assert.match(key, /^[a-zA-Z0-9_./-]+$/);
    assert.ok(key.split("/").every(part => part && part !== "." && part !== ".."));
    if (hash !== null) assert.match(hash, /^[a-f0-9]{64}$/);
    roots.add(key.startsWith("onnx/") ? key.split("/").slice(0, 2).join("/") : key.split("/")[0]);
    assert.equal((await worker.fetch(request(key), environment())).status, 307);
  }
  assert.equal(roots.size, 6);
});
