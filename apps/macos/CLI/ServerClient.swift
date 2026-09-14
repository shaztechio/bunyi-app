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

import Darwin
import Foundation

@MainActor
final class ServerClient {
    private let endpoint: ServerEndpoint

    init(endpoint: ServerEndpoint = ServerEndpoint()) {
        self.endpoint = endpoint
    }

    func status(_ request: CLIRequest) async throws -> CLIMessage {
        try await single(request, action: "status")
    }

    func stop(_ request: CLIRequest) async throws -> CLIMessage {
        try await single(request, action: "stop", timeoutMilliseconds: -1)
    }

    func execute(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        let descriptor = try await connect()
        defer { ServerSocket.close(descriptor) }
        try await send(
            ServerWire.request(
                request, action: "execute", detached: request.has("detach")),
            descriptor)
        if request.has("detach") {
            return try await terminal(
                request, descriptor: descriptor,
                timeoutMilliseconds: 5_000)
        }
        return try await stream(
            request, descriptor: descriptor, output: output,
            cancelled: cancelled, cancelJobID: request.operationID)
    }

    func job(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        let action: String
        switch request.operation {
        case "jobs.status": action = "job.status"
        case "jobs.follow": action = "job.follow"
        case "jobs.cancel": action = "job.cancel"
        default:
            throw CLICommandParser.invalid("Unknown jobs command.")
        }
        let descriptor = try await connect()
        defer { ServerSocket.close(descriptor) }
        var message = ServerWire.request(request, action: action)
        message["jobId"] = request.value("target")
        try await send(message, descriptor)
        if request.operation == "jobs.follow" {
            return try await stream(
                request, descriptor: descriptor, output: output,
                cancelled: cancelled, cancelJobID: nil)
        }
        return try await terminal(
            request, descriptor: descriptor, timeoutMilliseconds: 5_000)
    }

    private func single(
        _ request: CLIRequest, action: String,
        timeoutMilliseconds: Int32 = 5_000
    ) async throws -> CLIMessage {
        let descriptor = try await connect()
        defer { ServerSocket.close(descriptor) }
        try await send(ServerWire.request(request, action: action), descriptor)
        return try await terminal(
            request, descriptor: descriptor,
            timeoutMilliseconds: timeoutMilliseconds)
    }

    private func connect() async throws -> Int32 {
        let descriptor: Int32
        do {
            descriptor = try await Task.detached { [endpoint] in
                try ServerSocket.connect(to: endpoint)
            }.value
        } catch let error as ServerError {
            throw error
        } catch let error as POSIXError where [
            .ENOENT, .ECONNREFUSED, .ECONNRESET,
        ].contains(error.code) {
            throw unavailable()
        } catch {
            throw ServerError(
                code: "server_unavailable",
                message: "The Bunyi server is unavailable: \(error.localizedDescription)")
        }
        do {
            try await send([
                "action": "hello",
                "protocolVersion": ServerWire.version,
            ], descriptor)
            guard let response = try await receive(
                descriptor, timeoutMilliseconds: 5_000),
                  response["protocolVersion"] as? Int == ServerWire.version else {
                throw ServerError(
                    code: "server_protocol_mismatch",
                    message: "Client and server protocol versions differ.")
            }
            return descriptor
        } catch {
            ServerSocket.close(descriptor)
            if let error = error as? ServerError { throw error }
            if let error = error as? POSIXError, error.code == .ETIMEDOUT {
                throw ServerError(
                    code: "server_protocol_mismatch",
                    message: "The server did not complete the protocol handshake.")
            }
            throw error
        }
    }

    private func stream(
        _ request: CLIRequest, descriptor: Int32, output: CLIOutput,
        cancelled: CancellationState, cancelJobID: String?
    ) async throws -> CLIMessage {
        let cancellationMonitor = Task.detached {
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(50))
            }
            if cancelled.value {
                _ = Darwin.shutdown(descriptor, SHUT_RDWR)
            }
        }
        defer { cancellationMonitor.cancel() }
        do {
            while let message = try await receive(
                descriptor, timeoutMilliseconds: -1) {
                let type = message["type"] as? String
                if type == "result" || type == "error" || type == "accepted" {
                    return message
                }
                output.event(message)
            }
        } catch where cancelled.value {
            if let cancelJobID {
                await cancelAndWait(for: cancelJobID)
            }
            throw CLIError(
                "cancelled", "The operation was cancelled.", exitCode: 5)
        }
        if cancelled.value {
            if let cancelJobID {
                await cancelAndWait(for: cancelJobID)
            }
            throw CLIError(
                "cancelled", "The operation was cancelled.", exitCode: 5)
        }
        throw unavailable()
    }

    private func cancelAndWait(for jobID: String) async {
        let quietOutput = CLIOutput(json: true, jsonl: false)
        let independentCancellation = CancellationState()
        let cancel = CLIRequest(
            operation: "jobs.cancel", arguments: ["target": jobID],
            operationID: OperationID.make())
        _ = try? await job(
            cancel, output: quietOutput,
            cancelled: independentCancellation)
        while true {
            let status = CLIRequest(
                operation: "jobs.status", arguments: ["target": jobID],
                operationID: OperationID.make())
            guard let message = try? await job(
                status, output: quietOutput,
                cancelled: independentCancellation),
                  let state = message["state"] as? String else {
                // A stopped server has released its resident model.
                return
            }
            if ["succeeded", "failed", "cancelled"].contains(state) { return }
            try? await Task.sleep(for: .milliseconds(50))
        }
    }

    private func terminal(
        _ request: CLIRequest, descriptor: Int32,
        timeoutMilliseconds: Int32
    ) async throws -> CLIMessage {
        guard let message = try await receive(
            descriptor, timeoutMilliseconds: timeoutMilliseconds) else {
            throw unavailable()
        }
        return message
    }

    private func receive(
        _ descriptor: Int32, timeoutMilliseconds: Int32
    ) async throws -> CLIMessage? {
        let data = try await Task.detached {
            try ServerWire.read(
                from: descriptor, timeoutMilliseconds: timeoutMilliseconds)
        }.value
        return try data.map(ServerWire.decode)
    }

    private func send(_ message: CLIMessage, _ descriptor: Int32) async throws {
        let data = try ServerWire.encode(message)
        try await Task.detached {
            try ServerWire.write(data, to: descriptor)
        }.value
    }

    private func unavailable() -> ServerError {
        ServerError(
            code: "server_unavailable",
            message: "No Bunyi server is available. Run bunyi server start first.")
    }
}
