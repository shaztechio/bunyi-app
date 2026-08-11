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
//  Doctor.swift
//  Bunyi
//
//  Preflight checks: can this machine finish a generation right now?
//

import Foundation
import MLX

/// How much a finding matters.
///
/// The distinction that earns its keep is `blocker` versus `warning`. A blocker
/// is a fact about now — there is no room on the disk, the server does not
/// answer — and acting on it saves the user a wait that could not have
/// succeeded. A warning is a prediction, and the app refuses to stop a run on
/// one, because a prediction that stops work is wrong in the expensive
/// direction: it refuses runs that would have finished.
enum DoctorSeverity {
    case ok
    case warning
    case blocker
}

/// One check's result.
struct DoctorFinding: Identifiable {
    let id = UUID()
    /// What was checked, as a noun phrase: "Disk space", "Memory".
    let title: String
    /// What was found, and what to do about it. Per `spec/FEATURES.md` §10 this
    /// is the actionable half — "Free up 4.2 GB", not "insufficient space".
    let detail: String
    let severity: DoctorSeverity

    var symbolName: String {
        switch severity {
        case .ok:      "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .blocker: "xmark.octagon.fill"
        }
    }
}

/// Everything a run found.
struct DoctorReport {
    /// Which mode's model this is about.
    ///
    /// Carried rather than inferred because it is not always on screen. The
    /// History tab has no mode of its own, so a run started there reports on
    /// the last one generated with — perfectly sensible, and completely opaque
    /// unless the report says so.
    let mode: TTSMode
    let findings: [DoctorFinding]

    var blockers: [DoctorFinding] { findings.filter { $0.severity == .blocker } }
    var warnings: [DoctorFinding] { findings.filter { $0.severity == .warning } }

    /// Nothing stands in the way of a generation. Warnings do not count — see
    /// `DoctorSeverity`.
    var isClear: Bool { blockers.isEmpty }

    /// One line per finding, for the Logs. Deliberately flat text: the Logs
    /// window is what gets pasted into a bug report, and a report is only
    /// useful there if it survives being copied as plain text.
    var logLines: [String] {
        ["Doctor: checking \(mode.rawValue)"] + findings.map { finding in
            let mark = switch finding.severity {
            case .ok:      "ok"
            case .warning: "warning"
            case .blocker: "blocked"
            }
            return "Doctor: [\(mark)] \(finding.title) — \(finding.detail)"
        }
    }
}

/// The checks themselves.
///
/// Deliberately not an `@Observable` model: a run is a function of the machine
/// at one instant, not state the app carries around. Whoever asked holds the
/// report for as long as they are showing it.
@MainActor
enum Doctor {

    /// How much room beyond the model's own size counts as comfortable.
    ///
    /// A download that exactly fits leaves a full disk behind it, and the next
    /// thing to write anything — including this app, saving the WAV it just
    /// generated — fails instead. Below this is a warning, not a refusal.
    private static let comfortableDiskHeadroom: Int64 = 5_000_000_000

    /// What a generation needs on top of the weights: activations, the audio
    /// being assembled, and the codec. A fraction rather than a constant
    /// because it scales with the model — the 1.7B models want proportionally
    /// more working room than the 0.6B one.
    private static let memoryWorkingFactor = 1.3

    /// Space to keep free for output, when no download is pending. Generated
    /// clips are megabytes, so this is about not writing onto a disk that is
    /// already at the edge rather than about the file itself.
    private static let outputHeadroom: Int64 = 500_000_000

    /// Run the checks.
    ///
    /// - Parameter deep: also verify downloaded files against the server's
    ///   published digests. Hashing gigabytes takes real time, so this is for
    ///   the on-demand run only — never the one before a generation.
    static func run(mode: TTSMode,
                    engine: TTSEngine,
                    deep: Bool = false) async -> DoctorReport {
        var findings: [DoctorFinding] = []

        let modelDir = TTSEngine.modelDirectory(for: mode)
        let isPresent = TTSEngine.isModelComplete(for: mode)
        let modelsRoot = ModelsLocation.current()

        findings.append(modelFinding(mode: mode, isPresent: isPresent))

        // Sizing the model folder walks it, and hashing reads all of it. Both
        // are disk work and neither belongs on the actor driving the window.
        let modelBytes: Int64 = isPresent
            ? await Task.detached(priority: .userInitiated) {
                ModelStore.size(of: modelDir)
            }.value
            : Int64(mode.approxDownloadBytes)

        findings.append(diskFinding(modelsRoot: modelsRoot,
                                    needsDownload: !isPresent,
                                    modelBytes: modelBytes))
        findings.append(memoryFinding(mode: mode,
                                      modelBytes: modelBytes,
                                      isPresent: isPresent))

        // Only worth asking when a run would actually reach for the network. A
        // reachability failure on a model already on disk is not a problem the
        // user has — the app is offline-capable once the files are there.
        if !isPresent {
            findings.append(await sourceFinding(mode: mode))
        }

        findings.append(outputFinding(folder: engine.outputsFolder))

        if deep, isPresent {
            findings.append(await integrityFinding(mode: mode,
                                                   engine: engine,
                                                   dir: modelDir))
        }

        return DoctorReport(mode: mode, findings: findings)
    }

