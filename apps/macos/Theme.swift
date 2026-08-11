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
