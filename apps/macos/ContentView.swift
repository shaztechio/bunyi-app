//
//  ContentView.swift
//  Qwen3 TTS Studio
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

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Picker("Mode", selection: $mode) {
                ForEach(TTSMode.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .disabled(engine.status.isBusy)

            TextEditor(text: $text)
                .font(.body)
                .frame(minHeight: 120)
                .overlay(alignment: .topLeading) {
                    if text.isEmpty {
                        Text("What should the voice say?")
                            .foregroundStyle(.tertiary)
                            .padding(.top, 8).padding(.leading, 5)
                            .allowsHitTesting(false)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(.quaternary))

            modeControls

            HStack {
                Picker("Language", selection: $language) {
                    ForEach(languages, id: \.self) { Text($0.capitalized) }
                }
                .frame(maxWidth: 220)
                Spacer()
            }

            statusView

            HStack(spacing: 12) {
                Button(action: generate) {
                    Label("Generate", systemImage: "waveform")
                        .frame(minWidth: 120)
                }
                .keyboardShortcut(.return, modifiers: .command)
                .buttonStyle(.borderedProminent)
                .disabled(engine.status.isBusy ||
                          text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                if engine.lastOutputURL != nil {
                    Button(action: playLast) {
                        Label("Play", systemImage: "play.fill")
                    }
                    Button("Show in Finder") { engine.revealLastOutput() }
                }
                Spacer()
            }
        }
        .padding(20)
        .frame(minWidth: 560, minHeight: 520)
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
    }

    // MARK: Per-mode inputs

    @ViewBuilder
    private var modeControls: some View {
        switch mode {
        case .presetVoice:
            let available = engine.speakers.isEmpty ? defaultSpeakers : engine.speakers
            Picker("Speaker", selection: $speaker) {
                ForEach(available, id: \.self) { Text($0.replacingOccurrences(of: "_", with: " ")) }
            }
            .frame(maxWidth: 300)
            TextField("Style instruction (optional) — e.g. calm news anchor",
                      text: $instruct)
                .textFieldStyle(.roundedBorder)

        case .voiceDesign:
            TextField("Describe the voice — e.g. deep gravelly narrator in his 60s",
                      text: $instruct)
                .textFieldStyle(.roundedBorder)

        case .voiceClone:
            HStack {
                Picker("Saved voice", selection: $selectedVoiceID) {
                    Text("Custom").tag(UUID?.none)
                    ForEach(library.voices) { voice in
                        Text(voice.name).tag(UUID?.some(voice.id))
                    }
                }
                .frame(maxWidth: 260)
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
            if let voiceError {
                Text(voiceError).font(.caption).foregroundStyle(.red)
            }
            HStack {
                Button("Choose reference audio…") { showImporter = true }
                Text(referenceDescription)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            TextField("Reference transcript (auto-detected if left blank)",
                      text: $referenceText)
                .textFieldStyle(.roundedBorder)
        }
    }

    // MARK: Status

    @ViewBuilder
    private var statusView: some View {
        switch engine.status {
        case .idle:
            EmptyView()
        case .downloading(let fraction):
            VStack(alignment: .leading, spacing: 4) {
                Text("Downloading voice model — one-time, a few GB")
                    .font(.callout).foregroundStyle(.secondary)
                ProgressView(value: fraction)
                if let detail = engine.downloadDetail {
                    Text(detail)
                        .font(.caption).foregroundStyle(.secondary)
                        .monospacedDigit()
                }
            }
        case .loading:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Loading model…").foregroundStyle(.secondary)
            }
        case .transcribing:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Transcribing the reference clip…").foregroundStyle(.secondary)
            }
        case .generating(let tokens):
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text(tokens > 0 ? "Generating… (\(tokens) tokens)" : "Generating…")
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
        case .error(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .font(.callout)
        }
    }

    // MARK: Actions

    private func generate() {
        player?.stop()
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
            if engine.lastOutputURL != nil { playLast() }
        }
    }

    /// Stops in-flight work when the window is closed mid-operation. Cancels
    /// the generation task (cooperative — downloads and streaming stop at the
    /// next checkpoint) and resets engine state.
    private func stopWork() {
        genTask?.cancel()
        player?.stop()
        engine.stop()
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

    private func playLast() {
        guard let url = engine.lastOutputURL else { return }
        player = try? AVAudioPlayer(contentsOf: url)
        player?.play()
    }
}

#Preview {
    ContentView()
}
