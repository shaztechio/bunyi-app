//
//  ModelSettings.swift
//  Qwen3 TTS Studio
//
//  User-configurable model repos and models-folder location.
//

import Foundation

// MARK: - Model repo overrides

extension TTSMode {
    var repoDefaultsKey: String { "modelRepo.\(rawValue)" }

    /// Repo actually used: the Settings override when set, else the default.
    var effectiveRepoID: String {
        let custom = UserDefaults.standard.string(forKey: repoDefaultsKey)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if let custom, !custom.isEmpty { return custom }
        return repoID
    }
}

// MARK: - Models folder

/// Where model files live. Default is Application Support; the user can
/// point it anywhere via Settings, persisted as a security-scoped bookmark
/// so the sandbox re-grants access across launches.
@MainActor
enum ModelsLocation {
    private static let bookmarkKey = "modelsFolderBookmark"
    private static var activeScopedURL: URL?

    static var defaultDir: URL {
        FileManager.default.urls(for: .applicationSupportDirectory,
                                 in: .userDomainMask)[0]
            .appendingPathComponent("Qwen3TTSStudio/Models", isDirectory: true)
    }

    static var isCustom: Bool {
        UserDefaults.standard.data(forKey: bookmarkKey) != nil
    }

    /// Resolve the configured folder, starting security-scoped access once
    /// per launch. Falls back to the default on any bookmark problem.
    static func current() -> URL {
        if let active = activeScopedURL { return active }
        if let data = UserDefaults.standard.data(forKey: bookmarkKey) {
            var stale = false
            if let url = try? URL(resolvingBookmarkData: data,
                                  options: .withSecurityScope,
                                  relativeTo: nil,
                                  bookmarkDataIsStale: &stale),
               url.startAccessingSecurityScopedResource() {
                if stale, let fresh = try? url.bookmarkData(options: .withSecurityScope) {
                    UserDefaults.standard.set(fresh, forKey: bookmarkKey)
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

    static func set(_ url: URL) throws {
        let data = try url.bookmarkData(options: .withSecurityScope)
        UserDefaults.standard.set(data, forKey: bookmarkKey)
        activeScopedURL?.stopAccessingSecurityScopedResource()
        activeScopedURL = nil
        LogStore.shared.log("Models folder set to \(url.path)")
    }

    static func resetToDefault() {
        UserDefaults.standard.removeObject(forKey: bookmarkKey)
        activeScopedURL?.stopAccessingSecurityScopedResource()
        activeScopedURL = nil
        LogStore.shared.log("Models folder reset to \(defaultDir.path)")
    }
}
