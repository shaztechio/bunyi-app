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
                // "Preset voice" repeated down the list.
                Text(metadata[item.url]?.title ?? item.mode)
                    .fontWeight(.medium)
                    .lineLimit(1)
                Text(Self.subtitle(for: item, metadata: metadata[item.url]))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .help(Self.tooltip(for: item, metadata: metadata[item.url]))

            Spacer(minLength: 8)

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
        }
        .padding(.vertical, 2)
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
        if let voice = metadata?.speaker, !voice.isEmpty {
            parts.insert(voice, at: 1)
        }
        return parts.joined(separator: " · ")
    }

    /// The full record on hover — the text can be far longer than a row.
    private static func tooltip(for item: GeneratedOutput,
                                metadata: OutputMetadata?) -> String {
        guard let metadata else { return item.url.lastPathComponent }
        var lines = ["\(metadata.mode) · \(metadata.language)"]
        if let speaker = metadata.speaker, !speaker.isEmpty {
            lines.append("Speaker: \(speaker)")
        }
        if let instruct = metadata.instruct, !instruct.isEmpty {
            lines.append("Style: \(instruct)")
        }
        if let reference = metadata.referenceTranscript, !reference.isEmpty {
            lines.append("Reference: \(reference)")
        }
        lines.append("Model: \(metadata.modelRepo)")
        lines.append("")
        lines.append(metadata.text)
        return lines.joined(separator: "\n")
    }
}
