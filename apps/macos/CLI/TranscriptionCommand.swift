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

import Foundation
import BunyiMLXCore

@MainActor
enum TranscriptionCommand {
    static func run(_ request: CLIRequest, output: CLIOutput,
                    cancelled: CancellationState) async throws -> CLIMessage {
        if request.has("require-server") || request.has("detach") {
            throw CLIError(
                "server_unavailable",
                "No Bunyi server is available. Run bunyi server start first.",
                exitCode: 4)
        }
        guard let path = request.value("target") else {
            throw CLICommandParser.missing("Provide an audio path.")
        }
        let language = request.value("language") ?? "auto"
        let url = URL(fileURLWithPath: path)
        let text = try await CLIWhisperTranscriber.transcribe(
            url, language: language, trimReference: false,
            request: request, output: output, cancelled: cancelled)
        return CLIProtocol.result(request, [
            "inputPath": url.path,
            "transcript": text,
            "language": language,
            "local": true,
        ])
    }
}
