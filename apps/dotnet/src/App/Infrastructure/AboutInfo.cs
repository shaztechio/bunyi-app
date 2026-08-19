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

    /// <summary>
    /// The software this app is built on (spec §9a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every licence here was read from the package's own licence file or the
    /// model's own card, not from memory. Getting one wrong in a credits list is
    /// a licence claim the project cannot support.
    /// </para>
    /// <para>
    /// What ships or is downloaded, rather than everything that appears in a
    /// dependency graph — build and test tooling is not part of the app a user
    /// runs, and listing it would bury the things that are.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Credit> Credits { get; } =
    [
        new("Avalonia", "The windows, controls and drawing", "MIT",
            "https://avaloniaui.net"),
        new(".NET", "The runtime it all sits on", "MIT",
            "https://github.com/dotnet/runtime"),
        new("ONNX Runtime", "Runs the speech models", "MIT",
            "https://github.com/microsoft/onnxruntime"),
        new("whisper.cpp, via Whisper.net", "Listens to a reference recording", "MIT",
            "https://github.com/sandrohanea/whisper.net"),
        new("SoundFlow", "Plays clips and reads the recordings you choose", "MIT",
            "https://github.com/LSXPrime/SoundFlow"),
        new("ElBruno.QwenTTS", "Drives the preset-voice model", "MIT",
            "https://github.com/elbruno/ElBruno.QwenTTS"),
        new("CommunityToolkit.Mvvm", "Wires the windows to the code behind them", "MIT",
            "https://github.com/CommunityToolkit/dotnet"),
        new("Inter", "The typeface", "SIL Open Font License 1.1",
            "https://rsms.me/inter/"),
    ];

    /// <summary>
    /// The models, which are downloaded rather than shipped.
    /// </summary>
    /// <remarks>
    /// Separate from the libraries because they are the part that actually
    /// speaks, they are other people's work, and they arrive after the app does.
    /// </remarks>
    public static IReadOnlyList<Credit> ModelCredits { get; } =
    [
        new("Qwen3-TTS", "The voices, by the Qwen team at Alibaba", "Apache-2.0",
            "https://github.com/QwenLM/Qwen3-TTS"),
        new("Qwen3-TTS ONNX exports", "Converted for this runtime by elbruno and wavekat",
            "Apache-2.0", "https://huggingface.co/wavekat"),
        new("Whisper models", "Published by ggerganov as whisper.cpp", "MIT",
            "https://huggingface.co/ggerganov/whisper.cpp"),
    ];

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
