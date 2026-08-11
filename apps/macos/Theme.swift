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
/// next person adding a row. Six values, each with a job:
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
    /// Between major groups.
    static let section: CGFloat = 24

    /// The leading glyph column in an options row. Named because the row
    /// divider's inset is derived from it: a bare `18` written in two places
    /// drifts the moment one of them changes, which is exactly what left the
    /// divider lined up with nothing before Stage 1.
    static let iconColumn: CGFloat = 18

    /// Where an options row's label starts, and therefore where its divider
    /// starts. Derived rather than written as `40`.
    static var rowLabelInset: CGFloat { row + iconColumn + tight }
}

/// Corner radii. Cards and the controls nested inside them differ so the
/// nesting reads as nesting.
enum Radius {
    static let card: CGFloat = 12
    static let control: CGFloat = 8
}

/// Type, named by role rather than by size.
///
/// The window had no hierarchy at all before this: `.body`, `.callout`,
/// `.caption`, `.caption2` and nothing above `.medium`, so every string on
/// screen was 12–13 pt regular grey and the eye had nothing to anchor on.
/// Sizes are macOS points, not web rem — the website's scale does not
/// transfer, only its intent.
extension Font {
    /// The mode heading. `-0.3` tracking echoes the site's `-0.02em` on
    /// headings, which is what stops a semibold line looking loose.
    static let bunyiTitle = Font.system(size: 15, weight: .semibold)
    /// The script editor. It is the content of the window; 13 pt made it
    /// look like a form field.
    static let bunyiEditor = Font.system(size: 15)
}

extension View {
    /// The mode heading treatment, kept with the font it belongs to so the
    /// tracking cannot drift away from the size.
    func bunyiTitleStyle() -> some View {
        font(.bunyiTitle).tracking(-0.3)
    }
}
