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
//  LogStore.swift
//  Bunyi
//
//  In-memory log for the Logs window, mirrored to OSLog so messages also
//  show up in Console.app when debugging a user's machine.
//

import Foundation
import os

@MainActor
@Observable
public final class LogStore {
    public static let shared = LogStore()

    public struct Entry: Identifiable {
        public let id = UUID()
        public let date: Date
        public let message: String
    }

    public private(set) var entries: [Entry] = []

    private let osLog = Logger(
        subsystem: "app.bunyi.Bunyi", category: "app")
    private let cap = 2000

    public static var durableURL: URL {
        CLISettingsStore.dataRoot
            .appendingPathComponent("Logs", isDirectory: true)
            .appendingPathComponent("bunyi.log")
    }

    public static func ensureDurableDirectory() throws {
        let directory = durableURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700], ofItemAtPath: directory.path)
    }

    public func log(_ message: String) {
        // .notice (OSLogType.default) persists to the log store, so entries
        // are retrievable via `log show` after the fact — .info is not.
        osLog.notice("\(message, privacy: .public)")
        if CLISettingsStore.isCLI {
            appendDurable(message)
        }
        entries.append(Entry(date: .now, message: message))
        if entries.count > cap {
            entries.removeFirst(entries.count - cap)
        }
    }

    private func appendDurable(_ message: String) {
        do {
            try Self.ensureDurableDirectory()
            if !FileManager.default.fileExists(atPath: Self.durableURL.path) {
                _ = FileManager.default.createFile(
                    atPath: Self.durableURL.path, contents: nil,
                    attributes: [.posixPermissions: 0o600])
            }
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: Self.durableURL.path)
            let handle = try FileHandle(forWritingTo: Self.durableURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [
                .withInternetDateTime, .withFractionalSeconds,
            ]
            let line = "\(formatter.string(from: Date()))  \(message)\n"
            try handle.write(contentsOf: Data(line.utf8))
        } catch {
            // Logging must never turn a completed generation into a failure.
        }
    }

    public func clear() {
        entries.removeAll()
    }

    public var text: String {
        entries.map {
            "\($0.date.formatted(date: .omitted, time: .standard))  \($0.message)"
        }.joined(separator: "\n")
    }
}
