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
        // One `Text`, not two in an HStack.
        //
        // Splitting the line gave the timestamp its own column and let a long
        // message hang-indent under itself — but it also gave each half its
        // own selection scope, so dragging across the window selected a single
        // line, or just its timestamp. This window exists to be copied out of;
        // losing that costs more than the indent was worth.
        //
        // Concatenating with `+` produces a single `Text` with two styled
        // runs, which selects and drags as one piece the way it did before.
        // The column survives because the whole line is monospaced and the
        // time is padded to a fixed width — with every glyph the same width,
        // equal characters mean equal columns. What does not survive is the
        // hanging indent: a wrapped message returns to the left edge.
        (Text(Self.paddedTime(entry.date))
            .foregroundStyle(.tertiary)
         + Text(entry.message))
            .font(.system(.caption, design: .monospaced))
            .monospacedDigit()
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The timestamp, padded to a fixed width so the messages line up.
    ///
    /// Padded rather than framed: a frame needs two views, and two views is
    /// what broke selection. Trailing spaces are part of the copied text, but
    /// `LogStore.text` builds the clipboard string itself and is untouched, so
    /// the Copy button is unaffected — this only affects a manual drag, where
    /// a couple of spaces before each message is what the old single-`Text`
    /// version produced anyway.
    private static func paddedTime(_ date: Date) -> String {
        let time = date.formatted(date: .omitted, time: .standard)
        // Long enough for a 12-hour clock with a spaced meridiem ("9:18:18
        // p. m."); anything longer simply pushes its own line out rather than
        // wrapping, which is the lesser failure.
        let width = 13
        return time.count >= width
            ? time + "  "
            : time + String(repeating: " ", count: width - time.count)
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
