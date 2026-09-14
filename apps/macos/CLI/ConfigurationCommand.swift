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
import BunyiMLXCore

@MainActor
enum ConfigurationCommand {
    private static let sourcePrefix = "modelSource."

    static func run(
        _ request: CLIRequest, engine: TTSEngine? = nil
    ) throws -> CLIMessage {
        switch request.operation {
        case "config.list":
            return CLIProtocol.result(request, [
                "settings": try configuration(),
                "path": CLISettingsStore.settingsURL.path,
            ])
        case "config.get":
            guard let key = request.value("target"),
                  let value = try configuration()[key] else {
                throw CLICommandParser.invalid("Unknown configuration key.")
            }
            return CLIProtocol.result(request, ["key": key, "value": value])
        case "config.set":
            return try set(request, engine: engine)
        case "logs.path":
            return CLIProtocol.result(request, ["path": LogStore.durableURL.path])
        case "logs.tail":
            return try tail(request)
        case "logs.clear":
            return try clearLog(request)
        default:
            throw CLICommandParser.invalid("Unknown configuration command.")
        }
    }

    private static func configuration() throws -> CLIMessage {
        let settings = try CLISettingsStore.load()
        return [
            "modelsFolder": settings.modelsFolder
                ?? CLISettingsStore.dataRoot
                    .appendingPathComponent("Models", isDirectory: true).path,
            "unloadOnModeSwitch": settings.unloadOnModeSwitch ?? true,
            "modelSource.preset": settings.modelSource?["preset"]
                ?? TTSMode.presetVoice.repoID,
            "modelSource.design": settings.modelSource?["design"]
                ?? TTSMode.voiceDesign.repoID,
            "modelSource.clone": settings.modelSource?["clone"]
                ?? TTSMode.voiceClone.repoID,
        ]
    }

    private static func set(
        _ request: CLIRequest, engine: TTSEngine?
    ) throws -> CLIMessage {
        guard let key = request.value("target"),
              let value = request.value("value") else {
            throw CLICommandParser.missing("Provide a configuration key and value.")
        }
        var settings = try CLISettingsStore.load()
        let oldRoot = ModelsLocation.current()
        let ownsLease = engine?.loadedMode != nil
        var newRoot: URL?
        if key == "modelsFolder" {
            guard engine?.loadedMode == nil else {
                throw CLIError(
                    "model_loaded",
                    "Unload the server model before changing the models folder.",
                    exitCode: 4)
            }
            let folder = URL(
                fileURLWithPath: value,
                relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath))
                .standardizedFileURL
            var isDirectory: ObjCBool = false
            guard FileManager.default.fileExists(
                atPath: folder.path, isDirectory: &isDirectory),
                  isDirectory.boolValue else {
                throw CLICommandParser.missing(
                    "The models folder must already exist.")
            }
            settings.modelsFolder = folder.path
            newRoot = folder
        } else if key == "unloadOnModeSwitch" {
            guard let boolean = Bool(value.lowercased()) else {
                throw CLICommandParser.invalid(
                    "unloadOnModeSwitch accepts true or false.")
            }
            settings.unloadOnModeSwitch = boolean
        } else if key.hasPrefix(sourcePrefix) {
            let name = String(key.dropFirst(sourcePrefix.count))
            let mode = ModelCommand.mode(name)
            try validateSource(value)
            var sources = settings.modelSource ?? [:]
            sources[CLISettingsStore.modeKey(mode)] = value
            settings.modelSource = sources
        } else {
            throw CLICommandParser.invalid("Unknown configuration key.")
        }

        var leases: [ModelOperationLease] = []
        do {
            if !ownsLease {
                leases.append(try ModelOperationLease(
                    modelsRoot: oldRoot, operation: "config.set"))
            }
            if let newRoot,
               newRoot.standardizedFileURL != oldRoot.standardizedFileURL {
                leases.append(try ModelOperationLease(
                    modelsRoot: newRoot, operation: "config.set"))
            }
            try CLISettingsStore.save(settings)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy",
                "Another Bunyi process is using one of these models folders.",
                exitCode: 4)
        } catch {
            throw CLIError(
                "configuration_failed", error.localizedDescription, exitCode: 10)
        }
        withExtendedLifetime(leases) {}
        let result = try configuration()[key] ?? value
        return CLIProtocol.result(request, ["key": key, "value": result])
    }

    private static func validateSource(_ value: String) throws {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw CLICommandParser.invalid("A model source cannot be blank.")
        }
        let lower = trimmed.lowercased()
        if lower.hasPrefix("http://") || lower.hasPrefix("https://") {
            guard let url = URL(string: trimmed), url.host != nil else {
                throw CLICommandParser.invalid("The model source URL is invalid.")
            }
            return
        }
        let parts = trimmed.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2,
              parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }) else {
            throw CLICommandParser.invalid(
                "A Hugging Face source must be an organization/repository ID.")
        }
    }

    private static func tail(_ request: CLIRequest) throws -> CLIMessage {
        let count: Int
        if let value = request.value("lines") {
            guard let parsed = Int(value), (1...10_000).contains(parsed) else {
                throw CLICommandParser.invalid(
                    "--lines must be between 1 and 10000.")
            }
            count = parsed
        } else {
            count = 100
        }
        let lines: [String]
        if let text = try? String(
            contentsOf: LogStore.durableURL, encoding: .utf8) {
            lines = Array(text.split(separator: "\n").suffix(count)).map(String.init)
        } else {
            lines = []
        }
        return CLIProtocol.result(request, [
            "path": LogStore.durableURL.path,
            "lines": lines,
        ])
    }

    private static func clearLog(_ request: CLIRequest) throws -> CLIMessage {
        do {
            try LogStore.ensureDurableDirectory()
            try Data().write(to: LogStore.durableURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: LogStore.durableURL.path)
        } catch {
            throw CLIError("log_clear_failed", error.localizedDescription, exitCode: 10)
        }
        return CLIProtocol.result(request, ["path": LogStore.durableURL.path])
    }
}
