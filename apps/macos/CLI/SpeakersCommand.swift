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
enum SpeakersCommand {
    static func run(_ request: CLIRequest, output: CLIOutput,
                    cancelled: CancellationState,
                    engine existingEngine: TTSEngine? = nil) async throws -> CLIMessage {
        if request.has("require-server") || request.has("detach") {
            throw CLIError(
                "server_unavailable",
                "No Bunyi server is available. Run bunyi server start first.",
                exitCode: 4)
        }
        let engine = existingEngine ?? TTSEngine()
        let loading = Task { @MainActor in
            _ = try await engine.prepare(mode: .presetVoice)
        }
        let monitor = Task { @MainActor in
            var previous = ""
            while !Task.isCancelled {
                if cancelled.value {
                    loading.cancel()
                    engine.stop()
                }
                let event = progress(engine, request: request)
                let type = String(describing: event["type"] ?? "")
                if type != previous || type == "downloading" {
                    output.event(event)
                    previous = type
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
        defer {
            monitor.cancel()
            if existingEngine == nil {
                engine.unload(reason: "one-shot speakers command finished")
            }
        }
        do {
            try await loading.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Loading speakers was cancelled.", exitCode: 5)
        } catch let error as URLError where error.code == .cancelled {
            throw CLIError("cancelled", "Loading speakers was cancelled.", exitCode: 5)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "Another Bunyi process is using this models folder.",
                exitCode: 4)
        } catch let error as DownloadServiceError {
            let code: String
            switch error {
            case .unavailable: code = "download_service_unavailable"
            case .rateLimited: code = "download_rate_limited"
            case .httpStatus: code = "download_failed"
            }
            throw CLIError(code, error.localizedDescription, exitCode: 10)
        } catch {
            throw CLIError("model_load_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "speakers": engine.speakers,
            "modelSource": TTSMode.presetVoice.effectiveRepoID,
        ])
    }

    private static func progress(_ engine: TTSEngine,
                                 request: CLIRequest) -> CLIMessage {
        if let progress = engine.downloadFeedback {
            let type: String
            switch progress.phase {
            case .checking: type = "checking"
            case .sizing: type = "sizing"
            case .verifying: type = "verifying"
            case .waiting: type = "waiting"
            case .downloading, .reconnecting: type = "downloading"
            }
            var fields: CLIMessage = [
                "bytesCompleted": progress.available,
                "bytesTotal": progress.total > 0 ? progress.total : NSNull(),
                "rateBytesPerSecond": progress.rate > 0 ? progress.rate : NSNull(),
                "currentFile": progress.file ?? NSNull(),
                "detail": progress.title,
            ]
            if progress.phase == .waiting {
                fields["retryAt"] = progress.retryAt.map(CLIProtocol.timestamp) ?? NSNull()
                fields["retryAfterSeconds"] = progress.retryAt.map {
                    max(0, Int(ceil($0.timeIntervalSinceNow)))
                } ?? NSNull()
                fields["retryAttempt"] = progress.retryAttempt
                fields["host"] = progress.retryHost ?? NSNull()
            }
            return CLIProtocol.event(request, type: type, fields)
        }
        let type: String
        let detail: String
        switch engine.status {
        case .loading:
            type = "loading"
            detail = "Loading the preset voice model"
        default:
            type = "checking"
            detail = "Checking the preset voice model"
        }
        return CLIProtocol.event(request, type: type, ["detail": detail])
    }
}
