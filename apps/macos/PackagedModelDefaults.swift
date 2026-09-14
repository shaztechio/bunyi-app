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

import Foundation

/// Read-only distribution defaults. A user's per-mode source always wins.
public struct PackagedModelDefaults: Equatable, Sendable {
    public enum ModelDownloadSource: String, Sendable {
        case huggingFace
        case mirror
    }

    private enum ConfigurationError: LocalizedError {
        case invalid

        var errorDescription: String? {
            "Expected integer schemaVersion 1 and modelDownloadSource."
        }
    }

    public static let fileName = "bunyi.defaults.json"

    /// Loaded once per process. The app and CLI log `diagnostic` at startup.
    public static let current = load(from: distributionURL())

    public let modelDownloadSource: ModelDownloadSource
    public let diagnostic: String

    public var useMirror: Bool { modelDownloadSource == .mirror }

    public init(
        modelDownloadSource: ModelDownloadSource = .huggingFace,
        diagnostic: String = "Packaged model download default: Hugging Face."
    ) {
        self.modelDownloadSource = modelDownloadSource
        self.diagnostic = diagnostic
    }

    public static func load(from url: URL) -> PackagedModelDefaults {
        do {
            let object = try JSONSerialization.jsonObject(
                with: Data(contentsOf: url))
            guard let contents = object as? [String: Any],
                  let version = contents["schemaVersion"] as? NSNumber,
                  CFGetTypeID(version) != CFBooleanGetTypeID(),
                  !["f", "d"].contains(String(cString: version.objCType)),
                  version.intValue == 1,
                  let sourceText = contents["modelDownloadSource"] as? String,
                  let source = ModelDownloadSource(rawValue: sourceText) else {
                throw ConfigurationError.invalid
            }
            let name = source == .mirror
                ? "Bunyi mirror" : "Hugging Face"
            return PackagedModelDefaults(
                modelDownloadSource: source,
                diagnostic: "Packaged model download default: \(name).")
        } catch {
            return PackagedModelDefaults(diagnostic:
                "Could not read packaged model defaults from \(url.path); "
                + "using Hugging Face. \(error.localizedDescription)")
        }
    }

    /// App resources for the sandboxed GUI; beside the executable for the
    /// relocatable standalone CLI. The working directory is never consulted.
    static func distributionURL(
        bundleIdentifier: String? = Bundle.main.bundleIdentifier,
        executableURL: URL? = Bundle.main.executableURL,
        resourceURL: URL? = Bundle.main.resourceURL
    ) -> URL {
        if bundleIdentifier == "app.bunyi.cli", let executableURL {
            return executableURL.deletingLastPathComponent()
                .appendingPathComponent(fileName)
        }
        let directory = resourceURL
            ?? executableURL?.deletingLastPathComponent()
            ?? URL(fileURLWithPath: "/")
        return directory.appendingPathComponent(fileName)
    }
}
