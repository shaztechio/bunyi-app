//
//  Qwen3TTSStudioApp.swift
//  Qwen3 TTS Studio
//

import SwiftUI

@main
struct Qwen3TTSStudioApp: App {
    var body: some Scene {
        WindowGroup("Qwen3 TTS Studio") {
            ContentView()
        }
        .windowResizability(.contentSize)

        // Appears as Window → Logs; ⌘L opens or focuses it.
        Window("Logs", id: "logs") {
            LogsView()
        }
        .keyboardShortcut("l", modifiers: .command)
        .defaultSize(width: 640, height: 400)

        Settings {
            SettingsView()
        }
    }
}
