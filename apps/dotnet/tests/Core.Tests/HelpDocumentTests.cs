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

using Bunyi.Core.Help;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The help renderer's parse (spec §10).
/// </summary>
/// <remarks>
/// Worth testing rather than eyeballing: the failure mode of a Markdown subset
/// is a sentence that quietly loses a word, or a paragraph that swallows the
/// heading after it, and nobody re-reads the whole help text after every edit.
/// </remarks>
public class HelpDocumentTests
{
    private static IReadOnlyList<HelpBlock> Parse(string text) => HelpDocument.Parse(text);

    private static HelpBlock Only(string text) => Assert.Single(Parse(text));

    [Fact]
    public void A_heading_knows_its_level()
    {
        Assert.Equal(1, Only("# Bunyi Help").Level);
        Assert.Equal(2, Only("## Logs").Level);
        Assert.Equal(3, Only("### Models").Level);
    }

    [Fact]
    public void A_heading_keeps_its_words_and_loses_its_hashes()
    {
        var block = Only("## Where your files go");

        Assert.Equal(HelpBlockKind.Heading, block.Kind);
        Assert.Equal("Where your files go", block.PlainText);
    }

    [Fact]
    public void A_hash_with_no_space_is_not_a_heading()
    {
        // "#1 problem" is prose.
        Assert.Equal(HelpBlockKind.Paragraph, Only("#1 problem").Kind);
    }

    [Fact]
    public void Wrapped_lines_join_into_one_paragraph()
    {
        // The failure this prevents: one paragraph per source line, which reads
        // as a ragged column rather than prose.
        var block = Only("Bunyi turns text into speech\non your own computer.");

        Assert.Equal("Bunyi turns text into speech on your own computer.", block.PlainText);
    }

    [Fact]
    public void A_blank_line_starts_a_new_paragraph()
    {
        var blocks = Parse("First one.\n\nSecond one.");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("Second one.", blocks[1].PlainText);
    }

    [Fact]
    public void A_heading_ends_the_paragraph_before_it()
    {
        // Without a blank line, which HELP.md does not always have.
        var blocks = Parse("Some prose.\n## Logs\nMore prose.");

        Assert.Equal([HelpBlockKind.Paragraph, HelpBlockKind.Heading, HelpBlockKind.Paragraph],
            blocks.Select(b => b.Kind));
    }

