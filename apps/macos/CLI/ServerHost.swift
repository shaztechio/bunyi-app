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
final class ServerJob {
    let request: CLIRequest
    let created = Date()
    let cancellation = CancellationState()
    private(set) var state = "queued"
    private(set) var latest: CLIMessage
    private(set) var revision = 1
    private(set) var isComplete = false

    init(request: CLIRequest) {
        self.request = request
        latest = CLIProtocol.event(request, type: "queued")
    }

    func start() {
        guard !isComplete else { return }
        state = "running"
    }

    func publish(_ message: CLIMessage) {
        guard !isComplete else { return }
        latest = message
        revision += 1
    }

    func finish(_ message: CLIMessage, state: String) {
        guard !isComplete else { return }
        self.state = state
        latest = message
        revision += 1
        isComplete = true
    }

    func cancel() {
        guard !isComplete else { return }
        publish(CLIProtocol.event(request, type: "stopping", [
            "detail": "Waiting for the operation to stop",
        ]))
        cancellation.cancel()
    }

    func snapshot(for statusRequest: CLIRequest) -> CLIMessage {
        CLIProtocol.result(
            statusRequest,
            [
                "state": state,
                "jobId": request.operationID,
                "jobOperation": request.operation,
                "latestEvent": latest,
            ])
    }
}

@MainActor
final class ServerHost {
    private let endpoint: ServerEndpoint
    private let engine = TTSEngine()
    private let started = Date()
    private var jobs: [String: ServerJob] = [:]
    private var queue: [ServerJob] = []
    private var active: ServerJob?
    private var listener: Int32 = -1
    private var stopping = false
    private var stopped = false
    private var stoppedWaiters: [CheckedContinuation<Void, Never>] = []
    private var stopReplyPending = false
    private var stopReplySent = false

    init(endpoint: ServerEndpoint = ServerEndpoint()) {
        self.endpoint = endpoint
    }

    func run(ready: () -> Void) async throws {
        try endpoint.prepareDirectory()
        let instanceLease = try ServerInstanceLease(endpoint: endpoint)
        let socket = try ServerSocket.listen(at: endpoint)
        listener = socket
        let accepting = Task { @MainActor [weak self] in
            await self?.acceptLoop(socket)
        }
        let worker = Task { @MainActor [weak self] in
            await self?.workLoop()
        }
        ready()

        while !stopping {
            try? await Task.sleep(for: .milliseconds(50))
        }
        closeListener()
        accepting.cancel()
        for job in jobs.values where !job.isComplete { job.cancel() }
        await worker.value
        engine.unload(reason: "server stopped")
        _ = Darwin.unlink(endpoint.socket.path)
        stopped = true
        let waiters = stoppedWaiters
        stoppedWaiters.removeAll()
        waiters.forEach { $0.resume() }
        while stopReplyPending, !stopReplySent {
            try? await Task.sleep(for: .milliseconds(10))
        }
        withExtendedLifetime(instanceLease) {}
    }

    func requestStop() {
        guard !stopping else { return }
        stopping = true
        closeListener()
        for job in jobs.values where !job.isComplete { job.cancel() }
    }

    func waitUntilStopped() async {
        if stopped { return }
        await withCheckedContinuation { stoppedWaiters.append($0) }
    }

    private func closeListener() {
        guard listener >= 0 else { return }
        let closing = listener
        listener = -1
        ServerSocket.close(closing)
    }

    private func acceptLoop(_ socket: Int32) async {
        while !stopping, !Task.isCancelled {
            let accepted: Result<Int32, Error> = await Task.detached {
                Result { try ServerSocket.accept(from: socket) }
            }.value
            switch accepted {
            case .success(let descriptor):
                if stopping {
                    ServerSocket.close(descriptor)
                } else {
                    Task { @MainActor [weak self] in
                        await self?.handleConnection(descriptor)
                    }
                }
            case .failure:
                if !stopping {
                    requestStop()
                }
                return
            }
        }
    }

