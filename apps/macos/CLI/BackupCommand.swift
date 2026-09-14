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
enum BackupCommand {
    static func run(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState, engine: TTSEngine? = nil
    ) async throws -> CLIMessage {
        guard let path = request.value("target") else {
            throw CLICommandParser.missing("Provide a backup path.")
        }
        let url = URL(fileURLWithPath: path)
        if request.operation == "backup.create",
           FileManager.default.fileExists(atPath: url.path) {
            throw CLICommandParser.invalid(
                "Backup destination already exists; choose a new path.")
        }
        if request.operation == "backup.restore", engine?.loadedMode != nil {
            throw CLIError(
                "model_loaded",
                "Unload the server model before restoring a backup.",
                exitCode: 4)
        }

        let lease: ModelOperationLease?
        do {
            lease = engine?.loadedMode == nil
                ? try ModelOperationLease(
                    modelsRoot: ModelsLocation.current(),
                    operation: request.operation)
                : nil
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "Another Bunyi process is using this models folder.",
                exitCode: 4)
        }
        defer { withExtendedLifetime(lease) {} }

        let manager = BackupManager()
        if request.operation == "backup.create" {
            manager.startBackup(to: url)
        } else {
            manager.startRestore(from: url)
        }
        // Let the manager's MainActor task enter its working state before the
        // polling loop examines it.
        await Task.yield()
        var lastProgress = -1
        var requestedCancellation = false
        while manager.status.isBusy {
            if cancelled.value, !requestedCancellation {
                requestedCancellation = true
                manager.cancel()
            }
            let percent = manager.progress.map { Int($0 * 100) } ?? -1
            if percent != lastProgress || requestedCancellation {
                var fields: CLIMessage = [
                    "detail": statusText(manager.status),
                    "fraction": manager.progress ?? NSNull(),
                ]
                if requestedCancellation {
                    fields["detail"] = "Waiting for backup work to stop"
                }
                output.event(CLIProtocol.event(
                    request,
                    type: requestedCancellation ? "stopping" : "verifying",
                    fields))
                lastProgress = percent
            }
            try? await Task.sleep(for: .milliseconds(100))
        }
        if requestedCancellation || cancelled.value {
            throw CLIError(
                "cancelled", "Backup operation was cancelled.", exitCode: 5)
        }
        if case .error(let message) = manager.status {
            throw CLIError("backup_failed", message, exitCode: 10)
        }

        if request.operation == "backup.create" {
            return CLIProtocol.result(request, ["outputPath": url.path])
        }
        return CLIProtocol.result(request, [
            "modelsFolder": ModelsLocation.current().path,
            "restored": manager.lastRestored,
            "skipped": manager.lastSkipped,
        ])
    }

    private static func statusText(_ status: BackupManager.Status) -> String {
        switch status {
        case .idle: "Idle"
        case .working(let message): message
        case .stopping: "Stopping"
        case .done(let message): message
        case .error(let message): message
        }
    }
}
