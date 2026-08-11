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
//  SettingsView.swift
//  Bunyi
//

import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct SettingsView: View {
    @State private var backup = BackupManager()
    @AppStorage("appearance") private var appearance: AppAppearance = .system
    @AppStorage("modelRepo.Preset voice") private var presetRepo = ""
    @AppStorage("modelRepo.Voice design") private var designRepo = ""
    @AppStorage("modelRepo.Voice clone") private var cloneRepo = ""

    @State private var showFolderPicker = false
    @State private var folderPath = ""
    @State private var folderError: String?
    @State private var copiedCommand = false
    @State private var models: [DownloadedModel] = []
    @State private var pendingDeletion: DownloadedModel?
    @State private var deleteError: String?
    @State private var configs = ModelConfigLibrary()
    @State private var showSaveConfig = false
    @State private var newConfigName = ""
    @State private var configError: String?
    @State private var pendingConfigDeletion: ModelConfig?

    /// Modes currently pointed at a Hugging Face repo, and their repo IDs.
    ///
    /// A mode set to a self-hosted base URL has none. `effectiveRepoID` returns
    /// whatever the field holds, so building a command from it unconditionally
    /// produced `hf download https://models.example.com/customvoice
    /// --local-dir ".../models/https://models.example.com/customvoice"` —
    /// not a repo ID, not a usable path, and pointed at the wrong folder
    /// besides, since self-hosted downloads land in `models/self-hosted/<slug>`.
    /// Since the app ships a mirror configuration, all three modes being URLs
    /// is a normal state, not a corner case.
    private var hubModes: [(mode: TTSMode, repo: String)] {
        TTSMode.allCases.compactMap { mode in
            guard case .repo(let repo) = mode.effectiveSource else { return nil }
            return (mode, repo)
        }
    }

    /// One ready-to-run download line per Hub-backed mode, using the actual
    /// models-folder path.
    private var preDownloadCommand: String {
        hubModes.map { _, repo in
            "hf download \(repo) --local-dir \"\(folderPath)/models/\(repo)\""
        }.joined(separator: "\n")
    }

    /// The modes this section cannot help with, named so their absence from
    /// the command list reads as deliberate rather than as a missing line.
    private var selfHostedModeNames: [String] {
        TTSMode.allCases
            .filter { if case .baseURL = $0.effectiveSource { return true } else { return false } }
            .map(\.rawValue)
    }

    var body: some View {
        TabView {
            generalTab
                .tabItem { Label("General", systemImage: "gearshape") }
            modelsTab
                .tabItem { Label("Models", systemImage: "person.wave.2") }
            storageTab
                .tabItem { Label("Storage", systemImage: "internaldrive") }
            backupTab
                .tabItem { Label("Backup", systemImage: "archivebox") }
        }
        .frame(width: 560, height: 440)
        .onAppear(perform: refreshFolderPath)
        // A real binding, not .constant: dismissing with Escape must clear the
        // pending deletion, or the dialog reappears with no way out.
        .confirmationDialog(
            "Move this model to the Trash?",
            isPresented: Binding(
                get: { pendingDeletion != nil },
                set: { if !$0 { pendingDeletion = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Move to Trash", role: .destructive) {
                if let model = pendingDeletion { delete(model) }
                pendingDeletion = nil
            }
            Button("Cancel", role: .cancel) { pendingDeletion = nil }
        } message: {
            if let pendingDeletion {
                Text("\(pendingDeletion.name) — "
                     + "\(pendingDeletion.byteCount.formatted(.byteCount(style: .file))). "
                     + "It downloads again the next time you generate in that mode.")
            }
        }
        .fileImporter(isPresented: $showFolderPicker,
                      allowedContentTypes: [.folder]) { result in
            switch result {
            case .success(let url):
                let gotAccess = url.startAccessingSecurityScopedResource()
                defer { if gotAccess { url.stopAccessingSecurityScopedResource() } }
                do {
                    try ModelsLocation.set(url)
                    folderError = nil
                } catch {
                    folderError = "Could not save that folder: \(error.localizedDescription)"
                }
                refreshFolderPath()
            case .failure:
                break
            }
        }
    }

    // MARK: Tabs

    private var generalTab: some View {
        Form {
            // No eyebrow on this one. The tab is called General, the only
            // section in it holds a row labelled Appearance, and a header
            // between them could say nothing that is not already on screen
            // twice — the same trap Stage 3 fell into with the mode heading.
            // Backup, also a single unnamed section, is left alone for the
            // same reason.
            Section {
                Picker("Appearance", selection: $appearance) {
                    ForEach(AppAppearance.allCases) { mode in
                        Text(mode.title).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
                Text("System follows your macOS appearance; Light and Dark "
                    + "pin the app regardless. Applies immediately.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()
            }
        }
        .formStyle(.grouped)
    }

    private var modelsTab: some View {
        Form {
            Section {
                repoField("Preset voice", text: $presetRepo, mode: .presetVoice)
                repoField("Voice design", text: $designRepo, mode: .voiceDesign)
                repoField("Voice clone", text: $cloneRepo, mode: .voiceClone)
                Text("A Hugging Face repo ID (MLX conversion of Qwen3-TTS) or a "
                    + "base URL to self-host — each must match its mode: "
                    + "CustomVoice for preset voice, VoiceDesign for voice "
                    + "design, Base for voice clone. Leave blank for the "
                    + "default. Applies on the next generate.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()
                Text("To self-host, enter an https base URL holding the model "
                    + "files (e.g. https://example.com/qwen3-custom). The app "
                    + "reads manifest.sha256 there if present — and verifies "
                    + "every file against it — otherwise manifest.txt, "
                    + "otherwise the standard Qwen3-TTS file set. Plain http "
                    + "needs an App Transport Security exception; https is "
                    + "recommended.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()
            } header: {
                // The three fields had no label of any kind above them, so the
                // tab opened on an unheaded stack of text fields.
                Text("Model sources").eyebrow()
            }

            Section {
                HStack {
                    Button("Save current…") {
                        newConfigName = ""
                        showSaveConfig = true
                    }
                    Button("Reset to defaults") { resetModelFields() }
                        .disabled(presetRepo.isEmpty && designRepo.isEmpty
                                  && cloneRepo.isEmpty)
                }

                ForEach(configs.listed) { config in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(config.name)
                            Text(config.summary)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        Spacer(minLength: 12)
                        Button("Restore") { restore(config) }
                        // Nothing to delete: a built-in was never written to
                        // disk. Saving your own config under the same name
                        // replaces it in the list.
                        if !ModelConfigLibrary.isBuiltIn(config) {
                            Button("Delete") { pendingConfigDeletion = config }
                        }
                    }
                }

                Text("Restoring replaces every field above. As with any change "
                    + "here, it applies on the next generate.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()

                Text("The Bunyi mirror serves the same Hugging Face models "
                    + "(Apache-2.0) from models.bunyi.app, with checksums the "
                    + "app verifies as it downloads. Useful where Hugging Face "
                    + "is slow or unreachable. Hugging Face stays the default — "
                    + "it is where the models come from.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()

                if let configError {
                    errorCallout(configError)
                }
            } header: {
                Text("Configurations").eyebrow()
            }
        }
        .formStyle(.grouped)
        // Unlike a model, a configuration cannot go to the Trash — it is a
        // row in a JSON file. So confirm instead: the three URLs behind it are
        // long enough that retyping them is the real cost.
        .confirmationDialog(
            "Delete this configuration?",
            isPresented: Binding(
                get: { pendingConfigDeletion != nil },
                set: { if !$0 { pendingConfigDeletion = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                if let config = pendingConfigDeletion { deleteConfig(config) }
                pendingConfigDeletion = nil
            }
            Button("Cancel", role: .cancel) { pendingConfigDeletion = nil }
        } message: {
            if let pendingConfigDeletion {
                Text("\(pendingConfigDeletion.name) — \(pendingConfigDeletion.summary). "
                     + "This removes the saved sources, not any downloaded model.")
            }
        }
        .alert("Save this configuration", isPresented: $showSaveConfig) {
            TextField("Name", text: $newConfigName)
            Button("Save") { saveCurrentConfig() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Stores the three model sources above so you can switch back "
                 + "to them later. Saving over an existing name replaces it.")
        }
    }

    private func saveCurrentConfig() {
        do {
            try configs.save(name: newConfigName,
                             presetVoice: presetRepo,
                             voiceDesign: designRepo,
                             voiceClone: cloneRepo)
            configError = nil
        } catch {
            configError = error.localizedDescription
        }
    }

    private func deleteConfig(_ config: ModelConfig) {
        do {
            try configs.delete(config)
            configError = nil
        } catch {
            configError = error.localizedDescription
        }
    }

    private func restore(_ config: ModelConfig) {
        presetRepo = config.presetVoice
        designRepo = config.voiceDesign
        cloneRepo = config.voiceClone
    }

    /// Clearing the fields is what "default" means here — each mode falls back
    /// to its built-in repo, which is also what the placeholder text shows.
    private func resetModelFields() {
        presetRepo = ""
        designRepo = ""
        cloneRepo = ""
    }

    private var storageTab: some View {
        Form {
            Section {
                LabeledContent("Models folder") {
                    Text(folderPath)
                        .textSelection(.enabled)
                        .lineLimit(2)
                        .truncationMode(.middle)
                }
                HStack {
                    Button("Change…") { showFolderPicker = true }
                    Button("Show in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting(
                            [ModelsLocation.current()])
                    }
                    if ModelsLocation.isCustom {
                        Button("Use default") {
                            ModelsLocation.resetToDefault()
                            refreshFolderPath()
                        }
                    }
                }
                if let folderError {
                    errorCallout(folderError)
                }
            } header: {
                // Not "Models folder" — that is the label on the row directly
                // below, and a header repeating its first row says nothing.
                Text("Location").eyebrow()
            }

            Section {
                if models.isEmpty {
                    Text("Nothing downloaded yet. Models arrive the first time "
                        + "you generate in each mode.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .calloutBlock()
                } else {
                    ForEach(models) { model in
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(model.name)
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                Text(model.isSelfHosted ? "Self-hosted" : "Hugging Face")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 12)
                            Text(model.byteCount.formatted(.byteCount(style: .file)))
                                .foregroundStyle(.secondary)
                                .monospacedDigit()
                            Button("Delete") { pendingDeletion = model }
                        }
                    }
                    Text("Deleting moves the folder to the Trash. The model "
                        + "downloads again the next time you generate in that "
                        + "mode.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .calloutBlock()
                }
                if let deleteError {
                    errorCallout(deleteError)
                }
            } header: {
                Text("Downloaded models").eyebrow()
            }

            Section {
                Text("Want the models in place ahead of time? Pre-download "
                    + "them with the Hugging Face CLI — the `hf` command "
                    + "from Hugging Face's `huggingface_hub` Python package "
                    + "(install it with `pip install huggingface_hub`).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()

                if hubModes.isEmpty {
                    // Every mode is on a base URL, so there is nothing here to
                    // pre-download with. Saying so beats an empty code block.
                    // No count in the wording. Everything else here is
                    // driven by TTSMode.allCases, so a hardcoded "all three"
                    // would quietly become wrong the day a mode is added.
                    Text("Every mode is set to a self-hosted server on the "
                        + "Models tab, so there is nothing to fetch from "
                        + "Hugging Face. Bunyi downloads those on first use. "
                        + "Point a mode back at a Hugging Face repo and its "
                        + "command appears here.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .calloutBlock()
                } else {
                    Text("These fetch the models the app uses, matching the "
                        + "repos on the Models tab. Run them in Terminal (skip "
                        + "any you don't need); the app then uses the files "
                        + "directly and skips its own download.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .calloutBlock()

                    if !selfHostedModeNames.isEmpty {
                        // Which modes are missing, and why — otherwise a list
                        // with one line looks like two commands went astray.
                        Text("\(selfHostedModeNames.formatted(.list(type: .and))) "
                            + (selfHostedModeNames.count == 1 ? "is" : "are")
                            + " set to a self-hosted server, so "
                            + (selfHostedModeNames.count == 1 ? "it is" : "they are")
                            + " not listed — Bunyi downloads those on first use.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .calloutBlock()
                    }
                }

                if !hubModes.isEmpty {
                    HStack(alignment: .top, spacing: Space.tight) {
                        Text(preDownloadCommand)
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(Space.tight)
                            .background(.quaternary, in: RoundedRectangle(
                                cornerRadius: Radius.control))
                        Button {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(
                                preDownloadCommand, forType: .string)
                            copiedCommand = true
                            Task {
                                try? await Task.sleep(for: .seconds(1.5))
                                copiedCommand = false
                            }
                        } label: {
                            Image(systemName: copiedCommand
                                ? "checkmark" : "doc.on.doc")
                                .frame(width: 16)
                        }
                        .buttonStyle(.borderless)
                        .help("Copy command")
                    }
                }
            } header: {
                Text("Pre-download").eyebrow()
            }
        }
        .formStyle(.grouped)
    }

    private var backupTab: some View {
        Form {
            Section {
                HStack {
                    if backup.status.isBusy {
                        Button("Stop", role: .destructive) { backup.cancel() }
                    } else {
                        Button("Back up models…") { chooseBackupDestination() }
                        Button("Restore from backup…") { chooseRestoreArchive() }
                    }
                }
                backupStatus
                Text("Backs up every downloaded model to one zip. Restore "
                    + "adds models from a backup that aren't already in the "
                    + "models folder. Details in the Logs window (⌘L).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .calloutBlock()
            }
        }
        .formStyle(.grouped)
    }

    /// An inline error, wearing the callout treatment with a red rule.
    ///
    /// These were bare red `.caption` lines: the same size, weight and
    /// position as the explanatory prose beside them, differing only in hue —
    /// so the app's failures were the quietest thing in the window, and
    /// invisible to anyone who cannot separate red from grey. The rule and the
    /// glyph carry it now, and the colour is corroboration rather than the
    /// whole signal.
    ///
    /// Three of the four such strings are in this file. The fourth,
    /// `voiceError`, is in `ContentView.swift` and stays as it is until that
    /// view's own pass — deliberately, not by oversight.
    private func errorCallout(_ message: String) -> some View {
        Label(message, systemImage: "exclamationmark.triangle")
            .font(.caption)
            .foregroundStyle(.red)
            .calloutBlock(.error)
    }

    @ViewBuilder
    private var backupStatus: some View {
        switch backup.status {
        case .idle:
            EmptyView()
        case .working(let message):
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    if backup.progress == nil {
                        ProgressView().controlSize(.small)
                    }
                    Text(message).foregroundStyle(.secondary)
                    if let p = backup.progress {
                        Text("\(Int(p * 100))%")
                            .foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                if let p = backup.progress {
                    ProgressView(value: p)
                }
            }
        case .done(let message):
            Label(message, systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .font(.callout)
        case .error(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .font(.callout)
        }
    }

    private func chooseBackupDestination() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.zip]
        panel.nameFieldStringValue = "Bunyi Models "
            + Date().formatted(.iso8601.year().month().day()) + ".zip"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        backup.startBackup(to: url)
    }

    private func chooseRestoreArchive() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.zip]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        backup.startRestore(from: url)
    }

    private func repoField(_ label: String, text: Binding<String>,
                           mode: TTSMode) -> some View {
        TextField(label, text: text, prompt: Text(mode.repoID))
            .textFieldStyle(.roundedBorder)
            .autocorrectionDisabled()
    }

    private func refreshFolderPath() {
        folderPath = ModelsLocation.current().path
        refreshModels()
    }

    private func refreshModels() {
        models = ModelStore.all()
    }

    private func delete(_ model: DownloadedModel) {
        do {
            try ModelStore.delete(model)
            deleteError = nil
        } catch {
            deleteError = error.localizedDescription
        }
        refreshModels()
    }
}

#Preview {
    SettingsView()
}
