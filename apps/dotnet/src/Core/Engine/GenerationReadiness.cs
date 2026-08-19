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

namespace Bunyi.Core.Engine;

/// <summary>
/// Whether a mode has what it needs, and what to say when it does not
/// (spec §1).
/// </summary>
/// <remarks>
/// Checked before the button is pressed rather than inside the engine. The
/// engine does reject a clone with no reference clip — but only after preparing
/// the model, which on a first run means waiting out a multi-gigabyte download
/// to be told a file is missing. Voice design had no check at all in the
/// original and would generate an arbitrary voice from an empty description.
/// </remarks>
public enum RequiredInput
{
    /// <summary>The words to speak.</summary>
    Text,

    /// <summary>Voice design's description of the voice.</summary>
    Instruction,

    /// <summary>Voice clone's recording.</summary>
    Reference,

    /// <summary>What that recording says.</summary>
    Transcript,
}

/// <summary>Something the run needs, and a sentence saying so.</summary>
/// <param name="Input">Which field to point at.</param>
/// <param name="Reason">What to tell the user, in their words.</param>
public sealed record MissingInput(RequiredInput Input, string Reason);

public static class GenerationReadiness
{
    /// <summary>Whether the request can be generated as it stands.</summary>
    public static bool CanGenerate(GenerateRequest request) => Missing(request) is null;

    /// <summary>
    /// What is missing, and where to look for it.
    /// </summary>
    /// <remarks>
    /// The field is named as well as the reason, because a sentence alone
    /// cannot point at anything. Generate stays pressable and says what is
    /// wrong when pressed — a disabled button explains nothing, cannot be
    /// hovered for the explanation it is supposed to carry, and is skipped
    /// entirely by a screen reader.
    /// </remarks>
    public static MissingInput? Missing(GenerateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsBlank(request.Text))
        {
            return new MissingInput(RequiredInput.Text, "Type or paste some text to speak.");
        }

        return request.Mode switch
        {
            // A speaker is always selected, so text is the only requirement.
            TtsMode.PresetVoice => null,

            TtsMode.VoiceDesign when IsBlank(request.Instruct) => new MissingInput(
                RequiredInput.Instruction,
                "Describe the voice you want, such as “a warm older man with a slight rasp”."),

            TtsMode.VoiceClone when IsBlank(request.ReferenceAudioPath) => new MissingInput(
                RequiredInput.Reference,
                "Choose a short recording of the voice to clone."),

            TtsMode.VoiceClone when IsBlank(request.ReferenceTranscript) => new MissingInput(
                RequiredInput.Transcript,
                "Type what the recording says, or let it be filled in automatically."),

            _ => null,
        };
    }

    /// <summary>
    /// Why Generate is unavailable, or null when it is available.
    /// </summary>
    /// <remarks>
    /// §1 requires the button to say why on hover, so this is a sentence for a
    /// person rather than a code. Each names the one thing to do next.
    /// </remarks>
    public static string? BlockedReason(GenerateRequest request) => Missing(request)?.Reason;

    /// <summary>
    /// Whether the script is effectively empty. Whitespace counts as nothing.
    /// </summary>
    /// <remarks>
    /// Defined once because in the original it was not: the button's check
    /// trimmed while the example-prompt check did not, so a single typed space
    /// hid the examples and left Generate disabled — restoring, with one
    /// keystroke, exactly the dead end the examples exist to remove.
    /// </remarks>
    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// The clickable examples an unused window offers (spec §1).
/// </summary>
/// <remarks>
/// "An unused window suggests something to click." The first frame is otherwise
/// an empty box, a "ready" line and a button that does not work — which for a
/// non-technical audience is a dead end rather than a starting point.
/// </remarks>
public static class ExamplePrompts
{
    /// <summary>
    /// The examples for a mode, in the order they are shown.
    /// </summary>
    /// <remarks>
    /// <b>Voice clone deliberately has none.</b> What it lacks on a first run is
    /// a reference recording, which no shipped example can be, so filling in the
    /// one input it does have would leave Generate exactly as unavailable — an
    /// example that does not unblock anything teaches the wrong thing about why
    /// the button is off.
    /// </remarks>
    public static IReadOnlyList<string> For(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice =>
        [
            "Hello! We'll begin in just a few minutes.",
            "Your table is ready — please follow me.",
            "Once upon a time, in a village by the sea…",
        ],
        TtsMode.VoiceDesign =>
        [
            "Warm documentary narrator, unhurried",
            "Bright young podcast host",
            "Calm late-night radio DJ",
        ],
        _ => [],
    };

    /// <summary>
    /// The line above the examples.
    /// </summary>
    /// <remarks>
    /// It names the field they fill, because in voice design that is not the box
    /// they sit under: a design example fills the voice description and leaves
    /// the script empty on purpose. That field is what the mode adds and the one
    /// input whose shape nobody guesses; the script is a sentence anyone can
    /// write.
    /// </remarks>
    public static string? PromptFor(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => "Not sure what to say? Try one:",
        TtsMode.VoiceDesign => "Or describe a voice like one of these:",
        _ => null,
    };

    /// <summary>Whether an example fills the script rather than the voice description.</summary>
    public static bool FillsScript(TtsMode mode) => mode == TtsMode.PresetVoice;

    /// <summary>
    /// Whether the examples belong on screen.
    /// </summary>
    /// <remarks>
    /// They disappear as soon as the script has anything in it, and <b>do not
    /// return over a generated result</b> — an invitation to try something
    /// belongs to a window that has not been used yet, not beside audio the user
    /// just made. "Nothing generated yet" is a real condition, not a restatement
    /// of an empty script: clearing the box after a run leaves the result in the
    /// bottom bar, and suggestions beside it read as the app forgetting what it
    /// did.
    /// </remarks>
    public static bool ShouldShow(TtsMode mode, string? script, bool hasResult) =>
        GenerationReadiness.IsBlank(script) && !hasResult && For(mode).Count > 0;
}

/// <summary>The language choices offered in every mode (spec §1).</summary>
public static class Languages
{
    /// <summary>
    /// Auto first, then the ten the models support.
    /// </summary>
    /// <remarks>
    /// The list is pinned by §1. The preset-voice export's config also carries
    /// two dialects the spec does not offer, and they must not be added here
    /// without a spec change.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        "auto", "english", "chinese", "japanese", "korean", "german",
        "french", "russian", "portuguese", "spanish", "italian",
    ];

    /// <summary>The default, which lets the model decide from the text.</summary>
    public const string Default = "auto";
}

/// <summary>Speakers shown before a model has loaded and reported its own (spec §1).</summary>
public static class FallbackSpeakers
{
    /// <summary>
    /// The nine the CustomVoice models ship.
    /// </summary>
    /// <remarks>
    /// A fallback so the picker is not empty on a first run, before any model
    /// exists on disk. Verified to match the published ONNX export's
    /// <c>speaker_ids.json</c> exactly, and the macOS app's list.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        "Ryan", "Aiden", "Vivian", "Serena", "Uncle_Fu",
        "Dylan", "Eric", "Ono_Anna", "Sohee",
    ];

    public const string Default = "Ryan";
}
