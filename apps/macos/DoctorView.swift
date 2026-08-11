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
//  DoctorView.swift
//  Bunyi
//

import SwiftUI

/// A Doctor report, as a list rather than a paragraph.
///
/// The first version of this was an `.alert`, matching the rest of the app. Six
/// findings of two or three sentences each came out as a wall of text with no
/// shape: the one line that mattered — the blocker — sat in the middle of five
/// that did not, and there was no way to skim it. An alert is the right control
/// for one sentence and the wrong one for a report.
///
/// It is the app's only sheet for that reason, and not a precedent for
/// replacing the alerts that are carrying one sentence each.
struct DoctorView: View {
    let report: DoctorReport
    /// True when this is why a generation did not start, rather than a checkup
    /// the user asked for. Only the heading changes: the findings are the same
    /// findings, and a second layout for them would be two things to maintain.
    let blockedRun: Bool
    let onDismiss: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.card) {
            header

            VStack(alignment: .leading, spacing: Space.row) {
                // Blockers first. When a run was stopped, the reason must not
                // sit below three lines of good news.
                ForEach(ordered) { finding in
                    row(finding)
                }
            }

            HStack {
                Button("Copy") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(
                        report.logLines.joined(separator: "\n"), forType: .string)
                }
                .help("Copy these findings, for a bug report")

                Spacer()

                Button("Done", action: onDismiss)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(Space.window)
        .frame(width: 460)
    }

    private var ordered: [DoctorFinding] {
        report.blockers + report.warnings
            + report.findings.filter { $0.severity == .ok }
    }

    @ViewBuilder
    private var header: some View {
        VStack(alignment: .leading, spacing: Space.hair) {
            Text(blockedRun ? "Bunyi cannot generate yet" : "Checkup")
                .font(.system(size: 15, weight: .semibold))

            Text(summary)
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    private var summary: String {
        if !report.blockers.isEmpty {
            let n = report.blockers.count
            return blockedRun
                ? "\(n == 1 ? "One thing is" : "\(n) things are") in the way."
                : "\(n == 1 ? "One thing needs" : "\(n) things need") fixing "
                    + "before Bunyi can generate."
        }
        if !report.warnings.isEmpty {
            let n = report.warnings.count
            return "Bunyi can generate. \(n == 1 ? "One thing is" : "\(n) things are") "
                + "worth knowing about."
        }
        return "Everything Bunyi needs is in place."
    }

    private func row(_ finding: DoctorFinding) -> some View {
        HStack(alignment: .top, spacing: Space.tight) {
            Image(systemName: finding.symbolName)
                .foregroundStyle(color(for: finding.severity))
                // The severity is already in the words; the glyph repeating it
                // to VoiceOver would read every row twice.
                .accessibilityHidden(true)
                .frame(width: OptionRow.iconColumn, alignment: .center)

            VStack(alignment: .leading, spacing: 2) {
                Text(finding.title)
                    .font(.system(size: 12, weight: .semibold))
                Text(finding.detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)
        }
    }

    private func color(for severity: DoctorSeverity) -> Color {
        switch severity {
        case .ok:      .green
        case .warning: .orange
        case .blocker: .red
        }
    }
}
