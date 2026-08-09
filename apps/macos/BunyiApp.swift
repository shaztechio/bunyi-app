//
//  BunyiApp.swift
//  Bunyi
//

import SwiftUI

/// User-selectable appearance, persisted in UserDefaults. `.system` follows
/// macOS; light/dark pin the whole app via `preferredColorScheme`.
enum AppAppearance: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: String { rawValue }
    var title: String { rawValue.capitalized }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

@main
struct BunyiApp: App {
    @AppStorage("appearance") private var appearance: AppAppearance = .system

    var body: some Scene {
        WindowGroup("Bunyi") {
            ContentView()
                .preferredColorScheme(appearance.colorScheme)
        }
        .windowResizability(.contentSize)

        // Appears as Window → Logs; ⌘L opens or focuses it.
        Window("Logs", id: "logs") {
            LogsView()
                .preferredColorScheme(appearance.colorScheme)
        }
        .keyboardShortcut("l", modifiers: .command)
        .defaultSize(width: 640, height: 400)

        Settings {
            SettingsView()
                .preferredColorScheme(appearance.colorScheme)
        }
    }
}
