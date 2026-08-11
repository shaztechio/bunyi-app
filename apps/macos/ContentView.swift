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
//  ContentView.swift
//  Bunyi
//

import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

/// What the segmented control selects. History is not a generation mode —
/// TTSMode maps one-to-one onto model repos — so it sits beside them here
/// rather than becoming a fourth case there.
enum MainTab: Hashable {
    case generate(TTSMode)
    case history
}

struct ContentView: View {
    /// Opens the `Window(id: "logs")` scene declared in BunyiApp. Focuses the
    /// existing window if it is already open rather than making a second one.
    @Environment(\.openWindow) private var openWindow

    @State private var engine = TTSEngine()

    @State private var tab: MainTab = .generate(.presetVoice)

    /// The generation mode the rest of the view works with. Selecting History
    /// leaves it alone, so returning to a mode returns to the one you left.
    private var mode: TTSMode {
        if case .generate(let mode) = tab { return mode }
        return lastGenerateMode
    }

    @State private var lastGenerateMode: TTSMode = .presetVoice
    @State private var text: String = ""
    @State private var speaker: String = "Ryan"
    @State private var instruct: String = ""
    @State private var language: String = "auto"
    @State private var referenceAudioURL: URL?
    @State private var referenceText: String = ""
    @State private var showImporter = false

    @State private var player: AVAudioPlayer?
    @State private var isPlaying = false
    @State private var playbackTime: TimeInterval = 0

    @State private var library = VoiceLibrary()
    @State private var selectedVoiceID: UUID?
    @State private var showSaveVoice = false
    @State private var newVoiceName = ""
    @State private var voiceError: String?

    @State private var genTask: Task<Void, Never>?

    /// Focus has to be taken away from the script when work starts. `.disabled`
    /// and `.allowsHitTesting` both leave a TextEditor that already holds focus
    /// still receiving keystrokes — hit testing is about the mouse, and the
    /// keyboard goes to whatever is first responder regardless.
    @FocusState private var scriptFocused: Bool

    private let languages = [
        "auto", "english", "chinese", "japanese", "korean", "german",
        "french", "russian", "portuguese", "spanish", "italian",
    ]

    // Fallback list until a CustomVoice model is loaded and reports its own.
    private let defaultSpeakers = [
        "Ryan", "Aiden", "Vivian", "Serena", "Uncle_Fu",
        "Dylan", "Eric", "Ono_Anna", "Sohee",
    ]

