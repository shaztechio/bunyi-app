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

using Bunyi.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bunyi.App.ViewModels;

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Approach A: measured completion and evidence of receipt are independent.</summary>
public sealed class DownloadViewModel : ObservableObject
{
    private DownloadProgress _progress = new(DownloadPhase.Resolving);
    private DateTimeOffset _now;
    private long _displayedReceived;
    private long _receipt;
    private string _resource = "voice model";

    public bool Visible { get; private set; }
    public string Title => _progress.Phase switch
    {
        DownloadPhase.Resolving => $"Checking the {_resource}",
        DownloadPhase.Manifest => "Finding the model files",
        DownloadPhase.Sizing => "Calculating download size",
        DownloadPhase.Verifying => "Checking model files",
        DownloadPhase.Done => "Model files ready",
        _ => $"Downloading {_resource}",
    };
    public string Explanation => _resource == "transcription model"
        ? "Bunyi saves this model for reuse. Transcription starts automatically after setup."
        : "Bunyi saves this model for reuse. Speech starts automatically after setup.";
    public string Next => _resource == "transcription model"
        ? "Next: Prepare model → Transcribe your reference recording"
        : "Next: Check downloaded files → Load model → Create speech";
    public bool Transferring => _progress.Phase == DownloadPhase.Downloading;
    public bool HasOverallTotal => _progress.BytesTotal > 0;
    public bool HasFileTotal => _progress.CurrentFileTotal > 0;
    public bool HasFile => _progress.CurrentFile is not null;
    public double OverallFraction => _progress.Fraction;
    public double FileFraction => _progress.FileFraction;
    public string OverallPercent => HasOverallTotal ? $"{OverallFraction:P1}" : "Size unknown";
    public string FilePercent => HasFileTotal ? $"{FileFraction:P1}" : "Size unknown";
    public string OverallBytes => Bytes(_progress.BytesReceived + _progress.BytesReused, _progress.BytesTotal);
    public string FileBytes => Bytes(_progress.CurrentFileBytes, _progress.CurrentFileTotal);
    public string FileName => $"Current file · {_progress.CurrentFile}";
    private double QuietSeconds => Math.Max(0, (_now - LatestActivity).TotalSeconds);
    private DateTimeOffset LatestActivity => _progress.LastReceivedAt is { } last
        && (_progress.WaitingSince is not { } start || last >= start) ? last
        : _progress.WaitingSince ?? _now;
    public bool Stalled => Transferring && QuietSeconds >= 30;
    public string Receipt => !Transferring ? Title
        : Stalled ? "Download may be stalled"
        : QuietSeconds >= 3 || _progress.LastReceivedAt is null ? "Waiting for more data"
        : $"Received {_receipt:N0} {(_receipt == 1 ? "byte" : "bytes")}";
    public string LastArrival => !Transferring ? "Speech has not started"
        : _progress.LastReceivedAt is not { } last ? "Waiting for the first download bytes"
        : (_now - last).TotalSeconds < 1 ? "Last data arrived just now"
        : $"Last data arrived {Math.Max(0, (int)(_now - last).TotalSeconds)} seconds ago";
    public string SpeedAndEta => !Transferring ? string.Empty
        : Stalled ? "No data arriving · Download time remaining: unavailable"
        : QuietSeconds >= 3 ? "Download time remaining: estimating…"
        : _progress.BytesPerSecond > 0
            ? $"{DownloadProgress.Rate(_progress.BytesPerSecond)}/s · {DownloadProgress.EtaText(_progress.Eta)} to download"
            : "Download time remaining: estimating…";
    public string Announcement => $"{Title}. {Receipt}. Overall model download: {OverallPercent}. {LastArrival}.";

    public void Update(DownloadProgress progress, DateTimeOffset now, string resource = "voice model")
    {
        if (!Visible || progress.BytesReceived < _displayedReceived) _displayedReceived = 0;
        var change = progress.BytesReceived - _displayedReceived;
        if (change > 0) _receipt = change;
        _displayedReceived = progress.BytesReceived;
        _progress = progress;
        _now = now;
        _resource = resource;
        Visible = progress.Phase is not (DownloadPhase.Resolving or DownloadPhase.Done);
        OnPropertyChanged(string.Empty);
    }

    public void Tick(DateTimeOffset now)
    {
        if (!Visible) return;
        _now = now;
        OnPropertyChanged(nameof(Receipt));
        OnPropertyChanged(nameof(LastArrival));
        OnPropertyChanged(nameof(SpeedAndEta));
        OnPropertyChanged(nameof(Stalled));
    }

    public void Clear()
    {
        Visible = false;
        _displayedReceived = _receipt = 0;
        OnPropertyChanged(nameof(Visible));
    }

    private static string Bytes(long available, long total) => total > 0
        ? $"{available:N0} / {total:N0} bytes" : $"{available:N0} bytes available · total size unknown";
}
