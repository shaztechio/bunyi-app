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
enum ServerCommand {
    static func run(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        switch request.operation {
        case "server.run":
            return try await runForeground(
                request, output: output, cancelled: cancelled)
        case "server.start":
            return try await start(request)
        case "server.status":
            return try await ServerClient().status(request)
        case "server.stop":
            return try await ServerClient().stop(request)
        case "server.preload", "server.unload":
            return try await ServerClient().execute(
                request, output: output, cancelled: cancelled)
        case "jobs.status", "jobs.follow", "jobs.cancel":
            return try await ServerClient().job(
                request, output: output, cancelled: cancelled)
        default:
            throw CLICommandParser.invalid("Unknown server command.")
        }
    }

    private static func runForeground(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        let host = ServerHost()
        let monitor = Task { @MainActor in
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(50))
            }
            if cancelled.value { host.requestStop() }
        }
        defer { monitor.cancel() }
        try await host.run {
            output.event(CLIProtocol.event(request, type: "ready", [
                "pid": ProcessInfo.processInfo.processIdentifier,
                "protocolVersion": ServerWire.version,
            ]))
        }
        return CLIProtocol.result(request, ["stopped": true])
    }

    private static func start(_ request: CLIRequest) async throws -> CLIMessage {
        let client = ServerClient()
        do {
            let status = try await client.status(request)
            return CLIProtocol.result(request, [
                "started": false,
                "alreadyRunning": true,
                "pid": status["pid"] ?? NSNull(),
                "protocolVersion": status["protocolVersion"] ?? ServerWire.version,
            ])
        } catch let error as ServerError where error.code == "server_unavailable" {
            // No live endpoint: continue with explicit background startup.
        } catch {
            throw error
        }

        let process = Process()
        process.executableURL = Bundle.main.executableURL
            ?? URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
        process.arguments = ["server", "run", "--json"]
        process.environment = ProcessInfo.processInfo.environment.merging([
            "BUNYI_SERVER_BACKGROUND": "1",
        ]) { _, value in value }
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            throw ServerError(
                code: "server_unavailable",
                message: "Could not start the Bunyi server: \(error.localizedDescription)")
        }

        let deadline = ContinuousClock.now + .seconds(15)
        var lastError: Error?
        while ContinuousClock.now < deadline {
            do {
                let status = try await client.status(request)
                return CLIProtocol.result(request, [
                    "started": true,
                    "alreadyRunning": false,
                    "pid": status["pid"] ?? process.processIdentifier,
                    "protocolVersion": status["protocolVersion"] ?? ServerWire.version,
                ])
            } catch {
                lastError = error
                if !process.isRunning { break }
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
        throw ServerError(
            code: "server_unavailable",
            message: "The Bunyi server did not become ready: \(lastError?.localizedDescription ?? "unknown error")")
    }
}
