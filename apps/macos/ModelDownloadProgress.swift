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

/// Exact counters are independent of rounded percentages. No inferred network activity.
public struct ModelDownloadProgress: Sendable, Equatable {
    public enum Phase: String, Sendable {
        case checking, sizing, downloading, verifying, reconnecting, waiting
    }
    public var phase: Phase = .checking
    public var available: Int64 = 0
    public var total: Int64 = 0
    public var file: String?
    public var fileBytes: Int64 = 0
    public var fileTotal: Int64 = 0
    public var received: Int64 = 0
    public var receipt: Int64 = 0
    public var lastReceived: Date?
    public var waitingSince = Date()
    public var rate: Double = 0
    public var elapsed: TimeInterval = 0
    public var sampledAt = Date()
    public var slow = false
    public var retryAt: Date?
    public var retryHost: String?
    public var retryAttempt = 0
    public init(phase: Phase = .checking, available: Int64 = 0,
                total: Int64 = 0, file: String? = nil,
                fileBytes: Int64 = 0, fileTotal: Int64 = 0,
                received: Int64 = 0, receipt: Int64 = 0,
                lastReceived: Date? = nil, waitingSince: Date = Date(),
                rate: Double = 0, elapsed: TimeInterval = 0,
                sampledAt: Date = Date(), slow: Bool = false,
                retryAt: Date? = nil, retryHost: String? = nil,
                retryAttempt: Int = 0) {
        self.phase = phase
        self.available = available
        self.total = total
        self.file = file
        self.fileBytes = fileBytes
        self.fileTotal = fileTotal
        self.received = received
        self.receipt = receipt
        self.lastReceived = lastReceived
        self.waitingSince = waitingSince
        self.rate = rate
        self.elapsed = elapsed
        self.sampledAt = sampledAt
        self.slow = slow
        self.retryAt = retryAt
        self.retryHost = retryHost
        self.retryAttempt = retryAttempt
    }
    public func elapsedText(at now: Date) -> String {
        let seconds = max(0, Int(elapsed + max(0, now.timeIntervalSince(sampledAt))))
        let time = seconds >= 3600
            ? String(format: "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
            : String(format: "%d:%02d", seconds / 60, seconds % 60)
        return "Model download elapsed: \(time)"
    }
    public func isSlow(at now: Date) -> Bool { phase == .downloading && slow && !stalled(at: now) }

    public var title: String {
        switch phase {
        case .checking: "Checking the voice model"
        case .sizing: "Calculating download size"
        case .downloading: "Downloading voice model"
        case .verifying: "Checking model files"
        case .reconnecting: "Reconnecting — keeping downloaded bytes"
        case .waiting: "Waiting to retry download"
        }
    }
    public var fraction: Double { total > 0 ? min(1, Double(available) / Double(total)) : 0 }
    public var fileFraction: Double { fileTotal > 0 ? min(1, Double(fileBytes) / Double(fileTotal)) : 0 }
    public func quietSeconds(at now: Date) -> TimeInterval {
        max(0, now.timeIntervalSince(max(lastReceived ?? waitingSince, waitingSince)))
    }
    public func stalled(at now: Date) -> Bool { phase == .downloading && quietSeconds(at: now) >= 30 }
    public func receiptText(at now: Date) -> String {
        if phase == .waiting {
            let seconds = max(0, Int(ceil((retryAt ?? now).timeIntervalSince(now))))
            return "Retrying in \(seconds) \(seconds == 1 ? "second" : "seconds")"
        }
        guard phase == .downloading else { return title }
        if stalled(at: now) { return "Download may be stalled" }
        if quietSeconds(at: now) >= 3 || lastReceived == nil { return "Waiting for more data" }
        return "Received \(receipt.formatted()) \(receipt == 1 ? "byte" : "bytes")"
    }
    public func arrivalText(at now: Date) -> String {
        if phase == .waiting {
            return "\(retryHost ?? "The server") asked Bunyi to wait"
        }
        guard phase == .downloading else { return "Speech has not started" }
        guard let lastReceived else { return "Waiting for the first download bytes" }
        let seconds = max(0, Int(now.timeIntervalSince(lastReceived)))
        return seconds == 0 ? "Last data arrived just now" : "Last data arrived \(seconds) seconds ago"
    }
    public func speedText(at now: Date) -> String {
        if stalled(at: now) { return "No data arriving · Download time remaining: unavailable" }
        guard quietSeconds(at: now) < 3, rate > 0 else { return "Download time remaining: estimating…" }
        let speed = Int64(rate).formatted(.byteCount(style: .file)) + "/s"
        guard total > 0 else { return speed + " · Download size unknown" }
        let seconds = max(0, Double(total - available) / rate)
        let eta = seconds < 60 ? "Under a minute" : "About \(Int(ceil(seconds / 60))) min"
        return "\(speed) recently · \(eta) left to download"
    }
    public static func bytes(_ available: Int64, total: Int64) -> String {
        total > 0 ? "\(available.formatted()) / \(total.formatted()) bytes"
            : "\(available.formatted()) bytes available · total size unknown"
    }
}

/// Sampled at UI cadence, never once per byte. Quiet time remains in the denominator.
struct DownloadSpeedHistory: Sendable {
    private var samples: [(Date, Int64)] = []
    mutating func reset(at now: Date, received: Int64) { samples = [(now, received)] }
    mutating func sample(at now: Date, received: Int64) {
        samples.append((now, received))
        while samples.count > 1 && samples[1].0 <= now.addingTimeInterval(-30) { samples.removeFirst() }
    }
    func rate(at now: Date, received: Int64, window: TimeInterval) -> Double {
        guard var baseline = samples.first else { return 0 }
        for sample in samples {
            if sample.0 > now.addingTimeInterval(-window) { break }
            baseline = sample
        }
        let seconds = now.timeIntervalSince(baseline.0)
        return seconds >= 1 ? Double(max(0, received - baseline.1)) / seconds : 0
    }
}

/// Delegate callbacks only update this bounded mailbox. The main actor samples at 4 Hz.
final class DownloadReceiptMailbox: @unchecked Sendable {
    private let lock = NSLock()
    private var value = ModelDownloadProgress()
    private var completed: Int64 = 0
    private var receivedBeforeFile: Int64 = 0
    private var fileReceived: Int64 = 0
    private var offset: Int64 = 0
    private var displayedReceived: Int64 = 0
    private let started: Date
    private var fileStarted = Date()
    private var speed = DownloadSpeedHistory()
    private var otherTotal: Int64?
    init(started: Date = Date()) { self.started = started }

