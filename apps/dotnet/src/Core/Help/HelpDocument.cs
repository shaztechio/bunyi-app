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

using System.Text;

namespace Bunyi.Core.Help;

/// <summary>What a run of text looks like.</summary>
[Flags]
public enum HelpStyle
{
    /// <summary>Body text.</summary>
    None = 0,

    /// <summary>Bold, from <c>**…**</c>.</summary>
    Bold = 1,

    /// <summary>Italic, from <c>*…*</c>.</summary>
    Italic = 2,

    /// <summary>Monospaced, from <c>`…`</c>.</summary>
    Code = 4,
}

/// <summary>A stretch of text with one appearance.</summary>
/// <param name="Text">The words, with the markers removed.</param>
/// <param name="Style">How it is drawn.</param>
/// <param name="Link">Where it points, or null for ordinary text.</param>
public sealed record HelpRun(string Text, HelpStyle Style = HelpStyle.None, string? Link = null);

/// <summary>The kinds of block the help text is made of.</summary>
public enum HelpBlockKind
{
    /// <summary>A run of prose.</summary>
    Paragraph,

    /// <summary>A heading, with <see cref="HelpBlock.Level"/> from 1 to 3.</summary>
    Heading,

    /// <summary>One item of a bulleted list.</summary>
    Bullet,

    /// <summary>One item of a numbered list, numbered by <see cref="HelpBlock.Number"/>.</summary>
    Numbered,

    /// <summary>A fenced code block, kept verbatim.</summary>
    Code,
}

/// <summary>One block of the document.</summary>
public sealed record HelpBlock(
    HelpBlockKind Kind,
    IReadOnlyList<HelpRun> Runs,
    int Level = 0,
    int Number = 0)
{
    /// <summary>The block's text with every marker removed.</summary>
    public string PlainText => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// The help text, parsed (spec §10).
/// </summary>
/// <remarks>
/// <para>
/// A deliberately small Markdown subset — headings, paragraphs, bulleted and
/// numbered lists, fenced code, and inline bold, italic, code and links. It is
/// the same subset the macOS renderer supports, and
/// <c>apps/dotnet/HELP.md</c> is written to stay inside it.
/// </para>
/// <para>
/// Hand-written rather than taken from a package for two reasons: a general
/// Markdown library brings a rendering control with it, and
/// <c>apps/dotnet/AGENTS.md</c> rules out third-party control libraries; and
/// parsing here rather than in the window means the whole thing is testable
/// with no UI at all. The parse is the part that can be wrong in a way nobody
/// notices.
/// </para>
/// </remarks>
public static class HelpDocument
{
    /// <summary>Parses help text into blocks.</summary>
    /// <remarks>
    /// Anything the subset does not cover is kept as its literal text rather
    /// than dropped. Help that silently loses a sentence is worse than help
    /// that shows a stray character, because only one of the two is visible to
    /// whoever wrote it.
    /// </remarks>
    public static IReadOnlyList<HelpBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = new List<HelpBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var number = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            blocks.Add(new HelpBlock(
                HelpBlockKind.Paragraph, ParseRuns(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // The licence header is an HTML comment, and it is not help.
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                FlushParagraph();
                while (i < lines.Length && !lines[i].Contains("-->", StringComparison.Ordinal)) i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();

                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }

                blocks.Add(new HelpBlock(
                    HelpBlockKind.Code, [new HelpRun(code.ToString(), HelpStyle.Code)]));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                number = 0;   // a blank line ends a numbered list
                continue;
            }

            var heading = HeadingLevel(trimmed);
            if (heading > 0)
            {
                FlushParagraph();
                number = 0;
                blocks.Add(new HelpBlock(
                    HelpBlockKind.Heading,
                    ParseRuns(trimmed[heading..].TrimStart('#', ' ')),
                    Level: Math.Min(heading, 3)));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                number = 0;
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, ParseRuns(trimmed[2..])));
                continue;
            }

            var ordered = OrderedItem(trimmed);

            // A list starts at 1 or carries on from the item above it.
            // Without that rule a paragraph opening "2026. A year..." becomes
            // a list, which is a real sentence shape and an unrecoverable
            // misreading — the full stop would be eaten. This is CommonMark's
            // rule too, and for the same reason.
            if (ordered is not null &&
                (ordered.Value.Number == 1 || ordered.Value.Number == number + 1))
            {
                FlushParagraph();

                // Numbered from the document rather than renumbered from one,
                // so a list that starts at 2 is a visible mistake in HELP.md
                // rather than a silently corrected one.
                blocks.Add(new HelpBlock(
                    HelpBlockKind.Numbered, ParseRuns(ordered.Value.Text),
                    Number: ordered.Value.Number));
                number = ordered.Value.Number;
                continue;
            }

            // A wrapped line continues the paragraph above it.
            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;

        // "#Heading" is not a heading; "# Heading" is.
        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static (int Number, string Text)? OrderedItem(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;

        if (digits == 0 || digits + 1 >= line.Length) return null;
        if (line[digits] != '.' || line[digits + 1] != ' ') return null;

        return (int.Parse(line[..digits], System.Globalization.CultureInfo.InvariantCulture),
                line[(digits + 2)..]);
    }

    /// <summary>Splits one line into styled runs.</summary>
    /// <remarks>
    /// Code is taken first and its contents are left alone: <c>`**not bold**`</c>
    /// is a literal, and a path or a URL is full of characters that would
    /// otherwise read as markers.
    /// </remarks>
    internal static IReadOnlyList<HelpRun> ParseRuns(string text)
    {
        var runs = new List<HelpRun>();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            runs.Add(new HelpRun(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    runs.Add(new HelpRun(text[(i + 1)..end], HelpStyle.Code));
                    i = end;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                var link = ReadLink(text, i);
                if (link is not null)
                {
                    FlushPlain();
                    runs.Add(new HelpRun(link.Value.Text, HelpStyle.None, link.Value.Url));
                    i = link.Value.End;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                var bold = i + 1 < text.Length && text[i + 1] == '*';
                var marker = bold ? "**" : "*";
                var end = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);

                // An empty span is not emphasis, and neither is a lone star.
                if (end > i + marker.Length)
                {
                    FlushPlain();
                    runs.Add(new HelpRun(
                        text[(i + marker.Length)..end],
                        bold ? HelpStyle.Bold : HelpStyle.Italic));
                    i = end + marker.Length - 1;
                    continue;
                }
            }

            plain.Append(text[i]);
        }

        FlushPlain();
        return runs.Count > 0 ? runs : [new HelpRun(string.Empty)];
    }

    private static (string Text, string Url, int End)? ReadLink(string text, int start)
    {
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;

        var end = text.IndexOf(')', close + 2);
        if (end < 0) return null;

        return (text[(start + 1)..close], text[(close + 2)..end], end);
    }
}
