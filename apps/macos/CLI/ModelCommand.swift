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

@MainActor
enum ModelCommand {
    static func run(_ request: CLIRequest, output: CLIOutput,
                    cancelled: CancellationState) async throws -> CLIMessage {
        if request.has("require-server") {
            throw serverUnavailable()
        }
        switch request.operation {
        case "models.list":
            return list(request)
        case "models.status":
            return status(request)
        case "models.download":
            if request.has("detach") { throw serverUnavailable() }
            return try await download(request, output: output, cancelled: cancelled)
        case "models.verify":
            if request.has("detach") { throw serverUnavailable() }
            return try await verify(request, cancelled: cancelled)
        case "models.remove":
            return try remove(request)
        default:
            throw CLICommandParser.invalid("Unknown models command.")
        }
    }

    private static func list(_ request: CLIRequest) -> CLIMessage {
        let configured = Dictionary(uniqueKeysWithValues: TTSMode.allCases.map {
            (TTSEngine.modelDirectory(for: $0).standardizedFileURL, $0)
        })
        let models: [CLIMessage] = ModelStore.all().map { model in
            var value: CLIMessage = [
                "name": model.name,
                "folder": model.url.path,
                "bytes": model.byteCount,
                "selfHosted": model.isSelfHosted,
            ]
            if let mode = configured[model.url.standardizedFileURL] {
                value["mode"] = modeName(mode)
                value["complete"] = TTSEngine.isModelComplete(for: mode)
            }
            return value
        }
        return CLIProtocol.result(request, ["models": models])
    }

    private static func status(_ request: CLIRequest) -> CLIMessage {
        let modes = request.value("mode").map { [mode($0)] } ?? TTSMode.allCases
        return CLIProtocol.result(request, [
            "models": modes.map(modelStatus),
            "modelsRoot": ModelsLocation.current().path,
        ])
    }

