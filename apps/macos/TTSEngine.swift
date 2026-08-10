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
//  Owns model download, loading, and generation. One model resident at a
//  time (Apple unified memory friendly). Models are fetched from Hugging
//  Face via swift-transformers' Hub API into Application Support, so end
//  users never touch a terminal.
//

import AVFoundation
import Foundation
import Hub
import MLX
import Qwen3TTS

// MARK: - Modes

enum TTSMode: String, CaseIterable, Identifiable {
    case presetVoice = "Preset voice"
    case voiceDesign = "Voice design"
    case voiceClone = "Voice clone"

    var id: String { rawValue }

    /// Hugging Face repo backing each mode.
    /// Swap the CustomVoice repo for
    /// "AtomGradient/Qwen3-TTS-0.6B-CustomVoice-4bit-pruned-vocab-lite"
    /// (808 MB) if download size matters more than max fidelity.
    var repoID: String {
        switch self {
        case .presetVoice: "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16"
        case .voiceDesign: "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16"
        case .voiceClone:  "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16"
        }
    }

    /// Rough total download size, used only for throughput and ETA
    /// estimates in the log — the Hub API only reports a fraction.
    var approxDownloadBytes: Double {
        switch self {
        case .presetVoice: 1.4e9
        case .voiceDesign: 3.4e9
        case .voiceClone:  3.4e9
        }
    }
}

// MARK: - Engine state

enum EngineStatus: Equatable {
    case idle
    case downloading(Double)      // 0...1
    case loading
    case transcribing             // auto-transcribing the clone reference
    case generating(Int)          // codec tokens emitted so far
    /// Cancelled, but the inference engine is still computing. Cancellation is
    /// cooperative and only the consumer stops: the package generates on a
    /// thread of its own that runs to completion. Reporting idle here would be
    /// a lie the UI acts on — it re-enables Generate, and a second job would
    /// then touch the same model as the first.
    case stopping
    case error(String)

    var isBusy: Bool {
        switch self {
        case .idle, .error: false
        default: true
        }
    }
}

// MARK: - Engine

@MainActor
@Observable
final class TTSEngine {
    var status: EngineStatus = .idle
    var lastOutputURL: URL?

    /// Forgets the previous run's file so the UI stops offering it. Called when
    /// a new run starts: the old audio is still on disk and still reachable in
    /// the Outputs folder, but presenting Play beside a run in progress invites
    /// listening to the previous result and taking it for the new one.
    func clearLastOutput() {
        lastOutputURL = nil
    }

    /// The Outputs folder itself, for History and "Show in Finder".
    var outputsFolder: URL { outputDir }

    /// Stamped into every generated file so a WAV says which build made it.
    static var appVersion: String {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "0.0.0"
        let build = info?["CFBundleVersion"] as? String ?? "0"
        return build == short ? short : "\(short) (\(build))"
    }

