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
import BunyiMLXCore

@main
struct BunyiCLI {
    @MainActor
    static func main() async {
        LogStore.shared.log(PackagedModelDefaults.current.diagnostic)
        if ProcessInfo.processInfo.environment["BUNYI_SERVER_BACKGROUND"] == "1" {
            signal(SIGHUP, SIG_IGN)
        }
        let code = await run(Array(CommandLine.arguments.dropFirst()))
        Darwin.exit(code)
    }

    @MainActor
    private static func run(_ arguments: [String]) async -> Int32 {
        var request = CLIRequest(operation: "command", arguments: [:],
                                 operationID: OperationID.make())
        // Preserve a requested machine-readable channel even when parsing
        // fails before a CLIRequest can be constructed.
        let requestedJSON = arguments.contains("--json")
        let requestedJSONL = arguments.contains("--jsonl")
        let cancellation = CancellationState()
        signal(SIGINT, SIG_IGN)
        signal(SIGTERM, SIG_IGN)
        // The closures are MainActor-isolated because this entry point is.
        // Deliver on the main queue so Swift's runtime isolation check agrees.
        let interrupt = DispatchSource.makeSignalSource(
            signal: SIGINT, queue: .main)
        let terminate = DispatchSource.makeSignalSource(
            signal: SIGTERM, queue: .main)
        interrupt.setEventHandler { cancellation.cancel() }
        terminate.setEventHandler { cancellation.cancel() }
        interrupt.resume()
        terminate.resume()

        do {
            request = try CLICommandParser.parse(arguments)
            let stdin = request.has("stdin") ? FileHandle.standardInput.readDataToEndOfFile() : Data()
            request = try CLICommandParser.normalize(request, stdin: stdin)
            let output = CLIOutput(json: request.has("json"), jsonl: request.has("jsonl"))
            let result: CLIMessage
            if request.operation.hasPrefix("server.")
                || request.operation.hasPrefix("jobs.") {
                result = try await ServerCommand.run(
                    request, output: output, cancelled: cancellation)
            } else if shouldUseServer(request), !request.has("one-shot") {
                do {
                    result = try await ServerClient().execute(
                        request, output: output, cancelled: cancellation)
                } catch let error as ServerError
                    where error.code == "server_unavailable"
                        && !request.has("require-server")
                        && !request.has("detach") {
                    result = try await executeLocally(
                        request, output: output, cancelled: cancellation)
                }
            } else {
                result = try await executeLocally(
                    request, output: output, cancelled: cancellation)
            }
            output.finish(result)
            return exitCode(of: result)
        } catch let error as CLIError {
            let output = CLIOutput(
                json: request.has("json") || requestedJSON,
                jsonl: request.has("jsonl") || requestedJSONL)
            output.finish(CLIProtocol.failure(request, error))
            return error.exitCode
        } catch let error as ServerError {
            let failure = CLIError(error.code, error.message, exitCode: 4)
            let output = CLIOutput(
                json: request.has("json") || requestedJSON,
                jsonl: request.has("jsonl") || requestedJSONL)
            output.finish(CLIProtocol.failure(request, failure))
            return failure.exitCode
        } catch {
            let failure = CLIError("operation_failed", error.localizedDescription,
                                   exitCode: 10)
            let output = CLIOutput(
                json: request.has("json") || requestedJSON,
                jsonl: request.has("jsonl") || requestedJSONL)
            output.finish(CLIProtocol.failure(request, failure))
            return failure.exitCode
        }
    }

    @MainActor
    private static func executeLocally(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState
    ) async throws -> CLIMessage {
        switch request.operation {
        case "help":
            return CLIProtocol.result(request, ["help": CLICommandParser.help])
        case "version":
            let info = Bundle.main.infoDictionary
            let version = info?["CFBundleShortVersionString"] as? String ?? "0.1.0"
            return CLIProtocol.result(request, ["version": version])
        case "play":
            return try await PlaybackCommand.run(
                request, output: output, cancelled: cancelled)
        case "generate.preset", "generate.design", "generate.clone":
            return try await GenerationCommand.run(
                request, output: output, cancelled: cancelled)
        case "models.list", "models.status", "models.download",
             "models.verify", "models.remove":
            return try await ModelCommand.run(
                request, output: output, cancelled: cancelled)
        case "transcribe":
            return try await TranscriptionCommand.run(
                request, output: output, cancelled: cancelled)
        case "speakers":
            return try await SpeakersCommand.run(
                request, output: output, cancelled: cancelled)
        case "voices.list", "voices.add", "voices.remove",
             "history.list", "history.show", "history.remove":
            return try await LibraryCommand.run(
                request, output: output, cancelled: cancelled)
        case "config.list", "config.get", "config.set",
             "logs.path", "logs.tail", "logs.clear":
            return try ConfigurationCommand.run(request)
        case "doctor":
            return try await DoctorCommand.run(
                request, output: output, cancelled: cancelled)
        case "backup.create", "backup.restore":
            return try await BackupCommand.run(
                request, output: output, cancelled: cancelled)
        default:
            throw CLIError(
                "not_implemented",
                "This preview build does not implement \(request.operation) yet.",
                exitCode: 10)
        }
    }

    private static func shouldUseServer(_ request: CLIRequest) -> Bool {
        if request.has("require-server") || request.has("detach") { return true }
        return request.operation.hasPrefix("generate.")
            || [
                "models.status", "models.download", "models.verify",
                "models.remove",
            ].contains(request.operation)
            || request.operation == "speakers"
            || request.operation == "transcribe"
            || request.operation == "voices.add"
            || request.operation == "doctor"
            || request.operation.hasPrefix("backup.")
    }

    private static func exitCode(of message: CLIMessage) -> Int32 {
        guard message["type"] as? String == "error" else { return 0 }
        if let code = message["exitCode"] as? Int32 { return code }
        if let code = message["exitCode"] as? Int { return Int32(code) }
        if let code = message["exitCode"] as? NSNumber { return code.int32Value }
        return 10
    }
}
