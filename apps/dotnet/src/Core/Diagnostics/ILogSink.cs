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

namespace Bunyi.Core.Diagnostics;

/// <summary>
/// Somewhere to write a line for the user to read later (spec §8).
/// </summary>
/// <remarks>
/// Every class in Core takes one of these by constructor rather than reaching
/// for <see cref="LogStore.Shared"/>, so a test can hand it a recorder and
/// assert on what was said. §8 makes the log part of the product — it is where
/// the full text of an error goes, and what a user is asked to copy into a bug
/// report — so what gets logged is worth testing rather than assuming.
/// </remarks>
public interface ILogSink
{
    /// <summary>
    /// Records one line. Safe to call from any thread, and must not throw:
    /// logging is never the point of the operation it sits inside, and an
    /// exception here would fail work that had otherwise succeeded.
    /// </summary>
    void Log(string message);
}
