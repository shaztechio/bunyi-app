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

import AVFoundation
import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// One generated WAV on disk. `id` is the URL: the folder is the record, so a
/// file that disappears from the folder disappears from History.
struct GeneratedOutput: Identifiable, Hashable {
    let url: URL
    let created: Date
    let byteCount: Int64

    var id: URL { url }
    var name: String { url.deletingPathExtension().lastPathComponent }

    /// Filenames are `<Mode>-<ISO8601 timestamp>.wav`, so the mode is the part
    /// before the first dash. Falls back to the whole name for anything the
    /// user dropped in the folder themselves.
    var mode: String {
        let parts = name.split(separator: "-", maxSplits: 1)
        return parts.count == 2 ? String(parts[0]) : name
    }
}

/// Everything generated so far: play it back, or save a copy somewhere else.
///
/// Reads the Outputs folder on appear rather than keeping a list in memory, so
/// it reflects what is actually on disk — including files removed in Finder.
struct HistoryView: View {
    let engine: TTSEngine

    @State private var items: [GeneratedOutput] = []
    @State private var metadata: [URL: OutputMetadata] = [:]
    @State private var player: AVAudioPlayer?
    @State private var playingID: URL?
    @State private var error: String?
    @State private var pendingDeletion: GeneratedOutput?
    @State private var copiedID: URL?
    @State private var copyResetTask: Task<Void, Never>?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header