    /// Everything generated so far, newest first. Read from disk rather than
    /// tracked in memory: the folder is the record, it survives relaunches, and
    /// a file deleted in Finder should disappear from History without the app
    /// needing to be told.
    func generatedOutputs() -> [GeneratedOutput] {
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
    var downloadDetail: String?
    /// Transcript produced by auto-transcription, so the UI can show it and
    /// save it with the voice instead of storing an empty string.
    var lastReferenceTranscript: String?

    private var loadedRepo: String?
    /// Where the loaded model was read from. Compared against a deletion rather
    /// than matching repo-ID strings, which differ in shape between a Hub repo
    /// and a self-hosted base URL.
    private var loadedDir: URL?
    private var model: Qwen3TTSModel?

    private let log = LogStore.shared
    private var downloadStart = Date()
    private var downloadApproxBytes: Double = 0
    private var lastLoggedPercent = -1
    /// When bytes last arrived. The stall detector needs this because a
    /// download task buffers to the system temp directory and only moves the
    /// finished file into the models folder — so folder size, which is all the
    /// monitor could see, stays flat for minutes on a multi-gigabyte file and
    /// a healthy transfer looks dead.
    private var lastDownloadActivity = Date()

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

    var speakers: [String] { model?.supportedSpeakers ?? [] }

    /// Held so the observer can be removed. NotificationCenter keeps the token
    /// alive until it is, so dropping it on the floor leaks the registration
    /// and stacks up another unload attempt for every engine ever created.
    /// nonisolated so `deinit`, which is not main-actor isolated, can read it.
    /// Only ever written once during init and read once during deinit.
    private nonisolated(unsafe) var deletionObserver: (any NSObjectProtocol)?

    init() {
        // Settings can delete a model while it is loaded. Without this the app
        // keeps generating from memory with its folder gone, and the next
        // launch re-downloads with nothing having explained why.
        deletionObserver = NotificationCenter.default.addObserver(
            forName: ModelStore.didDeleteModel, object: nil, queue: .main
        ) { [weak self] note in
            guard let deleted = note.object as? URL else { return }
            MainActor.assumeIsolated { self?.forgetModel(at: deleted) }
        }
    }

    deinit {
        if let deletionObserver {
            NotificationCenter.default.removeObserver(deletionObserver)
        }
    }

    /// Drops the in-memory model if it came from `dir`.
    private func forgetModel(at dir: URL) {
        guard let loadedDir,
              loadedDir.standardizedFileURL == dir.standardizedFileURL else { return }
        log.log("Unloading \(loadedRepo ?? "the model") — its files were deleted")
        model = nil
        loadedRepo = nil
        self.loadedDir = nil
        MLX.GPU.clearCache()
    }

    // MARK: Model lifecycle

    /// Download (if needed) and load the model for `mode`.
    func prepare(mode: TTSMode) async throws -> Qwen3TTSModel {
        let repoID = mode.effectiveRepoID
        if let model, loadedRepo == repoID { return model }

        // Evict the previous model before loading the next one.
        if loadedRepo != nil { log.log("Unloading previous model") }
        self.model = nil
        self.loadedRepo = nil
        self.loadedDir = nil
        MLX.GPU.clearCache()

        log.log("Preparing \(mode.rawValue) — \(repoID)")
        let source = mode.effectiveSource
        let localDir = try await download(mode: mode, source: source)
        try await ensureTokenizerJSON(in: localDir, source: source)

        status = .loading
        log.log("Loading model into memory…")
        let loadStart = Date()
        let loaded = try await Qwen3TTSModel.fromPretrained(localDir.path)
        log.log(String(format: "Model loaded in %.1f s",
                       Date().timeIntervalSince(loadStart)))
        self.model = loaded
        self.loadedRepo = repoID
        self.loadedDir = localDir
        return loaded
    }

    private func download(mode: TTSMode, source: ModelSource) async throws -> URL {
        switch source {
        case .repo(let repoID):
            return try await downloadFromHub(mode: mode, repoID: repoID)
        case .baseURL(let url):
            return try await downloadFromBaseURL(mode: mode, base: url)
        }
    }

    private func downloadFromHub(mode: TTSMode, repoID: String) async throws -> URL {
        let base = ModelsLocation.current()
        let localDir = base.appendingPathComponent("models/\(repoID)",
                                                   isDirectory: true)

        // Pre-downloaded (or previously completed) models are used as-is,
        // without touching the network — this also makes the app fully
        // offline once a model is in place.
        if Self.hasCompleteModel(at: localDir) {
            log.log("Using existing model files at \(localDir.path)")
            return localDir
        }

        let hub = HubApi(downloadBase: base)
        status = .downloading(0)
        downloadStart = Date()
        downloadApproxBytes = mode.approxDownloadBytes
        lastLoggedPercent = -1
        lastDownloadActivity = Date()
        log.log("Checking model files (already-downloaded files are skipped)")
        let monitor = startDiskMonitor(at: base)
        defer { monitor.cancel() }
        // Snapshot is incremental: already-downloaded files are skipped, so
        // this is fast when the model is cached.
        let dir = try await hub.snapshot(
            from: Hub.Repo(id: repoID),
            matching: ["*.safetensors", "*.json", "*.model", "*.txt"]
        ) { @Sendable [weak self] progress in
            let fraction = progress.fractionCompleted
            Task { @MainActor in
                self?.noteDownloadProgress(fraction)
            }
        }
        downloadDetail = nil
        log.log("Model files ready at \(dir.path)")
        return dir
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
        let root = ModelsLocation.current()
        let localDir = root.appendingPathComponent(
            "models/self-hosted/\(Self.slug(for: base))", isDirectory: true)

        if Self.hasCompleteModel(at: localDir) {
            log.log("Using existing model files at \(localDir.path)")
            return localDir
        }

        status = .downloading(0)
        downloadStart = Date()
        downloadApproxBytes = mode.approxDownloadBytes
        lastLoggedPercent = -1
        lastDownloadActivity = Date()

        let files = try await fileList(base: base)
        log.log("Downloading \(files.count) files from \(base.absoluteString)")
        try FileManager.default.createDirectory(
            at: localDir, withIntermediateDirectories: true)
        let monitor = startDiskMonitor(at: localDir)
        defer { monitor.cancel() }

        for (index, relPath) in files.enumerated() {
            try Task.checkCancellation()
            try await downloadFile(relPath: relPath, base: base, into: localDir,
                                   fileIndex: index, fileCount: files.count)
            noteDownloadProgress(Double(index + 1) / Double(files.count))
        }

        downloadDetail = nil
        guard Self.hasCompleteModel(at: localDir) else {
            throw TTSError.selfHostIncomplete
        }
        log.log("Model files ready at \(localDir.path)")
        return localDir
    }

    /// manifest.txt from the server if present, else the built-in file set.
    private func fileList(base: URL) async throws -> [String] {
        let manifestURL = base.appendingPathComponent("manifest.txt")
        if let (data, response) = try? await URLSession.shared.data(from: manifestURL),
           (response as? HTTPURLResponse)?.statusCode == 200,
           let text = String(data: data, encoding: .utf8) {
            let paths = text.split(whereSeparator: \.isNewline)
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty && !$0.hasPrefix("#") }
                .compactMap { line -> String? in
                    guard let safe = Self.safeRelativePath(line) else {
                        log.log("Ignoring unsafe manifest path: \(line)")
                        return nil
                    }
                    return safe
                }
            if !paths.isEmpty {
                log.log("Using manifest.txt (\(paths.count) files)")
                return paths
            }
        }
        log.log("No manifest.txt — using the built-in Qwen3-TTS file list")
        return Self.defaultModelFiles
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

    /// Downloads one file, reporting progress within it.
    ///
    /// `fileFraction` places this file in the whole job: without it the bar only
    /// moves when a file finishes, and one file is nearly the entire model, so
    /// it appeared frozen for minutes on end.
    private func downloadFile(relPath: String, base: URL, into localDir: URL,
                              fileIndex: Int, fileCount: Int) async throws {
        let fileURL = base.appendingPathComponent(relPath)
        let dest = localDir.appendingPathComponent(relPath)

        // Already have it? Skip. Without this, stopping a download and starting
        // again fetched every file from the beginning — several gigabytes to
        // re-transfer because the last one was interrupted. The Hugging Face
        // path has always been incremental; this one was not.
        //
        // Size against the server's, not mere existence: a file interrupted
        // mid-write is present and wrong, and "it exists" would keep it
        // forever.
        if let local = try? FileManager.default.attributesOfItem(atPath: dest.path)[.size] as? Int64,
           let remote = await Self.remoteSize(of: fileURL), local == remote {
            log.log("Have \(relPath) already (\(local.formatted(.byteCount(style: .file))))")
            return
        }

        let code = try await HTTPFileDownloader.download(from: fileURL, to: dest) {
            [weak self] received, expected in
            guard expected > 0 else { return }
            let within = Double(received) / Double(expected)
            let overall = (Double(fileIndex) + within) / Double(fileCount)
            Task { @MainActor [weak self] in
                self?.noteDownloadProgress(overall)
            }
        }

        guard code == 200 else {
            if Self.requiredModelFiles.contains(relPath) {
                throw TTSError.selfHostFileMissing(relPath, code)
            }
            log.log("Skipped \(relPath) (HTTP \(code))")
            return
        }
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
    nonisolated private static func remoteSize(of url: URL) async -> Int64? {
        var request = URLRequest(url: url)
        request.httpMethod = "HEAD"
        guard let (_, response) = try? await URLSession.shared.data(for: request),
              let http = response as? HTTPURLResponse,
              http.statusCode == 200,
              response.expectedContentLength > 0 else { return nil }
        return response.expectedContentLength
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

        for url in candidates {
            if let (tmp, response) = try? await URLSession.shared.download(from: url),
               (response as? HTTPURLResponse)?.statusCode == 200 {
                try FileManager.default.moveItem(at: tmp, to: dest)
                log.log("Added tokenizer.json to \(dir.path)")
                return
            }
        }
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

    /// The Hub fraction only moves when a whole file completes, so during a
    /// multi-GB safetensors file it looks frozen. Watching bytes on disk
    /// tells stalled and slow apart.
    private func startDiskMonitor(at dir: URL) -> Task<Void, Never> {
        Task { [log, weak self] in
            var lastSize: Int64 = -1
            var lastChange = Date()
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(10))
                if Task.isCancelled { break }
                let size = Self.directorySize(dir)
                if size != lastSize {
                    lastSize = size
                    lastChange = Date()
                    log.log("\(size.formatted(.byteCount(style: .file))) on disk in the models folder")
                    continue
                }
                // Bytes still arriving counts as progress even though the file
                // has not landed yet. Without this the warning fires on every
                // multi-gigabyte download, and a warning that cries wolf is
                // worse than none — it trains people to ignore the real one.
                let receiving = await MainActor.run {
                    guard let self else { return false }
                    return Date().timeIntervalSince(self.lastDownloadActivity) < 30
                }
                if receiving {
                    lastChange = Date()
                } else if Date().timeIntervalSince(lastChange) >= 30 {
                    lastChange = Date()
                    log.log("No new data for 30 s — the connection may be stalled")
                }
            }
        }
    }

    nonisolated private static func directorySize(_ dir: URL) -> Int64 {
        var total: Int64 = 0
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileSizeKey]
        if let enumerator = FileManager.default.enumerator(
            at: dir, includingPropertiesForKeys: keys) {
            for case let url as URL in enumerator {
                let values = try? url.resourceValues(forKeys: Set(keys))
                total += Int64(values?.totalFileAllocatedSize ?? values?.fileSize ?? 0)
            }
        }
        return total
    }

