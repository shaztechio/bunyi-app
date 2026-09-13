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

@main
struct BunyiCLI {
    @MainActor
    static func main() async {
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
        let interrupt = DispatchSource.makeSignalSource(signal: SIGINT)
        let terminate = DispatchSource.makeSignalSource(signal: SIGTERM)
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
            switch request.operation {
            case "help":
                result = CLIProtocol.result(request, ["help": CLICommandParser.help])
            case "version":
                let info = Bundle.main.infoDictionary
                let version = info?["CFBundleShortVersionString"] as? String ?? "0.1.0"
                result = CLIProtocol.result(request, ["version": version])
            case "play":
                result = try await PlaybackCommand.run(
                    request, output: output, cancelled: cancellation)
            case "generate.preset", "generate.design", "generate.clone":
                result = try await GenerationCommand.run(
                    request, output: output, cancelled: cancellation)
            default:
                throw CLIError(
                    "not_implemented",
                    "This preview build does not implement \(request.operation) yet.",
                    exitCode: 10)
            }
            output.finish(result)
            return 0
        } catch let error as CLIError {
            let output = CLIOutput(
                json: request.has("json") || requestedJSON,
                jsonl: request.has("jsonl") || requestedJSONL)
            output.finish(CLIProtocol.failure(request, error))
            return error.exitCode
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
}
