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

    /// A block nested *inside* a card or a form section — Settings' copyable
    /// command box. Smaller than a card on purpose: a box with the same radius
    /// as the surface holding it reads as a second card lying on top of the
    /// first. The box was at 6 while everything around it was 10 or 12, which
    /// is the kind of difference nobody chose.
    static let control: CGFloat = 8
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

/// One colour per generation mode, for the History pills.
///
/// Deliberately **not** the accent. In History the accent already means "this
/// row is playing" — it is the progress ring — so a pill wearing it competes
/// with the one colour in that view carrying state.
///
/// Violet is the brand's own second stop; teal and amber are not, and are the
/// only invented colours in the app. They earn it by making a long list
/// scannable without reading every row. The mode name stays spelled out inside
/// the pill, so the colour is redundant rather than load-bearing — nothing is
/// lost to anyone who cannot separate these hues.
///
/// Fixed sRGB rather than semantic colours: these must stay recognisably the
/// same three in light and dark, which `.teal` and `.orange` do not guarantee.
extension TTSMode {
    var pillColor: Color {
        switch self {
        case .presetVoice: Color(.sRGB, red: 0.10, green: 0.55, blue: 0.55)
        case .voiceDesign: Color(.sRGB, red: 0.63, green: 0.24, blue: 0.89)
        case .voiceClone:  Color(.sRGB, red: 0.72, green: 0.45, blue: 0.05)
        }
    }
}

/// An icon button in a list row: no chrome until you point at it.
///
/// `.borderless` draws a bare glyph with no hover feedback at all, so a row of
/// them gives no sign of being clickable until something happens. A tinted
/// rounded square under the cursor is the least that reads as a control.
///
/// The hover state lives in a nested `View` rather than on the style itself:
/// a `ButtonStyle` is re-created on every evaluation, so `@State` declared on
/// it would be reset rather than remembered.
struct RowIconButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        HoverBody(configuration: configuration)
    }

    private struct HoverBody: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .frame(width: 22, height: 22)
                .background(
                    RoundedRectangle(cornerRadius: 5)
                        .fill(hovering ? AnyShapeStyle(.quaternary)
                                       : AnyShapeStyle(Color.clear)))
                .contentShape(RoundedRectangle(cornerRadius: 5))
                .onHover { hovering = $0 }
                .opacity(configuration.isPressed ? 0.55 : 1)
        }
    }
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

/// What a callout is saying, which decides the colour of its rule.
enum CalloutTone {
    /// Explanation. Accent rule — the same colour the rest of the app uses to
    /// mean "this is Bunyi talking".
    case explanation

    /// Something went wrong. Red rule, paired by the caller with an
    /// `exclamationmark.triangle`, because colour alone is the one channel
    /// that cannot be relied on.
    case error

    var rule: Color {
        switch self {
        case .explanation: .accentColor
        case .error:       .red
        }
    }
}

extension View {
    /// The website's `.callout`: a rule down the leading edge over a faint
    /// fill, marking a block as explanation rather than as a control.
    ///
    /// Settings needed this structurally rather than decoratively. Models is
    /// three text fields followed by nine lines of caption prose, Storage
    /// eight more, and all of it renders at the same size, weight and grey as
    /// the field labels — so the tab reads as a README with text fields buried
    /// in it. The rule does not shorten a word; it just stops the prose and
    /// the controls from looking like the same kind of thing.
    ///
    /// **The rule is the load-bearing part**, which is Stage 1's lesson about
    /// card borders again: `.quinary` over a grouped form's own surface is
    /// nearly invisible in light appearance, and 3 pt of accent is not.
    ///
    /// The leading corners stay square so the rule reads as an edge of the
    /// block rather than as a stripe floating beside it; only the trailing
    /// pair take the card radius.
    ///
    /// Named `calloutBlock` rather than `callout`: `.font(.callout)` is a size
    /// and this is a container, and the two would have been indistinguishable
    /// at a glance in a file that uses both.
    func calloutBlock(_ tone: CalloutTone = .explanation) -> some View {
        modifier(CalloutBlock(tone: tone))
    }

    /// A section label, in the site's `.eyebrow`: 10 pt bold, uppercase,
    /// tracked out, in the accent.
    ///
    /// Small and quiet on purpose. Its job is to say where one group of
    /// controls ends and the next begins — at 13 pt semibold it would compete
    /// with the controls it introduces, which is the opposite of the problem.
    func eyebrow() -> some View {
        self
            .font(.system(size: 10, weight: .bold))
            .textCase(.uppercase)
            .tracking(0.9)
            .foregroundStyle(Color.accentColor)
    }
}

private struct CalloutBlock: ViewModifier {
    /// 3 pt, from the site. Also part of the leading inset below, so the text
    /// keeps its distance from the rule whatever the rule's width becomes.
    private static let ruleWidth: CGFloat = 3

    let tone: CalloutTone

    func body(content: Content) -> some View {
        content
            // Without this a short line — an error, usually — would draw a
            // rule only as tall as itself and a fill only as wide, which
            // reads as a badge rather than as a block.
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, Space.tight)
            .padding(.leading, Self.ruleWidth + Space.tight)
            .padding(.trailing, Space.tight)
            .background {
                UnevenRoundedRectangle(bottomTrailingRadius: Radius.card,
                                       topTrailingRadius: Radius.card)
                    .fill(.quinary)
                    .overlay(alignment: .leading) {
                        Rectangle()
                            .fill(tone.rule)
                            .frame(width: Self.ruleWidth)
                    }
            }
    }
}