    // MARK: - Individual checks

    private static func modelFinding(mode: TTSMode, isPresent: Bool) -> DoctorFinding {
        // A missing model is not a fault. It is the first-run state, and the
        // app's answer to it is to download one. Reporting it as a failure
        // would make a healthy first launch look broken; what it actually does
        // is change what the disk and network checks are about.
        guard !isPresent else {
            return DoctorFinding(
                title: "Model",
                detail: "\(mode.rawValue) is downloaded and complete.",
                severity: .ok)
        }
        let size = Int64(mode.approxDownloadBytes)
            .formatted(.byteCount(style: .file))
        return DoctorFinding(
            title: "Model",
            detail: """
                \(mode.rawValue) is not downloaded yet. Generating will \
                download it first, about \(size).
                """,
            severity: .ok)
    }

    private static func diskFinding(modelsRoot: URL,
                                    needsDownload: Bool,
                                    modelBytes: Int64) -> DoctorFinding {
        // Measured on the volume holding the models folder, which Settings
        // lets the user move to another disk entirely. Checking the boot
        // volume would answer a question nobody asked.
        guard let free = freeBytes(at: modelsRoot) else {
            return DoctorFinding(
                title: "Disk space",
                detail: """
                    Could not read free space on the volume holding the models \
                    folder (\(modelsRoot.path)). Check that the disk is still \
                    connected.
                    """,
                severity: .warning)
        }

        let freeText = free.formatted(.byteCount(style: .file))
        guard needsDownload else {
            guard free < outputHeadroom else {
                return DoctorFinding(
                    title: "Disk space",
                    detail: "\(freeText) free. The model is already downloaded.",
                    severity: .ok)
            }
            return DoctorFinding(
                title: "Disk space",
                detail: """
                    Only \(freeText) free. There is room for the model, but \
                    little for the audio it produces.
                    """,
                severity: .warning)
        }

        let needText = modelBytes.formatted(.byteCount(style: .file))
        guard free >= modelBytes else {
            let short = (modelBytes - free).formatted(.byteCount(style: .file))
            return DoctorFinding(
                title: "Disk space",
                detail: """
                    \(freeText) free, and the download needs about \(needText). \
                    Free up \(short) or choose another models folder in \
                    Settings → Storage.
                    """,
                severity: .blocker)
        }
        guard free >= modelBytes + comfortableDiskHeadroom else {
            return DoctorFinding(
                title: "Disk space",
                detail: """
                    \(freeText) free and the download needs about \(needText), \
                    which fits but leaves the disk nearly full.
                    """,
                severity: .warning)
        }
        return DoctorFinding(
            title: "Disk space",
            detail: "\(freeText) free, and the download needs about \(needText).",
            severity: .ok)
    }

    private static func memoryFinding(mode: TTSMode,
                                      modelBytes: Int64,
                                      isPresent: Bool) -> DoctorFinding {
        let needed = Int64(Double(modelBytes) * memoryWorkingFactor)
        let available = availableMemory()
        let neededText = needed.formatted(.byteCount(style: .memory))
        let availableText = available.formatted(.byteCount(style: .memory))

        // Never a blocker. The number moves the instant another app quits, and
        // a machine short of memory still finishes — it swaps, which is slow
        // rather than fatal. Refusing here would refuse runs that work.
        guard available < needed else {
            return DoctorFinding(
                title: "Memory",
                detail: """
                    \(availableText) available, and the \(mode.rawValue) model \
                    wants about \(neededText).
                    """,
                severity: .ok)
        }
        let unknownSize = isPresent ? "" : " once it has downloaded"
        return DoctorFinding(
            title: "Memory",
            detail: """
                \(availableText) available and the \(mode.rawValue) model wants \
                about \(neededText)\(unknownSize). It will still run, but the Mac may \
                swap, which can make generation slow and playback stutter. \
                Quitting other apps first will help.
                """,
            severity: .warning)
    }

    private static func sourceFinding(mode: TTSMode) async -> DoctorFinding {
        let probe: URL
        let where_: String
        switch mode.effectiveSource {
        case .repo(let repoID):
            guard let url = URL(string:
                "https://huggingface.co/\(repoID)/resolve/main/config.json") else {
                return DoctorFinding(
                    title: "Model source",
                    detail: "\"\(repoID)\" is not a usable repository name.",
                    severity: .blocker)
            }
            probe = url
            where_ = "Hugging Face"
        case .baseURL(let base):
            probe = base.appendingPathComponent("config.json")
            where_ = base.host ?? base.absoluteString
        }

        guard await isReachable(probe) else {
            // Named as a network problem, not a model problem. Told "model
            // missing" when their own server is down, someone goes looking in
            // the wrong place — and self-hosting is exactly where this check
            // pays for itself.
            return DoctorFinding(
                title: "Model source",
                detail: """
                    \(where_) did not answer, so the download cannot start. \
                    Check your connection, or the source for this mode in \
                    Settings → Models.
                    """,
                severity: .blocker)
        }
        return DoctorFinding(
            title: "Model source",
            detail: "\(where_) is reachable.",
            severity: .ok)
    }