    func begin(file: String, completed: Int64, total: Int64, fileTotal: Int64, otherTotal: Int64? = nil) {
        lock.lock(); defer { lock.unlock() }
        self.completed = completed
        self.otherTotal = otherTotal
        receivedBeforeFile = value.received
        fileReceived = 0; offset = 0
        value.phase = .downloading; value.file = file
        value.available = max(value.available, completed); value.total = total
        value.fileBytes = 0; value.fileTotal = fileTotal
        value.waitingSince = Date()
        value.lastReceived = nil
        value.rate = 0; value.slow = false
        fileStarted = Date()
        speed.reset(at: fileStarted, received: value.received)
    }
    func prepared(offset: Int64, total: Int64) {
        lock.lock(); defer { lock.unlock() }
        self.offset = offset
        value.fileBytes = offset
        value.available = max(value.available, completed + offset)
        value.fileTotal = max(0, total)
        if total > 0, let otherTotal { value.total = otherTotal + total }
        value.waitingSince = Date()
        value.phase = .downloading
        fileStarted = Date()
        speed.reset(at: fileStarted, received: value.received)
    }
    func receive(_ bytes: Int64, total: Int64) {
        guard bytes > 0 else { return }
        lock.lock(); defer { lock.unlock() }
        fileReceived += bytes
        value.received = receivedBeforeFile + fileReceived
        value.fileBytes = offset + fileReceived
        value.available = max(value.available, completed + value.fileBytes)
        if total > 0 { value.fileTotal = total }
        value.lastReceived = Date()
    }
    func verifying() {
        lock.lock(); defer { lock.unlock() }
        value.phase = .verifying
    }
    func reconnecting() {
        lock.lock(); defer { lock.unlock() }
        value.phase = .reconnecting
    }
    func waiting(until: Date, host: String, attempt: Int = 0) {
        lock.lock(); defer { lock.unlock() }
        value.phase = .waiting
        value.retryAt = until
        value.retryHost = host
        value.retryAttempt = attempt
        value.slow = false
        value.rate = 0
    }
    func snapshot(at now: Date = Date()) -> ModelDownloadProgress {
        lock.lock(); defer { lock.unlock() }
        let delta = value.received - displayedReceived
        if delta > 0 { value.receipt = delta }
        displayedReceived = value.received
        value.elapsed = max(0, now.timeIntervalSince(started))
        value.sampledAt = now
        if value.phase == .downloading {
            speed.sample(at: now, received: value.received)
            value.rate = speed.rate(at: now, received: value.received, window: 10)
            let sustained = speed.rate(at: now, received: value.received, window: 30)
            value.slow = now.timeIntervalSince(fileStarted) >= 30 && sustained < 256 * 1024
                && (value.fileTotal <= 0 || Double(value.fileTotal - value.fileBytes) > max(1, sustained) * 60)
        } else { value.slow = false; value.rate = 0 }
        return value
    }
}

/// The response facts needed after URLSession releases its response object.
struct HTTPResponseInfo: Sendable, Equatable {
    let statusCode: Int
    let headers: [String: String]
    let expectedContentLength: Int64

