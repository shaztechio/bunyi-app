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

    /// The report being shown, if any. Nil means no dialog — including the
    /// common case of a clean preflight, which must be invisible.
    @State private var doctorReport: DoctorReport?
    /// Set when the report is the reason a generation did not start, so the
    /// dialog can say so rather than looking like an unprompted health check.
    @State private var doctorBlockedRun = false
    @State private var doctorRunning = false

    @State private var tab: MainTab = .generate(.presetVoice)

    /// The generation mode the rest of the view works with. Selecting History
    /// leaves it alone, so returning to a mode returns to the one you left.
    private var mode: TTSMode {
        if case .generate(let mode) = tab { return mode }
        return lastGenerateMode
    }

    @State private var lastGenerateMode: TTSMode = .presetVoice

    /// Spec §3e. Read here rather than passed in, because the setting is
    /// about what leaving a mode does and this is where a mode is left.
    @AppStorage("unloadOnModeSwitch") private var unloadOnModeSwitch = true
    @State private var text: String = ""
    @State private var speaker: String = "Ryan"
    @State private var instruct: String = ""
    @State private var language: String = "auto"
    @State private var referenceAudioURL: URL?
    @State private var referenceText: String = ""
    @State private var showImporter = false

    @State private var player: AVAudioPlayer?
    /// Which file `player` was built from. Tracked separately because a player
    /// built from `Data` reports a nil `url`.
    @State private var playerURL: URL?
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

    /// Whether the script is effectively empty. Whitespace counts as nothing.
    ///
    /// Defined once because it was not: `canGenerate` and
    /// `generateBlockedReason` trimmed, while `showExamples` used a plain
    /// `isEmpty`. A single typed space therefore hid the examples and left
    /// Generate disabled — restoring, with one keystroke, exactly the dead end
    /// the examples exist to remove.
    private var scriptIsBlank: Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Whether the current mode has everything it needs.
    ///
    /// Checked before the button is pressed rather than inside the engine: the
    /// engine already rejects a clone with no reference clip, but only after
    /// preparing the model — which on a first run means waiting out a 3.4 GB
    /// download to be told a file is missing. Voice design had no check at all
    /// and would generate some arbitrary voice from an empty description.
    private var canGenerate: Bool {
        guard !scriptIsBlank else {
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
        if scriptIsBlank {
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
            VStack(alignment: .leading, spacing: Space.card) {
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
                    VStack(alignment: .leading, spacing: Space.tight) {
                        textCard
                        exampleStrip
                    }
                    .disabled(engine.status.isBusy)
                    .allowsHitTesting(!engine.status.isBusy)
                    .opacity(engine.status.isBusy ? 0.6 : 1)
                    optionsCard
                        .disabled(engine.status.isBusy)
                }
            }
            .padding(Space.window)
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
        // A real binding, matching History's: dismissing with Escape clears the
        // report, so it does not reappear the moment anything redraws.
        .sheet(isPresented: Binding(
            get: { doctorReport != nil },
            set: { if !$0 { doctorReport = nil } }
        )) {
            if let report = doctorReport {
                DoctorView(report: report, blockedRun: doctorBlockedRun) {
                    doctorReport = nil
                }
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
            guard case .generate(let mode) = new else { return }
            // Compared against the mode being left rather than the previous
            // tab, so that going Preset → History → Design still counts
            // as leaving preset voice. History is not a mode and never holds a
            // model of its own.
            if mode != lastGenerateMode { releaseModelOfModeBeingLeft() }
            lastGenerateMode = mode
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
        .toolbar { windowToolbar }
    }

    /// Lets go of the model belonging to the mode just left (spec §3e).
    ///
    /// The engine would drop it anyway at the next `prepare`, which is the
    /// path that still has to work when this is turned off. Doing it here is
    /// what stops several gigabytes sitting in unified memory for a mode
    /// nobody is looking at until the next time one is generated.
    private func releaseModelOfModeBeingLeft() {
        guard unloadOnModeSwitch else { return }

        // The generation modes are disabled mid-run, so this should not be
        // reachable — but unloading a model out from under a running
        // generation is bad enough to refuse rather than to trust the picker
        // to have prevented.
        guard !engine.status.isBusy else { return }

        engine.unload(reason: "left \(lastGenerateMode.rawValue)")
    }

    // MARK: Mode bar

    private var modeBar: some View {
        VStack(alignment: .leading, spacing: Space.tight) {
            // Text, not Label. macOS's segmented picker renders the title and
            // discards the image, so the systemImage: these used to carry was
            // never drawn. The per-mode SF Symbol that fed it had no other
            // caller and went with it.
            Picker("Mode", selection: $tab) {
                ForEach(TTSMode.allCases) { mode in
                    Text(mode.rawValue).tag(MainTab.generate(mode))
                }
                Text("History").tag(MainTab.history)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .controlSize(.large)
            // History stays reachable mid-run — it only reads the folder, and
            // wanting to hear the previous result while waiting is reasonable.
            // The generation modes do not, because switching one evicts the
            // model the running job is using.
            .disabled(engine.status.isBusy && tab != .history)

            // One line, not two. A heading here was tried and removed: the
            // segmented control directly above already names the mode, so
            // "Voice clone" / "Clone a voice from a clip" / "Copy a voice
            // from a short reference clip" stacked three restatements of
            // the same idea. The picker is the heading; this is the
            // explanation under it.
            Text(tab == .history
                 ? "Everything you have generated, newest first."
                 : mode.subtitle)
                .font(.callout)
                .foregroundStyle(.secondary)
                .animation(.none, value: mode)
        }
    }

    /// The window's toolbar: Settings, Doctor, Logs, Help.
    ///
    /// Logs and Help used to float at the right of the subtitle row as two
    /// unlabelled grey glyphs, which read as debris rather than as controls. In
    /// the toolbar they get the platform's own treatment; Doctor and Settings
    /// were added to the same group afterwards.
    ///
    /// The move also makes an invariant structural. All four must stay usable
    /// while work is in progress — a long download is exactly when someone
    /// wants to read the help, watch the log, or ask what is wrong, and none of
    /// them touches the running job — and previously that held only because
    /// they sat outside the `.disabled(engine.status.isBusy)` scope in the view
    /// tree, which a comment had to defend. A toolbar is outside that scope by
    /// construction.
    @ToolbarContentBuilder
    private var windowToolbar: some ToolbarContent {
        // Settings, then Doctor, Logs, Help — the order the .NET app's header
        // row uses, so the two apps do not disagree about where a button is.
        // The last three still read by how far the answer is from the app:
        // whether it can run at all, what it did, what it is.
        ToolbarItemGroup(placement: .primaryAction) {
            // Settings has a home on macOS that Windows and Linux do not give
            // it — the app menu, and ⌘, — so this button is redundant here in a
            // way it is not there. It is here anyway, first, because the .NET
            // app puts it first and a user moving between the two should find
            // the same row in the same order. `SettingsLink` rather than a
            // hand-rolled action: it is the only supported way to open the
            // Settings scene, and it focuses the window if it is already up
            // instead of doing nothing.
            SettingsLink {
                Label("Settings", systemImage: "gearshape")
            }
            .help("Settings (⌘,)")

            Button {
                runDoctorOnDemand()
            } label: {
                Label("Doctor", systemImage: "stethoscope")
            }
            .help("Check whether this Mac can generate audio")
            .disabled(doctorRunning)

            // Window → Logs (⌘L) already opens this. The button is here for
            // the same reason the help one is: when a run is slow or fails,
            // the log is the answer, and nobody finds it in a menu.
            Button {
                openWindow(id: "logs")
            } label: {
                // "Logs", matching the window it opens and the Window → Logs
                // menu item. A toolbar shown with labels would otherwise offer
                // "Log" for a window called "Logs".
                Label("Logs", systemImage: "list.bullet.rectangle")
            }
            .help("Open Logs (⌘L)")

            // The Help menu already opens this book; the button is here
            // because the audience for this app does not go looking in menus.
            Button {
                NSApplication.shared.showHelp(nil)
            } label: {
                Label("Help", systemImage: "questionmark.circle")
            }
            .help("Open Bunyi Help")
        }
    }

    // MARK: Text input

    private var textCard: some View {
        TextEditor(text: $text)
            .focused($scriptFocused)
            // Otherwise it announces itself as "text entry area" — the role,
            // not the field. This is the thing the whole window is for, and a
            // screen reader had no way to say which of the two text inputs it
            // had landed in.
            .accessibilityLabel("Script")
            .font(.bunyiEditor)
            .scrollContentBackground(.hidden)
            .padding(Space.tight)
            // Grows instead of capping at 220. The options card below is
            // intrinsically sized and the window is 580 pt tall at minimum, so
            // a fixed cap left the bottom third of the window empty.
            .frame(minHeight: 160, maxHeight: .infinity)
            .background(Color(nsColor: .textBackgroundColor),
                        in: RoundedRectangle(cornerRadius: Radius.card))
            .overlay(RoundedRectangle(cornerRadius: Radius.card)
                .strokeBorder(Color.primary.opacity(0.08)))
            .overlay(alignment: .topLeading) {
                if text.isEmpty {
                    // Derived, not hand-tuned. The old 16/13 were eyeballed
                    // against TextEditor's internals and did not sit on the
                    // caret. The real offset is the padding we applied plus
                    // NSTextView's own container inset, which is 5 across and
                    // 0 down — SwiftUI adds no further top inset of its own.
                    Text("What should the voice say?")
                        .font(.bunyiEditor)
                        .foregroundStyle(.tertiary)
                        .padding(.top, Space.tight)
                        .padding(.leading, Space.tight + 5)
                        .allowsHitTesting(false)
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if !text.isEmpty {
                    // monospacedDigit so the counter stops reflowing on every
                    // keystroke as digits of different widths swap in.
                    Text("\(text.count) characters")
                        .font(.caption2)
                        .monospacedDigit()
                        .foregroundStyle(.tertiary)
                        .padding(Space.row)
                        .allowsHitTesting(false)
                }
            }
    }

    // MARK: First-run examples

    /// Whether the examples are on screen (`FEATURES.md` §1).
    ///
    /// Two conditions, and the second is the one that is easy to miss. Empty
    /// script is not by itself a first run: clearing the box after a
    /// generation leaves the result playing in the bottom bar, and offering
    /// "try one of these" beside audio the user just made reads as the app
    /// forgetting what it just did. `lastOutputURL` is nil until a run
    /// succeeds and is cleared again when the next one starts, so it is
    /// exactly "there is no result on screen".
    ///
    /// Clone mode contributes no examples, so the strip is absent there
    /// without a third condition here — see `TTSMode.examples`.
    private var showExamples: Bool {
        scriptIsBlank && engine.lastOutputURL == nil && !mode.examples.isEmpty
    }

    /// The clickable examples under the editor.
    ///
    /// `ViewThatFits` rather than a fixed row: the descriptions in voice
    /// design are phrases, not words, and three of them do not fit across a
    /// 620 pt window at the minimum size. One row when there is room, stacked
    /// when there is not — truncating an example prompt would leave something
    /// unreadable to click.
    @ViewBuilder
    private var exampleStrip: some View {
        if showExamples {
            VStack(alignment: .leading, spacing: Space.tight) {
                Text(mode.examplePrompt)
                    .font(.caption)
                    .foregroundStyle(.secondary)

                ViewThatFits(in: .horizontal) {
                    HStack(spacing: Space.tight) { exampleChips }
                    VStack(alignment: .leading, spacing: Space.tight) {
                        exampleChips
                    }
                }
            }
            // Left-aligned with the cards above and below, whichever branch
            // ViewThatFits picks.
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private var exampleChips: some View {
        ForEach(mode.examples, id: \.self) { example in
            Button(example) { apply(example: example) }
                .buttonStyle(ExampleChipStyle())
                // The chip's own text, said out loud. SwiftUI derives this
                // from the button's label already; stated anyway so the name
                // cannot drift if the chip's rendering changes.
                .accessibilityLabel(example)
        }
    }

    /// Fills the field the current mode's examples are for.
    ///
    /// Preset voice fills the script; **voice design fills `instruct`**, the
    /// voice description, because that is the input that mode adds and the one
    /// whose shape nobody guesses — the sentence to speak is the easy half.
    /// So a design example leaves the script empty on purpose, and the strip
    /// stays up until the user writes one.
    private func apply(example: String) {
        switch mode {
        case .presetVoice:
            text = example
        case .voiceDesign:
            instruct = example
        case .voiceClone:
            break   // no examples to click; see TTSMode.examples
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
                            Text(DisplayName.of($0))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                rowDivider
                optionRow(icon: "sparkles", label: "Style") {
                    TextField("Optional — e.g. calm news anchor", text: $instruct)
                        // The placeholder is a hint, not a name: it disappears
                        // the moment anything is typed, taking the only clue
                        // about what the field is with it.
                        .accessibilityLabel("Style")
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
                        .padding(.horizontal, Space.row).padding(.bottom, Space.tight)
                }
            }
        }
        // The border, not the fill, is what makes this read as a card.
        // `controlBackgroundColor` against the window in light appearance is
        // near-identical, so the card had no visible left or right edge — just
        // dividers hanging in white space.
        .background(Color(nsColor: .controlBackgroundColor),
                    in: RoundedRectangle(cornerRadius: Radius.card))
        .overlay(RoundedRectangle(cornerRadius: Radius.card)
            .strokeBorder(Color.primary.opacity(0.08)))
    }

    private var rowDivider: some View {
        Divider().padding(.leading, OptionRow.labelInset)
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
        HStack(spacing: Space.tight) {
            Image(systemName: icon)
                .foregroundStyle(.secondary)
                .frame(width: OptionRow.iconColumn)
                // Decoration. The row says Language, Speaker or Style right
                // beside it, so announcing the glyph reads the row twice — §12,
                // and the treatment DoctorView's severity icons already get.
                .accessibilityHidden(true)
            Text(label)
                .foregroundStyle(.secondary)
                .frame(width: 76, alignment: .leading)
            control()
            Spacer(minLength: 0)
        }
        .padding(.horizontal, Space.row)
        .padding(.vertical, Space.tight)
    }

    // MARK: Bottom bar (status + playback + generate)

    private var bottomBar: some View {
        HStack(spacing: Space.card) {
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
                .buttonStyle(ActionButtonStyle(role: .destructive))
                .help("Stop the current operation")
                // A custom ButtonStyle does not carry the Label's text into the
                // accessibility tree, so both action buttons announce nothing
                // without this — the two most important controls in the window,
                // silent. Read out of the tree rather than assumed.
                .accessibilityLabel("Stop")
            } else if tab != .history {
                Button(action: generate) {
                    Label("Generate", systemImage: "waveform")
                        .frame(minWidth: 110)
                }
                .keyboardShortcut(.return, modifiers: .command)
                .buttonStyle(ActionButtonStyle(role: .primary))
                .disabled(!canGenerate)
                // Still on hover, deliberately. `spec/FEATURES.md` §1 pins
                // "says why on hover" — surfacing this inline is a behaviour
                // change and needs the spec edited first, not a visual PR.
                .help(generateBlockedReason ?? "Generate audio (⌘↩)")
                // Redundant, and kept: the Label already names this, as an
                // AX client reading what VoiceOver reads confirmed (#162).
                // The explicit label pins the name if the Label ever becomes
                // an icon.
                .accessibilityLabel("Generate")
            }
        }
        .padding(.horizontal, Space.card)
        .padding(.vertical, Space.row)
        .frame(minHeight: 72)
        .background(.bar)
    }

    /// Play/pause toggle with a live progress bar and elapsed/total time.
    private var playbackControls: some View {
        HStack(spacing: Space.tight) {
            Button(action: togglePlayback) {
                Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                    .frame(width: 14)
            }
            .help(isPlaying ? "Pause" : "Play")

            VStack(spacing: Space.hair) {
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
            // .secondary, not .tertiary. This bar is the app's only feedback
            // channel, and the faintest style SwiftUI offers is the wrong
            // place to put the one line that says whether it is working.
            Label("Ready — press ⌘↩ to generate", systemImage: "checkmark.circle")
                .foregroundStyle(.secondary)
                .font(.callout)
        case .downloading(let fraction):
            VStack(alignment: .leading, spacing: Space.hair) {
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
        HStack(spacing: Space.tight) {
            ProgressView().controlSize(.small)
            Text(message).foregroundStyle(.secondary).monospacedDigit()
        }
    }

    // MARK: Actions

    /// The toolbar run: every finding, passes included, and the slow integrity
    /// check that the pre-generation run cannot afford.
    private func runDoctorOnDemand() {
        guard !doctorRunning else { return }
        doctorRunning = true
        Task {
            let report = await Doctor.run(mode: mode, engine: engine, deep: true)
            for line in report.logLines { LogStore.shared.log(line) }
            doctorBlockedRun = false
            doctorReport = report
            doctorRunning = false
        }
    }

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
            // §3e: before the preflight, not merely before the download.
            // Doctor's memory check is a prediction about the run that is about
            // to start, and measured with another mode's model still resident
            // it describes a machine that will not exist by the time it does.
            // Ordinarily already done — leaving the mode released it —
            // but this is the path that still has to hold with that turned off.
            engine.releaseModel(unlessNeededFor: mode)

            // Before `engine.generate`, so a blocker is reported before a
            // download starts rather than after several gigabytes of one. Deep
            // checks are left out: hashing the weights ahead of every run would
            // cost more than the corruption it catches.
            let report = await Doctor.run(mode: mode, engine: engine)
            guard report.isClear else {
                for line in report.logLines { LogStore.shared.log(line) }
                doctorBlockedRun = true
                doctorReport = report
                return
            }
            // Warnings do not stop anything, but they are the explanation when
            // a run is slow later, so they go to the log where that question
            // gets asked.
            for warning in report.warnings {
                LogStore.shared.log(
                    "Doctor: [warning] \(warning.title) — \(warning.detail)")
            }
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
        //
        // Compared against `playerURL`, not `player.url`: a player built from
        // Data has no url, so `player?.url != url` was always true once
        // playback started from memory — every Pause then Play would rebuild
        // the player and restart the clip from zero instead of resuming.
        if player == nil || playerURL != url
            || (player?.currentTime ?? 0) >= (player?.duration ?? 0) - 0.05 {
            guard let next = makePlayer(for: url) else { return }
            player = next
            playerURL = url
            playbackTime = 0
        }
        player?.play()
        isPlaying = player != nil
    }

    /// An `AVAudioPlayer` holding the clip in memory.
    ///
    /// `AVAudioPlayer(contentsOf:)` reads as it plays, so the first refill
    /// after the prepared buffer runs out is a disk read a few seconds in —
    /// which is where a stutter was reported, and when the system may still be
    /// reclaiming the memory the engine released at the end of the run. These
    /// are 24 kHz mono clips: a minute is under 3 MB, so holding one costs
    /// less than risking the read.
    ///
    /// No `.mappedIfSafe` — a mapped file is still backed by disk and faults
    /// in on the same schedule as reading it would.
    private func makePlayer(for url: URL) -> AVAudioPlayer? {
        // Read on the calling actor here, unlike `startPlayback`. This path is
        // a click on Play: nothing has just released gigabytes, the form is not
        // rebuilding, and the file is one the user has already heard, so it is
        // in the page cache. Deferring it would add a hop for no gain.
        guard let data = try? Data(contentsOf: url) else { return nil }
        return Self.makePlayer(from: data)
    }

    /// Both playback paths end here, so `prepareToPlay` cannot be forgotten on
    /// one of them — which is what left the very first version streaming
    /// unprepared and stuttering a second in.
    private static func makePlayer(from data: Data) -> AVAudioPlayer? {
        guard let player = try? AVAudioPlayer(data: data) else { return nil }
        player.prepareToPlay()
        return player
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
        guard let url = engine.lastOutputURL else { return }
        Task {
            // The read is off the main actor. It is the one genuinely
            // expensive step here — a long clip is megabytes — and it lands at
            // the worst possible moment: the engine has just handed several
            // gigabytes back to the system, so the kernel is still reclaiming,
            // and SwiftUI is rebuilding the whole form because status went
            // .idle. Doing file I/O on the main actor in that window is what
            // §2 of the spec rules out for the write side, for the same
            // reason.
            //
            // Data rather than the player crosses the boundary: AVAudioPlayer
            // is not Sendable, and Data is.
            let bytes = await Task.detached(priority: .userInitiated) {
                try? Data(contentsOf: url)
            }.value

            guard let bytes,
                  // A second Generate can finish while the read is in flight.
                  engine.lastOutputURL == url,
                  let next = Self.makePlayer(from: bytes) else { return }
            player = next
            playerURL = url
            playbackTime = 0
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

// MARK: - Mode presentation (plain-language subtitle)

private extension TTSMode {

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

    /// One-click examples for an unused window (`FEATURES.md` §1).
    ///
    /// Preset voice offers sentences to speak. Voice design offers voice
    /// *descriptions*, which is a different field — see `apply(example:)`.
    ///
    /// **Voice clone offers none, deliberately.** Its missing input on a first
    /// run is a reference recording, and no example the app ships can be one.
    /// A sentence to speak would fill the only input clone mode already has
    /// and leave Generate exactly as unavailable, which teaches the wrong
    /// thing about why the button is off. The Clip row says what to supply
    /// instead ("5–10 s of clean speech").
    ///
    /// None of these repeat the placeholder text of the field they fill. A
    /// suggestion that is word-for-word the grey text already on screen looks
    /// like the app failed to have a second idea.
    var examples: [String] {
        switch self {
        case .presetVoice:
            return ["Hello! We'll begin in just a few minutes.",
                    "Your table is ready — please follow me.",
                    "Once upon a time, in a village by the sea…"]
        case .voiceDesign:
            return ["Warm documentary narrator, unhurried",
                    "Bright young podcast host",
                    "Calm late-night radio DJ"]
        case .voiceClone:
            return []
        }
    }

    /// The line above the examples. It names the field they fill, since in
    /// voice design that is not the box they sit under.
    var examplePrompt: String {
        switch self {
        case .presetVoice: return "Not sure what to say? Try one:"
        case .voiceDesign: return "Or describe a voice like one of these:"
        // Never drawn — clone mode has no examples to introduce.
        case .voiceClone:  return ""
        }
    }
}

// MARK: - Example chip

/// One clickable example prompt.
///
/// A capsule rather than plain accent-coloured text: these sit directly under
/// a text box, and words under a text box read as a caption unless something
/// draws an edge around them. Filled only on hover, so three of them do not
/// compete with the two cards or the Generate button for attention while the
/// user reads the window.
///
/// Local to this file on purpose for now — `Theme.swift` is being changed in
/// parallel, and a chip with exactly one caller does not need to be shared
/// before it has a second. It belongs there once the two land, along with the
/// hover-body pattern it duplicates from `RowIconButtonStyle`.
private struct ExampleChipStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        HoverBody(configuration: configuration)
    }

    /// `@State` on the style itself would be reset on every evaluation, since
    /// a `ButtonStyle` is re-created each time — same reason
    /// `RowIconButtonStyle` nests a view.
    private struct HoverBody: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .font(.caption)
                .foregroundStyle(hovering ? AnyShapeStyle(.primary)
                                          : AnyShapeStyle(.secondary))
                .lineLimit(1)
                .padding(.horizontal, Space.row)
                .padding(.vertical, Space.hair)
                .background {
                    Capsule().fill(hovering ? AnyShapeStyle(.quaternary)
                                            : AnyShapeStyle(Color.clear))
                }
                .overlay {
                    Capsule().strokeBorder(Color.primary.opacity(0.12))
                }
                .contentShape(Capsule())
                .onHover { hovering = $0 }
                .opacity(configuration.isPressed ? 0.55 : 1)
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