    private static func outputFinding(folder: URL) -> DoctorFinding {
        let fm = FileManager.default
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        guard fm.isWritableFile(atPath: folder.path) else {
            return DoctorFinding(
                title: "Output folder",
                detail: """
                    Bunyi cannot write to \(folder.path), so a finished \
                    generation could not be saved.
                    """,
                severity: .blocker)
        }
        return DoctorFinding(
            title: "Output folder",
            detail: "Generated audio can be written to \(folder.path).",
            severity: .ok)
    }

    private static func integrityFinding(mode: TTSMode,
                                         engine: TTSEngine,
                                         dir: URL) async -> DoctorFinding {
        guard let entries = await engine.publishedDigests(for: mode) else {
            return DoctorFinding(
                title: "Model files",
                detail: """
                    This model's source publishes no checksums, so the files \
                    cannot be verified. Nothing is wrong — there is just \
                    nothing to check against.
                    """,
                severity: .ok)
        }
        let withDigests = entries.filter { $0.sha256 != nil }
        guard !withDigests.isEmpty else {
            return DoctorFinding(
                title: "Model files",
                detail: "The published manifest lists files but no checksums.",
                severity: .ok)
        }

        let bad = await Task.detached(priority: .utility) {
            withDigests.filter { entry in
                let file = dir.appendingPathComponent(entry.path)
                // The downloader's own hash, not a second one. A file is
                // "intact" if it would satisfy the check the download path
                // applies, and two implementations of that could disagree.
                guard let actual = try? HTTPFileDownloader.sha256Hex(of: file)
                else { return true }
                return actual != entry.sha256
            }.map(\.path)
        }.value

        guard bad.isEmpty else {
            // This is the failure that otherwise loads and speaks nonsense, so
            // it names the files: "the model is corrupt" leaves nothing to do,
            // whereas a named file can be deleted and fetched again.
            let names = bad.prefix(3).joined(separator: ", ")
            let more = bad.count > 3 ? " and \(bad.count - 3) more" : ""
            return DoctorFinding(
                title: "Model files",
                detail: """
                    \(bad.count) file\(bad.count == 1 ? "" : "s") do not match \
                    the published checksums: \(names)\(more). Delete \(mode.rawValue) \
                    in Settings → Storage and download it again.
                    """,
                severity: .blocker)
        }
        return DoctorFinding(
            title: "Model files",
            detail: """
                All \(withDigests.count) files match the checksums the server \
                publishes.
                """,
            severity: .ok)
    }

    // MARK: - Machine facts

    private static func freeBytes(at url: URL) -> Int64? {
        // ForImportantUsage rather than the plain capacity: it counts space the
        // system would purge to satisfy a real write, which is what a download
        // about to start actually gets.
        let values = try? url.resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    /// Memory a large allocation could actually get.
    ///
    /// Not `physicalMemory` minus what is used: on macOS most of "used" is
    /// reclaimable. Free, inactive and purgeable pages are the ones the kernel
    /// will hand over without swapping, which is the question being asked.
    ///
    /// MLX's own buffer cache is added because the engine gives it back before
    /// a generation starts — counting it as unavailable would have Doctor warn
    /// about memory the app is about to release itself.
    private static func availableMemory() -> Int64 {
        var stats = vm_statistics64_data_t()
        var count = mach_msg_type_number_t(
            MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard result == KERN_SUCCESS else {
            // Unknown rather than zero: zero would fire the low-memory warning
            // on every run, and a warning that is always on is noise.
            return Int64(ProcessInfo.processInfo.physicalMemory)
        }
        // sysconf rather than `vm_kernel_page_size`: that is an imported
        // global var, which Swift 6 will not let a concurrent context read.
        let page = Int64(sysconf(_SC_PAGESIZE))
        let reclaimable = Int64(stats.free_count)
            + Int64(stats.inactive_count)
            + Int64(stats.purgeable_count)
        return reclaimable * page + Int64(GPU.cacheMemory)
    }

    private static func isReachable(_ url: URL) async -> Bool {
        var request = URLRequest(url: url)
        request.httpMethod = "HEAD"
        // Short: this runs before every generation on a first run, and a dead
        // host that takes sixty seconds to admit it would be worse than the
        // failure it is reporting.
        request.timeoutInterval = 10
        guard let (_, response) = try? await URLSession.shared.data(for: request),
              let http = response as? HTTPURLResponse else { return false }
        return (200..<400).contains(http.statusCode)
    }
}
