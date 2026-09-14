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

/// One model folder on disk.
public struct DownloadedModel: Identifiable, Hashable {
    /// The folder itself, e.g. `…/models/mlx-community/Qwen3-TTS-…`.
    public let url: URL
    /// What the user recognises: the repo ID, or the self-hosted slug.
    public let name: String
    public let byteCount: Int64
    public let isSelfHosted: Bool

    public var id: URL { url }

    public init(url: URL, name: String, byteCount: Int64,
                isSelfHosted: Bool) {
        self.url = url
        self.name = name
        self.byteCount = byteCount
        self.isSelfHosted = isSelfHosted
    }
}

/// Collects every in-process model eviction synchronously, then lets deletion
/// await those asynchronous actor calls before touching the files on disk.
///
/// Notification delivery itself is synchronous, but the MLX runtime unload is
/// not. Keeping the registered operations here avoids a timing guess between
/// those two facts and still permits more than one live engine to respond.
public final class ModelDeletionRequest: @unchecked Sendable {
    public let url: URL

    private typealias Eviction = @MainActor @Sendable () async -> Void
    private let lock = NSLock()
    private var evictions: [Eviction] = []

    public init(url: URL) {
        self.url = url
    }

    public func register(_ eviction: @escaping @MainActor @Sendable () async -> Void) {
        lock.withLock {
            evictions.append(eviction)
        }
    }

    public func performEvictions() async {
        let registered = lock.withLock { evictions }
        for eviction in registered {
            await eviction()
        }
    }
}

/// Finds and removes downloaded models.
///
/// Deliberately free of any engine reference: Settings is a separate scene and
/// has no access to the running `TTSEngine`. Deleting posts a notification the
/// engine listens for, so a model that is currently loaded gets evicted from
/// memory rather than left in use with its files gone.
@MainActor
public enum ModelStore {
    /// Posted with a `ModelDeletionRequest` as `object`.
    public static let didDeleteModel = Notification.Name("app.bunyi.didDeleteModel")

    /// Every downloaded model, largest first.
    ///
    /// The layout is two levels under `models/`: `mlx-community/<repo>` for Hub
    /// downloads and `self-hosted/<slug>` for the rest. Read from disk rather
    /// than tracked, so a folder removed in the Finder simply stops appearing.
    public static func all() -> [DownloadedModel] {
        let root = ModelsLocation.current().appendingPathComponent("models", isDirectory: true)
        let fm = FileManager.default
        guard let groups = try? fm.contentsOfDirectory(
            at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]
        ) else { return [] }

        var found: [DownloadedModel] = []
        for group in groups where isDirectory(group) {
            let selfHosted = group.lastPathComponent == "self-hosted"
            guard let children = try? fm.contentsOfDirectory(
                at: group, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]
            ) else { continue }
            for child in children where isDirectory(child) {
                found.append(DownloadedModel(
                    url: child,
                    name: selfHosted
                        ? child.lastPathComponent
                        : "\(group.lastPathComponent)/\(child.lastPathComponent)",
                    byteCount: size(of: child),
                    isSelfHosted: selfHosted
                ))
            }
        }
        return found.sorted { $0.byteCount > $1.byteCount }
    }

    /// Moves a model to the Trash and tells the engine to let go of it.
    ///
    /// Trash rather than `removeItem`: this is gigabytes that take many minutes
    /// to fetch again, and a mis-click should be recoverable.
    public static func delete(
        _ model: DownloadedModel, ownsLease: Bool = false
    ) async throws {
        // Evict first. Deleting the files under a loaded model leaves the app
        // generating happily from memory while its folder is gone — and the
        // next launch silently re-downloads with no explanation.
        let request = ModelDeletionRequest(url: model.url)
        NotificationCenter.default.post(name: didDeleteModel, object: request)
        await request.performEvictions()
        let lease: ModelOperationLease?
        if ownsLease {
            lease = nil
        } else {
            lease = try ModelOperationLease(
                modelsRoot: ModelsLocation.current(),
                operation: "models.remove")
        }
        try FileManager.default.trashItem(at: model.url, resultingItemURL: nil)
        withExtendedLifetime(lease) {}
    }

    // MARK: Helpers

    private static func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true
    }

    /// Bytes a model folder occupies. Internal rather than private because
    /// Doctor sizes the same folders to work out how much memory a run will
    /// want, and a third copy of this walk is a third thing to keep in step.
    public nonisolated static func size(of dir: URL) -> Int64 {
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileSizeKey]
        guard let files = FileManager.default.enumerator(
            at: dir, includingPropertiesForKeys: keys
        ) else { return 0 }
        var total: Int64 = 0
        for case let url as URL in files {
            let values = try? url.resourceValues(forKeys: Set(keys))
            total += Int64(values?.totalFileAllocatedSize ?? values?.fileSize ?? 0)
        }
        return total
    }

    /// Logical file coverage for byte-progress accounting. Unlike allocated
    /// size, this matches HTTP Content-Length and never adds filesystem block
    /// padding when a batch advances to its next model.
    public nonisolated static func logicalSize(of dir: URL) -> Int64 {
        let keys: Set<URLResourceKey> = [.fileSizeKey, .isRegularFileKey]
        guard let files = FileManager.default.enumerator(
            at: dir, includingPropertiesForKeys: Array(keys)
        ) else { return 0 }
        var total: Int64 = 0
        for case let url as URL in files {
            let values = try? url.resourceValues(forKeys: keys)
            if values?.isRegularFile == true {
                total += Int64(values?.fileSize ?? 0)
            }
        }
        return total
    }
}
