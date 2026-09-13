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
enum ModelOperationLeaseChecks {
    static func main() throws {
        if CommandLine.arguments.count == 3,
           CommandLine.arguments[1] == "--probe" {
            probe(URL(fileURLWithPath: CommandLine.arguments[2]))
        }

        let root = FileManager.default.temporaryDirectory.appendingPathComponent(
            "bunyi-lease-check-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }

        try withLease(at: root) {
            let status = try childStatus(for: root)
            precondition(status == 4,
                         "A second process must be rejected while the lease is held")

            let lock = root.appendingPathComponent(".bunyi-operation.lock")
            let attributes = try FileManager.default.attributesOfItem(atPath: lock.path)
            precondition(attributes[.posixPermissions] as? Int == 0o600)
            let owner = try JSONSerialization.jsonObject(with: Data(contentsOf: lock))
                as? [String: Any]
            precondition(owner?["operation"] as? String == "contract-check")
            precondition(owner?["pid"] != nil)
        }

        let releasedStatus = try childStatus(for: root)
        precondition(releasedStatus == 0,
                     "The kernel lease must be released when its owner exits its scope")
    }

    private static func withLease(at root: URL, body: () throws -> Void) throws {
        let lease = try ModelOperationLease(
            modelsRoot: root, operation: "contract-check")
        try body()
        withExtendedLifetime(lease) {}
    }

    private static func childStatus(for root: URL) throws -> Int32 {
        let child = Process()
        child.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
        child.arguments = ["--probe", root.path]
        try child.run()
        child.waitUntilExit()
        return child.terminationStatus
    }

    private static func probe(_ root: URL) -> Never {
        do {
            let lease = try ModelOperationLease(
                modelsRoot: root, operation: "child-probe")
            withExtendedLifetime(lease) {}
            Darwin.exit(0)
        } catch is BunyiBusyError {
            Darwin.exit(4)
        } catch {
            Darwin.exit(10)
        }
    }
}
