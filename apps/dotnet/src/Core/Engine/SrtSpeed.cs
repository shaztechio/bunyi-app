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

using Bunyi.Core.Audio;

namespace Bunyi.Core.Engine;

public sealed record SrtSpeedInfo(double Speed, double RequiredSpeed, long OriginalSamples, long CurrentSamples,
    long? AvailableSamples, bool CanFit, bool Adjusted);

public sealed partial class SrtSession
{
    public SrtSpeedInfo? SpeedInfo(string id) => SpeedInfos().GetValueOrDefault(id);
    public IReadOnlyDictionary<string, SrtSpeedInfo> SpeedInfos()
    {
        var included = Cues.Where(c => c.Included).ToArray();
        var deadlines = new Dictionary<string, string>();
        for (var i = 0; i < included.Length; i++) deadlines[included[i].Id] = i + 1 < included.Length ? included[i + 1].Start : included[i].End;
        var result = new Dictionary<string, SrtSpeedInfo>();
        foreach (var cue in Cues)
        {
            if (Get(cue) is not { } clip) continue;
            long? available = null;
            if (cue.Included && SrtDocument.TrySamples(cue.Start, out var start) && SrtDocument.TrySamples(cue.End, out var end) && end > start
                && SrtDocument.TrySamples(deadlines[cue.Id], out var deadline) && deadline > start) available = deadline - start;
            var required = available is { } length ? clip.Samples / (double)length : 1;
            result[cue.Id] = new(clip.Speed, required, clip.Samples, clip.EffectiveSamples, available,
                available is >= 128 && required > 1 && required <= 1.25 && clip.EffectiveSamples != available, clip.Adjustment is not null);
        }
        return result;
    }

    public Task SpeedUpToFitAsync(string id, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SrtDocument.Validate(Cues);
        var info = SpeedInfo(id) ?? throw new InvalidOperationException("Generate this included cue before adjusting its speed.");
        if (!info.CanFit)
            throw new InvalidOperationException(info.RequiredSpeed > 1.25
                ? $"This cue needs {info.RequiredSpeed:0.##########}× speed, above the 1.25× limit. Shorten the text or change its timestamps. The take is unchanged."
                : "This cue does not need speeding up, or its time window is invalid. The take is unchanged.");
        var original = _clips[id];
        var target = info.AvailableSamples!.Value;
        var revision = Interlocked.Read(ref _revision);
        return Task.Run(() =>
        {
            var raw = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".f32");
            var preview = Path.ChangeExtension(raw, ".wav");
            var committed = false;
            try
            {
                var peak = SpeechTimeCompression.Write(original.RawPath, raw, original.Samples, target, ct);
                WritePreview(raw, preview, target, peak, ct);
                ct.ThrowIfCancellationRequested();
                if (revision != Interlocked.Read(ref _revision)
                    || !_clips.TryUpdate(id, original with { Adjustment = new AdjustedClip(raw, preview, target, peak, info.RequiredSpeed) }, original))
                    throw new OperationCanceledException("The cue changed while adjusting speed. Its take has not been replaced.");
                committed = true;
                Interlocked.Increment(ref _revision);
                Interlocked.Increment(ref _version);
                Recheck();
                if (original.Adjustment is { } previous)
                {
                    // A locked obsolete preview must not undo a successfully
                    // committed adjustment. Any leftover is private workspace
                    // data and is removed when the session is disposed.
                    try { File.Delete(previous.RawPath); File.Delete(previous.PreviewPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { if (!committed) { File.Delete(raw); File.Delete(preview); } }
        }, ct);
    }

    public void ResetSpeed(string id)
    {
        if (!_clips.TryGetValue(id, out var clip) || clip.Adjustment is not { } adjustment) return;
        if (!_clips.TryUpdate(id, clip with { Adjustment = null }, clip)) return;
        File.Delete(adjustment.RawPath); File.Delete(adjustment.PreviewPath);
        Interlocked.Increment(ref _revision);
        Interlocked.Increment(ref _version);
        Recheck();
    }
}