    init(_ response: HTTPURLResponse) {
        statusCode = response.statusCode
        expectedContentLength = response.expectedContentLength
        var normalized: [String: String] = [:]
        for (name, value) in response.allHeaderFields {
            normalized[String(describing: name).lowercased()] = String(describing: value)
        }
        headers = normalized
    }

    init(statusCode: Int, headers: [String: String] = [:],
         expectedContentLength: Int64 = -1) {
        self.statusCode = statusCode
        self.expectedContentLength = expectedContentLength
        var normalized: [String: String] = [:]
        for (name, value) in headers { normalized[name.lowercased()] = value }
        self.headers = normalized
    }

    func header(_ name: String) -> String? {
        headers[name.lowercased()]
    }

    var isSuccess: Bool { statusCode == 200 || statusCode == 206 }

    var bunyiDownloadsPaused: Bool {
        header("X-Bunyi-Download-Status")?
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .caseInsensitiveCompare("paused") == .orderedSame
    }

    func unavailableError(source: URL,
                          now: Date = Date()) -> DownloadServiceError {
        let delay = DownloadRetryPolicy.serverDelay(for: self, now: now)
        return .unavailable(
            source: source,
            paused: bunyiDownloadsPaused,
            retryAt: delay.map { now.addingTimeInterval($0) })
    }
}

/// HTTP failures remain errors through optional manifest and size probes.
public enum DownloadServiceError: LocalizedError {
    case unavailable(source: URL, paused: Bool, retryAt: Date?)
    case rateLimited(source: URL, retryAt: Date)
    case httpStatus(source: URL, status: Int)

    public var source: URL {
        switch self {
        case .unavailable(let source, _, _),
             .rateLimited(let source, _),
             .httpStatus(let source, _):
            source
        }
    }

    public var errorDescription: String? {
        switch self {
        case .unavailable(_, let paused, _):
            if paused {
                return "Model downloads are temporarily paused. Your downloaded files have been kept."
            }
            return "\(source.host ?? source.absoluteString) is temporarily unavailable. "
                + "Your downloaded files have been kept. Try again later."
        case .rateLimited(_, let retryAt):
            return "\(source.host ?? source.absoluteString) is limiting downloads. "
                + "Try again after \(retryAt.formatted(date: .abbreviated, time: .standard)). "
                + "Your downloaded files have been kept."
        case .httpStatus(_, let status):
            return "\(source.host ?? source.absoluteString) returned HTTP \(status)."
        }
    }
}

/// One bounded policy for manifest, size and file requests.
enum DownloadRetryPolicy {
    static let maximumRetries = 3
    static let maximumDelay: TimeInterval = 15 * 60

    /// Retry-After may be seconds or an HTTP date. Hugging Face's RateLimit
    /// header carries t=seconds. When both exist, waiting for the later reset
    /// is the only choice that does not retry early.
    static func delay(for response: HTTPResponseInfo, now: Date,
                      retryNumber: Int, jitter: Double) -> TimeInterval {
        let fallback = pow(2, Double(retryNumber)) + min(1, max(0, jitter))
        return max(0, serverDelay(for: response, now: now) ?? fallback)
    }

    static func serverDelay(for response: HTTPResponseInfo,
                            now: Date) -> TimeInterval? {
        var result: TimeInterval?
        if let value = response.header("Retry-After") {
            if let seconds = TimeInterval(value.trimmingCharacters(
                in: .whitespacesAndNewlines)), seconds.isFinite, seconds >= 0 {
                result = seconds
            } else if let date = httpDate(value) {
                result = max(0, date.timeIntervalSince(now))
            }
        }
        if let value = response.header("RateLimit") {
            for part in value.components(separatedBy:
                CharacterSet(charactersIn: ";,")) {
                let fields = part.split(separator: "=", maxSplits: 1)
                guard fields.count == 2,
                      fields[0].trimmingCharacters(in: .whitespaces) == "t",
                      let seconds = TimeInterval(fields[1].trimmingCharacters(
                        in: .whitespacesAndNewlines)),
                      seconds.isFinite, seconds >= 0 else { continue }
                result = max(result ?? 0, seconds)
            }
        }
        return result
    }

    /// Kept here so cancellation during a deliberate wait is covered without
    /// issuing another request. Task.sleep is cancellation-aware.
    static func wait(until deadline: Date, now: Date = Date()) async throws {
        let seconds = max(0, deadline.timeIntervalSince(now))
        try await Task.sleep(for: .seconds(seconds))
    }

    private static func httpDate(_ value: String) -> Date? {
        let formats = [
            "EEE',' dd MMM yyyy HH':'mm':'ss z",
            "EEEE',' dd-MMM-yy HH':'mm':'ss z",
            "EEE MMM d HH':'mm':'ss yyyy",
        ]
        for format in formats {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0)
            formatter.dateFormat = format
            if let date = formatter.date(from: value) { return date }
        }
        return nil
    }
}
