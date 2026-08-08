//
//  LogStore.swift
//  Qwen3 TTS Studio
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
        subsystem: "com.geppettoforge.Qwen3TTSStudio", category: "app")
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
