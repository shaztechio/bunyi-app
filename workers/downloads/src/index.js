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

import { AwsClient } from "aws4fetch";
import published from "./published-files.json" with { type: "json" };

export const LINK_LIFETIME_SECONDS = 300;
const files = new Set(Object.keys(published));
const hosts = new Set(["bunyi-downloads.bunyi.workers.dev", "models.bunyi.app"]);
const commonHeaders = {
  "Cache-Control": "no-store",
  "Cloudflare-CDN-Cache-Control": "no-store",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
};

function reply(status, message, method, extra = {}) {
  return new Response(method === "HEAD" ? null : message, {
    status, headers: { ...commonHeaders, "Content-Type": "text/plain; charset=utf-8", ...extra },
  });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.protocol !== "https:" || !hosts.has(url.hostname))
      return reply(404, "Not found", request.method);
    if (request.method !== "GET" && request.method !== "HEAD")
      return reply(405, "Only GET and HEAD are supported", request.method, { Allow: "GET, HEAD" });

    // Match canonical, explicitly published object paths. Do not accept an
    // arbitrary bucket/key, encoded separators, or caller-supplied S3 options.
    const key = url.pathname.slice(1);
    if (!files.has(key)) return reply(404, "Not found", request.method);
    if (env.DOWNLOADS_ENABLED !== "true")
      return reply(503, "Model downloads are temporarily paused", request.method, { "Retry-After": "60", "X-Bunyi-Download-Status": "paused" });

    try {
      // Reuse the existing kill switch's state, without modifying its Worker.
      // Missing, malformed or unreadable state fails closed.
      const state = await env.KILLSWITCH_STATE.get("killswitch:state", { type: "json", cacheTtl: 30 });
      if (!state || state.killed !== false)
        return reply(503, "Model downloads are temporarily paused", request.method, { "Retry-After": "60", "X-Bunyi-Download-Status": "paused" });
      if (!/^[a-f0-9]{32}$/.test(env.R2_ACCOUNT_ID ?? "") ||
          env.R2_BUCKET !== "bunyi-models" || !env.R2_ACCESS_KEY_ID || !env.R2_SECRET_ACCESS_KEY)
        return reply(503, "Download service is not configured", request.method);

      const target = new URL("https://" + env.R2_ACCOUNT_ID + ".r2.cloudflarestorage.com/");
      target.pathname = "/" + env.R2_BUCKET + "/" + key;
      target.searchParams.set("X-Amz-Expires", String(LINK_LIFETIME_SECONDS));
      const signer = new AwsClient({
        accessKeyId: env.R2_ACCESS_KEY_ID, secretAccessKey: env.R2_SECRET_ACCESS_KEY,
        service: "s3", region: "auto",
      });
      // Sign only the method and destination. The client retains Range and
      // If-Range on the 307; these must not become required signed headers.
      const signed = await signer.sign(target, { method: request.method, aws: { signQuery: true } });
      return new Response(null, { status: 307, headers: { ...commonHeaders, Location: signed.url } });
    } catch {
      // Never log credentials, signed URLs, or upstream exception messages.
      return reply(503, "Download service is temporarily unavailable", request.method, { "Retry-After": "60" });
    }
  },
};
