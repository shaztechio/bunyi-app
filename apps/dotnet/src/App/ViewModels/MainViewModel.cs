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

    /// <summary>
    /// Whether History is showing instead of a generation mode (spec §2a).
    /// </summary>
    /// <remarks>
    /// A fourth segment beside the three modes, not a mode itself: it has no
    /// text to speak, so it offers no Generate button.
    /// </remarks>
    [ObservableProperty] private bool _showingHistory;

    public MainViewModel(
        ITtsEngine engine, IAudioPlayer player, ILogSink log, Func<string>? outputFolder = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        History = new HistoryViewModel(
            player, log, outputFolder ?? (() => Core.Infrastructure.AppPaths.Outputs));

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

    /// <summary>History (spec §2a).</summary>
    public HistoryViewModel History { get; }

    /// <summary>Settings (spec §7), or null when the app did not supply one.</summary>
    public SettingsViewModel? Settings { get; init; }

    /// <summary>
    /// Runs every check on demand, including the slow one (spec §11).
    /// </summary>
    /// <remarks>
    /// Reports on the mode last generated with when History is showing, since
    /// History is not a generation mode — which is only sensible behaviour
    /// because the report says which mode it is about.
    /// </remarks>
    public Func<TtsMode, bool, CancellationToken, Task<DoctorReport>>? Doctor { get; init; }

    /// <summary>Shows a report, supplied by the view.</summary>
    public Func<DoctorReport, Task>? ShowReport { get; set; }

    /// <summary>The on-demand run, or null when no Doctor was supplied.</summary>
    public async Task<DoctorReport?> RunDoctorAsync()
    {
        if (Doctor is null) return null;

        var report = await Doctor(Mode, true, CancellationToken.None);

        // The same findings go to the log, so they can be copied into a bug
        // report without keeping the dialog open (§11).
        _log.Log(report.Describe());
        return report;
    }

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
    /// What the picker shows: the three modes, then History.
    /// </summary>
    /// <remarks>
    /// §1 opens "A segmented picker selects one of three modes" and §2a adds "A
    /// fourth segment beside the three generation modes" — so all four live in
    /// one control. They were two controls, a list and a toggle, and choosing a
    /// mode left History showing: the toggle was the only way out, which is not
    /// what a segment is.
    /// </remarks>
    public IReadOnlyList<object> AllSegments { get; } =
        [TtsMode.PresetVoice, TtsMode.VoiceDesign, TtsMode.VoiceClone, HistorySegment.Instance];

    /// <summary>
    /// Which segment is selected. History is not a mode, so it is its own
    /// value rather than a fourth entry in the enum.
    /// </summary>
    public object SelectedSegment
    {
        get => ShowingHistory ? HistorySegment.Instance : Mode;
        set
        {
            if (value is TtsMode mode)
            {
                ShowingHistory = false;
                Mode = mode;
            }
            else if (value is HistorySegment)
            {
                ShowingHistory = true;
            }

            OnPropertyChanged();
        }
    }

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
        !ShowingHistory && ExamplePrompts.ShouldShow(Mode, Script, LastOutputPath is not null);

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

    /// <summary>
    /// Whether the progress bar should animate rather than measure.
    /// </summary>
    /// <remarks>
    /// Only a download knows how far along it is. Loading a model and
    /// generating do not — and generating is the long one, so a determinate
    /// bar pinned at zero is what a user sees for most of a run. Moving says
    /// "still working"; a bar at zero says "stuck".
    /// </remarks>
    public bool IsIndeterminate => IsBusy && Progress <= 0;

    /// <summary>A spinner, for the phases that cannot report a fraction.</summary>
    public bool ShowSpinner => IsBusy && Progress <= 0;

    /// <summary>A bar, only while something can actually be measured.</summary>
    public bool ShowProgressBar => IsBusy && Progress > 0;

    /// <summary>
    /// Whether there is a result to play or reveal.
    /// </summary>
    /// <remarks>
    /// Hidden in History, which has its own per-row player — two players on
    /// screen can play over each other (spec §2a).
    /// </remarks>
    public bool HasResult => LastOutputPath is not null && !IsBusy && !ShowingHistory;

    /// <summary>
    /// Whether Generate belongs on screen.
    /// </summary>
    /// <remarks>
    /// §2a: no Generate button in History — there is no text on screen to
    /// speak, so it would either do nothing or silently act on a mode that is
    /// not visible. <b>Stop stays</b>, because a run can still be in progress
    /// while History is open and hiding it would strand the user.
    /// </remarks>
    public bool ShowGenerate => !ShowingHistory && !IsBusy;

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
        catch (PreflightFailedException failed)
        {
            // §11: blockers stop the run and are reported in a dialog. The
            // findings already went to the log inside the engine.
            Status = "Cannot generate yet.";
            if (ShowReport is not null) await ShowReport(failed.Report);
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
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ShowSpinner));
            OnPropertyChanged(nameof(ShowProgressBar));
            ProgressDetail = status.Detail;
            Status = Describe(status);

            if (_engine.Speakers.Count > 0 && !_engine.Speakers.SequenceEqual(Speakers))
            {
                // Read the choice BEFORE emptying the list. Clearing a bound
                // collection sets the picker's selection to null, and the
                // two-way binding writes that straight back here — so by the
                // time the list is refilled, Speaker is already gone.
                var wanted = Speaker;

                Speakers.Clear();
                foreach (var speaker in _engine.Speakers) Speakers.Add(speaker);

                // Keep the user's choice across the swap. The fallback list is
                // capitalised ("Ryan") and the model reports lowercase
                // ("ryan"), so an exact match would reset the picker to
                // whatever happens to be first the moment a model loads —
                // which is the "Preset voice forgot your speaker" defect the
                // macOS app already had once.
                var kept = Speakers.FirstOrDefault(
                    s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));
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
        OnPropertyChanged(nameof(ShowGenerate));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ShowSpinner));
        OnPropertyChanged(nameof(ShowProgressBar));
    }

    partial void OnScriptChanged(string value) => Refresh();
    partial void OnInstructChanged(string value) => Refresh();
    partial void OnIsBusyChanged(bool value) => Refresh();
    partial void OnLastOutputPathChanged(string? value) => Refresh();

    partial void OnShowingHistoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedSegment));
        // Read the folder every time it is shown, so it is never stale.
        if (value) History.Refresh();

        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowGenerate));
        OnPropertyChanged(nameof(ShowExamples));
    }

    partial void OnModeChanged(TtsMode value)
    {
        OnPropertyChanged(nameof(SelectedSegment));
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
        History.Dispose();
        _player.Dispose();
    }
}
