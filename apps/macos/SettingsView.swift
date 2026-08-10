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

    /// One ready-to-run download line per mode, using each mode's current
    /// (possibly overridden) repo and the actual models-folder path.
    private var preDownloadCommand: String {
        TTSMode.allCases.map { mode in
            let repo = mode.effectiveRepoID
            return "hf download \(repo) --local-dir \"\(folderPath)/models/\(repo)\""
        }.joined(separator: "\n")
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
                Text("To self-host, enter an https base URL holding the model "
                    + "files (e.g. https://example.com/qwen3-custom). The app "
                    + "reads manifest.txt there if present, otherwise the "
                    + "standard Qwen3-TTS file set. Plain http needs an App "
                    + "Transport Security exception; https is recommended.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Configurations") {
                HStack {
                    Button("Save current…") {
                        newConfigName = ""
                        showSaveConfig = true
                    }
                    Button("Reset to defaults") { resetModelFields() }
                        .disabled(presetRepo.isEmpty && designRepo.isEmpty
                                  && cloneRepo.isEmpty)
                }

                if configs.configs.isEmpty {
                    Text("Save the three fields above under a name, then switch "
                        + "between them — a self-hosted mirror and the Hugging "
                        + "Face defaults, say.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(configs.configs) { config in
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
                            Button("Delete") { pendingConfigDeletion = config }
                        }
                    }
                    Text("Restoring replaces all three fields. As with any "
                        + "change here, it applies on the next generate.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                if let configError {
                    Text(configError).font(.caption).foregroundStyle(.red)
                }
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
                    Text(folderError).font(.caption).foregroundStyle(.red)
                }
            }

            Section("Downloaded models") {
                if models.isEmpty {
                    Text("Nothing downloaded yet. Models arrive the first time "
                        + "you generate in each mode.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
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
                }
                if let deleteError {
                    Text(deleteError).font(.caption).foregroundStyle(.red)
                }
            }

            Section("Pre-download") {
                Text("Want the models in place ahead of time? Pre-download "
                    + "them with the Hugging Face CLI — the `hf` command "
                    + "from Hugging Face's `huggingface_hub` Python package "
                    + "(install it with `pip install huggingface_hub`).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text("These commands fetch the three models the app uses — "
                    + "one per mode, matching the repos on the Models tab. "
                    + "Run them in Terminal (skip any you don't need); the app "
                    + "then uses the files directly and skips its own download.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack(alignment: .top, spacing: 8) {
                    Text(preDownloadCommand)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .background(.quaternary,
                                    in: RoundedRectangle(cornerRadius: 6))
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
            }
        }
        .formStyle(.grouped)
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
