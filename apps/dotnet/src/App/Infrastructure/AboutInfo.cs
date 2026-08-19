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
using System.Text.Json;
using Bunyi.Core.Audio;

namespace Bunyi.App.Infrastructure;

/// <summary>
/// What the app is, for the About tab (spec §9a).
/// </summary>
/// <remarks>
/// <para>
/// macOS gets this free: AppKit's About panel, filled in from the bundle. There
/// is no equivalent here and no menu bar to hang one on, so it lives in Settings
/// — which is where a Windows or Linux user looks for it, and which §7 already
/// has open for other reasons.
/// </para>
/// <para>
/// Read from the assembly rather than written down, so a bumped version cannot
/// disagree with the build it is stamped on. That is not hypothetical: this
/// project has already shipped a macOS build whose version said 1.0 because the
/// number lived in two places.
/// </para>
/// </remarks>
public static class AboutInfo
{
    /// <summary>The app's name, as a person would say it.</summary>
    public const string Name = "Bunyi";

    /// <summary>What it is, in one line.</summary>
    public const string Tagline = "Local text to speech, on your own machine.";

    /// <summary>The licence it ships under.</summary>
    public const string Licence = "Apache-2.0";

    /// <summary>Where it comes from.</summary>
    public const string Home = "https://github.com/shaztechio/bunyi-app";

    /// <summary>The version this build was stamped with.</summary>
    public static string Version =>
        typeof(AboutInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The system this build is running on.
    /// </summary>
    /// <remarks>
    /// Named rather than left out, because Windows and Linux are one codebase
    /// and look identical — a version alone does not say which build a bug
    /// report is about. Shares its wording with the stamp written into every
    /// generated clip, so the two never disagree.
    /// </remarks>
    public static string Platform => OutputMetadata.CurrentPlatform;

    /// <summary>Version and platform together, as the tab shows them.</summary>
    public static string VersionLine => $"Version {Version} for {Platform}";

    // Declared before the lists that use it. Static initialisers run in
    // declaration order, so the other way round this is null while they load,
    // the match quietly becomes case-sensitive, and every entry comes back
    // empty — a credits page that renders as nothing at all.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The name the build gives the embedded credits.</summary>
    internal const string CreditsResource = "Bunyi.App.CREDITS.json";

    /// <summary>Which app this is, in the shared file's terms.</summary>
    internal const string AppKey = "dotnet";

    /// <summary>
    /// The software this app is built on (spec §9a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from <c>/spec/CREDITS.json</c>, which the macOS app reads too. One
    /// list, so the two cannot end up crediting different things — they share no
    /// code, but they should not disagree about whose work they are standing on.
    /// </para>
    /// <para>
    /// Entries are filtered to this app: most of what the two depend on differs
    /// entirely, and crediting MLX here would be crediting something that is not
    /// in the build.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Credit> Credits { get; } = Load("library");

    /// <summary>
    /// The models, which are downloaded rather than shipped.
    /// </summary>
    /// <remarks>
    /// Separate from the libraries because they are the part that actually
    /// speaks, they are other people's work, and they arrive after the app does.
    /// </remarks>
    public static IReadOnlyList<Credit> ModelCredits { get; } = Load("model");

    private static IReadOnlyList<Credit> Load(string kind)
    {
        using var stream = typeof(AboutInfo).Assembly
            .GetManifestResourceStream(CreditsResource);

        if (stream is null)
        {
            // A build that lost the file. Saying nothing beats inventing a list,
            // and the tests fail long before anyone sees this.
            return [];
        }

        var file = JsonSerializer.Deserialize<CreditsFile>(stream, JsonOptions);

        return file?.Entries?
            .Where(e => e.Kind == kind && e.Apps?.Contains(AppKey) == true)
            .Select(e => new Credit(e.Name, e.Does, e.Licence, e.Url))
            .ToList()
            ?? [];
    }

    private sealed record CreditsFile(IReadOnlyList<CreditEntry>? Entries);

    private sealed record CreditEntry(
        string Name, string Does, string Licence, string Url,
        string Kind, IReadOnlyList<string>? Apps);

    /// <summary>The copyright line, taken from the assembly.</summary>
    public static string Copyright =>
        typeof(AboutInfo).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? "Copyright 2026 Shazron Abdullah and Bunyi contributors";
}

/// <summary>One piece of software the app is built on.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Does">What it does here, in a person's words.</param>
/// <param name="Licence">The licence it is offered under.</param>
/// <param name="Home">Where to find it.</param>
public sealed record Credit(string Name, string Does, string Licence, string Home)
{
    /// <summary>The licence and link on one line, as the tab shows them.</summary>
    public string Detail => $"{Licence} · {Home}";
}
