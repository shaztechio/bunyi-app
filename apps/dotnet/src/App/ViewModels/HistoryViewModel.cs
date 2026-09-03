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
using Avalonia.Threading;
using Bunyi.App.Infrastructure;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

/// <summary>One row in History (spec §2a).</summary>
public sealed partial class HistoryRow(GeneratedOutput output) : ObservableObject
{
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playProgress;
    [ObservableProperty] private bool _copied;

    public GeneratedOutput Output { get; } = output;

    public string Path => Output.Path;
    public string Summary => Output.Summary();
    public string Mode => Output.Mode;
    public string SizeText => Output.SizeText();
    public string Details => Output.Details();

    /// <summary>
    /// The row as one sentence, for a screen reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mode first, because it is the category; then what was said; then who,
    /// when and how big. The same three things the eye reads left to right and
    /// top to bottom, in that order, as one announced item (spec §12).
    /// </para>
    /// <para>
    /// The full stop after the summary is only added when the summary has not
    /// already ended the sentence itself. Reading the real tree with
    /// <c>tools/UiaProbe</c> is what turned this up: rows were announcing
    /// "…in just a few minutes.. Serena" and "…by the sea…. Ryan", because the
    /// separator was unconditional and most scripts end in punctuation.
    /// </para>
    /// </remarks>
    public string AccessibleName =>
        $"{Mode}: {Summary}{(EndsASentence(Summary) ? "" : ".")} {Subtitle}";

    private static bool EndsASentence(string text) =>
        text.Length > 0 && ".!?…:;".Contains(text[^1], StringComparison.Ordinal);

    /// <summary>The single line beneath the summary: voice, date and size.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Output.Voice is { Length: > 0 } voice) parts.Add(voice);
            parts.Add(Output.Created.ToLocalTime().ToString("d MMM yyyy, HH:mm"));
            parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// History: everything generated so far (spec §2a).
/// </summary>
/// <remarks>
/// <para>
/// The list is read from the folder each time it is shown, so a file deleted
/// outside the app disappears and nothing has to be migrated between versions.
/// </para>
/// <para>
/// It stays usable while a generation is running — it only reads the folder.
/// The generation modes do not, because switching one evicts the model the
/// running job is using.
/// </para>
/// </remarks>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly IAudioPlayer _player;
    private readonly ILogSink _log;
    private readonly Func<string> _outputFolder;
    private readonly DispatcherTimer _ticker;

    [ObservableProperty] private HistoryRow? _playing;

    public HistoryViewModel(IAudioPlayer player, ILogSink log, Func<string> outputFolder)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _outputFolder = outputFolder ?? throw new ArgumentNullException(nameof(outputFolder));

        _player.Finished += OnPlaybackFinished;

