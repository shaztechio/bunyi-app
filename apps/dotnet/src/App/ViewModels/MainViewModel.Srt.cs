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
            _srt = new SrtSession(cues);
            SrtRows.Clear();
            foreach (var cue in cues) SrtRows.Add(new SrtCueRow(cue, SyncSrt));
            _srt.Changed += (_, _) => UiThread.Post(RefreshSrtRows);
            SrtChanged();
            RefreshSrtRows();
            Status = $"Imported {cues.Count} cues. Review caption flags, edit times and text, then Generate.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status = ex.Message; }
    }

    private async Task<bool> MayDiscardSrt() => !HasSrtWork || ConfirmDiscardSrt is not null && await ConfirmDiscardSrt();

    [RelayCommand]
    private async Task ClearSrtAsync()
    {
        if (IsBusy || !await MayDiscardSrt()) return;
        StopPlayback();
        _srt?.Dispose();
        _srt = null;
        SrtRows.Clear();
        SrtChanged();
    }

    private void SrtChanged()
    {
        OnPropertyChanged(nameof(HasSrt));
        OnPropertyChanged(nameof(CanImportSrt));
        OnPropertyChanged(nameof(HasSrtWork));
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
        var included = SrtRows.Where(r => r.Included).ToArray();
        var nextStarts = new Dictionary<string, long?>();
        for (var i = 0; i < included.Length; i++)
            nextStarts[included[i].Cue.Id] = i + 1 < included.Length && SrtDocument.TrySamples(included[i + 1].Start, out var start) ? start : null;
        foreach (var row in SrtRows)
            row.Refresh(diagnostics.GetValueOrDefault(row.Cue.Id), _srt.PreviewPath(row.Cue.Id), Language, nextStarts.GetValueOrDefault(row.Cue.Id));
        OnPropertyChanged(nameof(HasSrtWork));
    }
    private void InvalidateSrtVoice()
    {
        if (IsBusy || _srt is null) return;
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
    public SrtCue Cue => _original with { Start = Start, End = End, Text = Text, Included = Included };
    public string SpokenText => Cue.SpokenText;
    public string Flags => Cue.Flags;
    [ObservableProperty] private bool _included;
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
    public SrtCueRow(SrtCue cue, Action changed)
    {
        _original = cue; _changed = changed;
        _included = cue.Included; _start = cue.Start; _end = cue.End; _text = cue.Text;
    }
    private void Changed()
    {
        OnPropertyChanged(nameof(SpokenText)); OnPropertyChanged(nameof(Flags)); _changed();
    }
    partial void OnIncludedChanged(bool value) => Changed();
    partial void OnStartChanged(string value) => Changed();
    partial void OnEndChanged(string value) => Changed();
    partial void OnTextChanged(string value) => Changed();
    public void Refresh(SrtDiagnostic? diagnostic, string? preview, string language, long? nextStart = null)
    {
        Preview = preview;
        HasError = diagnostic?.Status == "error";
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
