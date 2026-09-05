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

using System.Runtime.InteropServices;

namespace Bunyi.Core.Audio;

/// <summary>Names backend identifiers returned by the native miniaudio library.</summary>
/// <remarks>
/// SoundFlow 1.4.1 puts Null first in its enum; the bundled native library puts
/// it last. Its ActiveBackend contains the native integer, so formatting the
/// managed enum mislabels WASAPI as Null and ALSA as PulseAudio. Passing the
/// managed AvailableBackends list also changes which backends are requested.
/// Query native names and enabled backends directly, without opening a device.
/// </remarks>
internal static class NativeAudioBackends
{
    internal static string Name(int backend) =>
        Marshal.PtrToStringAnsi(GetBackendName(backend)) ?? "Unknown";

    internal static IReadOnlyList<string> EnabledNames()
    {
        // The pinned library has 15 backend IDs. Leave room for additions;
        // a future overflow is a diagnostic failure, not a playback failure.
        var backends = new int[64];
        var result = GetEnabledBackends(backends, (nuint)backends.Length, out var count);
        if (result != 0 || count > (nuint)backends.Length)
            throw new InvalidOperationException($"Could not enumerate audio backends (result: {result}).");

        return backends.Take((int)count).Select(Name).ToArray();
    }

    internal static string Describe(int active) =>
        $"Audio backend: {Name(active)} (enabled: {string.Join(", ", EnabledNames())}).";

    [DllImport("miniaudio", EntryPoint = "ma_get_backend_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetBackendName(int backend);

    [DllImport("miniaudio", EntryPoint = "ma_get_enabled_backends", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetEnabledBackends([Out] int[] backends, nuint capacity, out nuint count);
}
