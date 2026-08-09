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
        }
        .formStyle(.grouped)
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
    }
}

#Preview {
    SettingsView()
}
