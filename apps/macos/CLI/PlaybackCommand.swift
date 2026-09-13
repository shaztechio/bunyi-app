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

import AVFoundation
import Foundation

@MainActor
enum PlaybackCommand {
    static func run(_ request: CLIRequest, output: CLIOutput,
                    cancelled: CancellationState) async throws -> CLIMessage {
        guard let path = request.value("target") else {
            throw CLICommandParser.missing("Provide an audio path.")
        }
        let url = URL(fileURLWithPath: path)
        guard ["wav", "mp3", "flac"].contains(url.pathExtension.lowercased()) else {
            throw CLIError(
                "playback_failed", "Bunyi supports WAV, MP3, and FLAC playback.",
                exitCode: 10)
        }
        let player: AVAudioPlayer
        do {
            player = try AVAudioPlayer(contentsOf: url)
        } catch {
            throw CLIError("playback_failed", "Bunyi could not open that audio file.",
                           exitCode: 10)
        }
        guard player.duration.isFinite, player.duration > 0, player.prepareToPlay(),
              player.play() else {
            throw CLIError("playback_failed", "Bunyi could not start audio playback.",
                           exitCode: 10)
        }
        while player.isPlaying {
            if cancelled.value {
                player.stop()
                throw CLIError("cancelled", "Playback was cancelled.", exitCode: 5)
            }
            output.event(CLIProtocol.event(request, type: "playing", [
                "positionSeconds": player.currentTime,
                "durationSeconds": player.duration,
            ]))
            try? await Task.sleep(for: .milliseconds(250))
        }
        if cancelled.value {
            throw CLIError("cancelled", "Playback was cancelled.", exitCode: 5)
        }
        return CLIProtocol.result(request, [
            "inputPath": url.path,
            "durationSeconds": player.duration,
            "played": true,
        ])
    }
}

final class CancellationState: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    var value: Bool { lock.withLock { cancelled } }
    func cancel() { lock.withLock { cancelled = true } }
}
