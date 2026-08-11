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
//  LogsView.swift
//  Bunyi
//

import AppKit
import SwiftUI

struct LogsView: View {
    private let store = LogStore.shared

    /// The timestamp column.
    ///
    /// `UI-PLAN.md` says 62. Measured, 62 truncates: this view draws the
    /// monospaced 10 pt caption, in which "10:04:37 PM" is 68 pt wide, so a
    /// 62 pt column would clip the meridiem off every afternoon line in a
    /// 12-hour locale — and a timestamp reading "10:04:37 P…" is worse than the
    /// misalignment this column exists to fix. 70 clears the widest form with a
    /// hair to spare. A 24-hour locale needs only 50 and simply gets a wider
    /// gutter; the column is fixed so that the messages line up, which is the
    /// whole point, and that rules out sizing it to the content.
    ///
    /// Local to this file because `Theme.swift` is being changed elsewhere this
    /// round. If a second view ever needs a timestamp column, this belongs
    /// beside `OptionRow` as a measurement rather than in `Space`.
    private static let timeColumn: CGFloat = 70

    var body: some View {
        Group {
            if store.entries.isEmpty {
                empty
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(store.entries) { entry in
                                line(entry)
                                    .id(entry.id)
                            }
                        }
                        // Horizontal room so a line does not start and end
                        // against the window edge; vertical is smaller because
                        // the lines are already a dense 2 pt apart.
                        .padding(.horizontal, Space.card)
                        .padding(.vertical, Space.row)
                        .textSelection(.enabled)
                    }
                    .defaultScrollAnchor(.bottom)
                    .onChange(of: store.entries.count) {
                        if let last = store.entries.last {
                            proxy.scrollTo(last.id, anchor: .bottom)
                        }
                    }
                }
            }
        }
        .frame(minWidth: 520, minHeight: 300)
        .toolbar {
            ToolbarItem {
                Button("Copy") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(store.text, forType: .string)
                }
                .disabled(store.entries.isEmpty)
            }
            ToolbarItem {
                Button("Clear") { store.clear() }
                    .disabled(store.entries.isEmpty)
            }
        }
        .navigationTitle("Logs")
    }

    /// One entry: a fixed timestamp column, then the message.
    ///
    /// Two views rather than one interpolated string. As one string the column
    /// could not align — the timestamp and the message were a single run of
    /// text — and a message longer than the window wrapped back under the
    /// timestamp instead of hanging beneath its own first word.
    ///
    /// `LogStore.text` still builds the clipboard string the old way, with two
    /// spaces, and is deliberately untouched: this is what the window looks
    /// like, not what Copy produces.
    private func line(_ entry: LogStore.Entry) -> some View {
        // First-text-baseline so the timestamp sits on the message's *first*
        // line once the message wraps, rather than being centred against the
        // whole block.
        HStack(alignment: .firstTextBaseline, spacing: Space.tight) {
            Text(entry.date.formatted(date: .omitted, time: .standard))
                // `design: .monospaced` is a request, not a guarantee — where
                // the face is substituted, monospacedDigit still pins 1 to the
                // width of 8, which is what keeps the column a column. Tertiary
                // so the eye can skip a timestamp it is not reading.
                .monospacedDigit()
                .foregroundStyle(.tertiary)
                .frame(width: Self.timeColumn, alignment: .leading)

            Text(entry.message)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.system(.caption, design: .monospaced))
    }

    private var empty: some View {
        VStack(spacing: Space.tight) {
            // Same treatment as History's empty tab, for the same reason: an
            // empty window should still look like part of this app.
            Image(systemName: "text.alignleft")
                .font(.system(size: 40))
                .foregroundStyle(LinearGradient.bunyiBrand)

            Text("No log messages yet")
                .font(.system(size: 15, weight: .semibold))

            Text("What the app is doing is written here as it happens.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

#Preview {
    LogsView()
}
