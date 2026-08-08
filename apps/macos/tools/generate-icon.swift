// Generates Assets.xcassets/AppIcon.appiconset for Qwen3 TTS Studio.
// Renders a gradient background + "waveform.badge.mic" symbol at every
// required macOS icon size. Run from apps/macos:
//   swift tools/generate-icon.swift
// Not part of the app target (project.yml lists sources explicitly).

import AppKit
import AVFoundation  // AVMakeRect

let sizes: [(points: Int, scale: Int)] = [
    (16, 1), (16, 2), (32, 1), (32, 2), (64, 1),
    (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2),
]

func render(pixels: Int) -> NSImage {
    let size = CGFloat(pixels)
    let image = NSImage(size: NSSize(width: size, height: size))
    image.lockFocus()
    defer { image.unlockFocus() }

    let rect = NSRect(x: 0, y: 0, width: size, height: size)

    // Indigo → violet diagonal gradient, full-bleed (macOS applies the
    // squircle mask itself).
    let gradient = NSGradient(colors: [
        NSColor(srgbRed: 0.36, green: 0.33, blue: 0.96, alpha: 1),
        NSColor(srgbRed: 0.63, green: 0.24, blue: 0.89, alpha: 1),
    ])!
    gradient.draw(in: rect, angle: -50)

    // Soft highlight toward the top so the tile doesn't look flat.
    let highlight = NSGradient(colors: [
        NSColor.white.withAlphaComponent(0.18),
        NSColor.white.withAlphaComponent(0),
    ])!
    highlight.draw(in: rect, angle: 90)

    // White waveform: symmetric rounded bars (SF Symbols can't be relied
    // on from a command-line tool).
    NSColor.white.setFill()
    let bars: [CGFloat] = [0.26, 0.52, 0.80, 1.0, 0.80, 0.52, 0.26]
    let barWidth = size * 0.085
    let spacing = size * 0.055
    let totalWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * spacing
    let maxHeight = size * 0.52
    var x = (size - totalWidth) / 2
    for height in bars {
        let h = maxHeight * height
        let bar = NSRect(x: x, y: (size - h) / 2, width: barWidth, height: h)
        NSBezierPath(roundedRect: bar, xRadius: barWidth / 2, yRadius: barWidth / 2).fill()
        x += barWidth + spacing
    }

    return image
}

let fm = FileManager.default
let setURL = URL(fileURLWithPath: "Assets.xcassets/AppIcon.appiconset",
                 relativeTo: URL(fileURLWithPath: fm.currentDirectoryPath))
try fm.createDirectory(at: setURL, withIntermediateDirectories: true)

var images: [[String: String]] = []
for entry in sizes {
    let pixels = entry.points * entry.scale
    let name = "icon-\(entry.points)pt@\(entry.scale)x.png"
    let png = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
        isPlanar: false, colorSpaceName: .deviceRGB,
        bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: png)
    render(pixels: pixels).draw(in: NSRect(x: 0, y: 0, width: pixels, height: pixels))
    NSGraphicsContext.restoreGraphicsState()
    try png.representation(using: .png, properties: [:])!
        .write(to: setURL.appendingPathComponent(name))
    images.append([
        "idiom": "mac",
        "size": "\(entry.points)x\(entry.points)",
        "scale": "\(entry.scale)x",
        "filename": name,
    ])
    print("wrote \(name)")
}

let contents: [String: Any] = [
    "images": images,
    "info": ["author": "xcode", "version": 1],
]
let json = try JSONSerialization.data(withJSONObject: contents,
                                      options: [.prettyPrinted, .sortedKeys])
try json.write(to: setURL.appendingPathComponent("Contents.json"))
print("wrote Contents.json")

// Assets.xcassets root Contents.json
let root = URL(fileURLWithPath: "Assets.xcassets/Contents.json",
               relativeTo: URL(fileURLWithPath: fm.currentDirectoryPath))
if !fm.fileExists(atPath: root.path) {
    try JSONSerialization.data(
        withJSONObject: ["info": ["author": "xcode", "version": 1]],
        options: [.prettyPrinted, .sortedKeys]
    ).write(to: root)
    print("wrote Assets.xcassets/Contents.json")
}