    private func handleConnection(_ descriptor: Int32) async {
        defer { ServerSocket.close(descriptor) }
        var operation = "server.request"
        var operationID = OperationID.make()
        do {
            guard let hello = try await receive(
                descriptor, timeoutMilliseconds: 5_000),
                  hello["action"] as? String == "hello",
                  hello["protocolVersion"] as? Int == ServerWire.version else {
                throw ServerError(
                    code: "server_protocol_mismatch",
                    message: "Client and server protocol versions differ.")
            }
            try await send(ServerWire.hello(), descriptor)
            guard let value = try await receive(
                descriptor, timeoutMilliseconds: 5_000),
                  let action = value["action"] as? String else {
                throw ServerError(
                    code: "invalid_arguments",
                    message: "The server request is missing its action.")
            }
            switch action {
            case "status":
                let request = try ServerWire.parseRequest(value)
                operation = request.operation
                operationID = request.operationID
                try await send(status(request), descriptor)
            case "stop":
                let request = try ServerWire.parseRequest(value)
                operation = request.operation
                operationID = request.operationID
                stopReplyPending = true
                requestStop()
                await waitUntilStopped()
                defer { stopReplySent = true }
                try await send(
                    CLIProtocol.result(request, ["stopped": true]), descriptor)
            case "execute":
                let request = try ServerWire.parseRequest(value)
                operation = request.operation
                operationID = request.operationID
                let job = try submit(request)
                if value["detached"] as? Bool == true {
                    try await send(CLIProtocol.accepted(request), descriptor)
                } else {
                    await follow(job, descriptor: descriptor,
                                 cancelOnDisconnect: true)
                }
            case "job.status":
                let request = try ServerWire.parseRequest(value)
                let job = try findJob(value["jobId"] as? String)
                operation = request.operation
                operationID = request.operationID
                try await send(job.snapshot(for: request), descriptor)
            case "job.follow":
                let request = try ServerWire.parseRequest(value)
                let job = try findJob(value["jobId"] as? String)
                operation = request.operation
                operationID = request.operationID
                await follow(job, descriptor: descriptor,
                             cancelOnDisconnect: false)
            case "job.cancel":
                let request = try ServerWire.parseRequest(value)
                let job = try findJob(value["jobId"] as? String)
                operation = request.operation
                operationID = request.operationID
                job.cancel()
                try await send(
                    CLIProtocol.result(request, [
                        "cancelRequested": true,
                        "jobId": job.request.operationID,
                    ]),
                    descriptor)
            default:
                throw ServerError(
                    code: "invalid_arguments",
                    message: "Unknown server action.")
            }
        } catch let error as ServerError {
            let request = CLIRequest(
                operation: operation, arguments: [:],
                operationID: operationID)
            try? await send(
                CLIProtocol.failure(
                    request, CLIError(error.code, error.message, exitCode: 4)),
                descriptor)
        } catch {
            // Disconnects are expected: an attached client leaving cancels its
            // job in follow(). There is no useful peer left to report them to.
        }
    }

    private func status(_ request: CLIRequest) -> CLIMessage {
        var fields: CLIMessage = [
            "pid": getpid(),
            "protocolVersion": ServerWire.version,
            "queueLength": queue.count,
            "uptimeSeconds": Date().timeIntervalSince(started),
            "socketPath": endpoint.socket.path,
        ]
        fields["loadedMode"] = engine.loadedMode.map(ModelCommand.modeName) ?? NSNull()
        fields["loadedFolder"] = engine.loadedModelFolder ?? NSNull()
        fields["activeOperation"] = active?.request.operationID ?? NSNull()
        return CLIProtocol.result(request, fields)
    }

    private func submit(_ request: CLIRequest) throws -> ServerJob {
        guard !stopping else {
            throw ServerError(
                code: "server_unavailable", message: "The Bunyi server is stopping.")
        }
        guard jobs[request.operationID] == nil else {
            throw ServerError(
                code: "invalid_arguments",
                message: "The operation identifier already exists.")
        }
        guard queue.count < 32 else {
            throw ServerError(
                code: "bunyi_busy", message: "The server job queue is full.")
        }
        let excess = max(0, jobs.count - 255)
        if excess > 0 {
            let expired = jobs.values
                .filter(\.isComplete)
                .sorted { $0.created < $1.created }
                .prefix(excess)
            for job in expired { jobs.removeValue(forKey: job.request.operationID) }
        }
        let job = ServerJob(request: request)
        jobs[request.operationID] = job
        queue.append(job)
        return job
    }

    private func findJob(_ id: String?) throws -> ServerJob {
        guard let id, let job = jobs[id] else {
            throw ServerError(
                code: "job_not_found",
                message: "That job is not retained by this server.")
        }
        return job
    }

