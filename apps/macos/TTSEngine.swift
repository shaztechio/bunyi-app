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
//  TTSEngine.swift
//  Bunyi
//
//  Observable app/CLI adapter over MLXTtsRuntime. It coordinates downloads,
//  progress, output metadata, and UI state while the runtime actor exclusively
//  owns and serializes access to the non-Sendable Qwen model.
//

import AVFoundation
import Foundation
import MLX
import Qwen3TTS

// MARK: - Modes

public enum TTSMode: String, CaseIterable, Identifiable, Sendable {
    case presetVoice = "Preset voice"
    case voiceDesign = "Voice design"
    case voiceClone = "Voice clone"

    public var id: String { rawValue }

    /// Hugging Face repo backing each mode.
    /// Swap the CustomVoice repo for
    /// "AtomGradient/Qwen3-TTS-0.6B-CustomVoice-4bit-pruned-vocab-lite"
    /// (808 MB) if download size matters more than max fidelity.
    public var repoID: String {
        switch self {
        case .presetVoice: "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16"
        case .voiceDesign: "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16"
        case .voiceClone:  "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16"
        }
    }

    /// Rough total download size, used only for throughput and ETA
    /// estimates in the log — the Hub API only reports a fraction.
    public var approxDownloadBytes: Double {
        switch self {
        case .presetVoice: 1.4e9
        case .voiceDesign: 3.4e9
        case .voiceClone:  3.4e9
        }
    }
}

// MARK: - Engine state

public enum EngineStatus: Equatable {
    case idle
    case downloading(Double)      // 0...1
    case checking
    case finalizing
    case loading
    case transcribing             // auto-transcribing the clone reference
    case generating(Int)          // codec tokens emitted so far
    /// Cancelled, but the inference worker has not reached its next frame
    /// boundary yet. Reporting idle here would re-enable Generate while the
    /// previous worker can still touch the shared model.
    case stopping
    case error(String)

    public var isBusy: Bool {
        switch self {
        case .idle, .error: false
        default: true
        }
    }
}

public struct GenerationSummary: Sendable {
    public let outputURL: URL
    public let mode: TTSMode
    public let durationSeconds: Double
    public let frames: Int
    public let elapsedSeconds: Double
    public let modelSource: String
    public let metadata: OutputMetadata
}

public struct RuntimeFailure: Sendable {
    public let code: String
    public let message: String
    public let exitCode: Int32
}

public struct ModelDownloadPlan: Sendable {
    public let mode: TTSMode
    public let totalBytes: Int64?
    public let downloadedBytes: Int64
    public let complete: Bool
}

public enum ModelDownloadPurpose: Sendable, Equatable {
    case voice
    case transcription
}

// MARK: - Engine

@MainActor
@Observable
public final class TTSEngine {
    public var status: EngineStatus = .idle
    /// Section/attempt context for long generation. The status enum retains
    /// the aggregate frame count used by existing callers.
    public var generationDetail: String?
    public var lastOutputURL: URL?
    public private(set) var lastGenerationSummary: GenerationSummary?
    public private(set) var lastFailure: RuntimeFailure?

    /// Forgets the previous run's file so the UI stops offering it. Called when
    /// a new run starts: the old audio is still on disk and still reachable in
    /// the Outputs folder, but presenting Play beside a run in progress invites
    /// listening to the previous result and taking it for the new one.
    public func clearLastOutput() {
        lastOutputURL = nil
    }

    /// The Outputs folder itself, for History and "Show in Finder".
    public var outputsFolder: URL { outputDir }

    /// Stamped into every generated file so a WAV says which build made it.
    public static var appVersion: String {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "0.0.0"
        let build = info?["CFBundleVersion"] as? String ?? "0"
        return build == short ? short : "\(short) (\(build))"
    }

    /// Everything generated so far, newest first. Read from disk rather than
    /// tracked in memory: the folder is the record, it survives relaunches, and
    /// a file deleted in Finder should disappear from History without the app
    /// needing to be told.
    public func generatedOutputs() -> [GeneratedOutput] {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .fileSizeKey]
        let files = (try? FileManager.default.contentsOfDirectory(
            at: outputDir,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        )) ?? []

