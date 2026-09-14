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

struct ServerError: LocalizedError, Sendable {
    let code: String
    let message: String

    var errorDescription: String? { message }
}

enum ServerWire {
    static let version = 1
    static let maximumMessageBytes = 1 << 20

    static func encode(_ value: CLIMessage) throws -> Data {
        guard JSONSerialization.isValidJSONObject(value) else {
            throw ServerError(
                code: "server_protocol_mismatch",
                message: "Bunyi tried to send an invalid server message.")
        }
        var data = try JSONSerialization.data(
            withJSONObject: value, options: [.sortedKeys])
        data.append(0x0A)
        return data
    }

    static func decode(_ data: Data) throws -> CLIMessage {
        guard let value = try JSONSerialization.jsonObject(with: data)
                as? CLIMessage else {
            throw ServerError(
                code: "server_protocol_mismatch",
                message: "The server message is not a JSON object.")
        }
        return value
    }

    static func write(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { raw in
            guard let start = raw.baseAddress else { return }
            var written = 0
            while written < raw.count {
                let count = Darwin.write(
                    descriptor, start.advanced(by: written), raw.count - written)
                if count > 0 {
                    written += count
                } else if count < 0, errno == EINTR {
                    continue
                } else {
                    throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                }
            }
        }
    }

    /// Reads one bounded newline-framed message. A negative timeout waits
    /// indefinitely; a timeout is reported as ETIMEDOUT.
    static func read(from descriptor: Int32, timeoutMilliseconds: Int32 = -1)
        throws -> Data? {
        var result = Data()
        var byte: UInt8 = 0
        while true {
            if timeoutMilliseconds >= 0 {
                var item = pollfd(
                    fd: descriptor, events: Int16(POLLIN), revents: 0)
                let ready = Darwin.poll(&item, 1, timeoutMilliseconds)
                if ready == 0 {
                    throw POSIXError(.ETIMEDOUT)
                }
                if ready < 0 {
                    if errno == EINTR { continue }
                    throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                }
            }
            let count = Darwin.read(descriptor, &byte, 1)
            if count == 0 {
                if result.isEmpty { return nil }
                throw POSIXError(.ECONNRESET)
            }
            if count < 0 {
                if errno == EINTR { continue }
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            if byte == 0x0A { return result }
            guard result.count < maximumMessageBytes else {
                throw ServerError(
                    code: "invalid_arguments",
                    message: "The server request exceeds the one-megabyte limit.")
            }
            result.append(byte)
        }
    }

    static func request(_ request: CLIRequest, action: String,
                        detached: Bool = false) -> CLIMessage {
        var arguments: CLIMessage = [:]
        for (key, value) in request.arguments {
            arguments[key] = value ?? NSNull()
        }
        return [
            "protocolVersion": version,
            "action": action,
            "detached": detached,
            "request": [
                "operation": request.operation,
                "operationId": request.operationID,
                "arguments": arguments,
            ] as CLIMessage,
        ]
    }

    static func parseRequest(_ value: CLIMessage) throws -> CLIRequest {
        guard let request = value["request"] as? CLIMessage,
              let operation = request["operation"] as? String,
              let operationID = request["operationId"] as? String,
              !operation.isEmpty, !operationID.isEmpty,
              operationID.count <= 128,
              let rawArguments = request["arguments"] as? CLIMessage else {
            throw ServerError(
                code: "invalid_arguments",
                message: "The server command is missing required fields.")
        }
        var arguments: [String: String?] = [:]
        for (key, value) in rawArguments {
            if value is NSNull {
                arguments[key] = .some(nil)
            } else if let value = value as? String {
                arguments[key] = value
            } else {
                throw ServerError(
                    code: "invalid_arguments",
                    message: "The server command contains an invalid argument.")
            }
        }
        return CLIRequest(
            operation: operation, arguments: arguments,
            operationID: operationID)
    }

    static func hello() -> CLIMessage {
        ["protocolVersion": version]
    }
}
