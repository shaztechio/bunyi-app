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
        .commands {
            // Replaces SwiftUI's default Help item, which opens a book that was
            // never registered and shows an empty window. `showHelp` resolves
            // the book named by CFBundleHelpBookName in Info.plist.
            CommandGroup(replacing: .help) {
                Button("Bunyi Help") {
                    NSApplication.shared.showHelp(nil)
                }
                .keyboardShortcut("?", modifiers: .command)
            }
        }

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