    private static func download(
        _ request: CLIRequest, output: CLIOutput, cancelled: CancellationState
    ) async throws -> CLIMessage {
        let modes = request.has("all")
            ? TTSMode.allCases
            : [mode(request.value("mode")!)]
        let engine = TTSEngine()
        output.event(CLIProtocol.event(
            request, type: "resolving",
            ["detail": "Resolving \(modes.count) model source\(modes.count == 1 ? "" : "s")"]))

        let planning = Task { @MainActor in
            try await engine.planModelDownloads(modes)
        }
        let planningCancellation = Task {
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(100))
            }
            if cancelled.value { planning.cancel() }
        }
        defer { planningCancellation.cancel() }
        let plans: [ModelDownloadPlan]
        do {
            plans = try await planning.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Model download was cancelled.", exitCode: 5)
        } catch let error as URLError where error.code == .cancelled {
            throw CLIError("cancelled", "Model download was cancelled.", exitCode: 5)
        } catch let error as DownloadServiceError {
            throw downloadFailure(error)
        } catch {
            throw CLIError("download_failed", error.localizedDescription, exitCode: 10)
        }
        planningCancellation.cancel()
        try checkAggregateDiskSpace(for: plans)
        let knownTotal: Int64? = plans.allSatisfy { $0.totalBytes != nil }
            ? plans.reduce(Int64(0)) { $0 + ($1.totalBytes ?? 0) }
            : nil
        output.event(CLIProtocol.event(request, type: "sizing", [
            "bytesCompleted": plans.reduce(Int64(0)) {
                $0 + ($1.complete ? $1.downloadedBytes : 0)
            },
            "bytesTotal": knownTotal ?? NSNull(),
            "detail": "Download plan is ready",
        ]))
        if cancelled.value {
            throw CLIError("cancelled", "Model download was cancelled.", exitCode: 5)
        }

        let operation = Task { @MainActor in
            try await engine.downloadModels(modes)
        }
        let monitor = Task { @MainActor in
            var previous = ""
            while !Task.isCancelled {
                if cancelled.value {
                    operation.cancel()
                    engine.stop()
                }
                if let event = progress(
                    engine, request: request, totalBytes: knownTotal) {
                    let type = String(describing: event["type"] ?? "")
                    let key = "\(type)-\(engine.downloadItemIndex)"
                    if key != previous || type == "downloading" || type == "waiting" {
                        output.event(event)
                        previous = key
                    }
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
        defer {
            monitor.cancel()
            engine.unload(reason: "model command finished")
        }

        let directories: [URL]
        do {
            directories = try await operation.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Model download was cancelled.", exitCode: 5)
        } catch let error as URLError where error.code == .cancelled {
            throw CLIError("cancelled", "Model download was cancelled.", exitCode: 5)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "Another Bunyi process is using this models folder.",
                exitCode: 4)
        } catch let error as DownloadServiceError {
            throw downloadFailure(error)
        } catch {
            throw CLIError("download_failed", error.localizedDescription, exitCode: 10)
        }

        let assets: [CLIMessage] = zip(modes, directories).map { mode, directory in
            [
                "id": modeName(mode),
                "source": mode.effectiveRepoID,
                "folder": directory.path,
                "bytes": ModelStore.size(of: directory),
                "complete": TTSEngine.isModelComplete(for: mode),
            ]
        }
        return CLIProtocol.result(request, [
            "assets": assets,
            "offlineReady": assets.allSatisfy { $0["complete"] as? Bool == true },
            // An empty list is meaningful: macOS uses the operating system's
            // on-device Speech service and has no fourth model to download.
            "transcriptionAssets": [CLIMessage](),
        ])
    }

    private static func verify(
        _ request: CLIRequest, cancelled: CancellationState
    ) async throws -> CLIMessage {
        let mode = mode(request.value("mode")!)
        let directory = TTSEngine.modelDirectory(for: mode)
        guard TTSEngine.isModelComplete(for: mode) else {
            throw CLIError(
                "missing_input", "\(displayName(mode)) is not downloaded completely.",
                exitCode: 3)
        }
        let engine = TTSEngine()
        let entries: [TTSEngine.ManifestEntry]?
        do {
            entries = try await engine.publishedDigests(for: mode)
        } catch {
            throw CLIError(
                "verification_failed", error.localizedDescription, exitCode: 10)
        }
        let checks = entries?.compactMap { entry -> (String, String)? in
            guard let digest = entry.sha256 else { return nil }
            return (entry.path, digest)
        } ?? []
        let bad: [String]
        do {
            bad = try await Task.detached(priority: .utility) {
                var failures: [String] = []
                for (path, digest) in checks {
                    guard !cancelled.value else { throw CancellationError() }
                    let file = directory.appendingPathComponent(path)
                    do {
                        let actual = try HTTPFileDownloader.sha256Hex(
                            of: file, shouldContinue: { !cancelled.value })
                        if actual != digest { failures.append(path) }
                    } catch is CancellationError {
                        throw CancellationError()
                    } catch {
                        failures.append(path)
                    }
                }
                return failures
            }.value
        } catch is CancellationError {
            throw CLIError(
                "cancelled", "Model verification was cancelled.", exitCode: 5)
        }
        guard bad.isEmpty else {
            throw CLIError(
                "model_verification_failed",
                "\(bad.count) model file\(bad.count == 1 ? "" : "s") failed checksum verification.",
                exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "mode": modeName(mode),
            "folder": directory.path,
            "complete": true,
            "verified": true,
            "checksumCount": checks.count,
            "checksumsAvailable": !checks.isEmpty,
        ])
    }

    private static func remove(_ request: CLIRequest) throws -> CLIMessage {
        let mode = mode(request.value("mode")!)
        let directory = TTSEngine.modelDirectory(for: mode)
        guard FileManager.default.fileExists(atPath: directory.path) else {
            throw CLIError(
                "missing_input", "\(displayName(mode)) is not downloaded.", exitCode: 3)
        }
        let source = mode.effectiveSource
        let model = DownloadedModel(
            url: directory,
            name: mode.effectiveRepoID,
            byteCount: ModelStore.size(of: directory),
            isSelfHosted: {
                if case .baseURL = source { return true }
                return false
            }())
        do {
            try ModelStore.delete(model)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "The model is loaded or another Bunyi process is using this models folder.",
                exitCode: 4)
        } catch {
            throw CLIError("remove_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, [
            "mode": modeName(mode),
            "removedFolder": directory.path,
            "recoverable": true,
        ])
    }

    private static func progress(_ engine: TTSEngine,
                                 request: CLIRequest,
                                 totalBytes: Int64?) -> CLIMessage? {
        guard let mode = engine.downloadMode else { return nil }
        let item: CLIMessage = [
            "id": modeName(mode),
            "index": engine.downloadItemIndex,
            "count": engine.downloadItemCount,
        ]
        guard let progress = engine.downloadFeedback else {
            return CLIProtocol.event(request, type: "checking", [
                "item": item,
                "bytesCompleted": engine.aggregateDownloadCompleted,
                "bytesTotal": totalBytes ?? NSNull(),
                "detail": "Checking \(displayName(mode))",
            ])
        }
        let type: String
        switch progress.phase {
        case .checking: type = "checking"
        case .sizing: type = "sizing"
        case .downloading, .reconnecting: type = "downloading"
        case .verifying: type = "verifying"
        case .waiting: type = "waiting"
        }
        var fields: CLIMessage = [
            "item": item,
            "bytesCompleted": engine.aggregateDownloadCompleted + progress.available,
            "bytesTotal": totalBytes ?? NSNull(),
            "rateBytesPerSecond": progress.rate > 0 ? progress.rate : NSNull(),
            "etaSeconds": NSNull(),
            "currentFile": progress.file ?? NSNull(),
            "detail": progress.title,
        ]
        if progress.phase == .waiting {
            fields["retryAt"] = progress.retryAt.map(CLIProtocol.timestamp) ?? NSNull()
            fields["retryAfterSeconds"] = progress.retryAt.map {
                max(0, Int(ceil($0.timeIntervalSinceNow)))
            } ?? NSNull()
            fields["host"] = progress.retryHost ?? NSNull()
            fields["retryAttempt"] = progress.retryAttempt
        }
        return CLIProtocol.event(request, type: type, fields)
    }

    private static func modelStatus(_ mode: TTSMode) -> CLIMessage {
        let directory = TTSEngine.modelDirectory(for: mode)
        let inspection = inspect(directory)
        return [
            "id": modeName(mode),
            "source": mode.effectiveRepoID,
            "folder": directory.path,
            "complete": TTSEngine.isModelComplete(for: mode),
            "missingFiles": inspection.missing,
            "partialFiles": inspection.partials,
            "downloadedBytes": ModelStore.logicalSize(of: directory),
            "approximateSizeBytes": Int64(mode.approxDownloadBytes),
            "loaded": false,
        ]
    }

    private static func inspect(_ directory: URL)
        -> (missing: [String], partials: [String]) {
        let fm = FileManager.default
        var missing: [String] = []
        if !fm.fileExists(atPath: directory.appendingPathComponent("config.json").path) {
            missing.append("config.json")
        }
        let top = (try? fm.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: nil)) ?? []
        if !top.contains(where: { $0.pathExtension == "safetensors" }) {
            missing.append("model.safetensors")
        }
        let index = directory.appendingPathComponent("model.safetensors.index.json")
        if let data = try? Data(contentsOf: index),
           let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let weights = root["weight_map"] as? [String: String] {
            for shard in Set(weights.values).sorted()
            where !fm.fileExists(atPath: directory.appendingPathComponent(shard).path) {
                missing.append(shard)
            }
        }
        var partials: [String] = []
        if let enumerator = fm.enumerator(at: directory, includingPropertiesForKeys: nil) {
            for case let url as URL in enumerator where url.pathExtension == "incomplete" {
                partials.append(String(url.path.dropFirst(directory.path.count + 1)))
            }
        }
        return (missing.sorted(), partials.sorted())
    }

    private static func checkAggregateDiskSpace(
        for plans: [ModelDownloadPlan]
    ) throws {
        let needed = plans.reduce(Int64(0)) { total, plan in
            guard !plan.complete else { return total }
            let fullSize = plan.totalBytes
                ?? Int64(plan.mode.approxDownloadBytes)
            return total + max(0, fullSize - plan.downloadedBytes)
        }
        guard needed > 0 else { return }
        let root = ModelsLocation.current()
        let free = try root.resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            .volumeAvailableCapacityForImportantUsage
        guard let free, free >= needed else {
            let available = free?.formatted(.byteCount(style: .file)) ?? "unknown"
            let required = needed.formatted(.byteCount(style: .file))
            throw CLIError(
                "insufficient_disk_space",
                "The models need about \(required), but \(available) is available.",
                exitCode: 3)
        }
    }

    static func mode(_ value: String) -> TTSMode {
        switch value {
        case "design": .voiceDesign
        case "clone": .voiceClone
        default: .presetVoice
        }
    }

    static func modeName(_ mode: TTSMode) -> String {
        switch mode {
        case .presetVoice: "preset"
        case .voiceDesign: "design"
        case .voiceClone: "clone"
        }
    }

    private static func displayName(_ mode: TTSMode) -> String {
        mode.rawValue
    }

    private static func serverUnavailable() -> CLIError {
        CLIError(
            "server_unavailable",
            "No Bunyi server is available. Run bunyi server start first.",
            exitCode: 4)
    }

    private static func downloadFailure(_ error: DownloadServiceError) -> CLIError {
        let code: String
        switch error {
        case .unavailable: code = "download_service_unavailable"
        case .rateLimited: code = "download_rate_limited"
        case .httpStatus: code = "download_failed"
        }
        return CLIError(code, error.localizedDescription, exitCode: 10)
    }
}
