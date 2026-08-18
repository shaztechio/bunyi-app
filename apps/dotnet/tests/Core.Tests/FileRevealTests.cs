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
using Bunyi.Core.Platform;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// A file manager cannot be launched in CI, so what is tested is the thing that
/// actually goes wrong: the arguments. Reveal is a one-click action in §2 and
/// §2a, and its failures are quiet — the wrong argument opens the wrong folder
/// rather than reporting anything.
/// </summary>
public class FileRevealTests
{
    [Fact]
    public void The_command_names_the_platform_file_manager()
    {
        var command = FileReveal.CommandFor(Path.GetTempPath());

        var expected =
            OperatingSystem.IsWindows() ? "explorer.exe" :
            OperatingSystem.IsMacOS() ? "open" :
            "dbus-send";

        Assert.Equal(expected, command.FileName);
    }

    [Fact]
    public void The_command_carries_an_absolute_path()
    {
        var command = FileReveal.CommandFor(".");

        Assert.All(command.Arguments, argument => Assert.DoesNotContain("/./", argument));
        Assert.Contains(command.Arguments, a => a.Contains(Path.GetFullPath(".").Replace('\\', '/'))
                                             || a.Contains(Path.GetFullPath(".")));
    }

    [Fact]
    public void On_Windows_select_has_no_space_after_the_comma()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), "clip.wav");
        var command = FileReveal.CommandFor(path);

        // "/select, <path>" makes explorer open Documents instead of selecting
        // the file: a silent wrong answer, not an error.
        var argument = Assert.Single(command.Arguments);
        Assert.StartsWith("/select,", argument);
        Assert.DoesNotContain("/select, ", argument);
        Assert.EndsWith("clip.wav", argument);
    }

    [Fact]
    public void On_Linux_the_freedesktop_interface_is_asked_to_select_the_item()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(Path.GetTempPath(), "clip.wav");
        var command = FileReveal.CommandFor(path);

        Assert.Contains("--dest=org.freedesktop.FileManager1", command.Arguments);
        Assert.Contains("org.freedesktop.FileManager1.ShowItems", command.Arguments);
        Assert.Contains(command.Arguments, a => a.StartsWith("array:string:file://", StringComparison.Ordinal));
    }

    [Fact]
    public void On_Linux_a_path_with_spaces_is_escaped_into_the_uri()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(Path.GetTempPath(), "my clip.wav");
        var command = FileReveal.CommandFor(path);

        var uri = Assert.Single(command.Arguments, a => a.StartsWith("array:string:", StringComparison.Ordinal));
        Assert.Contains("my%20clip.wav", uri);
        Assert.DoesNotContain("my clip.wav", uri);
    }

    [Fact]
    public void Only_the_platform_that_cannot_select_has_a_fallback()
    {
        var fallback = FileReveal.FallbackFor(Path.Combine(Path.GetTempPath(), "clip.wav"));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Null(fallback);   // both select the file outright
            return;
        }

        Assert.NotNull(fallback);
        Assert.Equal("xdg-open", fallback.FileName);
        // The folder, not the file: xdg-open on a .wav would play it.
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                     Assert.Single(fallback.Arguments).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Revealing_something_that_is_not_there_reports_rather_than_throws()
    {
        var log = new RecordingLog();

        var revealed = FileReveal.Reveal(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gone.wav"), log);

        Assert.False(revealed);
        Assert.Contains(log.Lines, l => l.Contains("not there"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => FileReveal.CommandFor(path));

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];
        public void Log(string message) => Lines.Add(message);
    }
}
