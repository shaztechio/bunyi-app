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
//  ModelSettings.swift
//  Bunyi
//
//  User-configurable model repos and models-folder location.
//

import Foundation

// MARK: - Model source

/// Where a mode's model comes from. A Hugging Face repo (default, via the
/// Hub API) or a plain base URL the user self-hosts (files fetched directly).
public enum ModelSource: Equatable {
    case repo(String)
    case baseURL(URL)
}

public struct DownloadRecoveryOffer: Equatable {
    public let mode: TTSMode
    public let sourceURL: URL
    public let paused: Bool
}

public extension TTSMode {
    var repoDefaultsKey: String { "modelRepo.\(rawValue)" }

    /// The configured value: the Settings override when set, else the default
    /// repo ID. May be a repo ID or an http(s) base URL.
    var effectiveRepoID: String {
        if CLISettingsStore.isCLI,
           let source = CLISettingsStore.source(for: self)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !source.isEmpty {
            return source
        }
        let custom = UserDefaults.standard.string(forKey: repoDefaultsKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if let custom, !custom.isEmpty { return custom }
        return repoID
    }

    /// Resolved source: an http(s) value is a self-hosted base URL, anything
    /// else is a Hugging Face repo ID.
    var effectiveSource: ModelSource {
        let value = effectiveRepoID
        let lower = value.lowercased()
        if lower.hasPrefix("https://") || lower.hasPrefix("http://"),
           let url = URL(string: value) {
            return .baseURL(url)
        }
        return .repo(value)
    }

    /// The exact MLX mirror endpoint shipped for this mode. Recovery is
    /// offered only for these values; a custom server returning the same status
    /// remains the user's configured server and is never changed silently.
    var bunyiMirrorURL: URL {
        let path = switch self {
        case .presetVoice: "customvoice"
        case .voiceDesign: "voicedesign"
        case .voiceClone: "voiceclone"
        }
        return URL(string: "https://models.bunyi.app/\(path)")!
    }

    func isUsingBuiltInMirror(_ source: URL) -> Bool {
        source.absoluteString == bunyiMirrorURL.absoluteString
            && effectiveRepoID == source.absoluteString
    }

    /// Recovery is an explicit persisted choice, not a one-run override.
    func useCanonicalHuggingFaceSource() {
        UserDefaults.standard.set(repoID, forKey: repoDefaultsKey)
    }
}

// MARK: - Models folder

/// Where model files live. Default is Application Support; the user can
/// point it anywhere via Settings, persisted as a security-scoped bookmark
/// so the sandbox re-grants access across launches.
@MainActor
public enum ModelsLocation {
    private static var activeScopedURL: URL?

    public static var defaultDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory,
                                 in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi/Models", isDirectory: true)
    }

    public static var isCustom: Bool {
        if CLISettingsStore.isCLI {
            return CLISettingsStore.loadLenient().modelsFolder != nil
        }
        return ModelsFolderBookmarkStore.read() != nil
    }

    /// Resolve the configured folder, starting security-scoped access once
    /// per launch. Falls back to the default on any bookmark problem.
    public static func current() -> URL {
        if CLISettingsStore.isCLI,
           let path = CLISettingsStore.loadLenient().modelsFolder {
            let url = URL(fileURLWithPath: path).standardizedFileURL
            var isDirectory: ObjCBool = false
            if FileManager.default.fileExists(
                atPath: url.path, isDirectory: &isDirectory),
               isDirectory.boolValue {
                return url
            }
            LogStore.shared.log(
                "Could not reopen the CLI models folder — using the default")
        }
        if let active = activeScopedURL { return active }
        if let data = ModelsFolderBookmarkStore.read() {
            var stale = false
            if let url = try? URL(resolvingBookmarkData: data,
                                  options: .withSecurityScope,
                                  relativeTo: nil,
                                  bookmarkDataIsStale: &stale),
               url.startAccessingSecurityScopedResource() {
                if stale, let fresh = try? url.bookmarkData(options: .withSecurityScope) {
                    ModelsFolderBookmarkStore.write(fresh)
                }
                activeScopedURL = url
                return url
            }
            LogStore.shared.log(
                "Could not reopen the custom models folder — using the default")
        }
        let dir = defaultDir
        try? FileManager.default.createDirectory(
            at: dir, withIntermediateDirectories: true)
        return dir
    }

    public static func set(_ url: URL) throws {
        let data = try url.bookmarkData(options: .withSecurityScope)
        ModelsFolderBookmarkStore.write(data)
        activeScopedURL?.stopAccessingSecurityScopedResource()
        activeScopedURL = nil
        LogStore.shared.log("Models folder set to \(url.path)")
    }

    public static func resetToDefault() {
        ModelsFolderBookmarkStore.clear()
        activeScopedURL?.stopAccessingSecurityScopedResource()
        activeScopedURL = nil
        LogStore.shared.log("Models folder reset to \(defaultDir.path)")
    }
}
