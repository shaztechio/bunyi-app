//
//  ContentView.swift
//  Bunyi
//

import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @State private var engine = TTSEngine()

    @State private var mode: TTSMode = .presetVoice
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

    private let languages = [
        "auto", "english", "chinese", "japanese", "korean", "german",
        "french", "russian", "portuguese", "spanish", "italian",
    ]

    // Fallback list until a CustomVoice model is loaded and reports its own.
    private let defaultSpeakers = [
        "Ryan", "Aiden", "Vivian", "Serena", "Uncle_Fu",
        "Dylan", "Eric", "Ono_Anna", "Sohee",
    ]

    /// Drives the playback progress bar and detects a naturally-finished clip.
    private let playbackTimer = Timer.publish(every: 0.1, on: .main, in: .common)
        .autoconnect()

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 16) {
                modeBar
                textCard
                optionsCard
            }
            .padding(20)
            .frame(maxWidth: .infinity, maxHeight: .infinity,
                   alignment: .topLeading)

            Divider()

            bottomBar
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
            Picker("Mode", selection: $mode) {
                ForEach(TTSMode.allCases) { mode in
                    Label(mode.rawValue, systemImage: mode.symbol).tag(mode)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .disabled(engine.status.isBusy)

            Text(mode.subtitle)
                .font(.callout)
                .foregroundStyle(.secondary)
                .animation(.none, value: mode)
        }
    }

    // MARK: Text input

    private var textCard: some View {
        TextEditor(text: $text)
            .font(.body)
            .scrollContentBackground(.hidden)
            .padding(8)
            .frame(minHeight: 140, maxHeight: 220)
            .background(Color(nsColor: .textBackgroundColor),
                        in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10)
                .strokeBorder(.quaternary))
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
                    let available = engine.speakers.isEmpty
                        ? defaultSpeakers : engine.speakers
                    Picker("Speaker", selection: $speaker) {
                        ForEach(available, id: \.self) {
                            Text($0.replacingOccurrences(of: "_", with: " "))
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
                    Spacer()
                }
                rowDivider
                optionRow(icon: "music.note", label: "Clip") {
                    Button("Choose…") { showImporter = true }
                    Text(referenceDescription)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
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
        .background(Color(nsColor: .controlBackgroundColor),
                    in: RoundedRectangle(cornerRadius: 10))
    }

    private var rowDivider: some View {
        Divider().padding(.leading, 40)
    }

    /// One labeled row inside the options card: icon + fixed-width label +
    /// the control.
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
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    // MARK: Bottom bar (status + playback + generate)

    private var bottomBar: some View {
        HStack(spacing: 16) {
            statusView
                .frame(maxWidth: .infinity, alignment: .leading)

            if engine.lastOutputURL != nil {
                playbackControls
            }

            Button(action: generate) {
                Label("Generate", systemImage: "waveform")
                    .frame(minWidth: 110)
            }
            .keyboardShortcut(.return, modifiers: .command)
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(engine.status.isBusy ||
                      text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
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
                ProgressView(value: playbackProgress)
                    // Ticks arrive 10×/s; tweening them looks like pulsing.
                    .animation(nil, value: playbackProgress)
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

    private var playbackProgress: Double {
        guard let player, player.duration > 0 else { return 0 }
        return playbackTime / player.duration
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
            if engine.lastOutputURL != nil { startPlayback() }
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
            player = try? AVAudioPlayer(contentsOf: url)
            playbackTime = 0
        }
        player?.play()
        isPlaying = player != nil
    }

    private func startPlayback() {
        guard let url = engine.lastOutputURL else { return }
        player = try? AVAudioPlayer(contentsOf: url)
        playbackTime = 0
        player?.play()
        isPlaying = player != nil
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
