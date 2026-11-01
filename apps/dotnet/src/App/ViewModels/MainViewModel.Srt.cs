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
using Bunyi.Core.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

public sealed partial class MainViewModel
{
    private SrtSession? _srt;
    private string? _srtCueId;
    private string? _srtProjectPath;
    private long _srtSavedVersion;
    private (string? Source, string? Folder) _srtModelContext;
    private (string? Source, string? Folder) CurrentSrtModelContext => (SrtSourceFor?.Invoke(Mode), Settings?.ModelsFolder);
    public string GenerateActionText
    {
        get
        {
            if (!HasSrt) return "Generate";
            var included = SrtRows.Where(r => r.Included).ToArray();
            if (included.Length == 0) return "Check cues";
            var missing = included.Count(r => !r.HasPreview);
            if (included.Any(r => r.HasError) && missing == 0) return "Check timing";
            if (missing == 0) return "Export track";
            return included.Length == missing ? "Generate cues" : $"Resume ({missing} {(missing == 1 ? "cue" : "cues")})";
        }
    }
    public int SrtErrorCount => SrtRows.Count(r => r.Included && r.HasError);
    public bool HasSrtErrors => SrtErrorCount > 0;
    public string SrtErrorSummary => $"{SrtErrorCount} {(SrtErrorCount == 1 ? "error" : "errors")}";
    public string SrtResumeSummary => $"{SrtRows.Count(r => r.Included && r.HasPreview)} cues ready · "
        + $"{SrtRows.Count(r => r.Included && !r.HasPreview)} missing";
    private void RefreshSrtModelContext()
    {
        if (CurrentSrtModelContext != _srtModelContext) InvalidateSrtVoice();
    }
    public bool SrtUnsaved => _srt is not null && (_srtProjectPath is null || _srt.Version != _srtSavedVersion);
    public string SrtProjectLabel => _srtProjectPath is null ? "Unsaved SRT project"
        : Path.GetFileName(_srtProjectPath) + (SrtUnsaved ? " · unsaved changes" : " · saved");
    public Func<TtsMode, string>? SrtSourceFor { get; init; }
    public Func<Task<string?>>? ChooseSrtProject { get; set; }
    public Func<string?, Task<string?>>? ChooseSrtProjectSave { get; set; }
    public ObservableCollection<SrtCueRow> SrtRows { get; } = [];
    public bool HasSrt => _srt is not null && Mode != TtsMode.VoiceDesign;
    public bool CanImportSrt => Mode != TtsMode.VoiceDesign;
    public bool HasSrtWork => _srt?.HasClips == true;
    public Func<Task<string?>>? ChooseSrt { get; set; }
    public Func<Task<bool>>? ConfirmDiscardSrt { get; set; }
    public event EventHandler<SrtCueRow>? SrtErrorFocusRequested;
    [ObservableProperty] private SrtCueRow? _selectedSrtCue;

