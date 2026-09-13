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
enum GenerationCommand {
    static func run(_ request: CLIRequest, output: CLIOutput,
                    cancelled: CancellationState) async throws -> CLIMessage {
        if request.has("require-server") || request.has("detach") {
            throw CLIError(
                "server_unavailable",
                "No Bunyi server is available. Run bunyi server start first.",
                exitCode: 4)
        }

        let mode: TTSMode
        switch request.operation {
        case "generate.preset": mode = .presetVoice
        case "generate.design": mode = .voiceDesign
        case "generate.clone": mode = .voiceClone
        default:
            throw CLICommandParser.invalid("Unknown generation mode.")
        }
        guard let text = request.value("text") else {
            throw CLICommandParser.missing("Provide text to speak.")
        }
        let language = request.value("language") ?? "auto"
        var referenceURL = request.value("reference").map(URL.init(fileURLWithPath:))
        var transcript = request.value("transcript")

        if let idText = request.value("saved-voice"),
           let id = UUID(uuidString: idText) {
            let library = VoiceLibrary()
            guard let voice = library.voice(with: id) else {
                throw CLIError(
                    "missing_input", "No saved voice has that ID.", exitCode: 3)
            }
            referenceURL = library.audioURL(for: voice)
            guard let referenceURL,
                  FileManager.default.isReadableFile(atPath: referenceURL.path),
                  (try? referenceURL.resourceValues(
                    forKeys: [.isRegularFileKey]).isRegularFile) == true else {
                throw CLIError(
                    "missing_input",
                    "The saved voice's reference audio is missing or unreadable.",
                    exitCode: 3)
            }
            transcript = voice.transcript
            guard transcript?.trimmingCharacters(
                in: .whitespacesAndNewlines).isEmpty == false else {
                throw CLIError(
                    "missing_input", "The saved voice has no reference transcript.",
                    exitCode: 3)
            }
        }

        if request.has("auto-transcribe") {
            guard let referenceURL else {
                throw CLICommandParser.missing("Provide --reference.")
            }
            output.event(CLIProtocol.event(
                request, type: "transcribing",
                ["detail": "Transcribing the reference clip on-device"]))
            let transcription = Task { @MainActor in
                try await ReferenceTranscriber.transcribeOnDevice(
                    url: referenceURL, locale: TTSEngine.locale(for: language))
            }
            let cancellationMonitor = Task {
                while !Task.isCancelled, !cancelled.value {
                    try? await Task.sleep(for: .milliseconds(100))
                }
                if cancelled.value { transcription.cancel() }
            }
            defer { cancellationMonitor.cancel() }
            do {
                transcript = try await transcription.value
            } catch is CancellationError {
                throw CLIError(
                    "cancelled", "Transcription was cancelled.", exitCode: 5)
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
            }
        }

        if cancelled.value {
            throw CLIError("cancelled", "Speech generation was cancelled.", exitCode: 5)
        }

        let engine = TTSEngine()
        let style = request.operation == "generate.design"
            ? request.value("voice") : request.value("style")
        let generation = Task { @MainActor in
            await engine.generate(
                mode: mode,
                text: text,
                speaker: request.value("speaker"),
                instruct: style,
                language: language,
                referenceAudioURL: referenceURL,
                referenceText: transcript)
        }
        let monitor = Task { @MainActor in
            var previous = ""
            while !Task.isCancelled {
                if cancelled.value {
                    generation.cancel()
                    engine.stop()
                }
                if let progress = progress(engine, request: request) {
                    let key = progress["type"].map(String.init(describing:)) ?? ""
                    if key != previous || key == "generating" || key == "downloading" {
                        output.event(progress)
                        previous = key
                    }
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
        await generation.value
        monitor.cancel()
        defer { engine.unload(reason: "one-shot command finished") }

        if let failure = engine.lastFailure {
            throw CLIError(failure.code, failure.message, exitCode: failure.exitCode)
        }
        guard let summary = engine.lastGenerationSummary else {
            throw CLIError(
                "generation_failed", "Speech generation did not produce a result.",
                exitCode: 10)
        }
        return result(request, summary: summary)
    }

    private static func progress(_ engine: TTSEngine,
                                 request: CLIRequest) -> CLIMessage? {
        switch engine.status {
        case .idle, .error:
            return nil
        case .checking:
            return CLIProtocol.event(
                request, type: "checking", ["detail": "Checking model files"])
        case .loading:
            return CLIProtocol.event(
                request, type: "loading", ["detail": "Loading the MLX model"])
        case .transcribing:
            return CLIProtocol.event(
                request, type: "transcribing",
                ["detail": "Transcribing the reference clip"])
        case .finalizing:
            return CLIProtocol.event(
                request, type: "finalizing",
                ["detail": "Writing the generated audio"])
        case .stopping:
            return CLIProtocol.event(
                request, type: "stopping",
                ["detail": "Waiting for MLX to release generation work"])
        case .generating(let frames):
            return CLIProtocol.event(request, type: "generating", [
                "frames": frames,
                "audioSeconds": Double(frames) / 12.5,
                "detail": engine.generationDetail ?? "Generating speech",
            ])
        case .downloading:
            guard let progress = engine.downloadFeedback else {
                return CLIProtocol.event(
                    request, type: "downloading",
                    ["detail": "Downloading model files"])
            }
            return CLIProtocol.event(request, type: "downloading", [
                "bytesCompleted": progress.available,
                "bytesTotal": progress.total > 0 ? progress.total : NSNull(),
                "rateBytesPerSecond": progress.rate > 0 ? progress.rate : NSNull(),
                "etaSeconds": progress.rate > 0 && progress.total > 0
                    ? Double(max(0, progress.total - progress.available)) / progress.rate
                    : NSNull(),
                "currentFile": progress.file ?? NSNull(),
                "detail": progress.title,
            ])
        }
    }

    private static func result(_ request: CLIRequest,
                               summary: GenerationSummary) -> CLIMessage {
        let metadata = summary.metadata
        var fields: CLIMessage = [
            "outputPath": summary.outputURL.path,
            "mode": modeName(summary.mode),
            "durationSeconds": summary.durationSeconds,
            "frames": summary.frames,
            "elapsedSeconds": summary.elapsedSeconds,
            "modelSource": summary.modelSource,
            "language": metadata.language,
            "text": metadata.text,
            "appVersion": metadata.appVersion,
            "created": CLIProtocol.timestamp(metadata.created),
        ]
        let optional: [(String, String?)] = [
            ("speaker", metadata.speaker),
            ("style", metadata.style),
            ("voiceDescription", metadata.voiceDescription),
            ("referenceTranscript", metadata.referenceTranscript),
            ("continuationModelSource", metadata.continuationModelRepo),
        ]
        for (key, value) in optional {
            if let value { fields[key] = value }
        }
        return CLIProtocol.result(request, fields)
    }

    static func modeName(_ mode: TTSMode) -> String {
        switch mode {
        case .presetVoice: "preset"
        case .voiceDesign: "design"
        case .voiceClone: "clone"
        }
    }
}
