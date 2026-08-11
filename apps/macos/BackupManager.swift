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

//
//  BackupManager.swift
//  Bunyi
//
//  Backs up the models folder to a single zip and restores from one.
//  Sandbox shapes both directions. The default container folder is zipped
//  with `zip -0` (stored, no compression) so it's fast — model weights are
//  already-incompressible safetensors — and the output size tracks the
//  source, which drives a real progress bar. A custom (bookmarked) folder
//  isn't reachable by a child process, so it falls back to NSFileCoordinator
//  (in-process, no progress). Restore extracts with /usr/bin/ditto on
//  container-local temp paths, then merges in-process.
//

import Foundation

@MainActor
@Observable
final class BackupManager {
    enum Status: Equatable {
        case idle
        case working(String)
        /// Stop was pressed and the child process is being torn down. A
        /// distinct state because the work does not end the instant Stop is
        /// pressed: `zip` has to die, a partial archive has to be removed, and
        /// on a 23 GB backup that gap was long enough that nothing appeared to
        /// happen — so Stop got pressed again. The engine already models this
        /// for generation (`EngineStatus.stopping`); backup did not.
        case stopping
        case done(String)
        case error(String)

        var isBusy: Bool {
            switch self {
            case .working, .stopping: true
            default: false
            }
        }

        /// Whether Stop can still do anything. False once stopping, so the
        /// button can be disabled rather than inviting a second press that
        /// only logs a second "Stopping…".
        var canCancel: Bool {
            if case .working = self { return true }
            return false
        }
    }

    var status: Status = .idle
    /// 0...1 while zipping when a total size is known; nil = indeterminate.
    var progress: Double?

    private let log = LogStore.shared
    private var currentTask: Task<Void, Never>?
    /// The running progress poller, held so `cancel()` can stop it reporting.
    private var sizeMonitor: Task<Void, Never>?
    /// Lets `cancel()` (main actor) terminate the child zip/ditto process
    /// created inside a detached task.
    private let running = RunningProcess()

    func cancel() {
        guard status.canCancel else { return }
        log.log("Stopping…")
        status = .stopping
        // The size monitor polls a directory the child process is in the
        // middle of abandoning, so left running it reports the archive
        // shrinking — "Archiving 0% — Zero kB" moments after Stop, which reads
        // as the backup starting over rather than ending. It is an unstructured
        // Task, so cancelling `currentTask` does not reach it; it has to be
        // cancelled by hand.
        sizeMonitor?.cancel()
        sizeMonitor = nil
        progress = nil
        running.cancel()
        currentTask?.cancel()
    }

    // MARK: Backup

    func startBackup(to destination: URL) {
        currentTask?.cancel()
        running.reset()
        currentTask = Task { await self.runBackup(to: destination) }
    }

