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

import Foundation

guard CommandLine.arguments.count == 3 || CommandLine.arguments.count == 4 else {
    FileHandle.standardError.write(Data(
        "usage: render-help.swift INPUT.md OUTPUT.html [HELP_BOOK_IDENTIFIER]\n".utf8
    ))
    exit(64)
}

let inputURL = URL(fileURLWithPath: CommandLine.arguments[1])
let outputURL = URL(fileURLWithPath: CommandLine.arguments[2])
/// Help Viewer resolves a book by this value, which must match both the help
/// bundle's CFBundleIdentifier and the app's CFBundleHelpBookName. Qualification
/// builds pass their own identifier so several installed Bunyi apps cannot
/// each claim the same book.
let helpBookIdentifier = CommandLine.arguments.count == 4
    ? CommandLine.arguments[3]
    : "app.bunyi.help"
let markdown = try String(contentsOf: inputURL, encoding: .utf8)

func escapeHTML(_ value: String) -> String {
    value
        .replacingOccurrences(of: "&", with: "&amp;")
        .replacingOccurrences(of: "<", with: "&lt;")
        .replacingOccurrences(of: ">", with: "&gt;")
        .replacingOccurrences(of: "\"", with: "&quot;")
}

func replacingMatches(in value: String, pattern: String, template: String) -> String {
    let expression = try! NSRegularExpression(pattern: pattern)
    let range = NSRange(value.startIndex..<value.endIndex, in: value)
    return expression.stringByReplacingMatches(in: value, range: range, withTemplate: template)
}

