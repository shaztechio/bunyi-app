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

import AppKit
import Foundation

private struct CreditsDocument: Decodable {
    let entries: [CreditsEntry]
}

private struct CreditsEntry: Decodable {
    let name: String
    let does: String
    let licence: String
    let url: URL
    let kind: String
    let apps: [String]
}

@MainActor
enum AboutCredits {
    static func show() {
        let info = Bundle.main.infoDictionary
        let short = info?["CFBundleShortVersionString"] as? String ?? "0.0.0"
        let build = info?["CFBundleVersion"] as? String ?? "0"
        NSApplication.shared.orderFrontStandardAboutPanel(options: [
            .applicationName: "Bunyi",
            .applicationVersion: "\(short) · macOS",
            .version: build,
            .credits: attributedCredits(),
        ])
    }

    private static func attributedCredits() -> NSAttributedString {
        guard let url = Bundle.main.url(
            forResource: "CREDITS", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let document = try? JSONDecoder().decode(
                CreditsDocument.self, from: data) else {
            return NSAttributedString(
                string: "Credits are unavailable in this build.")
        }
        let entries = document.entries.filter { $0.apps.contains("macos") }
        let result = NSMutableAttributedString(string: "")
        appendSection("Models", entries: entries.filter { $0.kind == "model" },
                      to: result)
        appendSection("Software", entries: entries.filter { $0.kind == "library" },
                      to: result)
        return result
    }

    private static func appendSection(
        _ title: String,
        entries: [CreditsEntry],
        to result: NSMutableAttributedString
    ) {
        guard !entries.isEmpty else { return }
        if result.length > 0 { result.append(NSAttributedString(string: "\n")) }
        let heading = NSMutableParagraphStyle()
        heading.alignment = .center
        heading.paragraphSpacing = 6
        result.append(NSAttributedString(
            string: "\(title)\n",
            attributes: [
                .font: NSFont.boldSystemFont(ofSize: NSFont.systemFontSize),
                .paragraphStyle: heading,
            ]))

        for entry in entries {
            let paragraph = NSMutableParagraphStyle()
            paragraph.alignment = .center
            paragraph.paragraphSpacing = 8
            result.append(NSAttributedString(
                string: "\(entry.name)\n",
                attributes: [
                    .font: NSFont.boldSystemFont(
                        ofSize: NSFont.smallSystemFontSize),
                    .link: entry.url,
                    .foregroundColor: NSColor.linkColor,
                    .underlineStyle: NSUnderlineStyle.single.rawValue,
                    .paragraphStyle: paragraph,
                ]))
            result.append(NSAttributedString(
                string: "\(entry.does)\nLicence: \(entry.licence)\n",
                attributes: [
                    .font: NSFont.systemFont(
                        ofSize: NSFont.smallSystemFontSize),
                    .paragraphStyle: paragraph,
                ]))
        }
    }
}