        return files
            .filter { $0.pathExtension.lowercased() == "wav" }
            .map { url in
                let values = try? url.resourceValues(forKeys: Set(keys))
                return GeneratedOutput(
                    url: url,
                    created: values?.contentModificationDate ?? .distantPast,
                    byteCount: Int64(values?.fileSize ?? 0)
                )
            }
            .sorted { $0.created > $1.created }
    }
    /// Human-readable download detail ("42% — about 3.1 MB/s, ~6 min left").
    public var downloadDetail: String?
    public var downloadFeedback: ModelDownloadProgress?
    public private(set) var downloadPurpose: ModelDownloadPurpose = .voice
    public var downloadRecovery: DownloadRecoveryOffer?
    /// Batch-download context consumed by the CLI's aggregate progress stream.
    public private(set) var downloadMode: TTSMode?
    public private(set) var downloadItemIndex = 0
    public private(set) var downloadItemCount = 0
    public private(set) var aggregateDownloadCompleted: Int64 = 0
    private var activeModelTransfer: ModelFileTransfer?
    private var activeWhisperDownload: WhisperModelDownload?
    public func reconnectDownload() {
        guard downloadFeedback?.phase == .downloading else { return }
        activeModelTransfer?.requestReconnect()
        activeWhisperDownload?.requestReconnect()
        downloadFeedback?.phase = .reconnecting
    }
    public func clearDownloadRecovery() { downloadRecovery = nil }
    /// Transcript produced by auto-transcription, so the UI can show it and
    /// save it with the voice instead of storing an empty string.
    public var lastReferenceTranscript: String?
    /// Leading audio window covered by `lastReferenceTranscript`. Nil means a
    /// provided transcript covers the full clip.
    public var lastReferenceTranscriptAudioSeconds: TimeInterval?

    private var loadedRepo: String?
    /// Where the loaded model was read from. Compared against a deletion rather
    /// than matching repo-ID strings, which differ in shape between a Hub repo
    /// and a self-hosted base URL.
    private var loadedDir: URL?
    private let runtime = MLXTtsRuntime()
    private var modelInfo: MLXTtsModelInfo?
    /// Retained for as long as a model is resident. The kernel releases this
    /// lease if the process crashes, while a clean unload releases it eagerly.
    private var modelLease: ModelOperationLease?

    private let log = LogStore.shared

    public var loadedMode: TTSMode? {
        guard let loadedRepo else { return nil }
        return TTSMode.allCases.first { $0.effectiveRepoID == loadedRepo }
    }

    public var loadedModelFolder: String? { loadedDir?.path }

    private struct HubTreeEntry: Decodable {
        let type: String
        let path: String
    }

    /// Output lives in the app's own Application Support folder: the sandbox
    /// grants it without extra entitlements (unlike ~/Music), and "Show in
    /// Finder" still surfaces the files.
    private let outputDir: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory,
                                           in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi/Outputs", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    public var speakers: [String] { modelInfo?.supportedSpeakers ?? [] }

    /// Held so the observer can be removed. NotificationCenter keeps the token
    /// alive until it is, so dropping it on the floor leaks the registration
    /// and stacks up another unload attempt for every engine ever created.
    /// nonisolated so `deinit`, which is not main-actor isolated, can read it.
    /// Only ever written once during init and read once during deinit.
    @ObservationIgnored
    private nonisolated(unsafe) var deletionObserver: (any NSObjectProtocol)?

    public init() {
        // Settings can delete a model while it is loaded. Without this the app
        // keeps generating from memory with its folder gone, and the next
        // launch re-downloads with nothing having explained why.
        deletionObserver = NotificationCenter.default.addObserver(
            forName: ModelStore.didDeleteModel, object: nil, queue: .main
        ) { [weak self] note in
            guard let request = note.object as? ModelDeletionRequest else { return }
            request.register { @MainActor [weak self] in
                await self?.forgetModel(at: request.url)
            }
        }
    }

    deinit {
        if let deletionObserver {
            NotificationCenter.default.removeObserver(deletionObserver)
        }
    }

    /// Hands MLX's buffer cache back after a generation.
    ///
    /// MLX keeps freed buffers in a cache rather than returning them, which is
    /// the right default while work is ongoing — the next allocation reuses
    /// them instead of asking the system. It is the wrong state to sit in
    /// afterwards: on unified memory that cache is real RAM, held against a
    /// generation that has already finished and been written to disk.
    ///
    /// `clearCache()` was only ever called when switching or forgetting a
    /// model, so a long run's buffers stayed allocated until the next model
    /// change — potentially for the rest of the session. The model itself
    /// stays resident; only the cache goes.
    ///
    /// Called on every path out of a generation, not only the one that
    /// produced a file: after the WAV is written and before `status = .idle`
    /// triggers auto-play, so the memory is back before playback needs any;
    /// after a stop, once the abandoned work has actually finished; and after a
    /// failure. Releasing only on success was backwards — a run is stopped or
    /// killed most often *because* the machine is short of memory, so the cache
    /// stayed held in exactly the cases that needed it back.
    ///
    /// The runtime actor performs the release beside the model it owns.
    private func releaseGenerationMemory() async {
        let release = await runtime.releaseWorkingMemory()
        guard release.freedBytes > 0 else { return }
        // The elapsed time is here to answer a specific question. Releasing
        // gigabytes is real work for the kernel, and if playback stutters
        // shortly after a generation, the two candidates are this call and the
        // player's first read from disk. A number distinguishes them: hundreds
        // of milliseconds here points at the release, single-digit
        // milliseconds points elsewhere.
        log.log(String(format: "Released %@ of MLX cache in %.0f ms (%@ still in use)",
                       release.freedBytes.formatted(.byteCount(style: .memory)),
                       release.elapsedSeconds * 1000,
                       release.activeBytes.formatted(.byteCount(style: .memory))))
    }

    /// Drops the in-memory model if it came from `dir`.
    private func forgetModel(at dir: URL) async {
        guard let loadedDir,
              loadedDir.standardizedFileURL == dir.standardizedFileURL else { return }
        guard !status.isBusy else {
            log.log("Refusing to unload a model while it is generating")
            return
        }
        await unload(reason: "its files were deleted")
    }

    /// Lets go of whatever model is loaded (spec §3e).
    ///
    /// On unified memory a loaded model is real RAM, and nothing asks for the
    /// mode you walked away from again — so the app should not be holding
    /// several gigabytes for one nobody is looking at.
    ///
    /// The MLX cache goes with it. Freed buffers are kept for reuse rather than
    /// returned, which is the right default while work is ongoing and the wrong
    /// state to sit in once the model they belong to is gone.
    ///
    /// Safe to call when nothing is loaded: that is the ordinary case when a
    /// mode is left before it has ever generated, and it says nothing then
    /// rather than logging an unload that did not happen.
    /// Releases the loaded model unless it is the one `mode` needs (spec §3e).
    ///
    /// Called before the preflight as well as before the download, because
    /// §11's memory check is a prediction about the run that is about to
    /// start: measured with the previous mode's model still resident it
    /// describes a machine that will not exist by the time the run begins, and
    /// warns about swapping that is not going to happen.
    ///
    /// Generating twice in the same mode releases nothing — it is the same
    /// model, and reloading several gigabytes per run would make every run as
    /// slow as the first.
    public func releaseModel(unlessNeededFor mode: TTSMode) async {
        guard loadedRepo != mode.effectiveRepoID else { return }
        await unload(reason: "preparing \(mode.rawValue)")
    }

    public func unload(reason: String) async {
        let wasLoaded = modelInfo != nil || loadedRepo != nil
        let what = loadedRepo.map(PrivacyRedactor.modelSource) ?? "the model"

        let runtimeReleased = await runtime.unload()
        guard !wasLoaded || runtimeReleased else {
            log.log("Refusing to unload \(what) while it is generating")
            return
        }
        modelInfo = nil
        loadedRepo = nil
        loadedDir = nil
        modelLease = nil

        guard wasLoaded || runtimeReleased else { return }
        log.log("Unloading \(what) — \(reason)")
    }

    // MARK: Model lifecycle

    /// Download (if needed) and load the model for `mode`.
    public func prepare(mode: TTSMode) async throws -> MLXTtsModelInfo {
        let repoID = mode.effectiveRepoID
        if let modelInfo, loadedRepo == repoID { return modelInfo }

        // By here the model for another mode must already be gone, or two are
        // resident at once for exactly as long as the download below needs the
        // room. Ordinarily it is: leaving the mode released it, and the
        // generate path releases it again ahead of the preflight. This is the
        // backstop for any caller that did neither.
        await releaseModel(unlessNeededFor: mode)

        let acquiredLease = modelLease == nil
        if acquiredLease {
            modelLease = try ModelOperationLease(
                modelsRoot: ModelsLocation.current(), operation: "model")
        }

        do {
            log.log("Preparing \(mode.rawValue) — \(PrivacyRedactor.modelSource(repoID))")
            let source = mode.effectiveSource
            status = .checking
            let localDir = try await download(mode: mode, source: source)
            try await ensureTokenizerJSON(in: localDir, source: source)

            status = .loading
            log.log("Loading model into memory…")
            let loadStart = Date()
            let loaded = try await runtime.prepare(
                repoID: repoID, directory: localDir)
            log.log(String(format: "Model loaded in %.1f s",
                           Date().timeIntervalSince(loadStart)))
            self.modelInfo = loaded
            self.loadedRepo = repoID
            self.loadedDir = localDir
            return loaded
        } catch {
            if acquiredLease { modelLease = nil }
            throw error
        }
    }

    /// Downloads and validates one or more models without loading them.
    ///
    /// One lease covers the whole batch so another Bunyi process cannot start
    /// inference or mutate the shared folder in the gaps between assets.
    public func downloadModels(
        _ modes: [TTSMode], ownsLease: Bool = false
    ) async throws -> [URL] {
        guard !status.isBusy else {
            throw BunyiBusyError(modelsRoot: ModelsLocation.current())
        }
        let operationLease = !ownsLease && modelLease == nil
            ? try ModelOperationLease(
                modelsRoot: ModelsLocation.current(), operation: "models.download")
            : nil
        downloadItemCount = modes.count
        downloadItemIndex = 0
        aggregateDownloadCompleted = 0
        downloadMode = nil
        defer {
            downloadFeedback = nil
            downloadDetail = nil
            downloadMode = nil
            downloadItemIndex = 0
            downloadItemCount = 0
            status = .idle
            withExtendedLifetime(operationLease) {}
        }

        var downloaded: [URL] = []
        for (offset, mode) in modes.enumerated() {
            try Task.checkCancellation()
            downloadMode = mode
            downloadItemIndex = offset + 1
            status = .checking
            let source = mode.effectiveSource
            let directory = try await download(mode: mode, source: source)
            try await ensureTokenizerJSON(in: directory, source: source)
            guard Self.hasCompleteModel(at: directory) else {
                throw TTSError.selfHostIncomplete
            }
            downloaded.append(directory)
            aggregateDownloadCompleted += await Task.detached(priority: .utility) {
                ModelStore.logicalSize(of: directory)
            }.value
        }
        return downloaded
    }

    /// Resolves every requested asset and asks each source for file sizes
    /// before a batch starts. A missing size keeps the aggregate total unknown
    /// rather than turning a known subtotal into a misleading total.
    public func planModelDownloads(_ modes: [TTSMode]) async throws -> [ModelDownloadPlan] {
        var plans: [ModelDownloadPlan] = []
        defer {
            downloadFeedback = nil
            downloadMode = nil
            downloadItemIndex = 0
            downloadItemCount = 0
            status = .idle
        }
        for (offset, mode) in modes.enumerated() {
            try Task.checkCancellation()
            downloadMode = mode
            downloadItemIndex = offset + 1
            downloadItemCount = modes.count
            status = .checking
            let directory = Self.modelDirectory(for: mode)
            let downloaded = await Task.detached(priority: .utility) {
                ModelStore.logicalSize(of: directory)
            }.value
            if Self.hasCompleteModel(at: directory) {
                plans.append(ModelDownloadPlan(
                    mode: mode, totalBytes: downloaded,
                    downloadedBytes: downloaded, complete: true))
                continue
            }

            let files: [ManifestEntry]
            let base: URL
            switch mode.effectiveSource {
            case .repo(let repoID):
                (files, base) = try await hubFiles(repoID: repoID)
            case .baseURL(let source):
                base = source
                files = try await fileList(base: source)
            }
            status = .downloading(0)
            downloadFeedback = ModelDownloadProgress(phase: .sizing)
            var total: Int64 = 0
            var allKnown = true
            for entry in files {
                try Task.checkCancellation()
                if let size = try await remoteSize(
                    of: base.appendingPathComponent(entry.path)) {
                    total += size
                } else {
                    allKnown = false
                }
            }
            let tokenizer = directory.appendingPathComponent("tokenizer.json")
            if !FileManager.default.fileExists(atPath: tokenizer.path),
               !files.contains(where: { $0.path == "tokenizer.json" }) {
                // The shared fallback is resolved after the model source. Its
                // size is deliberately unknown at this phase rather than omitted.
                allKnown = false
            }
            plans.append(ModelDownloadPlan(
                mode: mode, totalBytes: allKnown ? total : nil,
                downloadedBytes: downloaded, complete: false))
        }
        return plans
    }

    private func download(mode: TTSMode, source: ModelSource) async throws -> URL {
        switch source {
        case .repo(let repoID):
            return try await downloadFromHub(mode: mode, repoID: repoID)
        case .baseURL(let url):
            return try await downloadFromBaseURL(mode: mode, base: url)
        }
    }

    /// Where a mode's model lives, whether or not it has been downloaded yet.
    ///
    /// Both download paths derive this, and Doctor has to ask the same question
    /// before a run. Worked out separately in three places it would eventually
    /// disagree, and the symptom is nasty in both directions: Doctor reporting
    /// a model missing that the engine then loads, or reporting one present
    /// that the engine then re-downloads.
    public static func modelDirectory(for mode: TTSMode) -> URL {
        let root = ModelsLocation.current()
        switch mode.effectiveSource {
        case .repo(let repoID):
            return root.appendingPathComponent("models/\(repoID)", isDirectory: true)
        case .baseURL(let base):
            return root.appendingPathComponent("models/self-hosted/\(slug(for: base))",
                                               isDirectory: true)
        }
    }

    /// Whether that folder holds a model that can actually be loaded — the same
    /// test the download path uses to decide it has nothing to do.
    public static func isModelComplete(for mode: TTSMode) -> Bool {
        hasCompleteModel(at: modelDirectory(for: mode))
    }

    /// The digests a self-hosted server publishes, if it publishes any.
    ///
    /// Exposed for Doctor's on-demand integrity check, which needs the parsed
    /// manifest rather than a copy of the parser — the format has enough edge
    /// cases (tabs, `*` markers, unsafe paths) that a second reader of it would
    /// be a second set of bugs.
    public func publishedDigests(for mode: TTSMode) async throws -> [ManifestEntry]? {
        guard case .baseURL(let base) = mode.effectiveSource else { return nil }
        return try await manifest(at: base.appendingPathComponent("manifest.sha256"))
    }

    private func downloadFromHub(mode: TTSMode, repoID: String) async throws -> URL {
        let localDir = Self.modelDirectory(for: mode)
        if Self.hasCompleteModel(at: localDir) {
            log.log("Using existing model files at \(localDir.path)")
            return localDir
        }
        status = .checking
        let (files, base) = try await hubFiles(repoID: repoID)
        try await downloadEntries(files, base: base, into: localDir, allRequired: true)
        guard Self.hasCompleteModel(at: localDir) else { throw TTSError.selfHostIncomplete }
        return localDir
    }

    private func hubFiles(repoID: String) async throws
        -> (files: [ManifestEntry], base: URL) {
        // Discover through the public tree endpoint so its 429 response and
        // reset headers go through the same bounded policy as every file.
        var tree = URLComponents(string:
            "https://huggingface.co/api/models/\(repoID)/tree/main")!
        tree.queryItems = [
            URLQueryItem(name: "recursive", value: "true"),
            URLQueryItem(name: "expand", value: "false"),
        ]
        let treeURL = tree.url!
        let (treeData, treeResponse) = try await requestData(from: treeURL,
                                                             phase: .checking)
        guard treeResponse.statusCode == 200 else {
            throw DownloadServiceError.httpStatus(
                source: treeURL, status: treeResponse.statusCode)
        }
        let entries = try JSONDecoder().decode([HubTreeEntry].self, from: treeData)
        let extensions = Set(["safetensors", "json", "model", "txt"])
        let files = entries.compactMap { entry -> ManifestEntry? in
            guard entry.type == "file",
                  extensions.contains((entry.path as NSString).pathExtension.lowercased())
            else { return nil }
            let path = entry.path
            guard let safe = Self.safeRelativePath(path) else { return nil }
            return ManifestEntry(path: safe, sha256: nil)
        }.sorted { $0.path < $1.path }
        guard !files.isEmpty else { throw TTSError.selfHostIncomplete }
        let base = URL(string: "https://huggingface.co")!
            .appendingPathComponent(repoID).appendingPathComponent("resolve/main")
        return (files, base)
    }

    // MARK: Self-hosted download

    /// Standard Qwen3-TTS file set, used when a self-hosted server has no
    /// manifest.txt. Only `requiredModelFiles` fail the download on 404; the
    /// rest are best-effort — single-shard repos lack the index, and a server
    /// may omit tokenizer.json (which `ensureTokenizerJSON` then backfills).
    private static let defaultModelFiles: [String] = [
        "config.json",
        "generation_config.json",
        "model.safetensors",
        "model.safetensors.index.json",
        "preprocessor_config.json",
        "tokenizer_config.json",
        "vocab.json",
        "merges.txt",
        "tokenizer.json",
        "speech_tokenizer/config.json",
        "speech_tokenizer/configuration.json",
        "speech_tokenizer/preprocessor_config.json",
        "speech_tokenizer/model.safetensors",
    ]
    private static let requiredModelFiles: Set<String> = ["config.json", "model.safetensors"]

    private func downloadFromBaseURL(mode: TTSMode, base: URL) async throws -> URL {
        let localDir = Self.modelDirectory(for: mode)
        if Self.hasCompleteModel(at: localDir) { return localDir }
        status = .checking
        let files = try await fileList(base: base)
        try await downloadEntries(files, base: base, into: localDir)
        guard Self.hasCompleteModel(at: localDir) else { throw TTSError.selfHostIncomplete }
        return localDir
    }

    private func downloadEntries(_ files: [ManifestEntry], base: URL, into localDir: URL, allRequired: Bool = false) async throws {
        let setupStarted = Date()
        try FileManager.default.createDirectory(at: localDir, withIntermediateDirectories: true)
        downloadFeedback = ModelDownloadProgress(phase: .sizing)
        status = .downloading(0)
        defer { downloadFeedback = nil; downloadDetail = nil }
        var sizes: [String: Int64] = [:]
        var present = Set(files.map(\.path))
        for entry in files {
            try Task.checkCancellation()
            sizes[entry.path] = try await remoteSize(
                of: base.appendingPathComponent(entry.path))
        }
        func total() -> Int64 { present.allSatisfy { sizes[$0] != nil } ? sizes.values.reduce(0, +) : 0 }
        var completed: Int64 = 0
        let mailbox = DownloadReceiptMailbox(started: setupStarted)
        for entry in files {
            try Task.checkCancellation()
            let dest = localDir.appendingPathComponent(entry.path)
            let expected = sizes[entry.path]
            downloadFeedback = ModelDownloadProgress(phase: .checking, available: completed,
                total: total(), file: entry.path, fileTotal: expected ?? 0,
                elapsed: Date().timeIntervalSince(setupStarted))
            let reused: Int64? = try await Task.detached {
                guard let size = try? dest.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 0 else { return nil }
                if let digest = entry.sha256 {
                    return try HTTPFileDownloader.sha256Hex(of: dest) == digest ? Int64(size) : nil
                }
                return expected == Int64(size) ? Int64(size) : nil
            }.value
            if let reused {
                completed += reused; sizes[entry.path] = reused
                try? FileManager.default.removeItem(at: dest.appendingPathExtension(HTTPFileDownloader.partialExtension))
                log.log("Have \(entry.path) already (\(reused) bytes)")
                continue
            }
            let otherPaths = present.filter { $0 != entry.path }
            let otherTotal: Int64? = otherPaths.allSatisfy { sizes[$0] != nil }
                ? otherPaths.reduce(Int64(0)) { $0 + (sizes[$1] ?? 0) } : nil
            var response = HTTPResponseInfo(statusCode: 0)
            var responseRetries = 0
            while true {
                try Task.checkCancellation()
                mailbox.begin(file: entry.path, completed: completed, total: total(),
                              fileTotal: expected ?? 0, otherTotal: otherTotal)
                downloadFeedback = mailbox.snapshot()
                let ticker = Task { @MainActor [weak self] in
                    var loggedAt = Date()
                    var warned = false
                    while !Task.isCancelled {
                        do { try await Task.sleep(for: .milliseconds(250)) } catch { break }
                        guard let self, !Task.isCancelled else { break }
                        if self.status == .stopping { break }
                        let value = mailbox.snapshot()
                        self.downloadFeedback = value
                        self.status = .downloading(value.fraction)
                        let now = Date()
                        if value.stalled(at: now) && !warned {
                            self.log.log("No new data for 30 s — the connection may be stalled")
                            warned = true
                        } else if !value.stalled(at: now) { warned = false }
                        if value.phase == .downloading,
                           now.timeIntervalSince(loggedAt) >= 10 {
                            self.log.log("Download: \(value.received.formatted()) bytes received; \(value.file ?? "model files")")
                            loggedAt = now
                        }
                    }
                }
                let transfer = ModelFileTransfer(destination: dest, digest: entry.sha256,
                    expected: expected, mailbox: mailbox)
                activeModelTransfer = transfer
                do {
                    response = try await transfer.run(
                        from: base.appendingPathComponent(entry.path))
                } catch {
                    activeModelTransfer = nil
                    ticker.cancel()
                    try Task.checkCancellation()
                    if transfer.reconnectRequested {
                        mailbox.reconnecting()
                        downloadFeedback = mailbox.snapshot()
                        log.log("Reconnecting \(entry.path); retaining the partial file.")
                        continue
                    }
                    throw error
                }
                activeModelTransfer = nil
                ticker.cancel()
                try Task.checkCancellation()
                downloadFeedback = mailbox.snapshot()
                if response.shouldRetryDownload {
                    let source = base.appendingPathComponent(entry.path)
                    let retryNumber = min(responseRetries + 1,
                                          DownloadRetryPolicy.maximumRetries)
                    let delay = DownloadRetryPolicy.delay(
                        for: response, now: Date(),
                        retryNumber: retryNumber,
                        jitter: Double.random(in: 0...1))
                    let retryAt = Date().addingTimeInterval(delay)
                    guard responseRetries < DownloadRetryPolicy.maximumRetries,
                          delay <= DownloadRetryPolicy.maximumDelay else {
                        throw response.retryExhaustedError(
                            source: source, retryAt: retryAt)
                    }
                    responseRetries += 1
                    mailbox.waiting(until: retryAt,
                                    host: source.host ?? "The server",
                                    attempt: responseRetries)
                    downloadFeedback = mailbox.snapshot()
                    log.log("Download from \(source.host ?? "the server") returned HTTP \(response.statusCode); retrying in \(Int(ceil(delay))) s")
                    try await DownloadRetryPolicy.wait(until: retryAt)
                    continue
                }
                break
                }
            if !response.isSuccess {
                let source = base.appendingPathComponent(entry.path)
                if (500...599).contains(response.statusCode) {
                    throw response.unavailableError(source: source)
                }
                guard response.statusCode == 404 && !allRequired
                        && !Self.requiredModelFiles.contains(entry.path) else {
                    throw TTSError.selfHostFileMissing(entry.path,
                                                        response.statusCode)
                }
                present.remove(entry.path); sizes.removeValue(forKey: entry.path)
                continue
            }
            let size = Int64(try dest.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0)
            completed += size; sizes[entry.path] = size
        }
        // A previous Hub snapshot may have left partial cache transfers. Only
        // after all replacement files succeed, retire its obsolete scratch files.
        try Self.retireLegacyTransfers(in: localDir)
    }

    nonisolated private static func retireLegacyTransfers(in localDir: URL) throws {
        let legacyCache = localDir.appendingPathComponent(".cache/huggingface/download")
        if let old = FileManager.default.enumerator(at: legacyCache, includingPropertiesForKeys: nil) {
            for case let url as URL in old where url.pathExtension == "incomplete" {
                try FileManager.default.removeItem(at: url)
            }
        }
    }

    /// One file a self-hosted server publishes, and its digest if it published
    /// one.
    public struct ManifestEntry: Sendable {
        public let path: String
        /// Lowercase hex SHA-256, or nil when the manifest does not carry one.
        public let sha256: String?
    }

    /// The file list from the server: `manifest.sha256` first, `manifest.txt`
    /// second, the built-in set last.
    ///
    /// Two files rather than digests added to `manifest.txt`, because every
    /// already-installed Bunyi parses each line of that file as a path. A line
    /// reading `<digest>  model.safetensors` would be requested verbatim, 404,
    /// and — for a required file — fail the whole download. Old clients never
    /// ask for `manifest.sha256`, so a server can publish it whenever it likes
    /// and nothing in the field breaks.
    private func fileList(base: URL) async throws -> [ManifestEntry] {
        if let entries = try await manifest(at: base.appendingPathComponent("manifest.sha256")),
           !entries.isEmpty {
            let digests = entries.filter { $0.sha256 != nil }.count
            log.log("Using manifest.sha256 (\(entries.count) files, \(digests) with checksums)")
            return entries
        }
        if let entries = try await manifest(at: base.appendingPathComponent("manifest.txt")),
           !entries.isEmpty {
            log.log("Using manifest.txt (\(entries.count) files, no checksums)")
            return entries
        }
        log.log("No manifest — using the built-in Qwen3-TTS file list")
        return Self.defaultModelFiles.map { ManifestEntry(path: $0, sha256: nil) }
    }

    /// Parses either manifest format; nil if the file is not served.
    ///
    /// A line is `<64 hex digits><whitespace><path>` — the output `shasum -a
    /// 256` and `sha256sum` already produce, so the published manifest is also
    /// a file `shasum -c` can verify — or a bare path. Anything that does not
    /// start with a digest is treated as a path, which is what makes one parser
    /// enough for both files.
    private func manifest(at url: URL) async throws -> [ManifestEntry]? {
        let (data, response) = try await requestData(from: url, phase: .checking)
        if response.statusCode == 404 { return nil }
        guard response.statusCode == 200 else {
            throw DownloadServiceError.httpStatus(
                source: url, status: response.statusCode)
        }
        guard let text = String(data: data, encoding: .utf8) else {
            throw URLError(.cannotDecodeContentData)
        }
        return text.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
            .compactMap { line -> ManifestEntry? in
                // Any whitespace, not just a space. `shasum` and `sha256sum`
                // write two spaces, but the format is whitespace-separated and
                // tools do emit tabs. Splitting on " " alone would read such a
                // line as a bare path — and the failure would be silent, a file
                // fetched with no verification while the manifest looked
                // honoured.
                let parts = line.split(maxSplits: 1, omittingEmptySubsequences: true,
                                       whereSeparator: \.isWhitespace)
                if parts.count == 2, Self.isSHA256Hex(parts[0]) {
                    let path = parts[1].trimmingCharacters(in: .whitespaces)
                    // `shasum` marks binary reads with a leading '*' on the
                    // filename. Harmless to it, a 404 to us.
                    let cleaned = path.hasPrefix("*") ? String(path.dropFirst()) : path
                    guard let safe = Self.safeRelativePath(cleaned) else {
                        log.log("Ignoring unsafe manifest path: \(cleaned)")
                        return nil
                    }
                    return ManifestEntry(path: safe, sha256: parts[0].lowercased())
                }
                // A line that is nothing but a digest is a checksum whose path
                // went missing. Left alone it becomes a "file" named after the
                // digest, requested and 404ing — a confusing way to report a
                // malformed manifest.
                guard !Self.isSHA256Hex(line[...]) else {
                    log.log("Ignoring manifest line with a checksum but no path")
                    return nil
                }
                guard let safe = Self.safeRelativePath(line) else {
                    log.log("Ignoring unsafe manifest path: \(line)")
                    return nil
                }
                return ManifestEntry(path: safe, sha256: nil)
            }
    }

    private static func isSHA256Hex(_ s: Substring) -> Bool {
        s.count == 64 && s.allSatisfy(\.isHexDigit)
    }

    /// Executes one metadata request under the same bounded HTTP retry policy
    /// as file transfers. The original request is recreated on each attempt,
    /// so a Hugging Face resolve URL obtains a fresh signed redirect.
    private func requestData(from url: URL,
                             phase: ModelDownloadProgress.Phase) async throws
        -> (Data, HTTPResponseInfo) {
        try await requestData(URLRequest(url: url), phase: phase)
    }

    private func requestData(_ originalRequest: URLRequest,
                             phase: ModelDownloadProgress.Phase) async throws
        -> (Data, HTTPResponseInfo) {
        guard let source = originalRequest.url else {
            throw URLError(.badURL)
        }
        var retries = 0
        while true {
            try Task.checkCancellation()
            let (data, rawResponse) = try await URLSession.shared.data(
                for: originalRequest)
            guard let http = rawResponse as? HTTPURLResponse else {
                throw URLError(.badServerResponse)
            }
            let response = HTTPResponseInfo(http)
            if response.shouldRetryDownload {
                let retryNumber = min(retries + 1,
                                      DownloadRetryPolicy.maximumRetries)
                let delay = DownloadRetryPolicy.delay(
                    for: response, now: Date(), retryNumber: retryNumber,
                    jitter: Double.random(in: 0...1))
                let retryAt = Date().addingTimeInterval(delay)
                guard retries < DownloadRetryPolicy.maximumRetries,
                      delay <= DownloadRetryPolicy.maximumDelay else {
                    throw response.retryExhaustedError(
                        source: source, retryAt: retryAt)
                }
                retries += 1
                let previous = downloadFeedback
                var waiting = previous ?? ModelDownloadProgress(phase: phase)
                waiting.phase = .waiting
                waiting.retryAt = retryAt
                waiting.retryHost = source.host
                waiting.retryAttempt = retries
                waiting.slow = false
                waiting.rate = 0
                downloadFeedback = waiting
                log.log("Request to \(source.host ?? "the server") returned HTTP \(response.statusCode); retrying in \(Int(ceil(delay))) s")
                do {
                    try await DownloadRetryPolicy.wait(until: retryAt)
                } catch {
                    downloadFeedback = previous
                    throw error
                }
                downloadFeedback = previous.map { saved in
                    var restored = saved
                    restored.phase = phase
                    restored.retryAt = nil
                    restored.retryHost = nil
                    restored.retryAttempt = 0
                    return restored
                }
                continue
            }
            if (500...599).contains(response.statusCode) {
                throw response.unavailableError(source: source)
            }
            return (data, response)
        }
    }

    /// A manifest path, or nil if it would escape the model's folder.
    ///
    /// Entries go to `appendingPathComponent` and are then written to. Left
    /// unchecked, `../../../../etc/passwd` or an absolute `/tmp/whatever` from
    /// a server would put files outside the models directory entirely. The base
    /// URL is whatever the user typed into Settings, so the server is not
    /// necessarily one they audited — and a manifest is the one part of a
    /// self-hosted model that chooses its own filenames.
    ///
    /// Unsafe entries are skipped rather than failing the download: one bad
    /// line in a hand-built manifest should not cost someone a 3.4 GB refetch,
    /// and the log says which line was dropped.
    ///
    /// Backslashes and colons go too, for the same reason in two shapes. Both
    /// are legal in a POSIX filename and neither escapes anything here — but on
    /// Windows `\` is a path separator and a leading `C:` is drive-rooted, so
    /// `..\windows\system32` and `C:/Windows/System32` look inert on macOS and
    /// traverse on the .NET app. `C:foo` is worse again: drive-*relative*, so
    /// it lands wherever that drive's working directory happens to be.
    ///
    /// Colons are rejected anywhere, not only as a drive prefix. Windows
    /// forbids them in filenames outright, so an entry containing one is either
    /// an escape attempt or a file the .NET app could not create anyway — and a
    /// rule the two apps can state identically beats one that needs a
    /// position-dependent exception.
    private static func safeRelativePath(_ path: String) -> String? {
        guard !path.isEmpty,
              !path.hasPrefix("/"), !path.hasPrefix("~"),
              !path.contains("\\"), !path.contains(":"),
              // Empty components catch `a//b` and a trailing slash; neither
              // names a file, and both suggest a manifest built by hand.
              path.split(separator: "/", omittingEmptySubsequences: false)
                  .allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." })
        else { return nil }
        return path
    }

    /// The server's size for a file, or nil if it will not say.
    ///
    /// A HEAD per file is a handful of small round trips against a download
    /// measured in gigabytes — cheap enough to buy the ability to resume.
    ///
    /// Returns nil for compressed responses, and that is the point: URLSession
    /// asks for gzip, so a server that compresses JSON reports a length that
    /// does not match the file on disk. Rather than guess, those files are
    /// simply fetched again — they are a few kilobytes. The multi-gigabyte
    /// weights are served as octet-stream, uncompressed, and do report a
    /// usable length, which is the case that matters.
    private func remoteSize(of url: URL) async throws -> Int64? {
        var request = URLRequest(url: url)
        request.httpMethod = "HEAD"
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        let (_, response) = try await requestData(request, phase: .sizing)
        if response.statusCode == 404 { return nil }
        guard response.statusCode == 200 else {
            throw DownloadServiceError.httpStatus(
                source: url, status: response.statusCode)
        }
        return response.expectedContentLength > 0
            ? response.expectedContentLength : nil
    }

    /// Filesystem-safe folder name derived from a base URL.
    nonisolated private static func slug(for url: URL) -> String {
        let allowed = CharacterSet(charactersIn:
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_")
        let raw = (url.host ?? "server") + url.path
        let cleaned = String(raw.unicodeScalars.map {
            allowed.contains($0) ? Character($0) : "-"
        })
        let trimmed = cleaned.trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return trimmed.isEmpty ? "server" : trimmed
    }

    // MARK: Tokenizer

    /// The mlx-community conversions ship vocab.json + merges.txt but not
    /// the tokenizer.json that swift-transformers' AutoTokenizer requires.
    /// All Qwen3-TTS variants share the same text tokenizer (verified:
    /// identical 151,643-token vocab), so fetch one from a repo that
    /// includes it.
    private static let tokenizerJSONURL = URL(string:
        "https://huggingface.co/AtomGradient/Qwen3-TTS-0.6B-CustomVoice-bf16-pruned-vocab-lite/resolve/main/tokenizer.json")!

    private func ensureTokenizerJSON(in dir: URL, source: ModelSource) async throws {
        let dest = dir.appendingPathComponent("tokenizer.json")
        guard !FileManager.default.fileExists(atPath: dest.path) else { return }
        log.log("Model has no tokenizer.json — fetching a compatible one")

        // Prefer the self-hosted server's own copy, then the known HF URL.
        var candidates: [URL] = []
        if case .baseURL(let base) = source {
            candidates.append(base.appendingPathComponent("tokenizer.json"))
        }
        candidates.append(Self.tokenizerJSONURL)

        var serviceFailure: DownloadServiceError?
        for url in candidates {
            do {
                try await downloadEntries([ManifestEntry(path: "tokenizer.json", sha256: nil)],
                    base: url.deletingLastPathComponent(), into: dir, allRequired: true)
                log.log("Added tokenizer.json to \(dir.path)")
                return
            } catch is CancellationError { throw CancellationError() }
            catch let error as URLError where error.code == .cancelled { throw error }
            catch let error as DownloadServiceError {
                serviceFailure = serviceFailure ?? error
                log.log("Tokenizer source failed: \(error.localizedDescription)")
            }
            catch {
                log.log("Tokenizer source failed: \(error.localizedDescription)")
            }
        }
        if let serviceFailure { throw serviceFailure }
        throw TTSError.tokenizerDownloadFailed
    }

    /// A usable model folder: config plus weights, and no partial downloads
    /// anywhere in the tree (the Hub keeps them under .cache/huggingface).
    nonisolated private static func hasCompleteModel(at dir: URL) -> Bool {
        let fm = FileManager.default
        guard fm.fileExists(atPath: dir.appendingPathComponent("config.json").path),
              let enumerator = fm.enumerator(at: dir, includingPropertiesForKeys: nil)
        else { return false }

        // "Any .safetensors" used to be enough, which was wrong in a way that
        // only bites after an interrupted download: these models ship a second
        // weights file at speech_tokenizer/model.safetensors, so a folder
        // holding the tokenizer's weights and no model weights looked complete.
        // The app would then skip the download entirely and fail at load, with
        // nothing pointing at the real cause.
        // Any .incomplete anywhere means an interrupted download.
        for case let url as URL in enumerator where url.pathExtension == "incomplete" {
            return false
        }

        // The model's own weights sit beside config.json; the tokenizer's live
        // in a subfolder. Listing the top level directly avoids comparing URLs
        // for equality, which is unreliable — /var against /private/var alone
        // is enough to make a complete model look incomplete forever.
        let topLevel = (try? fm.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: nil)) ?? []
        guard topLevel.contains(where: { $0.pathExtension == "safetensors" }) else {
            return false
        }

        // A sharded model is only complete when every shard named by its index
        // is present — one shard of three passes every other check here.
        let indexURL = dir.appendingPathComponent("model.safetensors.index.json")
        if let data = try? Data(contentsOf: indexURL),
           let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let map = root["weight_map"] as? [String: String] {
            for shard in Set(map.values) {
                if !fm.fileExists(atPath: dir.appendingPathComponent(shard).path) {
                    return false
                }
            }
        }
        return true
    }

    // MARK: Long-text generation

    private struct SynthesisRequest {
        let mode: TTSMode
        let text: String
        let speaker: String?
        let instruct: String?
        let language: String
        let referenceAudio: [Float]?
        let referenceText: String?
    }

    private struct SectionTake: Sendable {
        let samples: [Float]
        let frames: Int
        let reachedEndOfSpeech: Bool
    }

    private final class SectionAccumulator {
        var samples: [Float] = []
        var frames = 0

        func accept(_ take: SectionTake, sampleRate: Int) {
            SectionAudioJoiner.append(take.samples, to: &samples,
                                      sampleRate: sampleRate)
            frames += take.frames
        }
    }

    /// One bounded attempt. The dependency resets its KV cache on every call;
    /// only EOS-terminated audio is returned to the section accumulator.
    private func synthesize(
        request: SynthesisRequest,
        sampleRate: Int,
        maximumFrames: Int?,
        progressLabel: String?,
        readySamples: Int
    ) async throws -> SectionTake {
        let control = MLXTtsGenerationControl()
        let (tokens, continuation) = AsyncStream.makeStream(of: Int.self)
        status = .generating(0)
        if let progressLabel {
            generationDetail = String(
                format: "%@ · 0.0s in this attempt · %.1fs ready",
                progressLabel,
                Double(readySamples) / Double(sampleRate))
        }
        let worker = Task {
            defer { continuation.finish() }
            if maximumFrames == nil, request.mode != .voiceClone {
                return try await runtime.generateStreaming(
                    mode: request.mode, text: request.text,
                    speaker: request.speaker, instruct: request.instruct,
                    language: request.language, control: control,
                    onFrame: { continuation.yield($0) })
            }
            return try await runtime.generateBounded(
                mode: request.mode, text: request.text,
                speaker: request.speaker, instruct: request.instruct,
                language: request.language,
                referenceSamples: request.referenceAudio,
                referenceText: request.referenceText,
                maximumFrames: maximumFrames, control: control,
                onFrame: { continuation.yield($0) })
        }

        do {
            for await count in tokens {
                try Task.checkCancellation()
                status = .generating(count)
                if let progressLabel {
                    generationDetail = String(
                        format: "%@ · %.1fs in this attempt · %.1fs ready",
                        progressLabel, Double(count) / 12.5,
                        Double(readySamples) / Double(sampleRate))
                }
                if count % 100 == 0 {
                    log.log("\(progressLabel ?? "Generation"): \(count) frames in this attempt")
                }
            }
            try Task.checkCancellation()
            let runtimeTake = try await worker.value
            let take = SectionTake(
                samples: runtimeTake.samples, frames: runtimeTake.frames,
                reachedEndOfSpeech: runtimeTake.reachedEndOfSpeech)
            guard !take.samples.isEmpty,
                  take.samples.allSatisfy(\.isFinite) else {
                throw TTSError.invalidSectionAudio
            }
            return take
        } catch {
            control.cancel()
            if Task.isCancelled || error is CancellationError {
                worker.cancel()
                pendingWork = Task { _ = try? await worker.value }
            }
            throw error
        }
    }

    private func generateSection(
        request: SynthesisRequest,
        sampleRate: Int,
        label: String,
        depth: Int,
        accumulator: SectionAccumulator
    ) async throws {
        do {
            try await SpeechSections.recover(request.text, depth: depth) {
                text, attemptDepth in
                let upper = SpeechDurationEstimate.forText(
                    text, language: request.language).upperSeconds
                let limit = Int(ceil(max(20, 2 * upper + 5) * 12.5))
                let progress = attemptDepth > 0
                    ? "Retrying \(label.lowercased()) with shorter text" : label
                let take = try await synthesize(
                    request: SynthesisRequest(
                        mode: request.mode, text: text,
                        speaker: request.speaker, instruct: request.instruct,
                        language: request.language,
                        referenceAudio: request.referenceAudio,
                        referenceText: request.referenceText),
                    sampleRate: sampleRate,
                    maximumFrames: limit, progressLabel: progress,
                    readySamples: accumulator.samples.count)
                guard take.reachedEndOfSpeech else {
                    if attemptDepth < 2 {
                        log.log("\(label): reached the \(limit)-frame limit. Retrying with shorter text (subdivision \(attemptDepth + 1) of 2).")
                    } else {
                        log.log("\(label): reached the \(limit)-frame limit at the final subdivision.")
                    }
                    return false
                }
                accumulator.accept(take, sampleRate: sampleRate)
                log.log("\(label): accepted \(take.frames) frames at EOS")
                return true
            }
        } catch SpeechSectionRecoveryError.exhausted {
            throw TTSError.generationDidNotFinish
        }
    }

    private func generateSections(
        request: SynthesisRequest,
        sampleRate: Int,
        accumulator: SectionAccumulator
    ) async throws {
        let sections = SpeechSections.split(request.text)
        for (index, text) in sections.enumerated() {
            try await generateSection(
                request: SynthesisRequest(
                    mode: request.mode, text: text, speaker: request.speaker,
                    instruct: request.instruct, language: request.language,
                    referenceAudio: request.referenceAudio,
                    referenceText: request.referenceText),
                sampleRate: sampleRate,
                label: "Section \(index + 1) of \(sections.count)", depth: 0,
                accumulator: accumulator)
        }
    }

    private func preparedCloneReference(
        url: URL,
        transcript: String?,
        transcriptAudioSeconds: TimeInterval?,
        language: String,
        sampleRate: Double
    ) async throws -> (audio: [Float], text: String) {
        let typed = transcript?.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasProvidedTranscript = typed?.isEmpty == false
        let audioMaximumSeconds = ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: hasProvidedTranscript,
            savedTranscriptAudioSeconds: transcriptAudioSeconds)
        let text: String
        if let typed, !typed.isEmpty {
            text = typed
        } else {
            text = try await transcribeReference(url: url, language: language)
            lastReferenceTranscript = text
            lastReferenceTranscriptAudioSeconds = audioMaximumSeconds
            logReferenceTranscription(text)
        }
        log.log("Preparing reference audio — resampling to \(Int(sampleRate / 1000)) kHz mono")
        if let audioMaximumSeconds {
            log.log(String(
                format: "Using the first %.1f seconds for matching reference audio and transcript",
                audioMaximumSeconds))
        }
        return (try Self.loadReferenceAudio(
            from: url, targetSampleRate: sampleRate,
            maximumSeconds: audioMaximumSeconds).asArray(Float.self), text)
    }

    private func generateLongText(
        initialModel: MLXTtsModelInfo,
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        referenceAudioURL: URL?,
        referenceText: String?,
        referenceTranscriptAudioSeconds: TimeInterval?
    ) async throws -> (samples: [Float], frames: Int,
                       continuationModelRepo: String?) {
        let accumulator = SectionAccumulator()
        var continuationRepo: String?

        if mode == .voiceDesign {
            var opening = SpeechSections.split(text, maximumSeconds: 8)[0]
            var accepted: SectionTake?
            for attempt in 0..<3 {
                let take = try await synthesize(
                    request: SynthesisRequest(
                        mode: .voiceDesign, text: opening, speaker: nil,
                        instruct: instruct, language: language,
                        referenceAudio: nil, referenceText: nil),
                    sampleRate: initialModel.sampleRate,
                    maximumFrames: 125,
                    progressLabel: attempt == 0
                        ? "Creating your designed voice"
                        : "Retrying a shorter voice opening",
                    readySamples: 0)
                if take.reachedEndOfSpeech {
                    accepted = take
                    break
                }
                guard attempt < 2 else {
                    throw TTSError.generationDidNotFinish
                }
                let halves = SpeechSections.bisect(opening)
                guard halves.count == 2 else {
                    throw TTSError.generationDidNotFinish
                }
                opening = halves[0]
            }
            guard let accepted else { throw TTSError.generationDidNotFinish }
            accumulator.accept(accepted, sampleRate: initialModel.sampleRate)

            generationDetail = "Preparing the clone model to keep your designed voice consistent"
            await unload(reason: "continuing a long designed voice through Voice clone")
            let cloneModel = try await prepare(mode: .voiceClone)
            continuationRepo = TTSMode.voiceClone.effectiveRepoID
            let remaining = String(text.dropFirst(opening.count))
            if !remaining.isEmpty {
                try await generateSections(
                    request: SynthesisRequest(
                        mode: .voiceClone, text: remaining, speaker: nil,
                        instruct: nil, language: language,
                        referenceAudio: accepted.samples,
                        referenceText: opening),
                    sampleRate: cloneModel.sampleRate,
                    accumulator: accumulator)
            }
        } else {
            var reference: (audio: [Float], text: String)?
            var gotAccess = false
            if mode == .voiceClone {
                guard let url = referenceAudioURL else {
                    throw TTSError.missingReference
                }
                log.log("Cloning voice from \(url.lastPathComponent)")
                gotAccess = url.startAccessingSecurityScopedResource()
                defer { if gotAccess { url.stopAccessingSecurityScopedResource() } }
                reference = try await preparedCloneReference(
                    url: url, transcript: referenceText,
                    transcriptAudioSeconds: referenceTranscriptAudioSeconds,
                    language: language,
                    sampleRate: Double(initialModel.sampleRate))
            }
            try await generateSections(
                request: SynthesisRequest(
                    mode: mode, text: text, speaker: speaker,
                    instruct: instruct, language: language,
                    referenceAudio: reference?.audio,
                    referenceText: reference?.text),
                sampleRate: initialModel.sampleRate,
                accumulator: accumulator)
        }
        return (accumulator.samples, accumulator.frames, continuationRepo)
    }

    private func saveLongText(
        _ samples: [Float],
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        referenceText: String?,
        continuationModelRepo: String?
    ) async throws -> (url: URL, gain: Double, metadata: OutputMetadata) {
        try Task.checkCancellation()
        let url = outputDir.appendingPathComponent(Self.fileName(for: mode))
        let temporary = outputDir.appendingPathComponent(
            ".bunyi-output-\(UUID().uuidString).partial.wav")
        defer { try? FileManager.default.removeItem(at: temporary) }
        let nonEmpty: (String?) -> String? = { value in
            let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed?.isEmpty == false ? trimmed : nil
        }
        let metadata = OutputMetadata(
            mode: mode.rawValue, text: text, language: language,
            speaker: mode == .presetVoice ? nonEmpty(speaker) : nil,
            style: mode == .presetVoice ? nonEmpty(instruct) : nil,
            voiceDescription: mode == .voiceDesign ? nonEmpty(instruct) : nil,
            referenceTranscript: mode == .voiceClone
                ? nonEmpty(referenceText) ?? nonEmpty(lastReferenceTranscript)
                : nil,
            modelRepo: PrivacyRedactor.modelSource(mode.effectiveRepoID),
            continuationModelRepo: continuationModelRepo.map(
                PrivacyRedactor.modelSource),
            appVersion: Self.appVersion, created: Date())

        status = .finalizing
        generationDetail = "Preparing your complete audio file…"
        let result = try await Task.detached(priority: .userInitiated) {
            dispatchPrecondition(condition: .notOnQueue(.main))
            let prepared = try OutputLevel.prepare(samples)
            try saveAudioArray(MLXArray(prepared.samples),
                               sampleRate: 24_000, to: temporary)
            try? WAVMetadata.embed(metadata, in: temporary)
            return prepared.gain
        }.value
        try Task.checkCancellation()
        try FileManager.default.moveItem(at: temporary, to: url)
        return (url, result, metadata)
    }

    // MARK: Generation

    public func generate(
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        referenceAudioURL: URL?,
        referenceText: String?,
        referenceTranscriptAudioSeconds: TimeInterval? = nil
    ) async {
        // Serialization is not a nicety here: one model, non-Sendable, shared
        // by everything below. `stop()` keeps the app busy until abandoned work
        // finishes, so reaching this while busy would mean the UI let through a
        // second job. Refuse rather than race.
        guard !status.isBusy else {
            log.log("Ignoring Generate — the previous job has not finished")
            return
        }
        lastGenerationSummary = nil
        lastFailure = nil
        downloadRecovery = nil
        generationDetail = nil
        lastReferenceTranscript = nil
        lastReferenceTranscriptAudioSeconds = nil
        do {
            let model = try await prepare(mode: mode)
            try Task.checkCancellation()
            let generateStart = Date()
            let effectiveSpeaker = mode == .presetVoice
                ? speaker ?? model.supportedSpeakers.first ?? "Ryan"
                : speaker

            if SpeechDurationEstimate.forText(
                text, language: language).needsSections {
                log.log("Generating long text in recoverable sections")
                let completed = try await generateLongText(
                    initialModel: model, mode: mode, text: text,
                    speaker: effectiveSpeaker, instruct: instruct, language: language,
                    referenceAudioURL: referenceAudioURL,
                    referenceText: referenceText,
                    referenceTranscriptAudioSeconds: referenceTranscriptAudioSeconds)
                let saved = try await saveLongText(
                    completed.samples, mode: mode, text: text,
                    speaker: effectiveSpeaker, instruct: instruct, language: language,
                    referenceText: referenceText,
                    continuationModelRepo: completed.continuationModelRepo)
                if saved.gain < 1 {
                    log.log(String(
                        format: "Output level: reduced by %.1f dB to prevent clipping.",
                        -20 * log10(saved.gain)))
                }
                log.log(String(
                    format: "Saved %@ — %.1f s accepted audio, %d frames, %.1f s total",
                    saved.url.path,
                    Double(completed.samples.count) / 24_000,
                    completed.frames,
                    Date().timeIntervalSince(generateStart)))
                await releaseGenerationMemory()
                generationDetail = nil
                lastOutputURL = saved.url
                lastGenerationSummary = GenerationSummary(
                    outputURL: saved.url, mode: mode,
                    durationSeconds: Double(completed.samples.count) / 24_000,
                    frames: completed.frames,
                    elapsedSeconds: Date().timeIntervalSince(generateStart),
                    modelSource: PrivacyRedactor.modelSource(mode.effectiveRepoID),
                    metadata: saved.metadata)
                status = .idle
                return
            }

            status = .generating(0)
            var referenceSamples: [Float]?
            var runtimeReferenceText: String?
            if mode == .voiceClone {
                guard let refURL = referenceAudioURL else {
                    throw TTSError.missingReference
                }
                log.log("Cloning voice from \(refURL.lastPathComponent)")
                let gotAccess = refURL.startAccessingSecurityScopedResource()
                defer { if gotAccess { refURL.stopAccessingSecurityScopedResource() } }

                // ICL cloning needs the reference transcript. If the user left
                // it blank, transcribe the clip on-device so they don't have to.
                let refText: String
                let typed = referenceText?.trimmingCharacters(in: .whitespacesAndNewlines)
                let hasProvidedTranscript = typed?.isEmpty == false
                let audioMaximumSeconds = ReferenceClipPolicy.audioMaximumSeconds(
                    hasProvidedTranscript: hasProvidedTranscript,
                    savedTranscriptAudioSeconds: referenceTranscriptAudioSeconds)
                if let typed, !typed.isEmpty {
                    refText = typed
                } else {
                    refText = try await transcribeReference(
                        url: refURL, language: language)
                    lastReferenceTranscript = refText
                    lastReferenceTranscriptAudioSeconds = audioMaximumSeconds
                    logReferenceTranscription(refText)
                    status = .generating(0)
                }

                log.log("Preparing reference audio — resampling to "
                    + "\(model.sampleRate / 1000) kHz mono")
                if let audioMaximumSeconds {
                    log.log(String(
                        format: "Using the first %.1f seconds for matching reference audio and transcript",
                        audioMaximumSeconds))
                }
                referenceSamples = try Self.loadReferenceAudio(
                    from: refURL, targetSampleRate: Double(model.sampleRate),
                    maximumSeconds: audioMaximumSeconds)
                    .asArray(Float.self)
                runtimeReferenceText = refText
            } else {
                if mode == .presetVoice {
                    log.log("Generating with speaker \(speaker ?? "default")")
                } else {
                    log.log("Generating designed voice")
                }
            }
            let take = try await synthesize(
                request: SynthesisRequest(
                    mode: mode, text: text, speaker: effectiveSpeaker,
                    instruct: (instruct?.isEmpty == false) ? instruct : nil,
                    language: language, referenceAudio: referenceSamples,
                    referenceText: runtimeReferenceText),
                sampleRate: model.sampleRate, maximumFrames: nil,
                progressLabel: nil, readySamples: 0)
            guard take.reachedEndOfSpeech else {
                throw TTSError.missingTermination
            }
            let audio = take.samples
            let generatedFrames = take.frames
            log.log("Generation ended at EOS after \(generatedFrames) frames")

            let url = outputDir.appendingPathComponent(Self.fileName(for: mode))
            try Task.checkCancellation()

            // The runtime actor evaluates MLX output before it crosses the
            // boundary, so file preparation receives ordinary Sendable
            // samples and remains safely off the main actor.
            status = .finalizing
            let rate = Double(model.sampleRate)
            // One UI field means two different things: the delivery
            // instruction in preset voice, the voice description in voice
            // design. Recording which it was is the difference between a file
            // that can be reproduced and one that merely has a string in it.
            let nonEmpty: (String?) -> String? = { value in
                let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines)
                return (trimmed?.isEmpty == false) ? trimmed : nil
            }
            let metadata = OutputMetadata(
                mode: mode.rawValue,
                text: text,
                language: language,
                speaker: mode == .presetVoice ? nonEmpty(effectiveSpeaker) : nil,
                style: mode == .presetVoice ? nonEmpty(instruct) : nil,
                voiceDescription: mode == .voiceDesign ? nonEmpty(instruct) : nil,
                referenceTranscript: mode == .voiceClone
                    ? nonEmpty(referenceText) ?? nonEmpty(lastReferenceTranscript)
                    : nil,
                modelRepo: PrivacyRedactor.modelSource(mode.effectiveRepoID),
                appVersion: Self.appVersion,
                created: Date()
            )
            let savedOutput = try await Task.detached(priority: .userInitiated) {
                // Proves the offload rather than trusting it: this traps if the
                // evaluation is ever back on the main thread.
                dispatchPrecondition(condition: .notOnQueue(.main))
                let prepared = try OutputLevel.prepare(audio)
                try saveAudioArray(MLXArray(prepared.samples), sampleRate: rate, to: url)
                // Tagging is best-effort: a file that plays but lacks its
                // metadata is a far better outcome than losing the audio
                // because a chunk could not be appended.
                try? WAVMetadata.embed(metadata, in: url)
                return (gain: prepared.gain, samples: prepared.samples.count)
            }.value
            if savedOutput.gain < 1 {
                log.log(String(format: "Output level: reduced by %.1f dB to prevent clipping.",
                               -20 * log10(savedOutput.gain)))
            }
            log.log(String(format: "Saved %@ (%.1f s total)", url.path,
                           Date().timeIntervalSince(generateStart)))
            await releaseGenerationMemory()
            lastOutputURL = url
            lastGenerationSummary = GenerationSummary(
                outputURL: url, mode: mode,
                durationSeconds: Double(savedOutput.samples) / rate,
                frames: generatedFrames,
                elapsedSeconds: Date().timeIntervalSince(generateStart),
                modelSource: PrivacyRedactor.modelSource(mode.effectiveRepoID),
                metadata: metadata)
            status = .idle
        } catch is CancellationError {
            if modelInfo == nil { modelLease = nil }
            lastFailure = RuntimeFailure(
                code: "cancelled", message: "Speech generation was cancelled.",
                exitCode: 5)
            await finishStopping()
        } catch let urlError as URLError where urlError.code == .cancelled {
            if modelInfo == nil { modelLease = nil }
            lastFailure = RuntimeFailure(
                code: "cancelled", message: "Speech generation was cancelled.",
                exitCode: 5)
            await finishStopping()
        } catch let error as DownloadServiceError {
            if modelInfo == nil { modelLease = nil }
            log.log("Error: \(String(describing: error))")
            await releaseGenerationMemory()
            generationDetail = nil
            if case .unavailable(_, let paused, _) = error,
               case .baseURL(let source) = mode.effectiveSource,
               mode.isUsingBuiltInMirror(source) {
                downloadRecovery = DownloadRecoveryOffer(
                    mode: mode, sourceURL: source, paused: paused)
            }
            let code: String
            switch error {
            case .unavailable: code = "download_service_unavailable"
            case .rateLimited: code = "download_rate_limited"
            case .httpStatus: code = "download_failed"
            }
            lastFailure = RuntimeFailure(
                code: code, message: error.localizedDescription, exitCode: 10)
            status = .error("Model download failed. \(error.localizedDescription)")
        } catch {
            if modelInfo == nil { modelLease = nil }
            log.log("Error: \(String(describing: error))")
            // A run that threw allocated just as much as one that succeeded.
            // Releasing only on success left the cache held by exactly the runs
            // most likely to have been killed by memory pressure in the first
            // place.
            await releaseGenerationMemory()
            generationDetail = nil
            let stage: String
            switch status {
            case .downloading: stage = "Model download failed"
            case .loading: stage = "Model loading failed"
            case .checking: stage = "Model preparation failed"
            case .finalizing: stage = "Preparing the audio file failed"
            default: stage = "Speech generation failed"
            }
            let code: String
            let exitCode: Int32
            if error is BunyiBusyError {
                code = "bunyi_busy"
                exitCode = 4
            } else if let ttsError = error as? TTSError {
                switch ttsError {
                case .missingReference:
                    code = "missing_input"
                    exitCode = 3
                case .generationDidNotFinish, .missingTermination:
                    code = "generation_did_not_finish"
                    exitCode = 10
                default:
                    code = "generation_failed"
                    exitCode = 10
                }
            } else {
                code = "generation_failed"
                exitCode = 10
            }
            lastFailure = RuntimeFailure(
                code: code, message: error.localizedDescription,
                exitCode: exitCode)
            status = .error("\(stage). \(error.localizedDescription)")
        }
    }

    /// Cancels the visible work. The generation Task is cancelled by the
    /// caller; downloads and streaming then stop at their next checkpoint.
    ///
    /// Does NOT report idle straight away. Generation cancellation is
    /// cooperative at codec-frame boundaries, so the detached inference worker
    /// can still be inside the shared non-Sendable model for a short interval.
    /// Going idle here would allow a second job, or a mode switch that releases
    /// the model and clears MLX buffers, before the first worker lets go.
    ///
    /// So the app stays busy, showing "Stopping…", until the abandoned work is
    /// actually finished. The wait is real work, not an artificial delay.
    public func stop() {
        downloadDetail = nil
        downloadFeedback = nil
        generationDetail = nil
        // Only shows the intent. `generate` always runs its cancellation path
        // afterwards, and that is what decides when idle is true — doing the
        // wait here instead would race with it, because the work to wait on is
        // not registered until the cancellation actually lands.
        status = status.isBusy ? .stopping : .idle
    }

    private func transcribeReference(url: URL, language: String) async throws
        -> String {
        let normalized = language.lowercased()
        if normalized != "auto" {
            status = .transcribing
            log.log("No transcript given — transcribing the reference clip on-device")
            do {
                return try await ReferenceTranscriber.transcribe(
                    url: url, locale: Self.locale(for: language),
                    maximumSeconds: ReferenceClipPolicy.automaticTranscriptSeconds)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                log.log("Apple on-device transcription did not succeed — "
                    + "using Bunyi's local multilingual transcriber")
            }
        } else {
            log.log("No transcript given — detecting its language and "
                + "transcribing locally")
        }

        let modelURL = try await prepareLocalTranscriptionModel()
        status = .transcribing
        generationDetail = "Transcribing the reference clip locally…"
        let control = WhisperTranscriptionControl()
        do {
            let text = try await LocalWhisperTranscriber.transcribe(
                url, modelURL: modelURL, language: language,
                maximumSeconds: ReferenceClipPolicy.automaticTranscriptSeconds,
                control: control)
            generationDetail = nil
            return text
        } catch is CancellationError {
            control.cancel()
            generationDetail = nil
            throw CancellationError()
        } catch {
            generationDetail = nil
            log.log("Local transcription failed: \(error.localizedDescription)")
            throw TTSError.transcriptionFailed
        }
    }

    private func prepareLocalTranscriptionModel() async throws -> URL {
        let download = WhisperModelDownload()
        activeWhisperDownload = download
        downloadPurpose = .transcription
        generationDetail = "Checking the local transcription model…"
        downloadFeedback = ModelDownloadProgress(
            phase: .checking,
            available: WhisperModelStore.status().downloadedBytes,
            total: WhisperModelStore.expectedBytes,
            file: WhisperModelStore.fileName,
            fileTotal: WhisperModelStore.expectedBytes)
        status = .checking

        let operation = Task { @MainActor in
            try await download.run(ownsLease: modelLease != nil)
        }
        let ticker = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !Task.isCancelled else { break }
                let progress = download.snapshot()
                self.downloadFeedback = progress
                switch progress.phase {
                case .downloading, .reconnecting, .waiting:
                    self.status = .downloading(progress.fraction)
                case .checking, .sizing, .verifying:
                    self.status = .checking
                }
            }
        }
        defer {
            ticker.cancel()
            activeWhisperDownload = nil
            downloadFeedback = nil
            downloadPurpose = .voice
        }

        return try await withTaskCancellationHandler {
            try await operation.value
        } onCancel: {
            operation.cancel()
        }
    }

    /// Work that was abandoned by cancellation but is still running inside the
    /// inference engine. Awaiting it is how the app knows the model is free.
    private var pendingWork: Task<Void, Never>?

    /// Ends a cancelled run: stay busy until the inference engine has really
    /// let go of the model, then report idle. Everything that starts a
    /// generation is gated on `status.isBusy`, so this window is what makes a
    /// second job impossible rather than merely unlikely.
    private func finishStopping() async {
        downloadDetail = nil
        generationDetail = nil
        if let pending = pendingWork {
            status = .stopping
            log.log("Stopping — the model is still generating; waiting for it")
            await pending.value
            pendingWork = nil
        }
        // After the abandoned work has actually finished, never before it.
        // Cancellation is cooperative (see `EngineStatus.stopping`); clearing
        // the cache before the worker reaches a frame boundary could hand back
        // buffers it is still using.
        //
        // A nil `pendingWork` is not a gap in that. Only two other points can
        // throw cancellation, and neither leaves MLX working: the check before
        // the save runs after the stream has ended, with the audio still an
        // unevaluated graph nothing is touching; and a cancellation during the
        // detached save cannot arrive here at all, because `try await
        // task.value` on an unstructured task does not throw when the awaiting
        // task is cancelled — it waits for the save, and the success path
        // below releases the cache once it is done.
        await releaseGenerationMemory()
        status = .idle
        log.log("Stopped the current operation")
    }

    /// Speech-recognition locale for the UI's language choice.
    public static func locale(for language: String) -> Locale {
        let map = [
            "english": "en-US", "chinese": "zh-CN", "japanese": "ja-JP",
            "korean": "ko-KR", "german": "de-DE", "french": "fr-FR",
            "russian": "ru-RU", "portuguese": "pt-BR", "spanish": "es-ES",
            "italian": "it-IT",
        ]
        return Locale(identifier: map[language.lowercased()] ?? "en-US")
    }

    /// Load reference audio as mono at the model's rate. The package's
    /// `loadAudioArray` keeps the file's native rate and takes channel 0;
    /// voice cloning needs 24 kHz mono, and `generateVoiceClone` has no
    /// sample-rate argument, so it assumes 24 kHz. Feeding a 44.1/48 kHz clip
    /// unchanged is exactly what produces distorted, wrong-pitch output.
    nonisolated private static func loadReferenceAudio(
        from url: URL, targetSampleRate: Double,
        maximumSeconds: TimeInterval? = nil
    ) throws -> MLXArray {
        let file = try AVAudioFile(forReading: url)
        let inFormat = file.processingFormat
        let framesToRead = maximumSeconds.map {
            min(file.length, AVAudioFramePosition($0 * inFormat.sampleRate))
        } ?? file.length
        guard let inBuffer = AVAudioPCMBuffer(
            pcmFormat: inFormat,
            frameCapacity: AVAudioFrameCount(framesToRead)) else {
            throw TTSError.referenceAudioUnreadable
        }
        try file.read(into: inBuffer, frameCount: AVAudioFrameCount(framesToRead))

        guard let outFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: targetSampleRate,
            channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inFormat, to: outFormat)
        else { throw TTSError.referenceAudioUnreadable }

        // Downsampling shrinks frame count, upsampling grows it; pad the
        // output capacity so a full conversion always fits.
        let ratio = targetSampleRate / inFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(inBuffer.frameLength) * ratio) + 4096
        guard let outBuffer = AVAudioPCMBuffer(
            pcmFormat: outFormat, frameCapacity: capacity) else {
            throw TTSError.referenceAudioUnreadable
        }

        // The input block is @Sendable but AVAudioConverter calls it
        // synchronously here, so the non-Sendable buffer crosses no real
        // thread boundary — carry it in an unchecked box to satisfy the check.
        let inBox = UncheckedAudioBuffer(value: inBuffer)
        nonisolated(unsafe) var provided = false
        var convError: NSError?
        converter.convert(to: outBuffer, error: &convError) { _, inStatus in
            if provided {
                inStatus.pointee = .endOfStream
                return nil
            }
            provided = true
            inStatus.pointee = .haveData
            return inBox.value
        }
        if let convError { throw convError }

        guard let channel = outBuffer.floatChannelData,
              outBuffer.frameLength > 0 else {
            throw TTSError.referenceAudioEmpty
        }
        let samples = Array(UnsafeBufferPointer(
            start: channel[0], count: Int(outBuffer.frameLength)))
        return MLXArray(samples)
    }

    /// `AVAudioConverter` invokes its input block synchronously even though the
    /// SDK annotates that block Sendable. The buffer never leaves this call.
    private final class UncheckedAudioBuffer: @unchecked Sendable {
        let value: AVAudioPCMBuffer
        init(value: AVAudioPCMBuffer) { self.value = value }
    }

    private static func fileName(for mode: TTSMode) -> String {
        let stamp = Date().formatted(.iso8601.year().month().day()
            .timeSeparator(.omitted).time(includingFractionalSeconds: false))
            .replacingOccurrences(of: ":", with: "")
        return "\(mode.rawValue.replacingOccurrences(of: " ", with: "-"))-\(stamp).wav"
    }

    private func logReferenceTranscription(_ text: String) {
        if CLISettingsStore.isCLI {
            log.log("Reference transcription completed")
        } else {
            log.log("Reference transcript: \"\(text)\"")
        }
    }

    public func revealLastOutput() {
        guard let url = lastOutputURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

public enum TTSError: LocalizedError {
    case missingReference
    case noAudio
    case tokenizerDownloadFailed
    case referenceAudioUnreadable
    case referenceAudioEmpty
    case transcriptionNotAuthorized
    case transcriptionUnavailable
    case transcriptionFailed
    case transcriptionEmpty
    case selfHostFileMissing(String, Int)
    case selfHostIncomplete
    case generationDidNotFinish
    case missingTermination
    case invalidSectionAudio

    public var errorDescription: String? {
        switch self {
        case .missingReference: "Choose a reference audio clip first."
        case .noAudio: "The model finished without producing audio. Try again."
        case .tokenizerDownloadFailed:
            "Couldn't fetch the tokenizer file this model is missing. Check your connection and try again."
        case .referenceAudioUnreadable:
            "Couldn't read that reference clip. Try a WAV, M4A, or MP3 file."
        case .referenceAudioEmpty:
            "The reference clip seems to be empty. Use 5–10 seconds of clean speech."
        case .transcriptionNotAuthorized:
            "Allow speech recognition in System Settings > Privacy, or type the reference transcript yourself."
        case .transcriptionUnavailable:
            "Speech recognition isn't available for this language. Type the reference transcript yourself."
        case .transcriptionFailed:
            "Automatic transcription failed. Use a clean speech clip and try again."
        case .transcriptionEmpty:
            "Couldn't make out any speech in the reference clip. Use a clean clip, or type the transcript yourself."
        case .selfHostFileMissing(let name, let code):
            "Your server is missing a required model file: \(name) (HTTP \(code)). Check the URL and that the file is published."
        case .selfHostIncomplete:
            "The download from your server didn't produce a complete model (needs config.json and a .safetensors file). Check the files or add a manifest.txt."
        case .generationDidNotFinish:
            "The model did not finish speaking before the safety limit. Please generate again. If it happens repeatedly, try a shorter passage."
        case .missingTermination:
            "The model did not report whether it finished speaking. Please generate again."
        case .invalidSectionAudio:
            "The model produced invalid section audio. Please generate again."
        }
    }

}

#if canImport(AppKit)
import AppKit
#endif
