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
final class LogStore {
    static let shared = LogStore()

    struct Entry: Identifiable {
        let id = UUID()
        let date: Date
        let message: String
    }

    private(set) var entries: [Entry] = []

    private let osLog = Logger(
        subsystem: "app.bunyi.Bunyi", category: "app")
    private let cap = 2000

    func log(_ message: String) {
        // .notice (OSLogType.default) persists to the log store, so entries
        // are retrievable via `log show` after the fact — .info is not.
        osLog.notice("\(message, privacy: .public)")
        entries.append(Entry(date: .now, message: message))
        if entries.count > cap {
            entries.removeFirst(entries.count - cap)
        }
    }

    func clear() {
        entries.removeAll()
    }

    var text: String {
        entries.map {
            "\($0.date.formatted(date: .omitted, time: .standard))  \($0.message)"
        }.joined(separator: "\n")
    }
}
