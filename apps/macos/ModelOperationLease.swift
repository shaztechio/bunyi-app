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

struct BunyiBusyError: LocalizedError, Sendable {
    let modelsRoot: URL

    var errorDescription: String? {
        "Another Bunyi operation owns this models folder. Unload its model "
            + "or wait for it to finish."
    }
}

/// An OS-released, cross-process lease for model mutation and inference.
///
/// The lock file deliberately remains on disk. Removing it while another
/// process holds its descriptor creates a second inode and allows two owners.
final class ModelOperationLease: @unchecked Sendable {
    private let descriptor: Int32

    init(modelsRoot: URL, operation: String) throws {
        try FileManager.default.createDirectory(
            at: modelsRoot, withIntermediateDirectories: true)
        let path = modelsRoot.appendingPathComponent(
            ".bunyi-operation.lock", isDirectory: false).path
        let fd = Darwin.open(
            path, O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW,
            S_IRUSR | S_IWUSR)
        guard fd >= 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            let failure = errno
            Darwin.close(fd)
            if failure == EWOULDBLOCK || failure == EAGAIN {
                throw BunyiBusyError(modelsRoot: modelsRoot)
            }
            throw POSIXError(POSIXErrorCode(rawValue: failure) ?? .EIO)
        }
        descriptor = fd
        _ = Darwin.fchmod(fd, S_IRUSR | S_IWUSR)
        let owner = [
            "pid": getpid(),
            "operation": operation,
            "startedAt": ISO8601DateFormatter().string(from: Date()),
        ] as [String: Any]
        if let data = try? JSONSerialization.data(
            withJSONObject: owner, options: [.sortedKeys]) {
            _ = Darwin.ftruncate(fd, 0)
            _ = data.withUnsafeBytes { bytes in
                Darwin.write(fd, bytes.baseAddress, bytes.count)
            }
            _ = Darwin.fsync(fd)
        }
    }

    deinit {
        _ = flock(descriptor, LOCK_UN)
        Darwin.close(descriptor)
    }
}