    private func workLoop() async {
        while true {
            guard !queue.isEmpty else {
                if stopping { return }
                try? await Task.sleep(for: .milliseconds(50))
                continue
            }
            let job = queue.removeFirst()
            active = job
            if job.cancellation.value {
                job.finish(
                    CLIProtocol.failure(
                        job.request,
                        CLIError(
                            "cancelled", "The operation was cancelled.",
                            exitCode: 5)),
                    state: "cancelled")
                active = nil
                continue
            }
            job.start()
            let output = CLIOutput(
                json: false, jsonl: false,
                eventSink: { [weak job] message in job?.publish(message) })
            do {
                let result = try await execute(
                    job.request, output: output,
                    cancelled: job.cancellation)
                let state = (result["type"] as? String) == "error"
                    ? (Self.exitCode(result) == 5 ? "cancelled" : "failed")
                    : "succeeded"
                job.finish(result, state: state)
            } catch {
                let failure = Self.failure(job.request, error)
                let state = Self.exitCode(failure) == 5
                    ? "cancelled" : "failed"
                job.finish(failure, state: state)
            }
            active = nil
        }
    }

    private func execute(
        _ rawRequest: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        var request = rawRequest
        for control in [
            "json", "jsonl", "one-shot", "require-server", "detach",
        ] {
            request.arguments.removeValue(forKey: control)
        }
        switch request.operation {
        case "generate.preset", "generate.design", "generate.clone":
            return try await GenerationCommand.run(
                request, output: output, cancelled: cancelled,
                engine: engine, unloadAfterward: false)
        case "models.list", "models.status", "models.download",
             "models.verify":
            return try await ModelCommand.run(
                request, output: output, cancelled: cancelled,
                engine: engine)
        case "models.remove":
            if engine.loadedMode == ModelCommand.mode(request.value("mode")!) {
                throw CLIError(
                    "bunyi_busy", "Unload this model before removing it.",
                    exitCode: 4)
            }
            return try await ModelCommand.run(
                request, output: output, cancelled: cancelled,
                engine: engine)
        case "transcribe":
            return try await TranscriptionCommand.run(
                request, output: output, cancelled: cancelled)
        case "speakers":
            return try await SpeakersCommand.run(
                request, output: output, cancelled: cancelled,
                engine: engine)
        case "server.preload":
            return try await preload(
                request, output: output, cancelled: cancelled)
        case "server.unload":
            engine.unload(reason: "server unload command")
            return CLIProtocol.result(request, ["unloaded": true])
        default:
            throw CLIError(
                "not_implemented",
                "This server does not implement \(request.operation) yet.",
                exitCode: 10)
        }
    }

    private func preload(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        let mode = ModelCommand.mode(request.value("mode")!)
        output.event(CLIProtocol.event(
            request, type: "checking",
            ["detail": "Checking \(mode.rawValue)"]))
        let loading = Task { @MainActor in
            _ = try await engine.prepare(mode: mode)
        }
        let monitor = Task { @MainActor in
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(100))
            }
            if cancelled.value {
                loading.cancel()
                engine.stop()
            }
        }
        defer { monitor.cancel() }
        do {
            try await loading.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Model preload was cancelled.", exitCode: 5)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "Another Bunyi process is using this models folder.",
                exitCode: 4)
        } catch {
            throw CLIError(
                "model_load_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "loadedMode": ModelCommand.modeName(mode),
            "loadedFolder": engine.loadedModelFolder ?? NSNull(),
        ])
    }

    private func follow(
        _ job: ServerJob, descriptor: Int32,
        cancelOnDisconnect: Bool, initialRevision: Int = 0
    ) async {
        var revision = initialRevision
        while true {
            if ServerSocket.isDisconnected(descriptor) {
                if cancelOnDisconnect { job.cancel() }
                return
            }
            if revision != job.revision {
                do {
                    try await send(job.latest, descriptor)
                } catch {
                    if cancelOnDisconnect { job.cancel() }
                    return
                }
                revision = job.revision
                if job.isComplete { return }
            }
            try? await Task.sleep(for: .milliseconds(50))
        }
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

    private static func failure(
        _ request: CLIRequest, _ error: Error
    ) -> CLIMessage {
        if let error = error as? CLIError {
            return CLIProtocol.failure(request, error)
        }
        if let error = error as? ServerError {
            return CLIProtocol.failure(
                request, CLIError(error.code, error.message, exitCode: 4))
        }
        if error is CancellationError {
            return CLIProtocol.failure(
                request, CLIError(
                    "cancelled", "The operation was cancelled.", exitCode: 5))
        }
        return CLIProtocol.failure(
            request, CLIError(
                "operation_failed", error.localizedDescription, exitCode: 10))
    }

    private static func exitCode(_ message: CLIMessage) -> Int32? {
        if let code = message["exitCode"] as? Int32 { return code }
        if let code = message["exitCode"] as? Int { return Int32(code) }
        if let code = message["exitCode"] as? NSNumber { return code.int32Value }
        return nil
    }
}
