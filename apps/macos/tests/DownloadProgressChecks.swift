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

@main
struct DownloadProgressChecks {
    static func main() async throws {
        let epoch = Date(timeIntervalSince1970: 0)
        var speed = DownloadSpeedHistory()
        speed.reset(at: epoch, received: 0)
        for second in 0...31 {
            speed.sample(at: epoch.addingTimeInterval(Double(second)), received: Int64(second * 55_000))
        }
        precondition(speed.rate(at: epoch.addingTimeInterval(31), received: 31 * 55_000, window: 10) == 55_000)
        speed.sample(at: epoch.addingTimeInterval(61), received: 31 * 55_000)
        precondition(speed.rate(at: epoch.addingTimeInterval(61), received: 31 * 55_000, window: 10) == 0)
        speed.reset(at: epoch.addingTimeInterval(62), received: 31 * 55_000)
        precondition(speed.rate(at: epoch.addingTimeInterval(62), received: 31 * 55_000, window: 10) == 0)
        var slow = ModelDownloadProgress(phase: .downloading, lastReceived: epoch, waitingSince: epoch,
            elapsed: 64, sampledAt: epoch, slow: true)
        precondition(slow.isSlow(at: epoch))
        precondition(!slow.isSlow(at: epoch.addingTimeInterval(30)))
        precondition(slow.elapsedText(at: epoch) == "Model download elapsed: 1:04")
        slow.phase = .verifying
        precondition(!slow.isSlow(at: epoch))
        precondition(slow.elapsedText(at: epoch.addingTimeInterval(3600)) == "Model download elapsed: 1:01:04")
        let mailbox = DownloadReceiptMailbox()
        mailbox.begin(file: "weights", completed: 2_350_000_000, total: 5_000_000_000,
                      fileTotal: 3_000_000_000, otherTotal: 2_000_000_000)
        mailbox.prepared(offset: 1_200_000_000, total: 3_000_000_000)
        let before = mailbox.snapshot()
        precondition(before.lastReceived == nil, "A resume offset is not a receipt")
        mailbox.receive(1, total: 3_000_000_000)
        let first = mailbox.snapshot()
        precondition(first.receipt == 1 && first.fileBytes == 1_200_000_001)
        precondition(first.available == 3_550_000_001)
        let percent = first.fraction.formatted(.percent.precision(.fractionLength(1)))
        mailbox.receive(1, total: 3_000_000_000)
        let second = mailbox.snapshot()
        precondition(second.receipt == 1 && second.available == first.available + 1)
        precondition(percent == second.fraction.formatted(.percent.precision(.fractionLength(1))))
        let last = second.lastReceived!
        precondition(second.receiptText(at: last) == "Received 1 byte")
        precondition(second.receiptText(at: last.addingTimeInterval(3)) == "Waiting for more data")
        precondition(second.stalled(at: last.addingTimeInterval(30)))
        mailbox.verifying()
        precondition(!mailbox.snapshot().stalled(at: last.addingTimeInterval(100)))
        mailbox.begin(file: "next", completed: first.available, total: 0, fileTotal: 0)
        let unknown = mailbox.snapshot()
        precondition(unknown.fileBytes == 0 && unknown.total == 0)
        precondition(unknown.available == second.available,
                     "Aggregate progress must not decrease between attempts")
        mailbox.prepared(offset: 0, total: 400)
        mailbox.receive(1, total: 400)
        let mixed = mailbox.snapshot()
        precondition(mixed.available >= unknown.available,
                     "A server ignoring Range must not decrease aggregate progress")
        precondition(mixed.total == 0 && mixed.fileTotal == 400)
        mailbox.waiting(
            until: epoch.addingTimeInterval(12), host: "example.test", attempt: 2)
        let waiting = mailbox.snapshot(at: epoch.addingTimeInterval(10))
        precondition(waiting.receiptText(at: epoch.addingTimeInterval(10))
                     == "Retrying in 2 seconds")
        precondition(waiting.arrivalText(at: epoch) == "example.test asked Bunyi to wait")
        precondition(waiting.retryAttempt == 2)
        precondition(!waiting.isSlow(at: epoch) && !waiting.stalled(at: epoch))

        let seconds = HTTPResponseInfo(
            statusCode: 429,
            headers: ["Retry-After": "5", "RateLimit": "\"api\";r=0;t=9"])
        precondition(DownloadRetryPolicy.delay(
            for: seconds, now: epoch, retryNumber: 1, jitter: 0.5) == 9)
        let httpDate = HTTPResponseInfo(
            statusCode: 429,
            headers: ["retry-after": "Thu, 01 Jan 1970 00:00:12 GMT"])
        precondition(DownloadRetryPolicy.delay(
            for: httpDate, now: epoch, retryNumber: 1, jitter: 0) == 12)
        let fallback = HTTPResponseInfo(statusCode: 429)
        precondition(DownloadRetryPolicy.delay(
            for: fallback, now: epoch, retryNumber: 1, jitter: 0.5) == 2.5)
        precondition(DownloadRetryPolicy.delay(
            for: fallback, now: epoch, retryNumber: 2, jitter: 0.5) == 4.5)
        precondition(DownloadRetryPolicy.delay(
            for: fallback, now: epoch, retryNumber: 3, jitter: 0.5) == 8.5)
        // Transient service failures use the same bounded timing as 429.
        let source = URL(string: "https://models.example/model")!
        for status in [500, 502, 503, 504] {
            let response = HTTPResponseInfo(statusCode: status)
            precondition(response.shouldRetryDownload)
            for retry in 1...DownloadRetryPolicy.maximumRetries {
                precondition(DownloadRetryPolicy.delay(
                    for: response, now: epoch, retryNumber: retry, jitter: 0.5)
                    == pow(2, Double(retry)) + 0.5)
            }
            let retryAt = epoch.addingTimeInterval(60)
            guard case .unavailable(let failedSource, let paused, let deadline) =
                response.retryExhaustedError(source: source, retryAt: retryAt) else {
                preconditionFailure("Exhausted server retries must remain an unavailable error")
            }
            precondition(failedSource == source && !paused && deadline == retryAt)
        }
        for status in [200, 206, 400, 401, 403, 404, 501, 505] {
            precondition(!HTTPResponseInfo(statusCode: status).shouldRetryDownload)
        }
        precondition(seconds.shouldRetryDownload)
        guard case .rateLimited = seconds.retryExhaustedError(source: source, retryAt: epoch) else {
            preconditionFailure("Exhausted 429 retries must remain rate limited")
        }
        let serviceTiming = HTTPResponseInfo(statusCode: 503, headers: ["Retry-After": "60"])
        precondition(DownloadRetryPolicy.delay(
            for: serviceTiming, now: epoch, retryNumber: 1, jitter: 0.5) == 60)
        let paused = HTTPResponseInfo(
            statusCode: 503,
            headers: ["x-bunyi-download-status": " PAUSED "])
        guard case .unavailable(_, let isPaused, _) = paused.unavailableError(
            source: URL(string: "https://models.bunyi.app/customvoice")!,
            now: epoch) else {
            preconditionFailure("A 503 did not produce an unavailable error")
        }
        precondition(isPaused)
        precondition(!paused.shouldRetryDownload)

        let deliberateWait = Task {
            try await DownloadRetryPolicy.wait(
                until: Date().addingTimeInterval(10))
        }
        deliberateWait.cancel()
        do {
            try await deliberateWait.value
            preconditionFailure("A cancelled retry wait completed")
        } catch is CancellationError {}

        print("Download progress checks passed: receipts, resume offsets, unknown totals, waits, retry timing and cancellation.")
        try await checkTransfers()
    }
}
