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
/// Opening a credits link (spec §9a).
/// </summary>
/// <remarks>
/// Launching a browser cannot be asserted on in CI. Choosing what to launch can
/// be, and that is where the mistakes live — so the choice is a pure function
/// and this is all about what it refuses.
/// </remarks>
public sealed class WebLinkTests
{
    [Fact]
    public void An_https_link_is_opened()
    {
        Assert.True(WebLink.IsSafe("https://github.com/shaztechio/bunyi-app"));
        Assert.NotNull(WebLink.CommandFor("https://github.com/shaztechio/bunyi-app"));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("//evil.example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_an_https_link_is_refused(string? url)
    {
        // This string reaches a shell handler. Everything the app opens is a
        // constant compiled into it, so nothing hostile should ever arrive —
        // but that sentence is exactly the kind that stops being true later.
        Assert.False(WebLink.IsSafe(url));
        Assert.Null(WebLink.CommandFor(url));
    }

    [Fact]
    public void Opening_a_refused_link_does_nothing_and_says_why()
    {
        var log = new RecordingLog();

        WebLink.Open("file:///etc/passwd", log);

        Assert.Contains(log.Lines, l => l.Contains("not https", StringComparison.Ordinal));
    }

    [Fact]
    public void The_platform_handler_is_the_one_the_user_already_chose()
    {
        // Not a browser this app picks. Whatever the system opens links with.
        var command = WebLink.CommandFor("https://example.com/a");

        Assert.NotNull(command);

        var expected = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open"
            : "xdg-open";

        Assert.Equal(expected, command!.FileName);
        Assert.Equal("https://example.com/a", Assert.Single(command.Arguments));
    }

    [Fact]
    public void The_url_is_passed_as_one_argument_rather_than_a_command_line()
    {
        // Built as an argument list, so a URL containing a space or a quote is
        // one argument and not an opportunity.
        var command = WebLink.CommandFor("https://example.com/search?q=a%20b&x=1");

        Assert.NotNull(command);
        Assert.Single(command!.Arguments);
        Assert.DoesNotContain(" ", command.FileName, StringComparison.Ordinal);
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Log(string message) => Lines.Add(message);
    }
}
