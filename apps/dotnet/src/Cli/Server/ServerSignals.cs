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

namespace Bunyi.Cli.Server;

/// <summary>Translate a Linux supervisor's normal stop signal into cooperative shutdown.</summary>
public static class ServerSignals
{
    public static IDisposable RegisterTermination(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        if (!OperatingSystem.IsLinux()) return new NoRegistration();
        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            // Suppress the OS's immediate exit. ServerHost cancels accepted jobs,
            // waits for actual model quiescence, unloads, and then exits normally.
            context.Cancel = true;
            cancellation.Cancel();
        });
    }

    private sealed class NoRegistration : IDisposable { public void Dispose() { } }
}
