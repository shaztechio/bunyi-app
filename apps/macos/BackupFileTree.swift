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

/// Copies a security-scoped model tree into app-owned temporary storage.
/// Regular files are streamed so Stop is observed between bounded chunks,
/// including in the middle of a multi-gigabyte weight file.
public enum BackupFileTree {
    public static func copyDirectory(
        from source: URL,
        to destination: URL,
        isCancelled: @escaping @Sendable () -> Bool,
        didCopyBytes: @escaping @Sendable (Int64) -> Void = { _ in }
    ) throws {
        let manager = FileManager.default
        try manager.createDirectory(
            at: destination, withIntermediateDirectories: true)
        guard let enumerator = manager.enumerator(
            at: source, includingPropertiesForKeys: nil,
            options: []) else {
            throw CocoaError(.fileReadUnknown)
        }
        // DirectoryEnumerator may spell /var and /tmp as /private/var and
        // /private/tmp even when the source URL does not. Resolve both sides
        // before dropping the root prefix. Resolve only an item's parent so a
        // symbolic link itself is copied as a link rather than followed.
        let sourceComponents = source.resolvingSymlinksInPath().pathComponents

        for case let item as URL in enumerator {
            if isCancelled() { throw CancellationError() }
            let normalizedItem = item.deletingLastPathComponent()
                .resolvingSymlinksInPath()
                .appendingPathComponent(item.lastPathComponent)
            let itemComponents = normalizedItem.pathComponents
            guard itemComponents.starts(with: sourceComponents) else {
                throw CocoaError(.fileReadInvalidFileName)
            }
            let relativeComponents = itemComponents.dropFirst(sourceComponents.count)
            let target = relativeComponents.reduce(destination) {
                $0.appendingPathComponent($1)
            }
            let attributes = try manager.attributesOfItem(atPath: item.path)
            let type = attributes[.type] as? FileAttributeType
            if type == .typeSymbolicLink {
                let link = try manager.destinationOfSymbolicLink(atPath: item.path)
                try manager.createSymbolicLink(atPath: target.path,
                                                withDestinationPath: link)
            } else if type == .typeDirectory {
                try manager.createDirectory(
                    at: target, withIntermediateDirectories: true)
            } else if type == .typeRegular {
                try copyFile(
                    from: item, to: target, isCancelled: isCancelled,
                    didCopyBytes: didCopyBytes)
            }
        }
    }

    private static func copyFile(
        from source: URL,
        to destination: URL,
        isCancelled: @escaping @Sendable () -> Bool,
        didCopyBytes: @escaping @Sendable (Int64) -> Void
    ) throws {
        let manager = FileManager.default
        try manager.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true)
        if manager.fileExists(atPath: destination.path) {
            try manager.removeItem(at: destination)
        }
        guard manager.createFile(atPath: destination.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown)
        }

        do {
            let input = try FileHandle(forReadingFrom: source)
            defer { try? input.close() }
            let output = try FileHandle(forWritingTo: destination)
            defer { try? output.close() }
            let chunkSize = 8 * 1024 * 1024
            while true {
                if isCancelled() { throw CancellationError() }
                let data = try input.read(upToCount: chunkSize) ?? Data()
                if data.isEmpty { break }
                try output.write(contentsOf: data)
                didCopyBytes(Int64(data.count))
            }
        } catch {
            try? manager.removeItem(at: destination)
            throw error
        }
    }
}
