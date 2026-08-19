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
using Bunyi.Core.Models;
using Bunyi.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

/// <summary>The main window's state (spec §1, §2).</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITtsEngine _engine;
    private readonly IAudioPlayer _player;
    private readonly IBatchTimerFactory _timers;

    /// <summary>
    /// The playback ticker, made when something first plays.
    /// </summary>
    /// <remarks>
    /// Lazily, and that matters beyond tidiness: a DispatcherTimer belongs to
    /// the UI thread from the moment it is constructed, so building one in this
    /// constructor made a view model that could only be created on that thread.
    /// Plain unit tests construct one on an ordinary thread, and paid for it
    /// with "the calling thread cannot access this object" during cleanup — an
    /// intermittent CI failure that landed on whichever test came next.
    /// </remarks>
    private IBatchTimer? _ticker;
    private readonly ILogSink _log;

    [ObservableProperty] private TtsMode _mode = TtsMode.PresetVoice;
    [ObservableProperty] private string _script = string.Empty;
    [ObservableProperty] private string _language = Languages.Default;
    [ObservableProperty] private string _speaker = FallbackSpeakers.Default;
    [ObservableProperty] private string _instruct = string.Empty;

    /// <summary>The recording a clone is taken from (spec §4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferenceName))]
    [NotifyPropertyChangedFor(nameof(HasReference))]
    [NotifyPropertyChangedFor(nameof(CanSaveVoice))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveVoiceCommand))]
    private string? _referenceAudioPath;

    /// <summary>What that recording says, typed or listened for (spec §4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveVoice))]
    [NotifyCanExecuteChangedFor(nameof(SaveVoiceCommand))]
    private string _referenceTranscript = string.Empty;

    /// <summary>Whether a transcript is being worked out right now.</summary>
    /// <remarks>
    /// Drives the spinner and disables the transcript field. Listening takes
    /// seconds, and a field that stays editable while something is about to
    /// overwrite it invites typing that is then thrown away.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpinner))]
    [NotifyPropertyChangedFor(nameof(CanEditTranscript))]
    [NotifyPropertyChangedFor(nameof(CanSaveVoice))]
    [NotifyCanExecuteChangedFor(nameof(SaveVoiceCommand))]
    private bool _isTranscribing;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressDetail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _lastOutputPath;
    [ObservableProperty] private bool _isPlaying;

    /// <summary>How far through the clip playback is, from 0 to 1.</summary>
    [ObservableProperty] private double _playProgress;

    /// <summary>How far into the clip playback has reached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private TimeSpan _elapsed;

    /// <summary>The clip's length.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan _duration;

    /// <summary>The position, as minutes and seconds.</summary>
    public string ElapsedText => Clock(Elapsed);

    /// <summary>The length, as minutes and seconds.</summary>
    public string DurationText => Clock(Duration);

    /// <summary>
    /// A duration as <c>m:ss</c>, matching macOS.
    /// </summary>
    /// <remarks>
    /// Rounded down rather than to nearest, so the elapsed time never shows a
    /// second the clip has not reached — and never briefly reads past the
    /// total at the end.
    /// </remarks>
    internal static string Clock(TimeSpan time)
    {
        var total = (int)Math.Max(0, Math.Floor(time.TotalSeconds));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    /// <summary>
    /// Whether History is showing instead of a generation mode (spec §2a).
    /// </summary>
    /// <remarks>
    /// A fourth segment beside the three modes, not a mode itself: it has no
    /// text to speak, so it offers no Generate button.
    /// </remarks>
    [ObservableProperty] private bool _showingHistory;

    public MainViewModel(
        ITtsEngine engine,
        IAudioPlayer player,
        ILogSink log,
        Func<string>? outputFolder = null,
        IBatchTimerFactory? timers = null,
        VoiceLibrary? voices = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _timers = timers ?? new DispatcherTimerFactory();

        History = new HistoryViewModel(
            player, log, outputFolder ?? (() => Core.Infrastructure.AppPaths.Outputs));

        Speakers = [.. FallbackSpeakers.All];

        _engine.StatusChanged += OnEngineStatusChanged;
        _player.Finished += (_, _) => UiThread.Post(StopPlayback);

        _voices = voices;
        ReloadVoices();
    }

    /// <summary>
    /// The saved voices, or null when this window has no library.
    /// </summary>
    /// <remarks>
    /// Null rather than one pointing at the user's own folder. The app supplies
    /// the real library; anything that does not — a test, a preview — gets a
    /// window with no saved voices rather than one quietly reading, pruning and
    /// rewriting somebody's actual library on construction.
    /// </remarks>
    private readonly VoiceLibrary? _voices;

    /// <summary>The saved voices, newest first (spec §5).</summary>
    public ObservableCollection<SavedVoice> SavedVoices { get; } = [];

    /// <summary>Whether there is anything in the library to offer.</summary>
    public bool HasSavedVoices => SavedVoices.Count > 0;

    /// <summary>Re-reads the library, pruning entries whose audio has gone.</summary>
    private void ReloadVoices()
    {
        SavedVoices.Clear();

        if (_voices is not null)
        {
            _voices.Load();
            foreach (var voice in _voices.Voices) SavedVoices.Add(voice);
        }

        OnPropertyChanged(nameof(HasSavedVoices));
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

    /// <summary>The Logs window's state, or null when there is no log to show.</summary>
    public LogsViewModel? Logs { get; init; }

    /// <summary>Shows a report, supplied by the view.</summary>
    public Func<DoctorReport, Task>? ShowReport { get; set; }

    /// <summary>Asks the window for a recording to clone from (spec §4).</summary>
    public Func<Task<string?>>? ChooseReference { get; set; }

    /// <summary>Listens to a recording and returns what it says (spec §4).</summary>
    public Func<string, CancellationToken, Task<string>>? Transcribe { get; set; }

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
            var was = SelectedSegment;

            if (value is TtsMode mode)
            {
                ShowingHistory = false;
                Mode = mode;
            }
            else if (value is HistorySegment)
            {
                ShowingHistory = true;
            }

            // Only when the tab actually changed. Avalonia re-sets the same
            // segment during layout, and clearing the last result on that would
            // make a finished clip vanish while nobody touched anything.
            if (!Equals(was, SelectedSegment)) LeaveTab();

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Puts the previous tab down: stop playing, and clear its result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The player belongs to the clip that was just made, and the clip belongs
    /// to the tab that made it. Carrying it across meant preset voice's result
    /// sat under clone mode's controls, playable, with nothing on screen saying
    /// which tab it came from.
    /// </para>
    /// <para>
    /// Refreshing here matters as much. Returning to the tab you left calls
    /// this setter without changing Mode, so OnModeChanged never fires — which
    /// is how Generate could end up disabled with nothing to press.
    /// </para>
    /// </remarks>
    private void LeaveTab()
    {
        StopPlayback();

        // Not deleted, just no longer this tab's business. It is in History,
        // which is where a finished clip lives.
        LastOutputPath = null;
        Duration = TimeSpan.Zero;

        Refresh();
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
    public bool ModeIsAvailable => ModelLayout.Exists(Mode);

    /// <summary>
    /// Whether to offer a speaker list.
    /// </summary>
    /// <remarks>
    /// Preset voice alone. Design mode takes a description instead and clone
    /// takes a recording, and neither export has speakers to offer — a picker
    /// there would be the trap §1 refuses for clone mode's emotion field.
    /// Written as the one mode that has them rather than as the modes that do
    /// not: the second form silently became wrong the moment clone worked.
    /// </remarks>
    public bool ShowSpeakers => ModeIsAvailable && Mode == TtsMode.PresetVoice;

    /// <summary>What the style or voice field suggests, which differs by mode.</summary>
    public string InstructPlaceholder => Mode == TtsMode.VoiceDesign
        ? "Describe the voice, e.g. a warm older man with a slight rasp"
        : "Optional — how it should be said";

    /// <summary>What the mode picker's subtitle says.</summary>
    public string ModeSubtitle => Mode switch
    {
        TtsMode.PresetVoice => "Choose a voice the model already knows.",
        TtsMode.VoiceDesign => "Describe a voice and the model builds it.",
        TtsMode.VoiceClone => "Clone a voice from a recording of it.",
        _ => string.Empty,
    };

    /// <summary>Whether the reference-recording row belongs on screen.</summary>
    public bool ShowReference => Mode == TtsMode.VoiceClone;

    /// <summary>Whether a recording has been chosen.</summary>
    public bool HasReference => !string.IsNullOrWhiteSpace(ReferenceAudioPath);

    /// <summary>
    /// The chosen recording, named the way a person would recognise it.
    /// </summary>
    /// <remarks>
    /// The file name rather than the path: the path is long, usually
    /// uninteresting, and on a narrow window pushes everything else off screen.
    /// </remarks>
    public string ReferenceName
    {
        get
        {
            if (!HasReference) return "No recording chosen";

            // A saved voice's copy is named after its id, so the file name is a
            // GUID and tells the user nothing about what they picked. Show what
            // they called it instead.
            if (SelectedVoice is { } voice
                && _voices is not null
                && string.Equals(ReferenceAudioPath, _voices.ClipPath(voice),
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"{voice.Name} — the saved recording";
            }

            return Path.GetFileName(ReferenceAudioPath!);
        }
    }

    /// <summary>What to tell someone about the recording they should pick.</summary>
    /// <remarks>
    /// The ten-second limit is the model's, and worth saying before the fact
    /// rather than after: hand it a longer clip with a transcript covering all
    /// of it and the clone finishes the recording instead of speaking the text.
    /// </remarks>
    public const string ReferenceHint =
        "A few seconds of clear speech. Only the first ten are used, "
        + "and it works best ending on a finished sentence.";

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
    /// <remarks>
    /// Listening counts. It is not a generation — the engine is idle and the
    /// window stays usable — but it is work the status line is reporting, and a
    /// status that changes with nothing moving beside it reads as stuck.
    /// </remarks>
    public bool ShowSpinner => IsTranscribing || (IsBusy && Progress <= 0);

    /// <summary>Whether the transcript can be typed into right now.</summary>
    public bool CanEditTranscript => !IsTranscribing;

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

    /// <summary>
    /// Chooses the recording to clone, and listens to it (spec §4).
    /// </summary>
    /// <remarks>
    /// The transcript is filled in as soon as the recording is chosen rather
    /// than when Generate is pressed. §4 wants it shown and editable, and it can
    /// only be either if it arrives while there is still something to edit it
    /// with.
    /// </remarks>
    [RelayCommand]
    private async Task PickReferenceAsync()
    {
        if (ChooseReference is null) return;

        var chosen = await ChooseReference();
        if (string.IsNullOrWhiteSpace(chosen)) return;

        ReferenceAudioPath = chosen;

        // A transcript already typed is the user's, and §4 says it always wins.
        if (string.IsNullOrWhiteSpace(ReferenceTranscript))
        {
            await ListenAsync();
        }
    }

    /// <summary>The saved voice in use, or null (spec §5).</summary>
    /// <remarks>
    /// Choosing one fills the recording and the transcript together. They were
    /// saved as a pair and only mean anything as a pair — half of a saved voice
    /// is the state that makes a clone finish the recording instead of speaking.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferenceName))]
    [NotifyPropertyChangedFor(nameof(CanDeleteVoice))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVoiceCommand))]
    private SavedVoice? _selectedVoice;

    partial void OnSelectedVoiceChanged(SavedVoice? value)
    {
        if (value is null) return;

        if (_voices is null) return;

        ReferenceAudioPath = _voices.ClipPath(value);
        ReferenceTranscript = value.Transcript;
    }

    /// <summary>What a voice about to be saved will be called.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveVoiceCommand))]
    private string _newVoiceName = string.Empty;

    /// <summary>Whether there is a complete voice to save (spec §5).</summary>
    public bool CanSaveVoice =>
        _voices is not null
        && HasReference
        && !string.IsNullOrWhiteSpace(ReferenceTranscript)
        && !string.IsNullOrWhiteSpace(NewVoiceName)
        && !IsTranscribing;

    /// <summary>Saves the current recording and transcript under a name.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveVoice))]
    private void SaveVoice()
    {
        try
        {
            var saved = _voices!.Save(NewVoiceName, ReferenceAudioPath!, ReferenceTranscript);

            ReloadVoices();
            NewVoiceName = string.Empty;

            // Point at the copy rather than the original, so the fields now
            // describe what the library holds.
            SelectedVoice = SavedVoices.FirstOrDefault(v => v.Id == saved.Id);
            Status = $"Saved the voice “{saved.Name}”.";
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException)
        {
            _log.Log($"Could not save that voice: {ex.Message}");
            Status = ex.Message;
        }
    }

    /// <summary>Whether there is a saved voice selected to remove.</summary>
    public bool CanDeleteVoice => SelectedVoice is not null;

    /// <summary>Removes a saved voice and its copied recording (spec §5).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteVoice))]
    private void DeleteVoice()
    {
        if (SelectedVoice is not { } voice || _voices is null) return;

        _voices.Delete(voice);
        SelectedVoice = null;
        ReloadVoices();

        // The fields keep whatever they had: the recording is gone from the
        // library, but the user may still be part-way through using it.
        Status = $"Deleted the voice “{voice.Name}”.";
    }

    /// <summary>Listens again, for when the transcript came out wrong.</summary>
    [RelayCommand]
    private Task ListenAgainAsync() => ListenAsync();

    private async Task ListenAsync()
    {
        if (Transcribe is null || !HasReference || IsTranscribing) return;

        IsTranscribing = true;
        Status = "Listening to the recording…";

        try
        {
            ReferenceTranscript = await Transcribe(ReferenceAudioPath!, CancellationToken.None);
            Status = "Ready";
        }
        catch (Exception ex)
        {
            // Not fatal: §4 makes this a convenience, and the field can be
            // typed into. Saying so beats a dialog that stops the work.
            _log.Log($"Could not transcribe the recording: {ex.Message}");
            Status = "Could not make out the recording — type what it says.";
        }
        finally
        {
            IsTranscribing = false;
        }
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
            StopPlayback();
            return;
        }

        IsPlaying = true;
        _player.Play(LastOutputPath);

        // Made here rather than in the constructor: playing is the first moment
        // this is certainly the UI thread.
        _ticker ??= _timers.Create(TimeSpan.FromMilliseconds(100), TickPlayback);
        _ticker.Start();
    }

    /// <summary>
    /// Moves the playback bar, and notices a clip that reached its end.
    /// </summary>
    /// <remarks>
    /// Read from the player rather than counted off a timer started beside it,
    /// so a clip that stalls does not leave the bar advancing over audio that
    /// is not moving. History does the same for its ring.
    /// </remarks>
    internal void TickPlayback()
    {
        if (!IsPlaying)
        {
            _ticker?.Stop();
            return;
        }

        var duration = _player.Duration;
        Elapsed = _player.Position;
        Duration = duration;

        // Clamped: Position can overshoot Duration slightly on the last tick,
        // and an unclamped fraction pushes the fill past the end of the track.
        PlayProgress = duration > TimeSpan.Zero
            ? Math.Clamp(_player.Position / duration, 0, 1)
            : 0;

        if (!_player.IsPlaying && PlayProgress > 0) StopPlayback();
    }

    private void StopPlayback()
    {
        _ticker?.Stop();
        if (_player.IsPlaying) _player.Stop();

        IsPlaying = false;
        PlayProgress = 0;
        Elapsed = TimeSpan.Zero;
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
        Mode == TtsMode.VoiceClone ? null : Instruct,
        Mode == TtsMode.VoiceClone ? ReferenceAudioPath : null,
        Mode == TtsMode.VoiceClone ? ReferenceTranscript : null);

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

    // Everything CurrentRequest reads has to refresh the button, or Generate
    // stays disabled after the very change that made it usable. Choosing a
    // recording in clone mode did exactly that: readiness was satisfied and
    // nothing said so.
    partial void OnScriptChanged(string value) => Refresh();
    partial void OnInstructChanged(string value) => Refresh();
    partial void OnIsBusyChanged(bool value) => Refresh();
    partial void OnLastOutputPathChanged(string? value) => Refresh();
    partial void OnLanguageChanged(string value) => Refresh();
    partial void OnSpeakerChanged(string value) => Refresh();
    partial void OnReferenceAudioPathChanged(string? value) => Refresh();
    partial void OnReferenceTranscriptChanged(string value) => Refresh();

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
        OnPropertyChanged(nameof(ShowSpeakers));
        OnPropertyChanged(nameof(ShowInstruct));
        OnPropertyChanged(nameof(ShowReference));
        OnPropertyChanged(nameof(InstructLabel));
        OnPropertyChanged(nameof(InstructPlaceholder));
        Refresh();
    }

    public void Dispose()
    {
        _engine.StatusChanged -= OnEngineStatusChanged;
        History.Dispose();
        _player.Dispose();
    }
}