    private func noteDownloadProgress(_ fraction: Double) {
        status = .downloading(fraction)
        lastDownloadActivity = Date()
        let elapsed = Date().timeIntervalSince(downloadStart)
        guard fraction > 0.001, elapsed > 1 else { return }

        // The Hub API only reports a fraction, so rate and ETA are estimates
        // based on the model's approximate size. Cached files complete
        // instantly and inflate the early numbers; they settle quickly.
        let rate = downloadApproxBytes * fraction / elapsed
        let secondsLeft = elapsed * (1 - fraction) / fraction
        let percent = Int(fraction * 100)
        let detail = "\(percent)% — about "
            + "\(Int64(rate).formatted(.byteCount(style: .file)))/s, "
            + Self.etaText(secondsLeft)
        downloadDetail = detail

        if percent / 5 != lastLoggedPercent / 5 || percent == 100 {
            lastLoggedPercent = percent
            log.log("Download \(detail)")
        }
    }

    private static func etaText(_ seconds: Double) -> String {
        seconds < 90
            ? "under a minute left"
            : "about \(Int((seconds / 60).rounded())) min left"
    }

    // MARK: Generation

    func generate(
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        referenceAudioURL: URL?,
        referenceText: String?
    ) async {
        // Serialization is not a nicety here: one model, non-Sendable, shared
        // by everything below. `stop()` keeps the app busy until abandoned work
        // finishes, so reaching this while busy would mean the UI let through a
        // second job. Refuse rather than race.
        guard !status.isBusy else {
            log.log("Ignoring Generate — the previous job has not finished")
            return
        }
        do {
            let model = try await prepare(mode: mode)
            try Task.checkCancellation()
            status = .generating(0)
            let generateStart = Date()

            let audio: MLXArray
            switch mode {
            case .voiceClone:
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
                if let typed, !typed.isEmpty {
                    refText = typed
                } else {
                    status = .transcribing
                    log.log("No transcript given — transcribing the reference "
                        + "clip on-device")
                    refText = try await ReferenceTranscriber.transcribe(
                        url: refURL, locale: Self.locale(for: language))
                    lastReferenceTranscript = refText
                    log.log("Reference transcript: \"\(refText)\"")
                    status = .generating(0)
                }

                log.log("Preparing reference audio — resampling to "
                    + "\(model.sampleRate / 1000) kHz mono")
                let refAudio = try Self.loadReferenceAudio(
                    from: refURL, targetSampleRate: Double(model.sampleRate))

                // generateVoiceClone is synchronous and heavy. Run it off the
                // main actor so the UI stays responsive, and bridge its onToken
                // callback back as live progress over an AsyncStream. The model
                // and MLXArray aren't Sendable, so cross the boundary in an
                // unchecked box — safe because only one generation runs at once.
                let inputs = Unchecked(value: (model, refAudio))
                let (tokens, continuation) = AsyncStream.makeStream(of: Int.self)
                let cloneTask = Task.detached(priority: .userInitiated) {
                    defer { continuation.finish() }
                    var count = 0
                    let (m, ref) = inputs.value
                    let wav = try m.generateVoiceClone(
                        text: text,
                        referenceAudio: ref,
                        referenceText: refText,
                        language: language,
                        onToken: { _ in
                            count += 1
                            continuation.yield(count)
                        }
                    )
                    return Unchecked(value: wav)
                }
                for await count in tokens {
                    if Task.isCancelled { break }
                    if count % 5 == 0 { status = .generating(count) }
                    if count % 100 == 0 { log.log("Generated \(count) tokens…") }
                }
                // generateVoiceClone is synchronous and ignores cancellation,
                // so the detached task runs to completion whatever we do here.
                // Register it before checking, so `stop()` can wait for the
                // model to be free instead of declaring victory early.
                if Task.isCancelled {
                    pendingWork = Task.detached { _ = try? await cloneTask.value }
                    try Task.checkCancellation()
                }
                audio = try await cloneTask.value.value
                try Task.checkCancellation()

            case .presetVoice, .voiceDesign:
                if mode == .presetVoice {
                    log.log("Generating with speaker \(speaker ?? "default")")
                } else {
                    log.log("Generating designed voice")
                }
                // Stream so the UI can show live token progress. Held in a
                // local so it can still be drained if we stop consuming it —
                // the package generates on its own thread, and abandoning the
                // stream would leave that thread running against the model
                // with nothing tracking it.
                let stream = model.generateStream(
                    text: text,
                    speaker: mode == .presetVoice ? speaker : nil,
                    instruct: (instruct?.isEmpty == false) ? instruct : nil,
                    language: language
                )
                var final: MLXArray?
                var tokenCount = 0
                do {
                    for try await event in stream {
                        try Task.checkCancellation()
                        switch event {
                        case .token:
                            tokenCount += 1
                            if tokenCount % 5 == 0 { status = .generating(tokenCount) }
                            if tokenCount % 100 == 0 {
                                log.log("Generated \(tokenCount) tokens…")
                            }
                        case .info:
                            break
                        case .audio(let wav):
                            final = wav
                        }
                    }
                } catch {
                    // Cancelled mid-generation. Keep consuming in the
                    // background so the producer thread reaches its end and
                    // lets go of the model; `stop()` waits on this before it
                    // reports idle.
                    let draining = Unchecked(value: stream)
                    pendingWork = Task.detached {
                        do {
                            for try await _ in draining.value {}
                        } catch {
                            // The producer's own failure; nothing to report
                            // here, the run is already being cancelled.
                        }
                    }
                    throw error
                }
                guard let wav = final else { throw TTSError.noAudio }
                audio = wav
            }

            let url = outputDir.appendingPathComponent(Self.fileName(for: mode))
            try Task.checkCancellation()

            // This is where the beachball came from. MLX is lazy: the array the
            // generator yields is an unevaluated graph, and nothing in the
            // package evaluates it. The first thing that does is
            // `audio.asArray(Float.self)` inside saveAudioArray — so calling
            // that here ran the whole audio decode on the main actor, freezing
            // the UI at the very end of every generation, in every mode.
            //
            // Same unchecked box as the clone path above, for the same reason:
            // MLXArray isn't Sendable, and only one generation runs at a time.
            let boxed = Unchecked(value: audio)
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
                speaker: mode == .presetVoice ? nonEmpty(speaker) : nil,
                style: mode == .presetVoice ? nonEmpty(instruct) : nil,
                voiceDescription: mode == .voiceDesign ? nonEmpty(instruct) : nil,
                referenceTranscript: mode == .voiceClone
                    ? nonEmpty(referenceText) ?? nonEmpty(lastReferenceTranscript)
                    : nil,
                modelRepo: mode.effectiveRepoID,
                appVersion: Self.appVersion,
                created: Date()
            )
            try await Task.detached(priority: .userInitiated) {
                // Proves the offload rather than trusting it: this traps if the
                // evaluation is ever back on the main thread.
                dispatchPrecondition(condition: .notOnQueue(.main))
                try saveAudioArray(boxed.value, sampleRate: rate, to: url)
                // Tagging is best-effort: a file that plays but lacks its
                // metadata is a far better outcome than losing the audio
                // because a chunk could not be appended.
                try? WAVMetadata.embed(metadata, in: url)
            }.value
            log.log(String(format: "Saved %@ (%.1f s total)", url.path,
                           Date().timeIntervalSince(generateStart)))
            lastOutputURL = url
            status = .idle
        } catch is CancellationError {
            await finishStopping()
        } catch let urlError as URLError where urlError.code == .cancelled {
            await finishStopping()
        } catch {
            log.log("Error: \(String(describing: error))")
            status = .error(error.localizedDescription)
        }
    }

    /// Cancels the visible work. The generation Task is cancelled by the
    /// caller; downloads and streaming then stop at their next checkpoint.
    ///
    /// Does NOT report idle straight away. Cancelling stops the consumer, not
    /// the inference engine: for preset and design the package is generating on
    /// a `Thread.detachNewThread` it owns, and for clone a detached task is
    /// still inside `generateVoiceClone`. Both keep using the loaded model.
    /// Going idle here would re-enable Generate, and a second job would then be
    /// running against the same non-Sendable model as the first — which is the
    /// exact invariant `Unchecked<T>` is justified by. Worse, switching mode
    /// first makes `prepare` release that model and call `MLX.GPU.clearCache()`
    /// out from under the thread still computing on it.
    ///
    /// So the app stays busy, showing "Stopping…", until the abandoned work is
    /// actually finished. The wait is real work, not an artificial delay.
    func stop() {
        downloadDetail = nil
        // Only shows the intent. `generate` always runs its cancellation path
        // afterwards, and that is what decides when idle is true — doing the
        // wait here instead would race with it, because the work to wait on is
        // not registered until the cancellation actually lands.
        status = status.isBusy ? .stopping : .idle
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
        if let pending = pendingWork {
            status = .stopping
            log.log("Stopping — the model is still generating; waiting for it")
            await pending.value
            pendingWork = nil
        }
        status = .idle
        log.log("Stopped the current operation")
    }

    /// Carries a non-Sendable value across an actor boundary. Safe here
    /// because generation is serialized — one job touches it at a time.
    private struct Unchecked<T>: @unchecked Sendable {
        let value: T
    }

    /// Speech-recognition locale for the UI's language choice.
    private static func locale(for language: String) -> Locale {
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
        from url: URL, targetSampleRate: Double
    ) throws -> MLXArray {
        let file = try AVAudioFile(forReading: url)
        let inFormat = file.processingFormat
        guard let inBuffer = AVAudioPCMBuffer(
            pcmFormat: inFormat,
            frameCapacity: AVAudioFrameCount(file.length)) else {
            throw TTSError.referenceAudioUnreadable
        }
        try file.read(into: inBuffer)

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
        let inBox = Unchecked(value: inBuffer)
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

    private static func fileName(for mode: TTSMode) -> String {
        let stamp = Date().formatted(.iso8601.year().month().day()
            .timeSeparator(.omitted).time(includingFractionalSeconds: false))
            .replacingOccurrences(of: ":", with: "")
        return "\(mode.rawValue.replacingOccurrences(of: " ", with: "-"))-\(stamp).wav"
    }

    func revealLastOutput() {
        guard let url = lastOutputURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

enum TTSError: LocalizedError {
    case missingReference
    case noAudio
    case tokenizerDownloadFailed
    case referenceAudioUnreadable
    case referenceAudioEmpty
    case transcriptionNotAuthorized
    case transcriptionUnavailable
    case transcriptionEmpty
    case selfHostFileMissing(String, Int)
    case selfHostIncomplete

    var errorDescription: String? {
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
        case .transcriptionEmpty:
            "Couldn't make out any speech in the reference clip. Use a clean clip, or type the transcript yourself."
        case .selfHostFileMissing(let name, let code):
            "Your server is missing a required model file: \(name) (HTTP \(code)). Check the URL and that the file is published."
        case .selfHostIncomplete:
            "The download from your server didn't produce a complete model (needs config.json and a .safetensors file). Check the files or add a manifest.txt."
        }
    }
}

#if canImport(AppKit)
import AppKit
#endif
