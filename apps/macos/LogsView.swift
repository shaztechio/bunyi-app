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
                LogTextView(entries: store.entries)
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

/// The log body, as a real text view.
///
/// SwiftUI cannot do this. `.textSelection(.enabled)` makes each `Text`
/// individually selectable, but a drag cannot cross from one view to the next,
/// so a `LazyVStack` of lines gives you one line at a time however each line is
/// built. Two attempts to fix it inside SwiftUI — splitting the line into a
/// timestamp column and a message, then putting it back together — moved the
/// boundary around without removing it, because the boundary is between views,
/// not inside them.
///
/// An `NSTextView` is one view holding all the text, so selection, ⌘A, ⌘C and
/// Find work the way they do in any other text pane. That is what this window
/// is for: copying a few lines into a bug report.
///
/// Read-only rather than disabled — a disabled text view will not let you
/// select either, which is the whole point.
private struct LogTextView: NSViewRepresentable {
    let entries: [LogStore.Entry]

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        guard let text = scroll.documentView as? NSTextView else { return scroll }
        text.isEditable = false
        text.isSelectable = true
        text.drawsBackground = false
        text.textContainerInset = NSSize(width: Space.card, height: Space.row)
        // NSTextContainer adds 5 pt of its own on each side by default, which
        // would sit on top of the inset above and make the gutter wider than
        // the SwiftUI version's. One source of padding, not two.
        text.textContainer?.lineFragmentPadding = 0
        // Wrap rather than scroll sideways: a log line can be a file path
        // hundreds of characters long, and a horizontal scrollbar makes every
        // other line harder to read to find it.
        text.isHorizontallyResizable = false
        text.textContainer?.widthTracksTextView = true
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let text = scroll.documentView as? NSTextView else { return }
        // Whether the view was already at the bottom *before* the update. New
        // entries should follow the tail, but not yank the view away from
        // someone who has scrolled up to read — or mid-selection.
        let wasAtBottom = scroll.contentView.bounds.maxY >= scroll.documentView!.bounds.maxY - 4
        text.textStorage?.setAttributedString(Self.attributed(entries))
        if wasAtBottom { text.scrollToEndOfDocument(nil) }
    }

    /// The same two-part line as before — a fixed-width timestamp in a dimmer
    /// colour, then the message — expressed as attributes on one string rather
    /// than as two views.
    private static func attributed(_ entries: [LogStore.Entry]) -> NSAttributedString {
        let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        let out = NSMutableAttributedString()
        for (index, entry) in entries.enumerated() {
            let time = entry.date.formatted(date: .omitted, time: .standard)
            out.append(NSAttributedString(
                string: time.padding(toLength: max(13, time.count),
                                     withPad: " ", startingAt: 0) + "  ",
                attributes: [.font: font, .foregroundColor: NSColor.tertiaryLabelColor]))
            // Newline *between* entries, not after each. Appending one to the
            // last line too would make ⌘A ⌘C end in a newline that the Copy
            // button's string does not have — two ways to copy the same log,
            // producing two different strings.
            let isLast = index == entries.count - 1
            out.append(NSAttributedString(
                string: entry.message + (isLast ? "" : "\n"),
                attributes: [.font: font, .foregroundColor: NSColor.labelColor]))
        }
        return out
    }
}

#Preview {
    LogsView()
}
