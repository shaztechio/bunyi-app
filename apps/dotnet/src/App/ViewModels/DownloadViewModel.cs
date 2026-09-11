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
using CommunityToolkit.Mvvm.Input;

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
    private string _mode = "";
    private DateTimeOffset? _started;
    private DateTimeOffset _fileStarted;
    private readonly List<(DateTimeOffset Time, long Bytes)> _samples = [];
    private bool _reconnectPending;
    public Action? Reconnect { get; set; }
    public IRelayCommand ReconnectCommand { get; }

    public DownloadViewModel()
    {
        ReconnectCommand = new RelayCommand(() =>
        {
            if (!CanReconnect) return;
            _reconnectPending = true;
            OnPropertyChanged(nameof(CanReconnect));
            Reconnect?.Invoke();
        });
    }

    public bool Visible { get; private set; }
    public string Context => string.IsNullOrEmpty(_mode) ? "Speech has not started"
        : $"{_mode} · Speech has not started";
    public string Title => _progress.Phase switch
    {
        DownloadPhase.Resolving => $"Checking the {_resource}",
        DownloadPhase.Manifest => "Finding the model files",
        DownloadPhase.Sizing => "Calculating download size",
        DownloadPhase.Verifying => "Checking model files",
        DownloadPhase.Reconnecting => "Reconnecting — keeping downloaded bytes",
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
    public string OverallLabel => "Overall model download" + (HasOverallTotal ? $" · {DownloadProgress.Bytes(_progress.BytesTotal)}" : "");
    public string FileTotalLabel => HasFileTotal ? $"File size · {DownloadProgress.Bytes(_progress.CurrentFileTotal)}" : "File size unknown";
    public string Elapsed => $"Model download elapsed: {Duration(Math.Max(0, (_now - (_started ?? _now)).TotalSeconds))}";
    private static string Duration(double seconds) => seconds >= 3600
        ? $"{(int)(seconds / 3600)}:{(int)(seconds / 60) % 60:00}:{(int)seconds % 60:00}"
        : $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";
    public double RecentRate => Rate(10);
    private double Rate(double window)
    {
        if (_samples.Count == 0) return 0;
        var baseline = _samples[0];
        foreach (var sample in _samples)
        {
            if (sample.Time > _now.AddSeconds(-window)) break;
            baseline = sample;
        }
        var seconds = (_now - baseline.Time).TotalSeconds;
        return seconds >= 1 ? Math.Max(0, _progress.BytesReceived - baseline.Bytes) / seconds : 0;
    }
    private void Sample()
    {
        if (!Transferring) return;
        _samples.Add((_now, _progress.BytesReceived));
        while (_samples.Count > 1 && _samples[1].Time <= _now.AddSeconds(-30)) _samples.RemoveAt(0);
    }
    public bool Slow => Transferring && !Stalled && (_now - _fileStarted).TotalSeconds >= 30
        && Rate(30) < 256 * 1024
        && (!HasFileTotal || _progress.CurrentFileTotal - _progress.CurrentFileBytes > Math.Max(1, Rate(30)) * 60);
    public bool CanReconnect => !_reconnectPending && Reconnect is not null && (Slow || Stalled);
    public string Health => Slow ? "Download is slow" : "";
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
        : RecentRate > 0
            ? $"{DownloadProgress.Rate(RecentRate)}/s recently · {DownloadProgress.EtaText(HasOverallTotal ? TimeSpan.FromSeconds(Math.Max(0, _progress.BytesTotal - _progress.BytesReceived - _progress.BytesReused) / RecentRate) : null)} to download"
            : "Download time remaining: estimating…";
    public string Announcement => $"{Title}. {Health}. {Receipt}. Overall model download: {OverallPercent}. {LastArrival}.";

    public void Update(DownloadProgress progress, DateTimeOffset now, string resource = "voice model", string mode = "")
    {
        _started ??= now;
        if (progress.Phase != DownloadPhase.Downloading) _reconnectPending = false;
        if (progress.Phase == DownloadPhase.Downloading &&
            (_progress.Phase != DownloadPhase.Downloading || progress.CurrentFile != _progress.CurrentFile
             || progress.BytesReceived < _displayedReceived))
        {
            _samples.Clear();
            _fileStarted = now;
            _samples.Add((now, progress.BytesReceived));
        }
        if (!Visible || progress.BytesReceived < _displayedReceived) _displayedReceived = 0;
        var change = progress.BytesReceived - _displayedReceived;
        if (change > 0) _receipt = change;
        _displayedReceived = progress.BytesReceived;
        _progress = progress;
        _now = now;
        _resource = resource;
        _mode = mode;
        Visible = progress.Phase is not (DownloadPhase.Resolving or DownloadPhase.Done);
        Sample();
        OnPropertyChanged(string.Empty);
    }

    public void Tick(DateTimeOffset now)
    {
        if (!Visible) return;
        _now = now;
        Sample();
        OnPropertyChanged(string.Empty);
    }

    public void Clear()
    {
        Visible = false;
        _displayedReceived = _receipt = 0;
        _started = null;
        _samples.Clear();
        _reconnectPending = false;
        OnPropertyChanged(nameof(Visible));
    }

    private static string Bytes(long available, long total) => total > 0
        ? $"{available:N0} / {total:N0} bytes" : $"{available:N0} bytes available · total size unknown";
}
