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

using System.Collections.ObjectModel;
using Bunyi.App.Infrastructure;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

/// <summary>The main window's state (spec §1, §2).</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITtsEngine _engine;
    private readonly IAudioPlayer _player;
    private readonly ILogSink _log;

    [ObservableProperty] private TtsMode _mode = TtsMode.PresetVoice;
    [ObservableProperty] private string _script = string.Empty;
    [ObservableProperty] private string _language = Languages.Default;
    [ObservableProperty] private string _speaker = FallbackSpeakers.Default;
    [ObservableProperty] private string _instruct = string.Empty;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressDetail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _lastOutputPath;
    [ObservableProperty] private bool _isPlaying;

    public MainViewModel(ITtsEngine engine, IAudioPlayer player, ILogSink log)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Speakers = [.. FallbackSpeakers.All];

        _engine.StatusChanged += OnEngineStatusChanged;
        _player.Finished += (_, _) => UiThread.Post(() => IsPlaying = false);
    }

    /// <summary>
    /// The engine, for the window's busy-close guard (spec §9).
    /// </summary>
    /// <remarks>
    /// Exposed because the decision to close belongs to the window, not to this
    /// class: it is the thing being closed, and it is what shows the dialog.
    /// </remarks>
    public ITtsEngine Engine => _engine;

    /// <summary>Languages offered in every mode (spec §1).</summary>
    public IReadOnlyList<string> AllLanguages { get; } = Languages.All;

    /// <summary>
    /// The speakers the picker shows.
    /// </summary>
    /// <remarks>
    /// Seeded with the built-in list so the picker is not empty on a first run,
    /// and replaced by the model's own once one has loaded (spec §1).
    /// </remarks>
    public ObservableCollection<string> Speakers { get; }

    /// <summary>The three generation modes, for the picker.</summary>
    public IReadOnlyList<TtsMode> AllModes { get; } =
        [TtsMode.PresetVoice, TtsMode.VoiceDesign, TtsMode.VoiceClone];

    /// <summary>
    /// Whether Generate can be pressed (spec §1).
    /// </summary>
    public bool CanGenerate => !IsBusy && GenerationReadiness.CanGenerate(CurrentRequest());

    /// <summary>
    /// Why it cannot, for the tooltip §1 requires.
    /// </summary>
    public string? BlockedReason => GenerationReadiness.BlockedReason(CurrentRequest());

    /// <summary>The examples an unused window offers (spec §1).</summary>
    public IReadOnlyList<string> Examples => ExamplePrompts.For(Mode);

    /// <summary>The line above them, naming the field they fill.</summary>
    public string? ExamplePrompt => ExamplePrompts.PromptFor(Mode);

    /// <summary>Whether they belong on screen at all.</summary>
    public bool ShowExamples =>
        ExamplePrompts.ShouldShow(Mode, Script, LastOutputPath is not null);

    /// <summary>Only preset voice is implemented so far.</summary>
    public bool ModeIsAvailable => Mode == TtsMode.PresetVoice;

    /// <summary>What the mode picker's subtitle says.</summary>
    public string ModeSubtitle => Mode switch
    {
        TtsMode.PresetVoice => "Choose a voice the model already knows.",
        TtsMode.VoiceDesign => "Describe a voice and the model builds it. Not implemented yet.",
        TtsMode.VoiceClone => "Clone a voice from a recording. Not implemented yet.",
        _ => string.Empty,
    };

    /// <summary>Whether the style field applies to this mode (spec §1).</summary>
    /// <remarks>
    /// Clone never gets one: the 12 Hz Base model cannot take an instruction,
    /// and §1 forbids offering the field there. Emotion for a cloned voice has
    /// to come from the reference clip's own delivery.
    /// </remarks>
    public bool ShowInstruct => Mode != TtsMode.VoiceClone;

    /// <summary>The label for the style field, which differs by mode.</summary>
    public string InstructLabel =>
        Mode == TtsMode.VoiceDesign ? "Voice" : "Style";

    /// <summary>Whether there is a result to play or reveal.</summary>
    public bool HasResult => LastOutputPath is not null && !IsBusy;

    [RelayCommand]
    private void UseExample(string? example)
    {
        if (string.IsNullOrEmpty(example)) return;

        // A design example fills the voice description, not the script: that
        // field is what the mode adds and the one input whose shape nobody
        // guesses (spec §1).
        if (ExamplePrompts.FillsScript(Mode)) Script = example;
        else Instruct = example;
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;

        try
        {
            await _engine.GenerateAsync(CurrentRequest(), null, CancellationToken.None);
            var path = _engine.LastOutputPath;
            LastOutputPath = path;

            // §2: the result plays itself once it is written.
            if (path is not null) Play();
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
        }
        catch (Exception ex)
        {
            // §10: the actionable sentence goes on screen, the full text to the
            // log.
            Status = ex.Message;
            _log.Log($"Generation failed: {ex}");
        }
    }

    /// <summary>Stop, which replaces Generate while anything is running (spec §2).</summary>
    [RelayCommand]
    private void Stop() => _engine.RequestStop();

    [RelayCommand]
    private void Play()
    {
        if (LastOutputPath is null) return;

        if (IsPlaying)
        {
            _player.Stop();
            IsPlaying = false;
            return;
        }

        IsPlaying = true;
        _player.Play(LastOutputPath);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (LastOutputPath is not null) FileReveal.Reveal(LastOutputPath, _log);
    }

    private GenerateRequest CurrentRequest() => new(
        Mode,
        Script,
        Language,
        Mode == TtsMode.PresetVoice ? Speaker : null,
        Mode == TtsMode.VoiceClone ? null : Instruct);

    private void OnEngineStatusChanged(object? sender, EngineStatus status) =>
        UiThread.Post(() =>
        {
            // Core raises this on whichever thread did the work; everything
            // below is bound, so it has to land on the UI thread.
            IsBusy = status.IsBusy;
            Progress = status.Progress;
            ProgressDetail = status.Detail;
            Status = Describe(status);

            if (_engine.Speakers.Count > 0 && !_engine.Speakers.SequenceEqual(Speakers))
            {
                Speakers.Clear();
                foreach (var speaker in _engine.Speakers) Speakers.Add(speaker);

                // Keep the user's choice across the swap. The fallback list is
                // capitalised ("Ryan") and the model reports lowercase
                // ("ryan"), so an exact match would silently reset the picker
                // to whatever happens to be first the moment a model loads —
                // which is the "Preset voice forgot your speaker" defect the
                // macOS app already had once.
                var kept = Speakers.FirstOrDefault(
                    s => string.Equals(s, Speaker, StringComparison.OrdinalIgnoreCase));
                Speaker = kept ?? Speakers[0];
            }

            Refresh();
        });

    private static string Describe(EngineStatus status) => status.State switch
    {
        EngineState.Idle => "Ready",
        EngineState.Downloading => status.Detail ?? "Getting the model…",
        EngineState.Loading => "Loading the model…",
        EngineState.Transcribing => "Listening to the recording…",
        EngineState.Generating => "Generating…",
        EngineState.Stopping => "Stopping…",
        EngineState.Error => status.Message ?? "Something went wrong.",
        _ => string.Empty,
    };

    /// <summary>Re-evaluates everything computed from the fields above.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(ShowExamples));
        OnPropertyChanged(nameof(HasResult));
    }

    partial void OnScriptChanged(string value) => Refresh();
    partial void OnInstructChanged(string value) => Refresh();
    partial void OnIsBusyChanged(bool value) => Refresh();
    partial void OnLastOutputPathChanged(string? value) => Refresh();

    partial void OnModeChanged(TtsMode value)
    {
        OnPropertyChanged(nameof(Examples));
        OnPropertyChanged(nameof(ExamplePrompt));
        OnPropertyChanged(nameof(ModeSubtitle));
        OnPropertyChanged(nameof(ModeIsAvailable));
        OnPropertyChanged(nameof(ShowInstruct));
        OnPropertyChanged(nameof(InstructLabel));
        Refresh();
    }

    public void Dispose()
    {
        _engine.StatusChanged -= OnEngineStatusChanged;
        _player.Dispose();
    }
}
