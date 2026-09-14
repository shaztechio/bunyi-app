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
enum LibraryCommand {
    static func run(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        switch request.operation {
        case "voices.list":
            return CLIProtocol.result(request, [
                "voices": VoiceLibrary().voices.map(voiceMessage),
            ])
        case "voices.add":
            return try await addVoice(
                request, output: output, cancelled: cancelled)
        case "voices.remove":
            return try removeVoice(request)
        case "history.list":
            return CLIProtocol.result(request, [
                "outputs": outputs().map(outputMessage),
            ])
        case "history.show":
            return CLIProtocol.result(request, [
                "output": try outputMessage(findOutput(request)),
            ])
        case "history.remove":
            return try removeOutput(request)
        default:
            throw CLICommandParser.invalid("Unknown library command.")
        }
    }

    private static func addVoice(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        guard let name = request.value("name"),
              let path = request.value("reference") else {
            throw CLICommandParser.missing(
                "Provide a name and reference audio.")
        }
        let reference = URL(fileURLWithPath: path)
        var transcript = request.value("transcript")
        if request.has("auto-transcribe") {
            transcript = try await transcribe(
                reference, request: request, output: output,
                cancelled: cancelled)
        }
        guard let transcript,
              !transcript.trimmingCharacters(
                in: .whitespacesAndNewlines).isEmpty else {
            throw CLICommandParser.missing(
                "Provide a transcript or use --auto-transcribe.")
        }
        if cancelled.value {
            throw CLIError("cancelled", "Saving the voice was cancelled.", exitCode: 5)
        }
        let library = VoiceLibrary()
        let voice: SavedVoice
        do {
            voice = try library.save(
                name: name, audioURL: reference, transcript: transcript)
        } catch {
            throw CLIError(
                "voice_save_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "voice": voiceMessage(voice),
            "clipPath": library.audioURL(for: voice).path,
        ])
    }

    private static func removeVoice(_ request: CLIRequest) throws -> CLIMessage {
        guard let value = request.value("target"),
              let id = UUID(uuidString: value) else {
            throw CLICommandParser.invalid(
                "Use the exact voice ID from voices list.")
        }
        let library = VoiceLibrary()
        guard let voice = library.voice(with: id) else {
            throw CLIError(
                "missing_input", "No saved voice has that ID.", exitCode: 3)
        }
        do {
            try library.deleteStrict(voice)
        } catch {
            throw CLIError(
                "voice_remove_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "removedId": voice.id.uuidString,
            "recoverable": false,
        ])
    }

    private static func transcribe(
        _ url: URL, request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> String {
        try await CLIWhisperTranscriber.transcribe(
            url, language: "auto", trimReference: true,
            request: request, output: output, cancelled: cancelled)
    }

    private static func voiceMessage(_ voice: SavedVoice) -> CLIMessage {
        [
            "id": voice.id.uuidString,
            "name": voice.name,
            "fileName": voice.fileName,
            "transcript": voice.transcript,
            "createdAt": CLIProtocol.timestamp(voice.createdAt),
        ]
    }

    private static var outputDirectory: URL {
        let directory = FileManager.default.urls(
            for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi/Outputs", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: directory, withIntermediateDirectories: true)
        return directory
    }

    private static func outputs() -> [GeneratedOutput] {
        let keys: Set<URLResourceKey> = [
            .contentModificationDateKey, .fileSizeKey, .isRegularFileKey,
        ]
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: outputDirectory, includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles])) ?? []
        return urls.compactMap { url in
            guard url.pathExtension.lowercased() == "wav",
                  let values = try? url.resourceValues(forKeys: keys),
                  values.isRegularFile == true else { return nil }
            return GeneratedOutput(
                url: url, created: values.contentModificationDate ?? .distantPast,
                byteCount: Int64(values.fileSize ?? 0))
        }.sorted { $0.created > $1.created }
    }

    private static func findOutput(_ request: CLIRequest) throws -> GeneratedOutput {
        guard let path = request.value("target") else {
            throw CLICommandParser.missing("Provide an output path.")
        }
        let wanted = URL(fileURLWithPath: path).standardizedFileURL
        guard let output = outputs().first(where: {
            $0.url.standardizedFileURL == wanted
        }) else {
            throw CLIError(
                "missing_input",
                "That exact path is not a Bunyi history output.", exitCode: 3)
        }
        return output
    }

    private static func removeOutput(_ request: CLIRequest) throws -> CLIMessage {
        let output = try findOutput(request)
        do {
            try FileManager.default.trashItem(
                at: output.url, resultingItemURL: nil)
        } catch {
            throw CLIError(
                "history_remove_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "removedPath": output.url.path,
            "recoverable": true,
        ])
    }

    private static func outputMessage(_ output: GeneratedOutput) -> CLIMessage {
        var message: CLIMessage = [
            "path": output.url.path,
            "name": output.name,
            "mode": output.mode,
            "createdAt": CLIProtocol.timestamp(output.created),
            "bytes": output.byteCount,
        ]
        message["metadata"] = WAVMetadata.read(from: output.url)
            .map(metadataMessage) ?? NSNull()
        return message
    }

    private static func metadataMessage(_ metadata: OutputMetadata) -> CLIMessage {
        var message: CLIMessage = [
            "mode": metadata.mode,
            "text": metadata.text,
            "language": metadata.language,
            "modelRepo": metadata.modelRepo,
            "appVersion": metadata.appVersion,
            "createdAt": CLIProtocol.timestamp(metadata.created),
        ]
        message["speaker"] = metadata.speaker ?? NSNull()
        message["style"] = metadata.style ?? NSNull()
        message["voiceDescription"] = metadata.voiceDescription ?? NSNull()
        message["referenceTranscript"] = metadata.referenceTranscript ?? NSNull()
        message["continuationModelRepo"] = metadata.continuationModelRepo ?? NSNull()
        return message
    }
}
