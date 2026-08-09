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
// Renders docs/assets/og-card.png, the 1200x630 link-preview image.
// Run from the repository root:
//
//   swift docs/tools/generate-og-card.swift
//
// 1200x630 is the landscape ratio Slack, Discord, LinkedIn, iMessage, and
// Facebook render large. The app icon alone is square, which those same
// services shrink to a thumbnail beside the text — legible, but not a card.
// Colors match apps/macos/tools/generate-icon.swift so the two agree.

import AppKit

let width: CGFloat = 1200
let height: CGFloat = 630

// Drawn into an explicitly sized bitmap rather than via NSImage.lockFocus():
// lockFocus uses the main display's backing scale, so on a Retina Mac it
// silently produces a 2400x1260 image that disagrees with the og:image:width
// and og:image:height the page declares.
guard let bitmap = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: Int(width),
    pixelsHigh: Int(height),
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0
) else {
    FileHandle.standardError.write(Data("error: could not allocate the bitmap\n".utf8))
    exit(1)
}
bitmap.size = NSSize(width: width, height: height)

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)

let rect = NSRect(x: 0, y: 0, width: width, height: height)

// Indigo -> violet, the icon's gradient at the icon's angle.
let gradient = NSGradient(colors: [
    NSColor(srgbRed: 0.36, green: 0.33, blue: 0.96, alpha: 1),
    NSColor(srgbRed: 0.63, green: 0.24, blue: 0.89, alpha: 1),
])!
gradient.draw(in: rect, angle: -50)

// Soft highlight toward the top so the card is not a flat wash.
let highlight = NSGradient(colors: [
    NSColor.white.withAlphaComponent(0.16),
    NSColor.white.withAlphaComponent(0),
])!
highlight.draw(in: rect, angle: 90)

// The icon's waveform, left of the wordmark, at the same proportions.
NSColor.white.setFill()
let bars: [CGFloat] = [0.26, 0.52, 0.80, 1.0, 0.80, 0.52, 0.26]
let barWidth: CGFloat = 26
let spacing: CGFloat = 17
let maxHeight: CGFloat = 210
let waveWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * spacing
var x: CGFloat = 96
let centerY = height / 2 - 16
for value in bars {
    let h = maxHeight * value
    let bar = NSRect(x: x, y: centerY - h / 2, width: barWidth, height: h)
    NSBezierPath(roundedRect: bar, xRadius: barWidth / 2, yRadius: barWidth / 2).fill()
    x += barWidth + spacing
}

let textLeft = 96 + waveWidth + 72

let title = "Bunyi"
title.draw(
    at: NSPoint(x: textLeft, y: centerY + 6),
    withAttributes: [
        .font: NSFont.systemFont(ofSize: 132, weight: .bold),
        .foregroundColor: NSColor.white,
    ]
)

let tagline = "Local text-to-speech\nfor your desktop"
let paragraph = NSMutableParagraphStyle()
paragraph.lineSpacing = 6
tagline.draw(
    in: NSRect(x: textLeft + 4, y: centerY - 118, width: width - textLeft - 96, height: 140),
    withAttributes: [
        .font: NSFont.systemFont(ofSize: 44, weight: .medium),
        .foregroundColor: NSColor.white.withAlphaComponent(0.92),
        .paragraphStyle: paragraph,
    ]
)

// Bottom-left domain, small: it reads as a source rather than a headline.
"bunyi.app".draw(
    at: NSPoint(x: 96, y: 56),
    withAttributes: [
        .font: NSFont.systemFont(ofSize: 30, weight: .semibold),
        .foregroundColor: NSColor.white.withAlphaComponent(0.72),
    ]
)

NSGraphicsContext.restoreGraphicsState()

guard let png = bitmap.representation(using: .png, properties: [:]) else {
    FileHandle.standardError.write(Data("error: could not render the card\n".utf8))
    exit(1)
}

let output = URL(fileURLWithPath: "docs/assets/og-card.png")
try png.write(to: output)
print("Wrote \(output.path) (\(Int(width))x\(Int(height)), \(png.count) bytes)")