    [Fact]
    public void Bullets_are_their_own_blocks()
    {
        var blocks = Parse("- Play\n- Download\n- Trash");

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(HelpBlockKind.Bullet, b.Kind));
        Assert.Equal("Download", blocks[1].PlainText);
    }

    [Fact]
    public void Numbered_items_keep_the_numbers_the_document_gave_them()
    {
        // Renumbering from one would silently correct a mistake in HELP.md,
        // which is exactly the mistake worth seeing.
        var blocks = Parse("1. Type your text.\n2. Click Generate.");

        Assert.Equal([1, 2], blocks.Select(b => b.Number));
        Assert.Equal("Click Generate.", blocks[1].PlainText);
    }

    [Fact]
    public void A_full_stop_in_prose_does_not_start_a_list()
    {
        // A real sentence shape, and an unrecoverable misreading: as a list
        // item the full stop is eaten. A list starts at 1 or carries on from
        // the item above it, which is CommonMark's rule for the same reason.
        Assert.Equal(HelpBlockKind.Paragraph, Only("2026. A year, not a list item.").Kind);
        Assert.Equal("2026. A year, not a list item.", Only("2026. A year, not a list item.").PlainText);
    }

    [Fact]
    public void A_numbered_list_carries_on_across_its_items()
    {
        var blocks = Parse("1. One.\n2. Two.\n3. Three.");

        Assert.All(blocks, b => Assert.Equal(HelpBlockKind.Numbered, b.Kind));
        Assert.Equal([1, 2, 3], blocks.Select(b => b.Number));
    }

    [Fact]
    public void A_new_list_after_a_blank_line_must_start_at_one_again()
    {
        // Otherwise a stray "4." far below a list of three joins it.
        var blocks = Parse("1. One.\n2. Two.\n\n3. Not a list.");

        Assert.Equal(HelpBlockKind.Paragraph, blocks[^1].Kind);
    }

    [Fact]
    public void A_dash_without_a_space_is_not_a_bullet()
    {
        Assert.Equal(HelpBlockKind.Paragraph, Only("-1 degrees").Kind);
    }

    [Fact]
    public void Fenced_code_is_kept_exactly_as_written()
    {
        var block = Only("```\nhuggingface-cli download  org/repo\n  --local-dir .\n```");

        Assert.Equal(HelpBlockKind.Code, block.Kind);
        Assert.Equal("huggingface-cli download  org/repo\n  --local-dir .", block.PlainText);
    }

    [Fact]
    public void Markers_inside_code_are_left_alone()
    {
        // A command line is full of characters that would otherwise read as
        // markers.
        var block = Only("```\nls *.wav **/*.onnx\n```");

        Assert.Equal("ls *.wav **/*.onnx", block.PlainText);
    }

    [Fact]
    public void The_licence_header_is_not_help()
    {
        var blocks = Parse("<!--\nCopyright 2026\nApache 2.0\n-->\n\n# Bunyi Help");

        Assert.Equal(HelpBlockKind.Heading, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void Bold_and_italic_become_runs()
    {
        var runs = Only("Press **Generate** to *start*.").Runs;

        Assert.Equal(HelpStyle.Bold, runs.Single(r => r.Text == "Generate").Style);
        Assert.Equal(HelpStyle.Italic, runs.Single(r => r.Text == "start").Style);
    }

    [Fact]
    public void Bold_is_taken_before_italic()
    {
        // "**x**" read one star at a time gives an empty italic run and a
        // stray "x**".
        var runs = Only("**Bunyi**").Runs;

        Assert.Equal(HelpStyle.Bold, Assert.Single(runs).Style);
        Assert.Equal("Bunyi", runs[0].Text);
    }

    [Fact]
    public void Inline_code_keeps_its_contents_literal()
    {
        var runs = Only("Use `%LOCALAPPDATA%\\Bunyi` for that.").Runs;

        var code = runs.Single(r => r.Style == HelpStyle.Code);
        Assert.Equal(@"%LOCALAPPDATA%\Bunyi", code.Text);
    }

    [Fact]
    public void Emphasis_inside_code_stays_written_out()
    {
        var runs = Only("`ls **/*.wav`").Runs;

        Assert.Equal("ls **/*.wav", Assert.Single(runs).Text);
    }

    [Fact]
    public void A_link_keeps_its_words_and_its_address()
    {
        var run = Assert.Single(Only("See [Doctor](#doctor).").Runs, r => r.Link is not null);

        Assert.Equal("Doctor", run.Text);
        Assert.Equal("#doctor", run.Link);
    }

    [Fact]
    public void A_lone_star_survives_as_a_star()
    {
        // Losing a character silently is worse than showing one.
        Assert.Equal("2 * 3 = 6", Only("2 * 3 = 6").PlainText);
    }

    [Fact]
    public void An_unclosed_marker_survives_as_text()
    {
        Assert.Equal("A **start with no end", Only("A **start with no end").PlainText);
    }

    [Fact]
    public void A_bracket_that_is_not_a_link_survives()
    {
        Assert.Equal("[not a link] here", Only("[not a link] here").PlainText);
    }

    [Fact]
    public void No_text_is_ever_dropped()
    {
        // The property that matters most: every word in the source shows up
        // somewhere in the output.
        const string source =
            "Press **Generate**, then see [Doctor](#doctor) for `%LOCALAPPDATA%` detail.";

        var plain = Only(source).PlainText;

        foreach (var word in new[] { "Press", "Generate", "then", "see", "Doctor", "detail" })
        {
            Assert.Contains(word, plain, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_document_parses_to_nothing()
    {
        Assert.Empty(Parse(string.Empty));
        Assert.Empty(Parse("\n\n   \n"));
    }

    [Fact]
    public void Windows_line_endings_parse_the_same_as_unix_ones()
    {
        // HELP.md is checked out with CRLF on Windows.
        Assert.Equal(
            Parse("# Title\n\nBody.").Select(b => b.PlainText),
            Parse("# Title\r\n\r\nBody.").Select(b => b.PlainText));
    }
}