        // Drives the ring. Polled rather than driven by the player, which
        // reports position but not a change event.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ticker.Tick += (_, _) => Tick();
    }

    /// <summary>The rows, newest first.</summary>
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    /// <summary>Whether the folder had nothing in it.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>What to say when it does.</summary>
    public string EmptyMessage => "Nothing generated yet. Anything you make will appear here.";

    /// <summary>Re-reads the folder. Called every time History is shown.</summary>
    public void Refresh()
    {
        var wasPlaying = Playing?.Path;

        Rows.Clear();
        foreach (var output in GeneratedOutputs.Read(_outputFolder()))
        {
            var row = new HistoryRow(output);
            if (row.Path == wasPlaying)
            {
                // Keep the ring on the row that is still playing.
                row.IsPlaying = true;
                Playing = row;
            }
            Rows.Add(row);
        }

        if (Playing is not null && !Rows.Contains(Playing)) StopPlayback();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Play, or stop if this row is already playing (spec §2a).
    /// </summary>
    /// <remarks>
    /// Play/stop, with no pause: these are short clips, and a paused row is a
    /// third state to explain for something a user would nearly always just
    /// play again.
    /// </remarks>
    [RelayCommand]
    private void TogglePlay(HistoryRow? row)
    {
        if (row is null) return;

        if (Playing == row)
        {
            StopPlayback();
            return;
        }

        StopPlayback();

        row.IsPlaying = true;
        row.PlayProgress = 0;
        Playing = row;
        _player.Play(row.Path);
        _ticker.Start();
    }

    [RelayCommand]
    private void Reveal(HistoryRow? row)
    {
        if (row is not null) FileReveal.Reveal(row.Path, _log);
    }

    /// <summary>
    /// Puts the whole record on the clipboard (spec §2a).
    /// </summary>
    /// <remarks>
    /// Hover is for looking; a tooltip cannot be pasted into a note, a bug
    /// report, or back into the app to reproduce a result. The button
    /// acknowledges the copy, because one that appears to do nothing gets
    /// pressed again.
    /// </remarks>
    [RelayCommand]
    private async Task CopyDetailsAsync(HistoryRow? row)
    {
        if (row is null) return;

        var clipboard = Clipboard;
        if (clipboard is null)
        {
            _log.Log("There is no clipboard available to copy to.");
            return;
        }

        // Avalonia 12 replaced SetTextAsync with a data-transfer object.
        var transfer = new Avalonia.Input.DataTransfer();
        transfer.Add(Avalonia.Input.DataTransferItem.CreateText(row.Details));
        await clipboard.SetDataAsync(transfer);
        row.Copied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        row.Copied = false;
    }

    /// <summary>
    /// Moves a clip to the Trash, after confirming (spec §2a).
    /// </summary>
    /// <remarks>
    /// Recoverable rather than an unrecoverable delete: the row label is
    /// truncated so the wrong icon is easy to hit, and the audio may be the
    /// only copy. Confirming is delegated to the view, which is the thing that
    /// can show a dialog — and refusing to act without a confirmer means a
    /// misconfigured host cannot silently delete a user's audio.
    /// </remarks>
    [RelayCommand]
    private async Task TrashAsync(HistoryRow? row)
    {
        if (row is null) return;

        if (ConfirmTrash is null)
        {
            _log.Log("Not moving anything to the Trash: nothing is available to confirm it.");
            return;
        }

        if (!await ConfirmTrash(row)) return;

        if (Playing == row) StopPlayback();
        if (Core.Platform.Trash.TryMoveToTrash(row.Path, _log)) Refresh();
    }

    /// <summary>
    /// Saves a copy wherever the user chooses (spec §2a).
    /// </summary>
    /// <remarks>
    /// A save panel rather than a fixed destination: on a sandboxed platform
    /// the choice is also what grants permission to write there, so the two
    /// apps behave the same way for the same reason.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadAsync(HistoryRow? row)
    {
        if (row is null || ChooseSaveLocation is null) return;

        var destination = await ChooseSaveLocation(row);
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            File.Copy(row.Path, destination, overwrite: true);
            _log.Log($"Saved a copy to {destination}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log($"Could not save a copy to {destination}: {ex.Message}");
        }
    }

    /// <summary>The clipboard, supplied by the view.</summary>
    public Avalonia.Input.Platform.IClipboard? Clipboard { get; set; }

    /// <summary>Asks the user to confirm a trash, supplied by the view.</summary>
    public Func<HistoryRow, Task<bool>>? ConfirmTrash { get; set; }

    /// <summary>Asks the user where to save a copy, supplied by the view.</summary>
    public Func<HistoryRow, Task<string?>>? ChooseSaveLocation { get; set; }

    private void Tick()
    {
        if (Playing is null)
        {
            _ticker.Stop();
            return;
        }

        var duration = _player.Duration;
        Playing.PlayProgress = duration > TimeSpan.Zero
            ? Math.Clamp(_player.Position / duration, 0, 1)
            : 0;

        // A clip that reaches its end returns the row to Play on its own.
        if (!_player.IsPlaying && Playing.PlayProgress > 0) StopPlayback();
    }

    private void OnPlaybackFinished(object? sender, EventArgs e) =>
        UiThread.Post(StopPlayback);

    private void StopPlayback()
    {
        _ticker.Stop();
        if (Playing is not null)
        {
            Playing.IsPlaying = false;
            Playing.PlayProgress = 0;
            Playing = null;
        }
        if (_player.IsPlaying) _player.Stop();
    }

    public void Dispose()
    {
        _player.Finished -= OnPlaybackFinished;
        _ticker.Stop();
    }
}
