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

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Bunyi.Core.Audio;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Bunyi.Core.Help;

namespace Bunyi.App.Views;

/// <summary>
/// In-app help, rendered from <c>HELP.md</c> (spec §10).
/// </summary>
/// <remarks>
/// <c>HELP.md</c> is the only copy of the help text, as on macOS, and it is
/// embedded in the assembly rather than shipped beside it so a portable build
/// has nothing to lose on the way out.
/// </remarks>
public partial class HelpWindow : Window
{
    /// <summary>The monospaced face, shared with the Logs window.</summary>
    private static FontFamily Mono =>
        Application.Current?.TryFindResource("BunyiMono", out var found) == true
        && found is FontFamily family
            ? family
            : FontFamily.Default;

    /// <summary>The name the build gives the embedded copy.</summary>
    internal const string ResourceName = "Bunyi.App.HELP.md";

    public HelpWindow()
    {
        InitializeComponent();
        Render(HelpDocument.Parse(LoadText()));

        this.FindControl<TextBlock>("AboutLine")!.Text = About();
    }

    /// <summary>
    /// What the app is, which version, and where it is running.
    /// </summary>
    /// <remarks>
    /// The platform is named because these are the two builds people confuse in
    /// a bug report — the Windows and Linux apps are one codebase and look
    /// identical, so "Bunyi 0.1.0" alone does not say which one was running.
    /// </remarks>
    internal static string About() =>
        $"Bunyi {Version} for {OutputMetadata.CurrentPlatform} · "
        + "Apache-2.0 · Copyright 2026 Shazron Abdullah and Bunyi contributors";

    /// <summary>The version this build was stamped with.</summary>
    internal static string Version =>
        typeof(HelpWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Reads the embedded help text.</summary>
    /// <remarks>
    /// A missing resource means a build that lost the file, which is worth
    /// saying out loud in the window: help that renders as an empty page looks
    /// like a broken window rather than a broken build.
    /// </remarks>
    internal static string LoadText()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return "# Help unavailable\n\nThis build shipped without its help text.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Turns parsed blocks into controls.</summary>
    internal void Render(IReadOnlyList<HelpBlock> blocks)
    {
        var panel = this.FindControl<StackPanel>("Body")!;
        panel.Children.Clear();

        foreach (var block in blocks) panel.Children.Add(Build(block));
    }

    /// <summary>One block as a control.</summary>
    internal static Control Build(HelpBlock block) => block.Kind switch
    {
        HelpBlockKind.Heading => Heading(block),
        HelpBlockKind.Code => Code(block),
        HelpBlockKind.Bullet => Item(block, "•"),
        HelpBlockKind.Numbered => Item(block, $"{block.Number}."),
        _ => Paragraph(block),
    };

    private static Control Heading(HelpBlock block)
    {
        // Sized by level, with the space above it rather than below: a heading
        // belongs to what follows it, and equal spacing on both sides makes it
        // read as if it belonged to the paragraph above.
        var (size, top) = block.Level switch
        {
            1 => (24.0, 0.0),
            2 => (18.0, 26.0),
            _ => (14.0, 18.0),
        };

        return new SelectableTextBlock
        {
            Inlines = Inlines(block, HelpStyle.Bold),
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 8),
        };
    }

    private static Control Paragraph(HelpBlock block) => new SelectableTextBlock
    {
        Inlines = Inlines(block),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 21,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static Control Item(HelpBlock block, string marker) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        Margin = new Thickness(4, 0, 0, 8),
        Children =
        {
            new TextBlock
            {
                Text = marker,
                Width = 26,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Top,
            },
            new SelectableTextBlock
            {
                [Grid.ColumnProperty] = 1,
                Inlines = Inlines(block),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
            },
        },
    };

    private static Control Code(HelpBlock block) => new Border
    {
        Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10),
        Margin = new Thickness(0, 2, 0, 12),
        Child = new SelectableTextBlock
        {
            Text = block.PlainText,
            FontFamily = Mono,
            FontSize = 12.5,

            // Commands are copied out of here, so they must not be re-wrapped
            // into something that no longer runs.
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    /// <summary>The runs of a block as styled inlines.</summary>
    internal static InlineCollection Inlines(HelpBlock block, HelpStyle inherited = HelpStyle.None)
    {
        var inlines = new InlineCollection();

        foreach (var run in block.Runs)
        {
            var style = run.Style | inherited;

            var span = new Run(run.Text)
            {
                FontWeight = style.HasFlag(HelpStyle.Bold) ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = style.HasFlag(HelpStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
            };

            if (style.HasFlag(HelpStyle.Code))
            {
                span.FontFamily = Mono;
                span.FontSize = 12.5;
            }

            // Links are marked but not clickable: every link in HELP.md points
            // within the document, and there is no anchor to jump to yet.
            // Underlining something that does nothing would be a worse lie than
            // leaving it as text.
            if (run.Link is not null) span.FontWeight = FontWeight.SemiBold;

            inlines.Add(span);
        }

        return inlines;
    }
}