    [RelayCommand]
    private async Task ImportSrtAsync()
    {
        if (IsBusy || !CanImportSrt || ChooseSrt is null) return;
        var file = await ChooseSrt();
        if (file is null) return;
        try
        {
            var cues = await SrtDocument.ReadAsync(file);
            if (!await MayDiscardSrt()) return;
            StopPlayback();
            _srt?.Dispose();
            AttachSrt(new SrtSession(cues), null);
            Status = $"Imported {cues.Count} cues. Review caption flags, edit times and text, then Generate.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status = ex.Message; }
    }

    private async Task<bool> MayDiscardSrt() => !SrtUnsaved || ConfirmDiscardSrt is not null && await ConfirmDiscardSrt();

    private void AttachSrt(SrtSession session, string? projectPath)
    {
        _srt = session;
        _srtProjectPath = projectPath;
        _srtSavedVersion = session.Version;
        _srtModelContext = CurrentSrtModelContext;
        SrtRows.Clear();
        foreach (var cue in session.Cues) SrtRows.Add(new SrtCueRow(cue, SyncSrt));
        session.Changed += (_, _) => UiThread.Post(RefreshSrtRows);
        SrtChanged();
        RefreshSrtRows();
    }

    [RelayCommand]
    private Task SaveSrtProjectAsync() => SaveSrtProject(false);
    [RelayCommand]
    private Task SaveSrtProjectAsAsync() => SaveSrtProject(true);
    private async Task SaveSrtProject(bool saveAs)
    {
        if (IsBusy || _srt is null || !HasSrt) return;
        var path = saveAs ? null : _srtProjectPath;
        path ??= ChooseSrtProjectSave is null ? null : await ChooseSrtProjectSave(_srtProjectPath);
        if (path is null) return;
        using var cancellation = new CancellationTokenSource();
        _generateCancellation = cancellation;
        IsBusy = true;
        Status = "Saving SRT project and cue takes…";
        try
        {
            await _srt.SaveProjectAsync(path, CurrentRequest(), SrtSourceFor?.Invoke(Mode), cancellation.Token);
            _srtProjectPath = path;
            _srtSavedVersion = _srt.Version;
            Status = "Project saved. You can close Bunyi and resume from this file.";
        }
        catch (OperationCanceledException) { Status = "Save stopped; the previous project file is unchanged."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { Status = "Could not save the project. " + ex.Message; }
        finally { _generateCancellation = null; IsBusy = false; RefreshSrtRows(); }
    }

    [RelayCommand]
    private async Task OpenSrtProjectAsync()
    {
        if (IsBusy || ChooseSrtProject is null) return;
        var path = await ChooseSrtProject();
        if (path is null) return;
        using var cancellation = new CancellationTokenSource();
        _generateCancellation = cancellation;
        IsBusy = true;
        Status = "Opening SRT project and checking saved takes…";
        SrtProjectLoad? opened = null;
        try
        {
            opened = await SrtSession.OpenProjectAsync(path, cancellation.Token);
            if (!await MayDiscardSrt()) return;
            StopPlayback();
            _srt?.Dispose();
            _srt = null;
            var request = opened.Request;
            Mode = request.Mode;
            Language = request.Language;
            Speaker = request.Speaker ?? FallbackSpeakers.Default;
            SelectedVoice = null;
            Instruct = request.Instruct ?? "";
            ReferenceAudioPath = request.ReferenceAudioPath;
            ReferenceTranscript = request.ReferenceTranscript ?? "";
            ReferenceTranscriptAudioSeconds = request.ReferenceTranscriptAudioSeconds;
            LastOutputPath = null;
            AttachSrt(opened.Session, path);
            var savedSource = opened.ModelSource;
            opened = null;
            Status = "Project opened. Completed cue takes are restored; Generate reuses matching takes.";
            if (savedSource is not null && savedSource != SrtSession.SafeModelSource(SrtSourceFor?.Invoke(Mode)))
                Status += " The current model source differs. Choose the original model in Settings to reuse these takes; otherwise Generate replaces them.";
        }
        catch (OperationCanceledException) { Status = "Open stopped. The current project is unchanged."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { Status = "Could not open the project. " + ex.Message; }
        finally { opened?.Session.Dispose(); _generateCancellation = null; IsBusy = false; RefreshSrtRows(); }
    }

    [RelayCommand]
    private async Task ClearSrtAsync()
    {
        if (IsBusy || !await MayDiscardSrt()) return;
        StopPlayback();
        _srt?.Dispose();
        _srt = null;
        _srtProjectPath = null;
        SrtRows.Clear();
        SrtChanged();
    }

    private void SrtChanged()
    {
        OnPropertyChanged(nameof(HasSrt));
        OnPropertyChanged(nameof(CanImportSrt));
        OnPropertyChanged(nameof(HasSrtWork));
        OnPropertyChanged(nameof(SrtUnsaved));
        OnPropertyChanged(nameof(SrtProjectLabel));
        OnPropertyChanged(nameof(GenerateActionText));
        OnPropertyChanged(nameof(SrtResumeSummary));
        OnPropertyChanged(nameof(SrtErrorCount));
        OnPropertyChanged(nameof(HasSrtErrors));
        OnPropertyChanged(nameof(SrtErrorSummary));
        Refresh();
    }
    private void SyncSrt()
    {
        if (_srt is null || IsBusy) return;
        StopPlayback();
        _srt.Update(SrtRows.Select(r => r.Cue).ToArray());
        LastOutputPath = null;
        RefreshSrtRows();
    }
    private void RefreshSrtRows()
    {
        if (_disposed || _srt is null) return;
        var diagnostics = _srt.Diagnostics.ToDictionary(d => d.Id);
        var speeds = _srt.SpeedInfos();
        var included = SrtRows.Where(r => r.Included).ToArray();
        var nextStarts = new Dictionary<string, long?>();
        for (var i = 0; i < included.Length; i++)
            nextStarts[included[i].Cue.Id] = i + 1 < included.Length && SrtDocument.TrySamples(included[i + 1].Start, out var start) ? start : null;
        foreach (var row in SrtRows)
            row.Refresh(diagnostics.GetValueOrDefault(row.Cue.Id), _srt.PreviewPath(row.Cue.Id), Language,
                nextStarts.GetValueOrDefault(row.Cue.Id), speeds.GetValueOrDefault(row.Cue.Id));
        OnPropertyChanged(nameof(HasSrtWork));
        OnPropertyChanged(nameof(SrtUnsaved));
        OnPropertyChanged(nameof(SrtProjectLabel));
        OnPropertyChanged(nameof(GenerateActionText));
        OnPropertyChanged(nameof(SrtResumeSummary));
        OnPropertyChanged(nameof(SrtErrorCount));
        OnPropertyChanged(nameof(HasSrtErrors));
        OnPropertyChanged(nameof(SrtErrorSummary));
    }
    private void InvalidateSrtVoice()
    {
        if (IsBusy || _srt is null) return;
        _srtModelContext = CurrentSrtModelContext;
        StopPlayback();
        _srt.InvalidateAll();
        LastOutputPath = null;
        RefreshSrtRows();
    }

    [RelayCommand]
    private void PreviewSrt(SrtCueRow? row)
    {
        if (IsBusy || row?.Preview is not { } path) return;
        StopPlayback();
        _player.Play(path);
    }

    [RelayCommand]
    private async Task RegenerateSrtAsync(SrtCueRow? row)
    {
        if (IsBusy || row is null || !row.Included || _srt is null) return;
        StopPlayback();
        _srt.Invalidate(row.Cue.Id);
        _srtCueId = row.Cue.Id;
        try { await GenerateAsync(); }
        finally { _srtCueId = null; }
    }

    [RelayCommand]
    private async Task SpeedUpSrtAsync(SrtCueRow? row)
    {
        if (IsBusy || row is null || _srt is null) return;
        StopPlayback();
        using var cancellation = new CancellationTokenSource();
        _generateCancellation = cancellation;
        IsBusy = true;
        _engine.ClearLastOutput();
        LastOutputPath = null;
        Status = $"Speeding up cue {row.Number} without regenerating speech…";
        try
        {
            await _srt.SpeedUpToFitAsync(row.Cue.Id, cancellation.Token);
            Status = $"Cue {row.Number} adjusted to {_srt.SpeedInfo(row.Cue.Id)!.Speed:0.00}×. Preview it, then export when all cues fit.";
        }
        catch (OperationCanceledException) { Status = "Speed adjustment stopped. The previous take is unchanged."; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        { Status = ex.Message; }
        finally { _generateCancellation = null; IsBusy = false; RefreshSrtRows(); }
    }

    [RelayCommand]
    private void ResetSrtSpeed(SrtCueRow? row)
    {
        if (IsBusy || row is null || _srt is null) return;
        StopPlayback();
        _srt.ResetSpeed(row.Cue.Id);
        LastOutputPath = null;
        Status = $"Cue {row.Number} restored to its original speed. Timing has been rechecked.";
        RefreshSrtRows();
    }

    [RelayCommand]
    private void FirstSrtError()
    {
        SelectedSrtCue = SrtRows.FirstOrDefault(r => r.HasError);
        if (SelectedSrtCue is not null)
        {
            Status = $"Cue {SelectedSrtCue.Number}: {SelectedSrtCue.StatusText}";
            SrtErrorFocusRequested?.Invoke(this, SelectedSrtCue);
        }
    }
}

public sealed partial class SrtCueRow : ObservableObject
{
    private readonly SrtCue _original;
    private readonly Action _changed;
    public string Number => _original.Number;
    public override string ToString() => $"Cue {Number}";
    public SrtCue Cue => _original with { Start = Start, End = End, Text = Text, Included = Included,
        AllowSpeedLimitOverride = AllowSpeedLimitOverride };
    public string SpokenText => Cue.SpokenText;
    public string Flags => Cue.Flags;
    [ObservableProperty] private bool _included;
    [ObservableProperty] private bool _allowSpeedLimitOverride;
    [ObservableProperty] private string _start;
    [ObservableProperty] private string _end;
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _statusText = "Not generated";
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string? _preview;
    public bool HasPreview => Preview is not null;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canSpeedUp;
    [ObservableProperty] private bool _hasSpeedAdjustment;
    [ObservableProperty] private string _speedDescription = "";
    public SrtCueRow(SrtCue cue, Action changed)
    {
        _original = cue; _changed = changed;
        _included = cue.Included; _start = cue.Start; _end = cue.End; _text = cue.Text;
        _allowSpeedLimitOverride = cue.AllowSpeedLimitOverride;
    }
    private void Changed()
    {
        OnPropertyChanged(nameof(SpokenText)); OnPropertyChanged(nameof(Flags)); _changed();
    }
    partial void OnIncludedChanged(bool value) => Changed();
    partial void OnAllowSpeedLimitOverrideChanged(bool value) => Changed();
    partial void OnStartChanged(string value) => Changed();
    partial void OnEndChanged(string value) => Changed();
    partial void OnTextChanged(string value) => Changed();
    public void Refresh(SrtDiagnostic? diagnostic, string? preview, string language, long? nextStart = null, SrtSpeedInfo? speed = null)
    {
        Preview = preview;
        HasError = diagnostic?.Status == "error";
        CanSpeedUp = speed?.CanFit == true;
        HasSpeedAdjustment = speed?.Adjusted == true;
        SpeedDescription = speed is null ? "" : speed.Adjusted
            ? $"Speed {speed.Speed:0.00}× · {speed.OriginalSamples / 24000d:0.000}s original → {speed.CurrentSamples / 24000d:0.000}s current. Preview to check quality."
            : "Original speed (1×)";
        if (AllowSpeedLimitOverride)
        {
            SpeedDescription += " · Override enabled. High speeds may sound unnatural or unintelligible. Preview before exporting.";
            if (speed?.CanFit == true) SpeedDescription += $" Fit requires {speed.RequiredSpeed:0.00}×.";
        }
        else if (speed?.RequiredSpeed > 1.25) SpeedDescription += $" · Needs {speed.RequiredSpeed:0.00}× to fit. Enable this cue's override to exceed 1.25×.";
        else if (speed?.CanFit == true) SpeedDescription += $" · Fit requires {speed.RequiredSpeed:0.00}× (maximum 1.25×).";
        StatusText = !Included ? "Excluded — no speech" : diagnostic is null ? "Not generated"
            : $"{diagnostic.Status}: {diagnostic.Message}" + (diagnostic.DurationSeconds is { } d ? $" · {d:0.000}s complete audio" : "");
        var estimate = SpeechDurationEstimate.ForText(SpokenText, language);
        Estimate = $"Estimated {estimate.LowerSeconds:0.0}–{estimate.UpperSeconds:0.0}s (advisory)";
        if (Included && SrtDocument.TrySamples(Start, out var start) && SrtDocument.TrySamples(End, out var end))
        {
            if (nextStart is { } next && estimate.UpperSeconds > (next - start) / 24000d)
                Estimate += " · may overlap the next cue";
            else if (estimate.UpperSeconds > (end - start) / 24000d)
                Estimate += " · may exceed its subtitle end";
        }
    }
}
