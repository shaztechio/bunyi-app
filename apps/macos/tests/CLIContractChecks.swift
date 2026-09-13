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

@main
enum CLIContractChecks {
    static func main() throws {
        let preset = try CLICommandParser.normalize(CLICommandParser.parse([
            "generate", "preset", "--text", "Hello", "--speaker", "Ryan", "--json",
        ]))
        precondition(preset.operation == "generate.preset")
        precondition(preset.value("text") == "Hello")
        precondition(preset.value("speaker") == "Ryan")

        try expect("invalid_arguments", ["--json", "--jsonl", "version"])
        try expect("invalid_arguments", ["models", "download", "--all", "--mode", "preset"])
        try expect("missing_input", ["generate", "design", "--voice", "warm"])
        try expect("invalid_arguments", ["play", "missing.wav", "--require-server"])
        try expect("invalid_arguments", ["server", "status", "--one-shot"])

        let request = try CLICommandParser.parse(["version", "--json"])
        let result = CLIProtocol.result(request, ["version": "1.2.0"])
        precondition(result["schemaVersion"] as? Int == 1)
        precondition(result["ok"] as? Bool == true)
        precondition(result["operationId"] as? String == request.operationID)
        precondition(request.operationID.count == 26)
        precondition(request.operationID.allSatisfy {
            "0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains($0)
        })

        let failure = CLIProtocol.failure(request, CLIError(
            "missing_input", "Missing.", exitCode: 3))
        precondition(failure["type"] as? String == "error")
        precondition(failure["exitCode"] as? Int32 == 3)
        let nested = failure["error"] as? [String: String]
        precondition(nested?["code"] == "missing_input")
    }

    private static func expect(_ code: String, _ arguments: [String]) throws {
        do {
            let parsed = try CLICommandParser.parse(arguments)
            _ = try CLICommandParser.normalize(parsed)
            preconditionFailure("Expected \(code) for \(arguments)")
        } catch let error as CLIError {
            precondition(error.code == code, "Expected \(code), got \(error.code)")
        }
    }
}