    private func runBackup(to destination: URL) async {
        let source = ModelsLocation.current()
        let modelsSubdir = source.appendingPathComponent("models", isDirectory: true)
        let fastPath = !ModelsLocation.isCustom
        // zip builds into its own working file (ziXXXXXX) in the output's
        // directory and only renames to the final name at the end, so give
        // it a dedicated dir and monitor the whole dir, not the final name.
        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("backup-\(UUID().uuidString)", isDirectory: true)
        let tempZip = tempDir.appendingPathComponent("models.zip")

        status = .working("Creating archive…")
        progress = nil
        log.log("Backing up models from \(source.path)")

        do {
            if fastPath {
                try FileManager.default.createDirectory(
                    at: tempDir, withIntermediateDirectories: true)
                let total = Self.directorySize(modelsSubdir)
                log.log("Creating \(destination.lastPathComponent) — "
                    + "\(total.formatted(.byteCount(style: .file))) to archive "
                    + "(stored, no compression)")
                let monitor = startSizeMonitor(total: total, label: "Archiving") {
                    Self.directorySize(tempDir)
                }
                do {
                    try await Task.detached(priority: .utility) { [running] in
                        try Self.createZipStored(
                            sourceFolder: source, to: tempZip, control: running)
                    }.value
                } catch {
                    monitor.cancel()
                    throw error
                }
                monitor.cancel()
                try Task.checkCancellation()

                // The child zip built inside the container (it can't write to
                // the sandbox-granted destination directly). Move it out — but
                // OFF the main thread, or a slow save freezes the UI. On the
                // same volume that's an instant rename; on a different volume
                // (network/external drive) it's a real multi-GB copy, so stream
                // it with a progress bar and Stop support.
                status = .working("Saving…")
                progress = nil
                if Self.sameVolume(tempZip, destination) {
                    try await Task.detached(priority: .utility) {
                        let fm = FileManager.default
                        if fm.fileExists(atPath: destination.path) {
                            try fm.removeItem(at: destination)
                        }
                        try fm.moveItem(at: tempZip, to: destination)
                    }.value
                } else {
                    let bytes = Self.fileSize(tempZip)
                    log.log("Saving to another drive — copying "
                        + "\(bytes.formatted(.byteCount(style: .file)))")
                    let saveMonitor = startSizeMonitor(total: bytes, label: "Saving") {
                        Self.fileSize(destination)
                    }
                    do {
                        try await Task.detached(priority: .utility) { [running] in
                            try Self.copyFile(
                                from: tempZip, to: destination, control: running)
                        }.value
                    } catch {
                        saveMonitor.cancel()
                        throw error
                    }
                    saveMonitor.cancel()
                }
            } else {
                log.log("Creating \(destination.lastPathComponent) — this can "
                    + "take a few minutes (custom folder: no progress, and "
                    + "Stop can't interrupt archiving mid-file)")
                try await Task.detached(priority: .utility) {
                    try Self.createZipCoordinated(of: source, to: destination)
                }.value
            }

            try? FileManager.default.removeItem(at: tempDir)
            progress = nil
            let size = (try? destination.resourceValues(forKeys: [.fileSizeKey])
                .fileSize).map { Int64($0).formatted(.byteCount(style: .file)) }
                ?? "unknown size"
            log.log("Backup finished: \(destination.path) (\(size))")
            status = .done("Backed up \(size) to \(destination.lastPathComponent)")
        } catch is CancellationError {
            try? FileManager.default.removeItem(at: tempDir)
            if fastPath { try? FileManager.default.removeItem(at: destination) }
            progress = nil
            log.log("Backup cancelled")
            status = .idle
        } catch {
            try? FileManager.default.removeItem(at: tempDir)
            progress = nil
            log.log("Backup failed: \(String(describing: error))")
            status = .error(error.localizedDescription)
        }
    }