func inlineMarkdown(_ value: String) -> String {
    var rendered = escapeHTML(value)
    // Fragments as well as absolute URLs. Without the second alternative a
    // link to another section of this book renders as literal brackets in the
    // finished page — silently, since nothing here fails, and the only way to
    // notice is to read the built book rather than the Markdown.
    rendered = replacingMatches(
        in: rendered,
        pattern: #"\[([^\]]+)\]\((https?://[^\s\)]+|#[A-Za-z0-9\-]+)\)"#,
        template: #"<a href="$2">$1</a>"#
    )
    rendered = replacingMatches(in: rendered, pattern: #"`([^`]+)`"#, template: #"<code>$1</code>"#)
    rendered = replacingMatches(in: rendered, pattern: #"\*\*([^*]+)\*\*"#, template: #"<strong>$1</strong>"#)
    // After bold, so the inner pair of `**text**` is never mistaken for emphasis.
    // Unhandled, `*before*` reached the reader as literal asterisks.
    rendered = replacingMatches(in: rendered, pattern: #"\*([^*\s][^*]*)\*"#, template: #"<em>$1</em>"#)
    return rendered
}

func slug(_ value: String) -> String {
    let lowered = value.lowercased()
    let allowed = lowered.unicodeScalars.map { scalar -> Character in
        CharacterSet.alphanumerics.contains(scalar) ? Character(String(scalar)) : "-"
    }
    return String(allowed)
        .split(separator: "-", omittingEmptySubsequences: true)
        .joined(separator: "-")
}

var body: [String] = []
var paragraph: [String] = []
var listKind: String?
var listItem: [String] = []
var codeLines: [String] = []
var inCodeBlock = false
/// HTML comments pass through verbatim rather than being escaped into visible
/// text. HELP.md carries its licence header this way: present in the rendered
/// Help Book, invisible to the reader.
var inHTMLComment = false
/// Buffered rows of a GitHub-flavoured Markdown table.
///
/// Tables were not handled at all, so every row fell through to the paragraph
/// branch and the whole table collapsed into one run-on line of literal pipe
/// characters. HELP.md uses one to say where materials appear on each
/// distribution, which is exactly the kind of content a table exists for.
var tableLines: [String] = []

func closeParagraph() {
    guard !paragraph.isEmpty else { return }
    body.append("<p>\(inlineMarkdown(paragraph.joined(separator: " ")))</p>")
    paragraph.removeAll()
}

/// Emits the buffered list item. Items are buffered rather than written per
/// line so a Markdown item wrapped across lines stays a single `<li>`; writing
/// each line immediately turned the continuation into a stray paragraph and
/// closed the list early.
func closeListItem() {
    guard !listItem.isEmpty else { return }
    body.append("<li>\(inlineMarkdown(listItem.joined(separator: " ")))</li>")
    listItem.removeAll()
}

/// Splits `| a | b |` into its cells, dropping the empty edges the delimiters
/// produce. A leading empty header cell is meaningful — HELP.md's table uses one
/// for its row-label column — so only the outer edges are trimmed.
func tableCells(_ row: String) -> [String] {
    var trimmed = Substring(row)
    if trimmed.hasPrefix("|") { trimmed = trimmed.dropFirst() }
    if trimmed.hasSuffix("|") { trimmed = trimmed.dropLast() }
    return trimmed.components(separatedBy: "|").map {
        $0.trimmingCharacters(in: .whitespaces)
    }
}

func isSeparatorRow(_ row: String) -> Bool {
    let cells = tableCells(row)
    guard !cells.isEmpty else { return false }
    return cells.allSatisfy { cell in
        !cell.isEmpty && cell.allSatisfy { $0 == "-" || $0 == ":" || $0 == " " }
    }
}

/// Emits the buffered table. A missing separator row means it was never a table,
/// so the rows are handed back to the paragraph path rather than silently
/// rendered as one — better a plain paragraph than a malformed `<table>`.
func closeTable() {
    guard !tableLines.isEmpty else { return }
    let rows = tableLines
    tableLines.removeAll()
    guard rows.count >= 2, isSeparatorRow(rows[1]) else {
        for row in rows { paragraph.append(row) }
        closeParagraph()
        return
    }
    var html = ["<div class=\"table-wrap\"><table>"]
    html.append("<thead><tr>")
    for cell in tableCells(rows[0]) {
        html.append("<th>\(inlineMarkdown(cell))</th>")
    }
    html.append("</tr></thead><tbody>")
    for row in rows.dropFirst(2) {
        html.append("<tr>")
        for cell in tableCells(row) {
            html.append("<td>\(inlineMarkdown(cell))</td>")
        }
        html.append("</tr>")
    }
    html.append("</tbody></table></div>")
    body.append(html.joined())
}

func closeList() {
    closeListItem()
    guard let currentListKind = listKind else { return }
    body.append("</\(currentListKind)>")
    listKind = nil
}

for rawLine in markdown.components(separatedBy: .newlines) {
    let line = rawLine.trimmingCharacters(in: .whitespaces)
    // Any line that is not another row ends the table. Done once here rather
    // than in each branch below, all of which `continue`.
    if !inCodeBlock, !inHTMLComment, !tableLines.isEmpty, !line.hasPrefix("|") {
        closeTable()
    }
    if inHTMLComment {
        body.append(rawLine)
        if line.contains("-->") { inHTMLComment = false }
        continue
    }
    if line.hasPrefix("<!--") {
        closeParagraph()
        closeList()
        body.append(rawLine)
        if !line.contains("-->") { inHTMLComment = true }
        continue
    }
    if line.hasPrefix("```") {
        closeParagraph()
        closeList()
        if inCodeBlock {
            body.append("<pre><code>\(escapeHTML(codeLines.joined(separator: "\n")))</code></pre>")
            codeLines.removeAll()
        }
        inCodeBlock.toggle()
        continue
    }
    if inCodeBlock {
        codeLines.append(rawLine)
        continue
    }
    if line.isEmpty {
        closeParagraph()
        closeList()
        continue
    }
    if let match = line.range(of: #"^#{1,3} "#, options: .regularExpression) {
        closeParagraph()
        closeList()
        let level = line[match].filter { $0 == "#" }.count
        let title = String(line[match.upperBound...])
        let anchor = slug(title)
        body.append("<a name=\"\(anchor)\"></a><h\(level) id=\"\(anchor)\">\(inlineMarkdown(title))</h\(level)>")
        continue
    }
    // A continuation line: indented, and we are part-way through a list item.
    if !listItem.isEmpty, rawLine.hasPrefix("  ") {
        listItem.append(line)
        continue
    }
    if line.hasPrefix("- ") {
        closeParagraph()
        closeListItem()
        if listKind != "ul" {
            closeList()
            listKind = "ul"
            body.append("<ul>")
        }
        listItem.append(String(line.dropFirst(2)))
        continue
    }
    if let match = line.range(of: #"^[0-9]+\. "#, options: .regularExpression) {
        closeParagraph()
        closeListItem()
        if listKind != "ol" {
            closeList()
            listKind = "ol"
            body.append("<ol>")
        }
        listItem.append(String(line[match.upperBound...]))
        continue
    }
    if line.hasPrefix("|") {
        closeParagraph()
        closeList()
        tableLines.append(line)
        continue
    }
    closeList()
    paragraph.append(line)
}

closeTable()
closeParagraph()
closeList()
if inCodeBlock {
    body.append("<pre><code>\(escapeHTML(codeLines.joined(separator: "\n")))</code></pre>")
}

let html = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="AppleTitle" content="\(escapeHTML(helpBookIdentifier))" />
  <meta name="AppleIcon" content="../shrd/Bunyi.png" />
  <meta name="description" content="Generate speech on your Mac with preset voices, voice design, and voice cloning." />
  <meta name="keywords" content="text to speech, voice, speech, preset voice, voice design, voice clone, model, download, offline, backup, troubleshooting" />
  <title>Bunyi Help</title>
  <style>
    :root { color-scheme: light dark; }
    body { font: 15px -apple-system, BlinkMacSystemFont, sans-serif; line-height: 1.5; margin: 0 auto; max-width: 760px; padding: 32px 40px 64px; }
    h1 { font-size: 30px; margin: 0 0 18px; }
    h2 { border-top: 1px solid color-mix(in srgb, currentColor 18%, transparent); font-size: 22px; margin: 34px 0 12px; padding-top: 24px; }
    h3 { font-size: 17px; margin: 26px 0 8px; }
    p { margin: 0 0 12px; }
    li { margin: 5px 0; }
    code { background: color-mix(in srgb, currentColor 10%, transparent); border-radius: 4px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; padding: 1px 4px; }
    pre { background: color-mix(in srgb, currentColor 8%, transparent); border-radius: 8px; overflow-x: auto; padding: 12px; }
    /* Inside a block the inline pill is wrong twice over: a rounded background
       nested in pre's own, and horizontal padding applied per line fragment,
       which frays the edges of a multi-line command. */
    pre code { background: none; border-radius: 0; font-size: 13px; padding: 0; white-space: pre; }
    .table-wrap { margin: 0 0 14px; overflow-x: auto; }
    table { border-collapse: collapse; font-size: 14px; }
    th, td { border: 1px solid color-mix(in srgb, currentColor 22%, transparent); padding: 6px 11px; text-align: left; vertical-align: top; }
    th { background: color-mix(in srgb, currentColor 8%, transparent); font-weight: 600; }
    /* The first column is a row label, and an empty corner header reads as a
       missing cell if it carries the header fill. */
    thead th:empty { background: none; border-left-color: transparent; border-top-color: transparent; }
    a { color: -apple-system-blue; }
  </style>
</head>
<body>
\(body.joined(separator: "\n"))
</body>
</html>
"""

try FileManager.default.createDirectory(
    at: outputURL.deletingLastPathComponent(),
    withIntermediateDirectories: true
)
try html.write(to: outputURL, atomically: true, encoding: .utf8)
