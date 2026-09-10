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
        precondition(unknown.available == first.available)
        mailbox.prepared(offset: 0, total: 400)
        mailbox.receive(1, total: 400)
        let mixed = mailbox.snapshot()
        precondition(mixed.total == 0 && mixed.fileTotal == 400)
        print("Download progress checks passed: one-byte receipts, resume offsets, unknown totals, file transitions and stalls.")
        try await checkTransfers()
    }
}
