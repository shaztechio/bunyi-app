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
//  Theme.swift
//  Bunyi
//
//  The window's type and spacing scales, in one place. See
//  `UI-PLAN.md` for what they are and why these values.
//

import SwiftUI

/// Spacing, as a scale rather than a per-site judgement.
///
/// `ContentView` alone used 2, 3, 6, 8, 10, 12, 13, 16, 18, 20, 40, 76, 110
/// and 130 — fourteen values, most of them one pixel from another. A reader
/// could not tell which differences were deliberate, and neither could the
/// next person adding a row.
///
/// Five values, each with a job and each with a caller. A step nobody uses is
/// a step nobody can check, so later stages add theirs when they need them
/// rather than this growing a vocabulary in advance.
enum Space {
    /// Inside a label/value pair.
    static let hair: CGFloat = 4
    /// Between a control and its caption; between icon and label.
    static let tight: CGFloat = 8
    /// Between rows within a card.
    static let row: CGFloat = 12
    /// Between cards.
    static let card: CGFloat = 16
    /// Window inset. 20 is the macOS convention and the one value here that
    /// is not ours to pick.
    static let window: CGFloat = 20
}

/// Measurements of one options row. Not part of `Space`: a glyph column is a
/// width and an inset is a position, neither of which is a gap between two
/// things, and folding them in would make the scale above six-plus-whatever.
enum OptionRow {
    /// The leading glyph column.
    static let iconColumn: CGFloat = 18

    /// Where the label starts, and therefore where the divider starts.
    /// Derived rather than written as `40` — a literal here silently stopped
    /// matching whenever the padding, the glyph column or the gap changed,
    /// which is what left the divider aligned with nothing before Stage 1.
    static var labelInset: CGFloat { Space.row + iconColumn + Space.tight }
}

/// Corner radii.
enum Radius {
    static let card: CGFloat = 12
}

/// The brand gradient, ported from `.btn-primary` on bunyi.app.
///
/// The two stops are `--indigo` and `--violet` from `docs/index.html`, which
/// are also the two stops `tools/generate-icon.swift` renders the app icon
/// from. The site runs them at `100deg`; `.topLeading → .bottomTrailing` is
/// the nearest SwiftUI expresses without a custom `UnitPoint`, and at button
/// size the difference is not visible.
///
/// Deliberately used in one place only. A gradient that appears on every
/// surface stops meaning "this is the action" and starts meaning nothing.
extension LinearGradient {
    static let bunyiBrand = LinearGradient(
        colors: [Color(.sRGB, red: 0.36, green: 0.33, blue: 0.96),
                 Color(.sRGB, red: 0.63, green: 0.24, blue: 0.89)],
        startPoint: .topLeading, endPoint: .bottomTrailing)
}

/// The bottom bar's one action, in its two forms.
///
/// **One style for both** because Generate and Stop occupy the same slot and
/// swap when work starts. Two styles meant two sizes in the same place, and —
/// worse — two different degrees of presence: Generate was a solid gradient
/// capsule while Stop was `.bordered` with `.tint(.red)`, which macOS draws as
/// a faint outline with red text. Beside a filled capsule it read as barely
/// there in light appearance and all but vanished against a dark `.bar`. The
/// button that abandons a running job is the last one that should be hard to
/// find.
///
/// `.borderedProminent` cannot do the disabled state either: macOS draws it as
/// the tint at low opacity behind a *white* label — white on pale indigo,
/// close to illegible, and the app's resting state on first launch.
struct ActionButtonStyle: ButtonStyle {
    enum Role {
        /// Generate. Brand gradient; greys out when unavailable.
        case primary
        /// Stop. Filled red, and never disabled — it only exists while there
        /// is something to stop.
        case destructive
    }

    var role: Role = .primary
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(isEnabled ? AnyShapeStyle(.white)
                                       : AnyShapeStyle(.secondary))
            .padding(.horizontal, Space.row)
            .padding(.vertical, Space.tight)
            .background { fill }
            // Pressed state has to be explicit: a custom ButtonStyle gets none
            // of the system's own feedback, and a button that does not respond
            // to a click reads as broken.
            .opacity(configuration.isPressed ? 0.8 : 1)
    }

    @ViewBuilder
    private var fill: some View {
        switch (role, isEnabled) {
        case (.primary, true):    Capsule().fill(LinearGradient.bunyiBrand)
        case (.destructive, _):   Capsule().fill(Color.red)
        case (.primary, false):   Capsule().fill(.quaternary)
        }
    }
}

/// Type, named by role rather than by size.
///
/// The window had no hierarchy at all before this: `.body`, `.callout`,
/// `.caption`, `.caption2` and nothing above `.medium`, so every string on
/// screen was 12–13 pt regular grey and the eye had nothing to anchor on.
/// Sizes are macOS points, not web rem — the website's scale does not
/// transfer, only its intent.
extension Font {
    /// The script editor. It is the content of the window; 13 pt made it
    /// look like a form field.
    static let bunyiEditor = Font.system(size: 15)
}