            if items.isEmpty {
                empty
            } else {
                List(items) { item in
                    row(item)
                        .listRowInsets(EdgeInsets(top: 6, leading: 8,
                                                  bottom: 6, trailing: 8))
                }
                .listStyle(.inset)
                .alternatingRowBackgrounds()
            }
        }
        .onAppear(perform: reload)
        // A generation finishing writes a new file; refresh so it appears
        // without the user having to leave the tab and come back.
        .onChange(of: engine.lastOutputURL) { _, _ in reload() }
        .alert("Could not save", isPresented: .constant(error != nil)) {
            Button("OK") { error = nil }
        } message: {
            Text(error ?? "")
        }
        // Confirmed rather than immediate: the row shows a truncated label, so
        // the wrong trash icon is easy to hit and the audio may be the only
        // copy. It goes to the Trash, not `removeItem`, so it stays
        // recoverable even after confirming.
        .confirmationDialog(
            "Move this audio to the Trash?",
            isPresented: .constant(pendingDeletion != nil),
            titleVisibility: .visible
        ) {
            Button("Move to Trash", role: .destructive) {
                if let item = pendingDeletion { trash(item) }
                pendingDeletion = nil
            }
            Button("Cancel", role: .cancel) { pendingDeletion = nil }
        } message: {
            Text(pendingDeletion.map {
                metadata[$0.url]?.title ?? $0.url.lastPathComponent
            } ?? "")
        }
    }

    private var header: some View {
        HStack {
            Text(items.isEmpty ? "No audio yet"
                 : "\(items.count) file\(items.count == 1 ? "" : "s")")
                .foregroundStyle(.secondary)

            Spacer()

            Button(action: reload) {
                Label("Refresh", systemImage: "arrow.clockwise")
            }
            .help("Re-read the Outputs folder")

            Button {
                NSWorkspace.shared.open(engine.outputsFolder)
            } label: {
                Label("Show in Finder", systemImage: "folder")
            }
            .help("Open the Outputs folder")
        }
    }

    private var empty: some View {
        VStack(spacing: 6) {
            Image(systemName: "waveform")
                .font(.system(size: 34))
                .foregroundStyle(.tertiary)
            Text("Audio you generate will be listed here.")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func row(_ item: GeneratedOutput) -> some View {
        HStack(spacing: 12) {
            Button {
                toggle(item)
            } label: {
                Image(systemName: playingID == item.url ? "pause.fill" : "play.fill")
                    .frame(width: 14)
            }
            .buttonStyle(.borderless)
            .help(playingID == item.url ? "Pause" : "Play")

            VStack(alignment: .leading, spacing: 2) {
                // The prompt when the file carries one, the mode otherwise —
                // "what did it say" identifies a clip far better than
                // "Preset voice" repeated down the list. A prompt can be
                // paragraphs long, so the row shows one line and the whole
                // thing is on hover.
                Text(metadata[item.url]?.title ?? item.mode)
                    .fontWeight(.medium)
                    .lineLimit(1)
                Text(Self.subtitle(for: item, metadata: metadata[item.url]))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            // Copying beats hover for anything you want to keep: a tooltip
            // cannot be pasted into a note, a bug report, or back into the app
            // to reproduce a result.
            Button {
                copyDetails(item)
            } label: {
                Image(systemName: copiedID == item.url
                      ? "checkmark" : "doc.on.doc")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(copiedID == item.url ? .green : .primary)
            .help("Copy details")

            Button {
                save(item)
            } label: {
                Label("Download", systemImage: "square.and.arrow.down")
            }
            .buttonStyle(.borderless)
            .help("Save a copy elsewhere")

            Button {
                NSWorkspace.shared.activateFileViewerSelecting([item.url])
            } label: {
                Image(systemName: "folder")
            }
            .buttonStyle(.borderless)
            .help("Show in Finder")

            Button {
                pendingDeletion = item
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.red)
            .help("Move to Trash")
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        // On the whole row, not just the label: hovering anywhere in the row is
        // what people do, and a tooltip that only appears over the text reads
        // as no tooltip at all.
        .help(Self.tooltip(for: item, metadata: metadata[item.url]))
    }

    // MARK: Behaviour

    private func reload() {
        items = engine.generatedOutputs()
        // Read the tags once per refresh rather than per row: SwiftUI evaluates
        // a row body repeatedly, and each read opens the file.
        metadata = Dictionary(
            uniqueKeysWithValues: items.compactMap { item in
                WAVMetadata.read(from: item.url).map { (item.url, $0) }
            }
        )
        // A file can vanish from the folder while it is playing.
        if let playingID, !items.contains(where: { $0.url == playingID }) {
            stop()
        }
    }

    private func toggle(_ item: GeneratedOutput) {
        if playingID == item.url {
            stop()
            return
        }
        stop()
        do {
            let next = try AVAudioPlayer(contentsOf: item.url)
            next.play()
            player = next
            playingID = item.url
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func stop() {
        player?.stop()
        player = nil
        playingID = nil
    }

    /// Puts the record on the clipboard as readable text — the same thing the
    /// tooltip shows, in a form that can be pasted.
    private func copyDetails(_ item: GeneratedOutput) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(
            Self.tooltip(for: item, metadata: metadata[item.url]),
            forType: .string
        )
        // Without an acknowledgement a copy button looks like it did nothing.
        copiedID = item.url
        copyResetTask?.cancel()
        copyResetTask = Task {
            try? await Task.sleep(for: .seconds(1.5))
            guard !Task.isCancelled else { return }
            copiedID = nil
        }
    }

    /// Trash rather than delete: this is the user's audio, the row label is
    /// truncated, and a mis-click should be undoable from the Finder.
    private func trash(_ item: GeneratedOutput) {
        if playingID == item.url { stop() }
        do {
            try FileManager.default.trashItem(at: item.url,
                                              resultingItemURL: nil)
            // The engine still points at this file if it was the last run's
            // output; leaving that dangling would offer Play on a file in the
            // Trash.
            if engine.lastOutputURL == item.url { engine.clearLastOutput() }
            reload()
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// Save panel rather than a fixed destination: the app is sandboxed, and
    /// the user choosing the location is what grants access to write there.
    private func save(_ item: GeneratedOutput) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = item.url.lastPathComponent
        panel.allowedContentTypes = [.wav]
        panel.canCreateDirectories = true
        panel.title = "Save Audio"

        guard panel.runModal() == .OK, let destination = panel.url else { return }
        do {
            // The panel already asked about replacing, so an existing file here
            // is one the user chose to overwrite.
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.copyItem(at: item.url, to: destination)
        } catch {
            self.error = error.localizedDescription
        }
    }

    private static func subtitle(for item: GeneratedOutput,
                                 metadata: OutputMetadata?) -> String {
        let date = item.created.formatted(date: .abbreviated, time: .shortened)
        let size = ByteCountFormatter.string(fromByteCount: item.byteCount,
                                             countStyle: .file)
        var parts = [metadata?.mode ?? item.mode, date, size]
        // The voice, whichever way this mode picked one — a speaker name, a
        // designed-voice description, or a clone. Showing only `speaker` left
        // every Voice design row with nothing identifying its voice.
        if let voice = metadata?.voiceSummary {
            parts.insert(Self.trimmed(voice), at: 1)
        }
        return parts.joined(separator: " · ")
    }

    /// Voice descriptions are free text and can be a sentence; a row is not.
    private static func trimmed(_ value: String) -> String {
        let flat = value.replacingOccurrences(of: "\n", with: " ")
        return flat.count <= 40 ? flat : String(flat.prefix(39)) + "…"
    }

    /// The full record on hover — the text can be far longer than a row.
    private static func tooltip(for item: GeneratedOutput,
                                metadata: OutputMetadata?) -> String {
        guard let metadata else {
            // Generated before the app embedded metadata. Say so, rather than
            // showing a bare filename that looks like something went wrong.
            return """
            \(item.url.lastPathComponent)

            This file was made before Bunyi started recording how audio was \
            generated, so it carries no details.
            """
        }
        var lines = ["\(metadata.mode) · \(metadata.language)"]
        if let speaker = metadata.speaker {
            lines.append("Speaker: \(speaker)")
        }
        if let voice = metadata.voiceDescription {
            lines.append("Voice: \(voice)")
        }
        if let style = metadata.style {
            lines.append("Style: \(style)")
        }
        if let reference = metadata.referenceTranscript {
            lines.append("Reference: \(reference)")
        }
        lines.append("Model: \(metadata.modelRepo)")
        lines.append("")
        lines.append(metadata.text)
        return lines.joined(separator: "\n")
    }
}
