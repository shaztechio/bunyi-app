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
        output.event(CLIProtocol.event(request, type: "transcribing", [
            "detail": "Transcribing the audio on-device",
            "inputPath": url.path,
        ]))

        let transcription = Task { @MainActor in
            try await ReferenceTranscriber.transcribeOnDevice(
                url: url, locale: TTSEngine.locale(for: language))
        }
        let cancellationMonitor = Task {
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(100))
            }
            if cancelled.value { transcription.cancel() }
        }
        defer { cancellationMonitor.cancel() }

        let text: String
        do {
            text = try await transcription.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Transcription was cancelled.", exitCode: 5)
        } catch TTSError.transcriptionNotAuthorized {
            throw CLIError(
                "speech_permission_required",
                "Allow speech recognition for bunyi in System Settings > Privacy & Security > Speech Recognition, then try again.",
                exitCode: 3)
        } catch TTSError.transcriptionUnavailable {
            throw CLIError(
                "transcription_unavailable",
                "On-device speech recognition is unavailable for this language.",
                exitCode: 10)
        } catch {
            throw CLIError(
                "transcription_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "inputPath": url.path,
            "transcript": text,
            "language": language,
            "local": true,
        ])
    }
}