    /// Polls a growing output so a determinate bar can show real progress —
    /// the zip's working dir while archiving, or the destination file while
    /// copying to another volume. `measure` returns bytes written so far.
    /// Starts a poller, and records it as the one `cancel()` should stop.
    ///
    /// Recorded here rather than by the caller, because leaving it to callers
    /// is exactly what went wrong: only the archiving monitor was stored, so
    /// pressing Stop during the cross-volume "Saving…" copy left that monitor
    /// running — still logging, and still writing `progress` after `cancel()`
    /// had cleared it. Doing it here means any monitor added later is tracked
    /// without anyone remembering to.
    private func startSizeMonitor(
        total: Int64, label: String, measure: @escaping @Sendable () -> Int64
    ) -> Task<Void, Never> {
        let task = Task { [weak self, log] in
            guard total > 0 else { return }
            var lastDecile = -1
            while !Task.isCancelled {
                let size = measure()
                let fraction = min(Double(size) / Double(total), 1.0)
                self?.progress = fraction
                let decile = Int(fraction * 10)
                if decile != lastDecile {
                    lastDecile = decile
                    log.log("\(label) \(Int(fraction * 100))% — "
                        + "\(size.formatted(.byteCount(style: .file)))")
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
        sizeMonitor = task
        return task
    }

    nonisolated private static func createZipStored(
        sourceFolder: URL, to zip: URL, control: RunningProcess
    ) throws {
        let errorPipe = Pipe()
        let process = try control.start {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
            p.currentDirectoryURL = sourceFolder
            // -0 store, -r recurse, -q quiet, -X drop extra attrs.
            p.arguments = ["-0", "-r", "-q", "-X", zip.path, "models"]
            p.standardError = errorPipe
            return p
        }
        try process.run()
        process.waitUntilExit()
        control.finish()
        guard process.terminationStatus == 0 else {
            if control.wasCancelled { throw CancellationError() }
            let message = String(
                data: errorPipe.fileHandleForReading.readDataToEndOfFile(),
                encoding: .utf8) ?? ""
            throw BackupError.zipFailed(message)
        }
    }

    /// NSFileCoordinator with .forUploading yields a zipped temp copy of
    /// the directory; the copy must happen inside the accessor block.
    nonisolated private static func createZipCoordinated(
        of source: URL, to destination: URL
    ) throws {
        let coordinator = NSFileCoordinator()
        var coordinationError: NSError?
        var copyError: Error?
        coordinator.coordinate(readingItemAt: source, options: .forUploading,
                               error: &coordinationError) { zipURL in
            do {
                let fm = FileManager.default
                if fm.fileExists(atPath: destination.path) {
                    try fm.removeItem(at: destination)
                }
                try fm.copyItem(at: zipURL, to: destination)
            } catch {
                copyError = error
            }
        }
        if let coordinationError { throw coordinationError }
        if let copyError { throw copyError }
    }

    // MARK: Restore

    func startRestore(from zipURL: URL) {
        currentTask?.cancel()
        running.reset()
        currentTask = Task { await self.runRestore(from: zipURL) }
    }

    private func runRestore(from zipURL: URL) async {
        let target = ModelsLocation.current()
        status = .working("Restoring…")
        progress = nil
        log.log("Restoring models from \(zipURL.lastPathComponent)")
        do {
            let result = try await Task.detached(priority: .utility) { [running] in
                try Self.performRestore(zip: zipURL, into: target, control: running)
            }.value
            for name in result.restored { log.log("Restored \(name)") }
            for name in result.skipped { log.log("Skipped \(name) (already present)") }
            let summary = result.restored.isEmpty
                ? "Nothing to restore — all models in the backup are already present"
                : "Restored \(result.restored.count) model\(result.restored.count == 1 ? "" : "s")"
            log.log(summary)
            status = .done(summary)
        } catch is CancellationError {
            log.log("Restore cancelled")
            status = .idle
        } catch {
            log.log("Restore failed: \(String(describing: error))")
            status = .error(error.localizedDescription)
        }
    }

    private struct RestoreResult: Sendable {
        var restored: [String] = []
        var skipped: [String] = []
    }

    nonisolated private static func performRestore(
        zip: URL, into target: URL, control: RunningProcess
    ) throws -> RestoreResult {
        let fm = FileManager.default
        let tmp = fm.temporaryDirectory
            .appendingPathComponent("restore-\(UUID().uuidString)", isDirectory: true)
        defer { try? fm.removeItem(at: tmp) }
        let extractDir = tmp.appendingPathComponent("extract", isDirectory: true)
        try fm.createDirectory(at: extractDir, withIntermediateDirectories: true)

        // Copy the zip into the container so ditto (which only inherits the
        // plain sandbox, not the open-panel grant) can read it.
        let localZip = tmp.appendingPathComponent("backup.zip")
        let gotAccess = zip.startAccessingSecurityScopedResource()
        defer { if gotAccess { zip.stopAccessingSecurityScopedResource() } }
        try fm.copyItem(at: zip, to: localZip)
        try extractZip(localZip, to: extractDir, control: control)

        guard let modelsRoot = findModelsRoot(in: extractDir) else {
            throw BackupError.notAModelsBackup
        }

        let targetModels = target.appendingPathComponent("models", isDirectory: true)
        var result = RestoreResult()
        for org in try subdirectories(of: modelsRoot) {
            try Task.checkCancellation()
            let targetOrg = targetModels
                .appendingPathComponent(org.lastPathComponent, isDirectory: true)
            try fm.createDirectory(at: targetOrg, withIntermediateDirectories: true)
            for repo in try subdirectories(of: org) {
                let name = "\(org.lastPathComponent)/\(repo.lastPathComponent)"
                let dest = targetOrg
                    .appendingPathComponent(repo.lastPathComponent, isDirectory: true)
                if fm.fileExists(atPath: dest.path) {
                    result.skipped.append(name)
                } else {
                    try fm.moveItem(at: repo, to: dest)
                    result.restored.append(name)
                }
            }
        }
        return result
    }

    nonisolated private static func extractZip(
        _ zip: URL, to dir: URL, control: RunningProcess
    ) throws {
        let errorPipe = Pipe()
        let process = try control.start {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            p.arguments = ["-xk", zip.path, dir.path]
            p.standardError = errorPipe
            return p
        }
        try process.run()
        process.waitUntilExit()
        control.finish()
        guard process.terminationStatus == 0 else {
            if control.wasCancelled { throw CancellationError() }
            let message = String(
                data: errorPipe.fileHandleForReading.readDataToEndOfFile(),
                encoding: .utf8) ?? ""
            throw BackupError.extractFailed(message)
        }
    }

    /// The zip's root varies ("models/…" if the user zipped that level,
    /// "Models/models/…" from our own backups), so find the shallowest
    /// directory literally named "models".
    nonisolated private static func findModelsRoot(in dir: URL) -> URL? {
        var queue: [(URL, Int)] = [(dir, 0)]
        while !queue.isEmpty {
            let (url, depth) = queue.removeFirst()
            if depth > 0, url.lastPathComponent == "models" { return url }
            guard depth < 3 else { continue }
            for sub in (try? subdirectories(of: url)) ?? [] {
                queue.append((sub, depth + 1))
            }
        }
        return nil
    }

    nonisolated private static func subdirectories(of dir: URL) throws -> [URL] {
        try FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ).filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
    }

    // MARK: Sizes

    nonisolated private static func directorySize(_ dir: URL) -> Int64 {
        var total: Int64 = 0
        let keys: [URLResourceKey] = [.fileSizeKey, .totalFileAllocatedSizeKey]
        if let e = FileManager.default.enumerator(
            at: dir, includingPropertiesForKeys: keys) {
            for case let url as URL in e {
                let v = try? url.resourceValues(forKeys: Set(keys))
                total += Int64(v?.fileSize ?? v?.totalFileAllocatedSize ?? 0)
            }
        }
        return total
    }

    nonisolated private static func fileSize(_ url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
    }

    /// Whether both paths live on the same volume, so a move is a rename.
    /// `b` may not exist yet, so its parent directory is checked. Unknown →
    /// false, which routes through the streamed copy (correct either way).
    nonisolated private static func sameVolume(_ a: URL, _ b: URL) -> Bool {
        let va = try? a.resourceValues(forKeys: [.volumeIdentifierKey])
            .volumeIdentifier
        let vb = try? b.deletingLastPathComponent()
            .resourceValues(forKeys: [.volumeIdentifierKey]).volumeIdentifier
        guard let va, let vb else { return false }
        return va.isEqual(vb)
    }

    /// Streamed copy so a slow cross-volume save (network/external drive) runs
    /// off the main thread with progress and honors Stop. Writes straight to
    /// the destination path, which the size monitor polls.
    nonisolated private static func copyFile(
        from src: URL, to dst: URL, control: RunningProcess
    ) throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: dst.path) { try fm.removeItem(at: dst) }
        guard fm.createFile(atPath: dst.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        let input = try FileHandle(forReadingFrom: src)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: dst)
        defer { try? output.close() }
        let chunk = 8 * 1024 * 1024
        while true {
            if control.wasCancelled {
                try? output.close()
                try? fm.removeItem(at: dst)
                throw CancellationError()
            }
            let data = try input.read(upToCount: chunk) ?? Data()
            if data.isEmpty { break }
            try output.write(contentsOf: data)
        }
    }
}

/// Thread-safe holder so a cancel on the main actor can terminate a child
/// process started inside a detached task.
final class RunningProcess: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private(set) var wasCancelled = false

    func reset() {
        lock.lock(); defer { lock.unlock() }
        process = nil
        wasCancelled = false
    }

    func start(_ make: () -> Process) throws -> Process {
        lock.lock(); defer { lock.unlock() }
        if wasCancelled { throw CancellationError() }
        let p = make()
        process = p
        return p
    }

    func finish() {
        lock.lock(); defer { lock.unlock() }
        process = nil
    }

    func cancel() {
        lock.lock()
        wasCancelled = true
        let p = process
        lock.unlock()
        p?.terminate()
    }
}

enum BackupError: LocalizedError {
    case notAModelsBackup
    case zipFailed(String)
    case extractFailed(String)

    var errorDescription: String? {
        switch self {
        case .notAModelsBackup:
            "That zip doesn't look like a models backup — no models folder inside."
        case .zipFailed(let detail):
            "Couldn't create the archive. \(detail)"
        case .extractFailed(let detail):
            "Couldn't unpack the backup. \(detail)"
        }
    }
}
