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

import SwiftUI

/// The live receipt panel. Time passing changes the age, never the byte counters.
struct DownloadProgressView: View {
    let progress: ModelDownloadProgress
    let mode: String
    @State private var announcedAt = Date.distantPast
    @State private var announcedPhase: ModelDownloadProgress.Phase?

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            VStack(alignment: .leading, spacing: 6) {
                Text(progress.title).font(.headline)
                Text("\(mode) · Speech has not started").foregroundStyle(Color.accentColor)
                    .fixedSize(horizontal: false, vertical: true)
                Text("Bunyi saves this model for reuse. Speech starts automatically after setup.")
                    .font(.caption).fixedSize(horizontal: false, vertical: true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(progress.receiptText(at: context.date)).fontWeight(.medium)
                    Text(progress.arrivalText(at: context.date)).font(.caption)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(progress.stalled(at: context.date) ? Color.orange.opacity(0.12) : Color.accentColor.opacity(0.08),
                            in: RoundedRectangle(cornerRadius: 6))
                meter("Overall model download", available: progress.available,
                      total: progress.total, fraction: progress.fraction)
                if let file = progress.file {
                    meter("Current file · \(file)", available: progress.fileBytes,
                          total: progress.fileTotal, fraction: progress.fileFraction)
                }
                if progress.phase == .downloading {
                    Text(progress.speedText(at: context.date)).font(.caption)
                }
                Text("Next: Check downloaded files → Load model → Create speech")
                    .font(.caption).fixedSize(horizontal: false, vertical: true)
            }
            .monospacedDigit()
            .padding(12)
            .background(.background, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color(nsColor: .separatorColor), lineWidth: 1))
            .onChange(of: context.date, initial: true) { _, now in announce(at: now) }
            .onChange(of: progress.phase) { _, _ in announce(at: Date()) }
        }
    }

    private func meter(_ title: String, available: Int64, total: Int64, fraction: Double) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(title).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                Text(total > 0 ? fraction.formatted(.percent.precision(.fractionLength(1))) : "Size unknown")
            }
            if total > 0 {
                ProgressView(value: fraction).accessibilityLabel(title + " progress")
            }
            Text(ModelDownloadProgress.bytes(available, total: total))
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func announce(at now: Date) {
        guard progress.phase != announcedPhase || now.timeIntervalSince(announcedAt) >= 10 else { return }
        announcedAt = now; announcedPhase = progress.phase
        let percent = progress.total > 0 ? progress.fraction.formatted(.percent.precision(.fractionLength(1))) : "size unknown"
        AccessibilityNotification.Announcement("\(progress.title). \(progress.receiptText(at: now)). Overall model download: \(percent). \(progress.arrivalText(at: now)).").post()
    }
}
