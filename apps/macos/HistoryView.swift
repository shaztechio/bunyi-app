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
    @State private var progress: Double = 0
    @State private var copiedID: URL?
    @State private var copyResetTask: Task<Void, Never>?

    /// Drives the ring, and notices a clip that reached its end — the player
    /// stops on its own and tells the view nothing. AVAudioPlayer has a
    /// delegate for that, but it needs an NSObject; polling matches how the
    /// main window already tracks playback.
    private let tick = Timer.publish(every: 0.2, on: .main, in: .common)
        .autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header

            if items.isEmpty {
                empty
            } else {
                List(items) { item in
                    row(item)
                        // One rule for row height. Insets plus a vertical
                        // padding inside the row were two mechanisms setting
                        // the same rhythm, so changing either half-worked.
                        .listRowInsets(EdgeInsets(top: 0, leading: 8,
                                                  bottom: 0, trailing: 8))
                        .frame(minHeight: 44)
                }
                .listStyle(.inset)
                .alternatingRowBackgrounds()
            }
        }
        .onAppear(perform: reload)
        // A generation finishing writes a new file; refresh so it appears
        // without the user having to leave the tab and come back.
        .onChange(of: engine.lastOutputURL) { _, _ in reload() }
        .onReceive(tick) { _ in
            guard playingID != nil, let player else { return }
            guard player.isPlaying else { stop(); return }
            progress = player.duration > 0
                ? player.currentTime / player.duration : 0
        }
        // A real binding, not .constant: dismissing with Escape or a click
        // outside sets isPresented false, and with a constant binding nothing
        // clears `error` — so the alert immediately comes back. The title is
        // generic because this reports playback and Trash failures too, not
        // only saving.
        .alert("Something went wrong", isPresented: Binding(
            get: { error != nil },
            set: { if !$0 { error = nil } }
        )) {
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
            isPresented: Binding(
                get: { pendingDeletion != nil },
                set: { if !$0 { pendingDeletion = nil } }
            ),
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
        VStack(spacing: Space.tight) {
            // The gradient echoes the app icon and the Generate button, so an
            // empty tab still looks like part of this app rather than a
            // placeholder someone forgot to finish.
            Image(systemName: "waveform")
                .font(.system(size: 40))
                .foregroundStyle(LinearGradient.bunyiBrand)

            Text("Nothing here yet")
                .font(.system(size: 15, weight: .semibold))

            Text("Audio you generate is listed here, newest first.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func row(_ item: GeneratedOutput) -> some View {
        HStack(spacing: 12) {
            Button {
                toggle(item)
            } label: {
                // The ring is the progress bar, so a playing row needs no
                // separate track taking up width — the control and its
                // progress are the same object.
                ZStack {
                    if playingID == item.url {
                        Circle()
                            .stroke(.quaternary, lineWidth: 2)
                        Circle()
                            .trim(from: 0, to: progress)
                            .stroke(Color.accentColor,
                                    style: StrokeStyle(lineWidth: 2, lineCap: .round))
                            // Trim starts at 3 o'clock; sweeping from the top
                            // is what reads as progress.
                            .rotationEffect(.degrees(-90))
                            // Matched to the tick, so the ring sweeps instead
                            // of stepping.
                            .animation(.linear(duration: 0.2), value: progress)
                    }
                    Image(systemName: playingID == item.url ? "stop.fill" : "play.fill")
                        .font(.system(size: 9))
                }
                .frame(width: 22, height: 22)
                .contentShape(Circle())
            }
            .buttonStyle(RowIconButtonStyle())
            .help(playingID == item.url ? "Stop" : "Play")
            .accessibilityLabel(playingID == item.url ? "Stop" : "Play")

            // The record tooltip covers the text and the empty middle, not
            // the buttons. On the whole row it won every hover, so each
            // button's own tooltip never appeared — the one exception being
            // Copy, whose label changes state and re-registers its help.
            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 2) {
                    // The prompt when the file carries one, the mode otherwise
                    // — "what did it say" identifies a clip far better than
                    // "Preset voice" repeated down the list. A prompt can be
                    // paragraphs long, so the row shows one line and the whole
                    // thing is on hover.
                    Text(metadata[item.url]?.title ?? item.mode)
                        .fontWeight(.medium)
                        .lineLimit(1)
                    HStack(spacing: 6) {
                        // The mode as a pill, ported from `.tag` on
                        // bunyi.app. It is the one value in this line that is
                        // a category rather than a detail, so it earns a shape
                        // instead of a share of a run-on string.
                        let name = metadata[item.url]?.mode ?? item.mode
                        let tint = Self.mode(named: name)?.pillColor
                            ?? Color.secondary
                        Text(name.uppercased())
                            .font(.system(size: 9, weight: .bold))
                            .tracking(0.6)
                            .foregroundStyle(tint)
                            .padding(.horizontal, 5)
                            .padding(.vertical, 1)
                            .background(tint.opacity(0.14), in: Capsule())

                        Text(Self.subtitle(for: item, metadata: metadata[item.url]))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }

                Spacer(minLength: 8)
            }
            .contentShape(Rectangle())
            .help(Self.tooltip(for: item, metadata: metadata[item.url]))

            // Copying beats hover for anything you want to keep: a tooltip
            // cannot be pasted into a note, a bug report, or back into the app
            // to reproduce a result.
            Button {
                copyDetails(item)
            } label: {
                Image(systemName: copiedID == item.url
                      ? "checkmark" : "doc.on.doc")
            }
            .buttonStyle(RowIconButtonStyle())
            .foregroundStyle(copiedID == item.url ? .green : .primary)
            .help("Copy details")
            .accessibilityLabel("Copy details")

            Button {
                save(item)
            } label: {
                // Icon only, like the three buttons beside it. A Label renders
                // icon *and* text here, so this was the one wide element in
                // every row and the cluster never lined up. The tooltip and
                // the accessibility label carry the word instead.
                Image(systemName: "square.and.arrow.down")
            }
            .buttonStyle(RowIconButtonStyle())
            .help("Save a copy elsewhere")
            .accessibilityLabel("Download")

            Button {
                NSWorkspace.shared.activateFileViewerSelecting([item.url])
            } label: {
                Image(systemName: "folder")
            }
            .buttonStyle(RowIconButtonStyle())
            .help("Show in Finder")
            .accessibilityLabel("Show in Finder")

            Button {
                pendingDeletion = item
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(RowIconButtonStyle())
            // Secondary, not red. Unconditional red put a warning-coloured
            // column down the whole list, which reads as a list of problems
            // rather than a list of recordings. The confirmation dialog is
            // what protects the file; the colour was never doing that work.
            .foregroundStyle(.secondary)
            .help("Move to Trash")
            .accessibilityLabel("Move to Trash")
        }
        .contentShape(Rectangle())
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

    /// Play, or stop what is playing. No resume: these are short clips, and a
    /// paused row is a third state to explain for something you would nearly
    /// always just play again.
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
            progress = 0
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func stop() {
        player?.stop()
        player = nil
        playingID = nil
        // Snap back rather than animating to zero, which reads as a rewind.
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) { progress = 0 }
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

    /// The mode a row's label names, or nil for a file Bunyi did not write.
    ///
    /// Two spellings reach here. Embedded metadata carries the raw value
    /// ("Voice clone"); the filename fallback carries a dash, and
    /// `GeneratedOutput.mode` splits on the first one — so a
    /// `Voice-clone-2026….wav` with no metadata yields just "Voice".
    /// Normalising dashes to spaces makes the first case exact and the second
    /// fail cleanly to a neutral pill rather than mislabelling it.
    private static func mode(named name: String) -> TTSMode? {
        let normalized = name.replacingOccurrences(of: "-", with: " ")
        return TTSMode.allCases.first {
            $0.rawValue.caseInsensitiveCompare(normalized) == .orderedSame
        }
    }

    private static func subtitle(for item: GeneratedOutput,
                                 metadata: OutputMetadata?) -> String {
        let date = item.created.formatted(date: .abbreviated, time: .shortened)
        let size = ByteCountFormatter.string(fromByteCount: item.byteCount,
                                             countStyle: .file)
        // The mode is no longer in here — it is the pill in front of this
        // line. Joining four values with " · " gave all four the same weight,
        // which is why a column of them read as noise rather than as a list
        // you can scan.
        var parts = [date, size]
        // The voice, whichever way this mode picked one — a speaker name, a
        // designed-voice description, or a clone. Showing only `speaker` left
        // every Voice design row with nothing identifying its voice.
        if let voice = metadata?.voiceSummary {
            parts.insert(Self.trimmed(voice), at: 0)
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
