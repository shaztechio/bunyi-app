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
enum ModelSource: Equatable {
    case repo(String)
    case baseURL(URL)
}

extension TTSMode {
    var repoDefaultsKey: String { "modelRepo.\(rawValue)" }

    /// The configured value: the Settings override when set, else the default
    /// repo ID. May be a repo ID or an http(s) base URL.
    var effectiveRepoID: String {
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
            .appendingPathComponent("Bunyi/Models", isDirectory: true)
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
