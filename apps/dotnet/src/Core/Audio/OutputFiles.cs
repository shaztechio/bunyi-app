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

using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Platform;

namespace Bunyi.Core.Audio;

/// <summary>Companions are located by the WAV path, never by untrusted JSON.</summary>
public static class OutputFiles
{
    public static bool TryTrash(string wav, ILogSink log)
    {
        if (!Trash.TryMoveToTrash(wav, log)) return false;
        var sidecar = SrtSession.SidecarPath(wav);
        if (File.Exists(sidecar) && !Trash.TryMoveToTrash(sidecar, log))
            log.Log("The audio was moved to Trash, but its timeline remains beside the original path.");
        return true;
    }

    public static void Copy(string source, string destination)
    {
        var sourceSidecar = SrtSession.SidecarPath(source);
        var destinationSidecar = SrtSession.SidecarPath(destination);
        File.Copy(source, destination, overwrite: true);
        if (File.Exists(sourceSidecar)) File.Copy(sourceSidecar, destinationSidecar, overwrite: true);
        else if (File.Exists(destinationSidecar)) File.Delete(destinationSidecar); // Do not keep a stale timeline.
    }
}
