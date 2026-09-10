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
struct ModelDownloadProgress: Sendable, Equatable {
    enum Phase: String, Sendable { case checking, sizing, downloading, verifying }
    var phase: Phase = .checking
    var available: Int64 = 0
    var total: Int64 = 0
    var file: String?
    var fileBytes: Int64 = 0
    var fileTotal: Int64 = 0
    var received: Int64 = 0
    var receipt: Int64 = 0
    var lastReceived: Date?
    var waitingSince = Date()
    var rate: Double = 0

    var title: String {
        switch phase {
        case .checking: "Checking the voice model"
        case .sizing: "Calculating download size"
        case .downloading: "Downloading voice model"
        case .verifying: "Checking model files"
        }
    }
    var fraction: Double { total > 0 ? min(1, Double(available) / Double(total)) : 0 }
    var fileFraction: Double { fileTotal > 0 ? min(1, Double(fileBytes) / Double(fileTotal)) : 0 }
    func quietSeconds(at now: Date) -> TimeInterval {
        max(0, now.timeIntervalSince(max(lastReceived ?? waitingSince, waitingSince)))
    }
    func stalled(at now: Date) -> Bool { phase == .downloading && quietSeconds(at: now) >= 30 }
    func receiptText(at now: Date) -> String {
        guard phase == .downloading else { return title }
        if stalled(at: now) { return "Download may be stalled" }
        if quietSeconds(at: now) >= 3 || lastReceived == nil { return "Waiting for more data" }
        return "Received \(receipt.formatted()) \(receipt == 1 ? "byte" : "bytes")"
    }
    func arrivalText(at now: Date) -> String {
        guard phase == .downloading else { return "Speech has not started" }
        guard let lastReceived else { return "Waiting for the first download bytes" }
        let seconds = max(0, Int(now.timeIntervalSince(lastReceived)))
        return seconds == 0 ? "Last data arrived just now" : "Last data arrived \(seconds) seconds ago"
    }
    func speedText(at now: Date) -> String {
        if stalled(at: now) { return "No data arriving · Download time remaining: unavailable" }
        guard quietSeconds(at: now) < 3, rate > 0 else { return "Download time remaining: estimating…" }
        let speed = Int64(rate).formatted(.byteCount(style: .file)) + "/s"
        guard total > 0 else { return speed + " · Download size unknown" }
        let seconds = max(0, Double(total - available) / rate)
        let eta = seconds < 60 ? "Under a minute" : "About \(Int(ceil(seconds / 60))) min"
        return "\(speed) · \(eta) left to download"
    }
    static func bytes(_ available: Int64, total: Int64) -> String {
        total > 0 ? "\(available.formatted()) / \(total.formatted()) bytes"
            : "\(available.formatted()) bytes available · total size unknown"
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
    private var started = Date()
    private var otherTotal: Int64?

    func begin(file: String, completed: Int64, total: Int64, fileTotal: Int64, otherTotal: Int64? = nil) {
        lock.lock(); defer { lock.unlock() }
        self.completed = completed
        self.otherTotal = otherTotal
        receivedBeforeFile = value.received
        fileReceived = 0; offset = 0
        value.phase = .downloading; value.file = file
        value.available = completed; value.total = total
        value.fileBytes = 0; value.fileTotal = fileTotal
        value.waitingSince = Date()
    }
    func prepared(offset: Int64, total: Int64) {
        lock.lock(); defer { lock.unlock() }
        self.offset = offset
        value.fileBytes = offset; value.available = completed + offset
        value.fileTotal = max(0, total)
        if total > 0, let otherTotal { value.total = otherTotal + total }
        value.waitingSince = Date()
        value.phase = .downloading
    }
    func receive(_ bytes: Int64, total: Int64) {
        guard bytes > 0 else { return }
        lock.lock(); defer { lock.unlock() }
        fileReceived += bytes
        value.received = receivedBeforeFile + fileReceived
        value.fileBytes = offset + fileReceived
        value.available = completed + value.fileBytes
        if total > 0 { value.fileTotal = total }
        value.lastReceived = Date()
        value.rate = Double(value.received) / max(0.001, Date().timeIntervalSince(started))
    }
    func verifying() {
        lock.lock(); defer { lock.unlock() }
        value.phase = .verifying
    }
    func snapshot() -> ModelDownloadProgress {
        lock.lock(); defer { lock.unlock() }
        let delta = value.received - displayedReceived
        if delta > 0 { value.receipt = delta }
        displayedReceived = value.received
        return value
    }
}