    /// Whether the current mode has everything it needs.
    ///
    /// Checked before the button is pressed rather than inside the engine: the
    /// engine already rejects a clone with no reference clip, but only after
    /// preparing the model — which on a first run means waiting out a 3.4 GB
    /// download to be told a file is missing. Voice design had no check at all
    /// and would generate some arbitrary voice from an empty description.
    private var canGenerate: Bool {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }
        switch mode {
        case .presetVoice:
            return true     // a speaker is always selected
        case .voiceDesign:
            return !instruct.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case .voiceClone:
            return referenceAudioURL != nil
        }
    }

    /// Why Generate is unavailable, shown on hover — a disabled button with no
    /// explanation is a dead end.
    private var generateBlockedReason: String? {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return "Enter some text to speak."
        }
        switch mode {
        case .presetVoice:
            return nil
        case .voiceDesign:
            return instruct.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "Describe the voice you want." : nil
        case .voiceClone:
            return referenceAudioURL == nil
                ? "Choose a reference clip, or pick a saved voice." : nil
        }
    }

    /// The list the picker shows: the model's own speakers once one is loaded,
    /// the fallback until then.
    private var availableSpeakers: [String] {
        engine.speakers.isEmpty ? defaultSpeakers : engine.speakers
    }

    /// Speakers are identified to the model by its exact spelling, which is
    /// lowercase and underscored ("uncle_fu"). Only the label is prettified, so
    /// the list does not visibly change the moment a model finishes loading.
    private static func speakerLabel(_ speaker: String) -> String {
        speaker
            .replacingOccurrences(of: "_", with: " ")
            .split(separator: " ")
            .map { $0.prefix(1).uppercased() + $0.dropFirst() }
            .joined(separator: " ")
    }

    /// Keep the chosen speaker across the swap from the fallback list to the
    /// model's. The names match apart from case, so a plain identity check
    /// leaves the Picker with a selection that is not in its list — which it
    /// renders as blank, silently losing the choice just as a generation ends.
    private func reconcileSpeaker(with available: [String]) {
        guard !available.isEmpty, !available.contains(speaker) else { return }
        if let same = available.first(where: {
            $0.caseInsensitiveCompare(speaker) == .orderedSame
        }) {
            speaker = same
        } else if let first = available.first {
            speaker = first
        }
    }

    /// Drives the playback progress bar and detects a naturally-finished clip.
    private let playbackTimer = Timer.publish(every: 0.1, on: .main, in: .common)
        .autoconnect()

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 16) {
                modeBar

                if tab == .history {
                    HistoryView(engine: engine)
                } else {
                    // Locked while work is in progress. Editing the text or
                    // switching speaker mid-run changed nothing about the audio
                    // being produced — the values were already passed to the
                    // engine — so the controls invited edits that silently did
                    // not apply. The help button inside modeBar is deliberately
                    // outside this: SwiftUI's disabled() cannot be undone by a
                    // child, so anything that must stay live has to sit outside
                    // the disabled scope.
                    // allowsHitTesting as well as disabled: TextEditor keeps
                    // taking keystrokes through .disabled() on macOS, so the
                    // script stayed editable during a run in every mode while
                    // the pickers around it correctly greyed out. Refusing hits
                    // is what actually stops typing; the opacity is what makes
                    // it look refused.
                    textCard
                        .disabled(engine.status.isBusy)
                        .allowsHitTesting(!engine.status.isBusy)
                        .opacity(engine.status.isBusy ? 0.6 : 1)
                    optionsCard
                        .disabled(engine.status.isBusy)
                }
            }
            .padding(20)
            // No `alignment: .topLeading`: the text card now grows to fill, so
            // pinning the stack to the top would leave the same gap the cap
            // used to. History supplies its own scrolling list and fills on
            // its own.
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            // History has no Generate button, so an idle bar would read
            // "press ⌘↩ to generate" beside nothing that generates — and the
            // list is better off with the height. It comes back while a run is
            // in progress, which is reachable from History on purpose: that is
            // where the progress and the Stop button live.
            if tab != .history || engine.status.isBusy {
                Divider()

                bottomBar
            }
        }
        .frame(minWidth: 620, minHeight: 580)
        .background(WindowCloseGuard(
            isBusy: { engine.status.isBusy },
            onConfirmedClose: stopWork))
        .fileImporter(isPresented: $showImporter,
                      allowedContentTypes: [.audio]) { result in
            if case .success(let url) = result {
                referenceAudioURL = url
                selectedVoiceID = nil   // a fresh clip is no longer a saved voice
            }
        }
        .alert("Save this voice", isPresented: $showSaveVoice) {
            TextField("Name", text: $newVoiceName)
            Button("Save") { saveCurrentVoice() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Keeps this reference clip and transcript so you can pick the "
                 + "voice again from the menu.")
        }
        .onChange(of: engine.status.isBusy) { _, busy in
            // Resign first, then the disabled modifier keeps it from coming
            // back: a disabled view cannot take focus.
            if busy { scriptFocused = false }
        }
        .onChange(of: availableSpeakers) { _, new in
            reconcileSpeaker(with: new)
        }
        .onChange(of: tab) { _, new in
            if case .generate(let mode) = new { lastGenerateMode = mode }
        }
        .onReceive(playbackTimer) { _ in
            guard isPlaying, let player else { return }
            playbackTime = player.currentTime
            if !player.isPlaying {          // clip finished on its own
                isPlaying = false
                // Snap back to zero — an animated rewind looks like a pulse.
                var txn = Transaction()
                txn.disablesAnimations = true
                withTransaction(txn) { playbackTime = 0 }
            }
        }
    }

    // MARK: Mode bar

    private var modeBar: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("Mode", selection: $tab) {
                ForEach(TTSMode.allCases) { mode in
                    Label(mode.rawValue, systemImage: mode.symbol)
                        .tag(MainTab.generate(mode))
                }
                Label("History", systemImage: "clock.arrow.circlepath")
                    .tag(MainTab.history)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            // History stays reachable mid-run — it only reads the folder, and
            // wanting to hear the previous result while waiting is reasonable.
            // The generation modes do not, because switching one evicts the
            // model the running job is using.
            .disabled(engine.status.isBusy && tab != .history)

            HStack(alignment: .firstTextBaseline) {
                Text(tab == .history
                     ? "Everything you have generated, newest first."
                     : mode.subtitle)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .animation(.none, value: mode)

                Spacer(minLength: 12)

                // Both of these are deliberately not disabled while busy. A
                // long download is exactly when someone wants to read the help
                // or watch the log, and neither touches the running job.
                HStack(spacing: 10) {
                    // Window → Logs (⌘L) already opens this. The button is here
                    // for the same reason the help one is: when a run is slow or
                    // fails, the log is the answer, and nobody finds it in a menu.
                    Button {
                        openWindow(id: "logs")
                    } label: {
                        Image(systemName: "list.bullet.rectangle")
                            .imageScale(.large)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("Open the log (⌘L)")
                    .accessibilityLabel("Open the log")

                    // The Help menu already opens this book; the button is here
                    // because the audience for this app does not go looking in
                    // menus.
                    Button {
                        NSApplication.shared.showHelp(nil)
                    } label: {
                        Image(systemName: "questionmark.circle")
                            .imageScale(.large)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("Open Bunyi Help")
                    .accessibilityLabel("Open Bunyi Help")
                }
            }
        }
    }

    // MARK: Text input

    private var textCard: some View {
        TextEditor(text: $text)
            .focused($scriptFocused)
            .font(.body)
            .scrollContentBackground(.hidden)
            .padding(8)
            // Grows instead of capping at 220. The options card below is
            // intrinsically sized and the window is 580 pt tall at minimum, so
            // a fixed cap left the bottom third of the window empty.
            .frame(minHeight: 160, maxHeight: .infinity)
            .background(Color(nsColor: .textBackgroundColor),
                        in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.primary.opacity(0.08)))
            .overlay(alignment: .topLeading) {
                if text.isEmpty {
                    Text("What should the voice say?")
                        .foregroundStyle(.tertiary)
                        .padding(.top, 16).padding(.leading, 13)
                        .allowsHitTesting(false)
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if !text.isEmpty {
                    Text("\(text.count) characters")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .padding(10)
                        .allowsHitTesting(false)
                }
            }
    }

    // MARK: Per-mode options card

    private var optionsCard: some View {
        VStack(spacing: 0) {
            optionRow(icon: "globe", label: "Language") {
                Picker("Language", selection: $language) {
                    ForEach(languages, id: \.self) { Text($0.capitalized) }
                }
                .labelsHidden()
                .fixedSize()
            }

            switch mode {
            case .presetVoice:
                rowDivider
                optionRow(icon: "person.wave.2", label: "Speaker") {
                    Picker("Speaker", selection: $speaker) {
                        ForEach(availableSpeakers, id: \.self) {
                            Text(Self.speakerLabel($0))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                rowDivider
                optionRow(icon: "sparkles", label: "Style") {
                    TextField("Optional — e.g. calm news anchor", text: $instruct)
                        .textFieldStyle(.roundedBorder)
                }

            case .voiceDesign:
                rowDivider
                optionRow(icon: "sparkles", label: "Voice") {
                    TextField("Describe it — e.g. deep gravelly narrator in his 60s",
                              text: $instruct)
                        .textFieldStyle(.roundedBorder)
                }

            case .voiceClone:
                rowDivider
                optionRow(icon: "bookmark", label: "Voice") {
                    Picker("Saved voice", selection: $selectedVoiceID) {
                        Text("Custom").tag(UUID?.none)
                        ForEach(library.voices) { voice in
                            Text(voice.name).tag(UUID?.some(voice.id))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                    .onChange(of: selectedVoiceID) { _, id in applySavedVoice(id) }

                    Button("Save this voice…") {
                        newVoiceName = ""
                        voiceError = nil
                        showSaveVoice = true
                    }
                    .disabled(referenceAudioURL == nil)

                    if let voice = library.voice(with: selectedVoiceID) {
                        Button("Delete") { deleteSavedVoice(voice) }
                    }
                }
                rowDivider
                optionRow(icon: "music.note", label: "Clip") {
                    Button("Choose…") { showImporter = true }
                    Text(referenceDescription)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                rowDivider
                optionRow(icon: "text.quote", label: "Transcript") {
                    TextField("Auto-detected if left blank", text: $referenceText)
                        .textFieldStyle(.roundedBorder)
                }
                if let voiceError {
                    Text(voiceError)
                        .font(.caption)
                        .foregroundStyle(.red)
                        .padding(.horizontal, 12).padding(.bottom, 8)
                }
            }
        }
        // The border, not the fill, is what makes this read as a card.
        // `controlBackgroundColor` against the window in light appearance is
        // near-identical, so the card had no visible left or right edge — just
        // dividers hanging in white space.
        .background(Color(nsColor: .controlBackgroundColor),
                    in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12)
            .strokeBorder(Color.primary.opacity(0.08)))
    }

    private var rowDivider: some View {
        Divider().padding(.leading, 40)
    }

    /// One labeled row inside the options card: icon + fixed-width label +
    /// the control.
    ///
    /// The trailing `Spacer` is what keeps the row left-aligned, and it is not
    /// optional. `optionsCard` is a `VStack` with the default `.center`
    /// alignment, so the widest row sets the card's width and every narrower
    /// row is centered inside it. Style holds a greedy `TextField` and fills
    /// the card; Language and Speaker hold `.fixedSize()` pickers and do not —
    /// so those two floated in the middle of the window while Style started at
    /// the left edge, in all three modes. `rowDivider`'s 40 pt leading inset,
    /// which is 12 + 18 + 10 measured from a left-aligned row, then lined up
    /// with nothing.
    private func optionRow<Control: View>(
        icon: String, label: String,
        @ViewBuilder control: () -> Control
    ) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .foregroundStyle(.secondary)
                .frame(width: 18)
            Text(label)
                .foregroundStyle(.secondary)
                .frame(width: 76, alignment: .leading)
            control()
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    // MARK: Bottom bar (status + playback + generate)

    private var bottomBar: some View {
        HStack(spacing: 16) {
            statusView
                .frame(maxWidth: .infinity, alignment: .leading)

            // History has its own per-row playback, and its own player. Showing
            // this one too would put two players on screen that can play over
            // each other.
            if tab != .history, engine.lastOutputURL != nil {
                playbackControls
            }

            // Stop replaces Generate while work is in progress, rather than
            // sitting beside it greyed out. Downloading a model can take many
            // minutes, and until now the only way out was closing the window
            // and confirming the prompt.
            //
            // Stop survives in History — a run can still be going while it is
            // open, and hiding the only way to abandon it would strand the
            // user. Generate does not: History has no text to speak, and the
            // button would either do nothing or silently act on a mode that is
            // not on screen.
            if engine.status.isBusy {
                Button(action: stopWork) {
                    Label("Stop", systemImage: "stop.fill")
                        .frame(minWidth: 110)
                }
                // Escape is what people press to abandon something.
                .keyboardShortcut(.cancelAction)
                .buttonStyle(.bordered)
                .controlSize(.large)
                .tint(.red)
                .help("Stop the current operation")
            } else if tab != .history {
                Button(action: generate) {
                    Label("Generate", systemImage: "waveform")
                        .frame(minWidth: 110)
                }
                .keyboardShortcut(.return, modifiers: .command)
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(!canGenerate)
                .help(generateBlockedReason ?? "Generate audio (⌘↩)")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(minHeight: 72)
        .background(.bar)
    }

    /// Play/pause toggle with a live progress bar and elapsed/total time.
    private var playbackControls: some View {
        HStack(spacing: 10) {
            Button(action: togglePlayback) {
                Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                    .frame(width: 14)
            }
            .help(isPlaying ? "Pause" : "Play")

            VStack(spacing: 3) {
                playbackBar
                HStack {
                    Text(timeString(playbackTime))
                    Spacer()
                    Text(timeString(player?.duration ?? 0))
                }
                .font(.caption2)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            }
            .frame(width: 130)

            Button(action: engine.revealLastOutput) {
                Image(systemName: "folder")
            }
            .help("Show in Finder")
        }
    }

    /// The playback bar, drawn rather than delegated to `ProgressView`.
    ///
    /// `ProgressView` cannot do this. On macOS it is backed by an
    /// `NSProgressIndicator`, which tweens its own value changes, and no
    /// SwiftUI escape hatch reaches that: `.animation(nil, value:)` and a
    /// transaction with `disablesAnimations` both govern SwiftUI's animations,
    /// not AppKit's. Both were tried here and neither worked.
    ///
    /// The tween is invisible while a clip plays, because the value only creeps
    /// forward a hundredth at a time. It is very visible at the end, where the
    /// value drops the full width at once — which is what read as the bar
    /// draining backwards instead of resetting.
    ///
    /// Two rounded rectangles have no opinion about how their frames change.
    private var playbackBar: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                Capsule()
                    .fill(Color.accentColor)
                    .frame(width: geo.size.width * playbackProgress)
            }
        }
        .frame(height: 4)
    }

    /// Clamped: `currentTime` can overshoot `duration` slightly on the last
    /// tick, and an unclamped fraction would push the fill past the track.
    private var playbackProgress: Double {
        guard let player, player.duration > 0 else { return 0 }
        return min(max(playbackTime / player.duration, 0), 1)
    }

    @ViewBuilder
    private var statusView: some View {
        switch engine.status {
        case .idle:
            Label("Ready — press ⌘↩ to generate", systemImage: "checkmark.circle")
                .foregroundStyle(.tertiary)
                .font(.callout)
        case .downloading(let fraction):
            VStack(alignment: .leading, spacing: 4) {
                Label("Downloading voice model — one-time, a few GB",
                      systemImage: "arrow.down.circle")
                    .font(.callout).foregroundStyle(.secondary)
                ProgressView(value: fraction)
                if let detail = engine.downloadDetail {
                    Text(detail)
                        .font(.caption).foregroundStyle(.secondary)
                        .monospacedDigit()
                        .lineLimit(1)
                }
            }
        case .loading:
            busyLine("Loading model…")
        case .transcribing:
            busyLine("Transcribing the reference clip…")
        case .generating(let tokens):
            busyLine(tokens > 0 ? "Generating… (\(tokens) tokens)" : "Generating…")
        case .stopping:
            // Says why it is still busy. The alternative — going idle while the
            // model is still generating — reads as finished and invites a
            // second Generate on top of the first.
            busyLine("Stopping — finishing the current job…")
        case .error(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .font(.callout)
                .lineLimit(2)
        }
    }

    private func busyLine(_ message: String) -> some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text(message).foregroundStyle(.secondary).monospacedDigit()
        }
    }

    // MARK: Actions

    private func generate() {
        player?.stop()
        isPlaying = false
        playbackTime = 0
        genTask?.cancel()
        // Drop the previous run's audio the moment a new one starts: the
        // playback controls disappear with it, so there is nothing offering to
        // play the old result while a new one is being made. It also means a
        // cancelled run leaves nothing to play, rather than quietly falling
        // back to the file from before.
        engine.clearLastOutput()
        genTask = Task {
            await engine.generate(
                mode: mode,
                text: text,
                speaker: speaker,
                instruct: instruct,
                language: language,
                referenceAudioURL: referenceAudioURL,
                referenceText: referenceText
            )
            // Show what auto-transcription heard, so it's visible and gets
            // stored if the user then saves this voice.
            if referenceText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
               let auto = engine.lastReferenceTranscript {
                referenceText = auto
            }
            guard !Task.isCancelled, engine.lastOutputURL != nil else { return }
            startPlayback()
        }
    }

    /// Stops in-flight work when the window is closed mid-operation. Cancels
    /// the generation task (cooperative — downloads and streaming stop at the
    /// next checkpoint) and resets engine state.
    private func stopWork() {
        genTask?.cancel()
        player?.stop()
        isPlaying = false
        engine.stop()
    }

    // MARK: Playback

    private func togglePlayback() {
        guard let url = engine.lastOutputURL else { return }
        if isPlaying {
            player?.pause()
            isPlaying = false
            return
        }
        // Fresh clip, or one that played to the end → start over.
        if player == nil || player?.url != url
            || (player?.currentTime ?? 0) >= (player?.duration ?? 0) - 0.05 {
            let next = try? AVAudioPlayer(contentsOf: url)
            next?.prepareToPlay()
            player = next
            playbackTime = 0
        }
        player?.play()
        isPlaying = player != nil
    }

    /// Auto-play once a generation finishes.
    ///
    /// Deliberately not the same shape as `togglePlayback`. Generation ending
    /// sets `status = .idle`, which re-enables the whole form — `textCard` and
    /// `optionsCard` drop `disabled`, `allowsHitTesting` and `opacity` — and
    /// SwiftUI rebuilds all of it. Playing from here put the player's first
    /// buffer refill in the middle of that rebuild, and it was audible: about a
    /// second in, the clip stuttered once. The same file played from History,
    /// which rebuilds nothing, never did.
    ///
    /// So: fill the buffers now, and start the hardware on the next runloop
    /// turn, once the rebuild has been and gone.
    private func startPlayback() {
        guard let url = engine.lastOutputURL,
              let next = try? AVAudioPlayer(contentsOf: url) else { return }
        next.prepareToPlay()
        player = next
        playbackTime = 0
        DispatchQueue.main.async {
            // A second Generate, or the window closing, can replace or clear
            // the player before this lands — starting it then would play over
            // the top of whatever came after.
            guard player === next else { return }
            next.play()
            isPlaying = true
        }
    }

    private func timeString(_ time: TimeInterval) -> String {
        let total = Int(time.rounded(.down))
        return String(format: "%d:%02d", total / 60, total % 60)
    }

    // MARK: Saved voices

    private var referenceDescription: String {
        if let voice = library.voice(with: selectedVoiceID) { return voice.name }
        return referenceAudioURL?.lastPathComponent
            ?? "5–10 s of clean speech, WAV/M4A/MP3"
    }

    private func applySavedVoice(_ id: UUID?) {
        guard let voice = library.voice(with: id) else { return }
        referenceAudioURL = library.audioURL(for: voice)
        referenceText = voice.transcript
        voiceError = nil
    }

    private func saveCurrentVoice() {
        guard let url = referenceAudioURL else { return }
        let name = newVoiceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return }
        do {
            let voice = try library.save(name: name, audioURL: url,
                                         transcript: referenceText)
            referenceAudioURL = library.audioURL(for: voice)
            selectedVoiceID = voice.id
            voiceError = nil
        } catch {
            voiceError = "Couldn't save that voice: \(error.localizedDescription)"
        }
    }

    private func deleteSavedVoice(_ voice: SavedVoice) {
        library.delete(voice)
        selectedVoiceID = nil
        referenceAudioURL = nil
        referenceText = ""
    }
}

// MARK: - Mode presentation (SF Symbols + plain-language subtitle)

private extension TTSMode {
    var symbol: String {
        switch self {
        case .presetVoice: return "person.wave.2"
        case .voiceDesign: return "wand.and.stars"
        case .voiceClone: return "waveform.badge.mic"
        }
    }

    var subtitle: String {
        switch self {
        case .presetVoice:
            return "Pick a built-in voice and give it a style."
        case .voiceDesign:
            return "Describe a brand-new voice in words."
        case .voiceClone:
            return "Copy a voice from a short reference clip."
        }
    }
}

// All colors in this view are semantic (`.secondary`, `.bar`,
// `NSColor.textBackgroundColor`, …), so it follows the system
// appearance — these previews pin both variants against regressions.
#Preview("Light") {
    ContentView()
        .preferredColorScheme(.light)
}

#Preview("Dark") {
    ContentView()
        .preferredColorScheme(.dark)
}
