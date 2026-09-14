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
import Security

typealias CLIMessage = [String: Any]

struct CLIRequest: Sendable {
    let operation: String
    var arguments: [String: String?]
    let operationID: String

    func has(_ name: String) -> Bool { arguments.keys.contains(name) }
    func value(_ name: String) -> String? { arguments[name] ?? nil }
}

struct CLIError: LocalizedError, Sendable {
    let code: String
    let message: String
    let exitCode: Int32

    init(_ code: String, _ message: String, exitCode: Int32 = 2) {
        self.code = code
        self.message = message
        self.exitCode = exitCode
    }

    var errorDescription: String? { message }
}

enum CLIProtocol {
    static let schemaVersion = 1

    static func envelope(_ request: CLIRequest, type: String) -> CLIMessage {
        [
            "schemaVersion": schemaVersion,
            "type": type,
            "operation": request.operation,
            "operationId": request.operationID,
        ]
    }

    static func result(_ request: CLIRequest, _ fields: CLIMessage = [:]) -> CLIMessage {
        var message = envelope(request, type: "result")
        message["ok"] = true
        fields.forEach { message[$0.key] = $0.value }
        return message
    }

    static func accepted(_ request: CLIRequest) -> CLIMessage {
        var message = envelope(request, type: "accepted")
        message["ok"] = true
        message["jobId"] = request.operationID
        return message
    }

    static func event(_ request: CLIRequest, type: String,
                      _ fields: CLIMessage = [:]) -> CLIMessage {
        var message = envelope(request, type: type)
        message["timestamp"] = timestamp()
        fields.forEach { message[$0.key] = $0.value }
        return message
    }

    static func failure(_ request: CLIRequest, _ error: CLIError) -> CLIMessage {
        var message = envelope(request, type: "error")
        message["ok"] = false
        message["error"] = ["code": error.code, "message": error.message]
        message["exitCode"] = error.exitCode
        return message
    }

    static func timestamp(_ date: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}

/// Time-sortable, 26-character Crockford Base32 operation identifier.
enum OperationID {
    private static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")

    static func make(now: Date = Date()) -> String {
        var timestamp = UInt64(max(0, now.timeIntervalSince1970 * 1_000))
        var time = Array(repeating: Character("0"), count: 10)
        for index in stride(from: 9, through: 0, by: -1) {
            time[index] = alphabet[Int(timestamp & 31)]
            timestamp >>= 5
        }
        var random = [UInt8](repeating: 0, count: 10)
        let status = SecRandomCopyBytes(kSecRandomDefault, random.count, &random)
        if status != errSecSuccess {
            random = withUnsafeBytes(of: UUID().uuid) { Array($0) }.prefix(10).map { $0 }
        }
        var accumulator: UInt32 = 0
        var bits = 0
        var encoded = ""
        for byte in random {
            accumulator = (accumulator << 8) | UInt32(byte)
            bits += 8
            while bits >= 5 {
                bits -= 5
                encoded.append(alphabet[Int((accumulator >> UInt32(bits)) & 31)])
            }
        }
        return String(time) + String(encoded.prefix(16))
    }
}

@MainActor
final class CLIOutput {
    private let lock = NSLock()
    private let json: Bool
    private let jsonl: Bool
    private let eventSink: (@MainActor (CLIMessage) -> Void)?
    private var finished = false

    init(json: Bool, jsonl: Bool,
         eventSink: (@MainActor (CLIMessage) -> Void)? = nil) {
        self.json = json
        self.jsonl = jsonl
        self.eventSink = eventSink
    }

    func event(_ message: CLIMessage) {
        lock.withLock {
            guard !finished else { return }
            if let eventSink {
                eventSink(message)
                return
            }
            guard !json else { return }
            if jsonl {
                writeJSON(message, to: .standardOutput)
            } else {
                let detail = message["detail"] ?? message["type"] ?? "working"
                writeLine(String(describing: detail), to: .standardError)
            }
        }
    }

    func finish(_ message: CLIMessage) {
        lock.withLock {
            guard !finished else { return }
            finished = true
            if json || jsonl {
                writeJSON(message, to: .standardOutput)
                return
            }
            if let error = message["error"] as? [String: Any] {
                writeLine(String(describing: error["message"] ?? "Bunyi failed."),
                          to: .standardError)
                return
            }
            for key in ["help", "outputPath", "transcript", "path", "version"] {
                if let value = message[key] {
                    writeLine(String(describing: value), to: .standardOutput)
                    return
                }
            }
            if message["played"] as? Bool == true,
               let path = message["inputPath"] {
                writeLine("Played \(path)", to: .standardOutput)
                return
            }
            writeJSON(message, to: .standardOutput)
        }
    }

    private func writeJSON(_ message: CLIMessage, to handle: FileHandle) {
        guard JSONSerialization.isValidJSONObject(message),
              let data = try? JSONSerialization.data(
                withJSONObject: message, options: [.sortedKeys]) else { return }
        handle.write(data)
        handle.write(Data([0x0A]))
    }

    private func writeLine(_ line: String, to handle: FileHandle) {
        if let data = (line + "\n").data(using: .utf8) { handle.write(data) }
    }
}
