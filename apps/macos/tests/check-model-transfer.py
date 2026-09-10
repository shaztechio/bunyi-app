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

"""Run Swift's delegate/file-IO checks against a real loopback HTTP connection."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import subprocess
import sys
import threading

release = threading.Event()
payload = bytes([7]) * 4096

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/release":
            release.set()
            self.send_response(200)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        start = 0
        if self.path != "/ignore-range" and self.headers.get("Range"):
            start = int(self.headers["Range"].removeprefix("bytes=").removesuffix("-"))
        self.send_response(206 if start else 200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(payload) - start))
        if start:
            self.send_header("Content-Range", f"bytes {start}-4095/4096")
        self.end_headers()
        try:
            if self.path == "/one-byte":
                self.wfile.write(payload[:1])
                self.wfile.flush()
                release.wait(15)
                self.wfile.write(payload[1:])
            else:
                self.wfile.write(payload[start:])
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    result = subprocess.run([sys.argv[1], f"http://127.0.0.1:{server.server_port}"], timeout=60)
finally:
    release.set()
    server.shutdown()
    server.server_close()
sys.exit(result.returncode)
