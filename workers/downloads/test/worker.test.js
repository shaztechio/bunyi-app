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
import { readFileSync } from "node:fs";
import { AwsClient } from "aws4fetch";
import worker from "../src/index.js";
import { LINK_LIFETIME_SECONDS } from "../src/permission-contract.js";
import published from "../src/published-files.json" with { type: "json" };

const key = "onnx/customvoice/vocoder.onnx.data";
const origin = "https://bunyi-downloads.bunyi.workers.dev/";
function binding(access = 'allowed', transform = x => x) {
  return { fetch: async (_url, init) => {
    const { nonce, bucket } = JSON.parse(init.body);
    return Response.json(transform({ version: 1, access, nonce, bucket, revision: 1, authorizedAt: Date.now(), maxAgeMs: 2000 }));
  } };
}
function environment(overrides = {}) {
  return {
    R2_ACCOUNT_ID: "a22215ba113580f2c0f3fe4cb775335d", R2_BUCKET: "bunyi-models",
    R2_ACCESS_KEY_ID: "test-access-key", R2_SECRET_ACCESS_KEY: "test-secret",
    DOWNLOADS_ENABLED: "true",
    DOWNLOAD_PERMISSION: binding(),
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
for (const state of [null, {}, [], { access: 'allowed' }, { access: 'blocked' }]) {
  test('malformed permission returns unavailable: ' + JSON.stringify(state), async () => {
    const response = await worker.fetch(request(), environment({ DOWNLOAD_PERMISSION: binding('allowed', () => state) }));
    assert.equal(response.status, 503);
    assert.equal(response.headers.get('x-bunyi-download-status'), null);
    assert.equal(response.headers.get('location'), null);
  });
}
for (const host of ['models.bunyi.app', 'bunyi-downloads.bunyi.workers.dev']) {
  for (const method of ['GET', 'HEAD']) {
    test(host + ' ' + method + ' checks fresh permission and returns paused on block', async () => {
      let blocked = false, reads = 0;
      const env = environment({ DOWNLOAD_PERMISSION: { fetch: async (...args) => {
        reads++; return binding(blocked ? 'blocked' : 'allowed').fetch(...args);
      } }, KILLSWITCH_STATE: { get: () => assert.fail('no KV fallback') } });
      const req = () => new Request('https://' + host + '/' + key, { method });
      assert.equal((await worker.fetch(req(), env)).status, 307);
      blocked = true;
      const denied = await worker.fetch(req(), env);
      assert.equal(denied.status, 503);
      assert.equal(denied.headers.get('x-bunyi-download-status'), 'paused');
      assert.equal(denied.headers.get('retry-after'), '60');
      assert.equal(denied.headers.get('location'), null);
      assert.equal(reads, 2);
    });
  }
}
test('emergency pause precedes permission reads', async () => {
  for (const enabled of [undefined, '', 'false']) {
    const res = await worker.fetch(request(), environment({ DOWNLOADS_ENABLED: enabled,
      DOWNLOAD_PERMISSION: { fetch: () => assert.fail('must not read') } }));
    assert.equal(res.status, 503);
    assert.equal(res.headers.get('x-bunyi-download-status'), 'paused');
  }
});
test('configuration and service failures are generic and never fall back to KV', async () => {
  for (const overrides of [{ R2_ACCESS_KEY_ID: '' }, { R2_SECRET_ACCESS_KEY: '' },
    { R2_BUCKET: 'wrong' }, { R2_ACCOUNT_ID: 'wrong' }, { DOWNLOAD_PERMISSION: undefined },
    { DOWNLOAD_PERMISSION: { fetch: async () => { throw new Error('private exception'); } } },
    { DOWNLOAD_PERMISSION: { fetch: async () => new Response('unavailable', { status: 503 }) } }]) {
    const res = await worker.fetch(request(), environment({ ...overrides,
      KILLSWITCH_STATE: { get: () => assert.fail('no fallback') } }));
    assert.equal(res.status, 503);
    assert.equal(res.headers.get('location'), null);
    assert.equal(res.headers.get('x-bunyi-download-status'), null);
    assert.ok(!(await res.text()).includes('private exception'));
  }
});
test('permission timeout is bounded even when the service ignores abort', async () => {
  const res = await worker.fetch(request(), environment({ DOWNLOAD_PERMISSION: { fetch: () => new Promise(() => {}) } }));
  assert.equal(res.status, 503);
  assert.equal(res.headers.get('location'), null);
  assert.equal(res.headers.get('x-bunyi-download-status'), null);
});
for (const change of [g => ({ ...g, nonce: 'reused' }), g => ({ ...g, bucket: 'other' }),
  g => ({ ...g, authorizedAt: Date.now() - 3000 }), g => ({ ...g, authorizedAt: Date.now() + 10000 }),
  g => ({ ...g, revision: 0 }), g => ({ ...g, access: true }), g => ({ ...g, maxAgeMs: 60000 })]) {
  test('reject invalid or expired grant ' + change.toString(), async () => {
    const res = await worker.fetch(request(), environment({ DOWNLOAD_PERMISSION: binding('allowed', change) }));
    assert.equal(res.status, 503);
    assert.equal(res.headers.get('location'), null);
  });
}
test('S3 expiry is anchored to permission time', async () => {
  const authorizedAt = Date.now() - 1000;
  const res = await worker.fetch(request(), environment({ DOWNLOAD_PERMISSION: binding('allowed', g => ({ ...g, authorizedAt })) }));
  assert.equal(res.status, 307);
  assert.equal(new URL(res.headers.get('location')).searchParams.get('X-Amz-Date'),
    new Date(authorizedAt).toISOString().replace(/[:-]|\.\d{3}/g, ''));
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

test('a delay during signing cannot produce a redirect from an expired grant', async () => {
  const realSign = AwsClient.prototype.sign, realNow = Date.now;
  try {
    AwsClient.prototype.sign = async function (...args) {
      const signed = await realSign.apply(this, args);
      const delayed = realNow() + 3000;
      Date.now = () => delayed;
      return signed;
    };
    const res = await worker.fetch(request(), environment());
    assert.equal(res.status, 503);
    assert.equal(res.headers.get('location'), null);
  } finally { AwsClient.prototype.sign = realSign; Date.now = realNow; }
});

test('production and staging deploys preserve issuance pause and isolated bindings', () => {
  const config = name => {
    const text = readFileSync(new URL('../' + name, import.meta.url), 'utf8');
    return JSON.parse(text.slice(text.indexOf('{')));
  };
  const prod = config('wrangler.jsonc'), stage = config('wrangler.staging.jsonc');
  for (const c of [prod, stage]) {
    assert.equal(c.vars.DOWNLOADS_ENABLED, 'false');
    assert.equal(c.services[0].entrypoint, 'PermissionService');
    assert.equal(c.kv_namespaces, undefined);
  }
  assert.equal(stage.vars.R2_BUCKET, 'bunyi-models-staging');
  assert.equal(stage.services[0].service, 'r2-killswitch-staging');
  assert.deepEqual(stage.routes, []);
  assert.equal(prod.services[0].service, 'r2-killswitch');
});
