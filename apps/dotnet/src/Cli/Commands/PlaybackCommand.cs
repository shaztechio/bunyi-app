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

using Bunyi.Cli.Protocol;
using Bunyi.Core.Audio;

namespace Bunyi.Cli.Commands;

public static class PlaybackCommand
{
    public static async Task<Dictionary<string, object?>> ExecuteAsync(CommandRequest request, IFileAudioPlayback player,
        Action<Dictionary<string, object?>> emit, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var duration = await player.PlayAsync(request.Get("target")!, new InlineProgress<AudioPlaybackProgress>(p =>
                emit(CliProtocol.Event(request, "playing", ("positionSeconds", p.PositionSeconds), ("durationSeconds", p.DurationSeconds)))), ct);
            ct.ThrowIfCancellationRequested();
            return CliProtocol.Result(request, ("inputPath", request.Get("target")), ("durationSeconds", duration), ("played", true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new CliException("playback_failed", "Could not play the audio file. Check its format and the default audio output. " + CliLog.Redact(ex.Message), 10);
        }
    }
}
