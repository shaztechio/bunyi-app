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
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Whisper.net;

namespace Bunyi.Core.Transcription;

/// <summary>
/// Turns a reference clip into the transcript voice clone needs (spec §4).
/// </summary>
/// <remarks>
/// <para>
/// §4 makes this convenience rather than a requirement: the transcript is
/// "effectively mandatory" for a clone, and "a typed transcript always overrides
/// auto-detection". So this fills a blank field, and anything the user writes
/// wins — the caller enforces that, and it is why this never returns a value
/// the user did not ask for.
/// </para>
/// <para>
/// Whisper rather than an OS speech API, which §4 requires by name: the same
/// words come out on Windows and Linux, and nothing leaves the machine. macOS
/// uses the Speech framework and can be asked to keep recognition on-device;
/// there is no equivalent here worth trusting, and a cloud round trip for a
/// clip of someone's voice is not a trade this app should make quietly.
/// </para>
/// </remarks>
public sealed class WhisperTranscriber : IReferenceTranscriber, IDisposable
{
    /// <summary>The rate Whisper works at, whatever the model wants.</summary>
    public const int SampleRate = 16_000;

    private readonly Func<CancellationToken, Task<string>> _modelPath;
    private readonly ILogSink _log;

    private WhisperFactory? _factory;
    private string? _loadedFrom;

    /// <param name="modelPath">
    /// Produces the ggml model file, downloading it if it is not there yet.
    /// </param>
    public WhisperTranscriber(Func<CancellationToken, Task<string>> modelPath, ILogSink log)
    {
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(
        string audioPath, string language, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);

        // Decoded before the model is fetched. A clip that cannot be read is a
        // mistake worth reporting now, rather than after a 141 MB download.
        var samples = ReferenceAudio.Load(audioPath, SampleRate, _log);

        var path = await _modelPath(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (_factory is null || _loadedFrom != path)
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(path);
            _loadedFrom = path;
        }

        var builder = _factory.CreateBuilder();

        // A named language beats detection: §1 already asked the user which one
        // this is, and Whisper guessing differently would transcribe the right
        // sounds into the wrong words.
        var code = LanguageCode(language);
        builder = code is null ? builder.WithLanguageDetection() : builder.WithLanguage(code);

        using var processor = builder.Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        var transcript = Tidy(text.ToString());

        if (transcript.Length == 0)
        {
            throw new InvalidOperationException(
                "Nothing could be heard in that recording. Try a clearer clip, "
                + "or type the transcript yourself.");
        }

        _log.Log($"Transcribed the reference clip: \"{transcript}\"");
        return transcript;
    }

    /// <summary>
    /// Collapses the whitespace Whisper leaves between segments.
    /// </summary>
    /// <remarks>
    /// Each segment arrives with a leading space, so joining them gives double
    /// spaces at every boundary. The transcript is shown to the user and is
    /// editable, so it should look like something a person would have typed.
    /// </remarks>
    internal static string Tidy(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The two-letter code Whisper wants, or null to let it decide.
    /// </summary>
    /// <remarks>
    /// §1's language list is spelled in full words because that is what the TTS
    /// models' own configs use. Whisper wants ISO codes, so the two meet here
    /// rather than in the caller — and "auto" is a real answer, not a missing
    /// one.
    /// </remarks>
    internal static string? LanguageCode(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "english" => "en",
            "chinese" => "zh",
            "japanese" => "ja",
            "korean" => "ko",
            "german" => "de",
            "french" => "fr",
            "russian" => "ru",
            "portuguese" => "pt",
            "spanish" => "es",
            "italian" => "it",
            _ => null,   // "auto", empty, or a name this build does not know
        };

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _loadedFrom = null;
    }
}
