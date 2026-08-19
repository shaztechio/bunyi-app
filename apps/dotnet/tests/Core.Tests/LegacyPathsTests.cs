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
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Moving the Whisper model in beside the others.
/// </summary>
/// <remarks>
/// It was fetched into a corner of the models folder — invisible to
/// Settings ▸ Storage, which lists <c>models/&lt;org&gt;/&lt;repo&gt;</c>, so
/// 141 MB could be neither seen nor deleted. Noticed by asking why Whisper was
/// not in that list.
/// </remarks>
public sealed class LegacyPathsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public LegacyPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void It_moves_the_model_in_with_the_others()
    {
        Old("ggerganov/whisper.cpp", "ggml-base.bin", "weights");

        Assert.True(LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog()));

        Assert.True(File.Exists(New("ggerganov/whisper.cpp", "ggml-base.bin")));
        Assert.Equal("weights", File.ReadAllText(New("ggerganov/whisper.cpp", "ggml-base.bin")));
    }

    [Fact]
    public void The_old_folder_is_gone_afterwards()
    {
        // Otherwise Storage still cannot see it and the disk still holds it.
        Old("ggerganov/whisper.cpp", "ggml-base.bin", "weights");

        LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog());

        Assert.False(Directory.Exists(Path.Combine(_root, "whisper")));
    }

    [Fact]
    public void A_folder_that_was_never_there_is_not_a_problem()
    {
        Assert.False(LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog()));
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        Old("ggerganov/whisper.cpp", "ggml-base.bin", "weights");

        Assert.True(LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog()));
        Assert.False(LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog()));

        Assert.True(File.Exists(New("ggerganov/whisper.cpp", "ggml-base.bin")));
    }

    [Fact]
    public void A_model_already_in_the_right_place_wins()
    {
        // The copy in the corner is the spare, and keeping both would leave the
        // 141 MB this is meant to reclaim.
        Old("ggerganov/whisper.cpp", "ggml-base.bin", "the old one");

        var current = New("ggerganov/whisper.cpp", "ggml-base.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "the one in use");

        LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog());

        Assert.Equal("the one in use", File.ReadAllText(current));
        Assert.False(Directory.Exists(Path.Combine(_root, "whisper")));
    }

    [Fact]
    public void Anything_unexpected_in_there_is_left_alone()
    {
        // This tidies one known mistake; it is not licence to delete a folder
        // whose contents nobody recognises.
        Old("ggerganov/whisper.cpp", "ggml-base.bin", "weights");

        var stray = Path.Combine(_root, "whisper", "models", "notes.txt");
        File.WriteAllText(stray, "someone put this here");

        LegacyPaths.MoveMisplacedWhisper(_root, new QuietLog());

        Assert.True(File.Exists(stray));
    }

    private void Old(string repo, string file, string content)
    {
        var path = Path.Combine(
            _root, "whisper", "models",
            repo.Replace('/', Path.DirectorySeparatorChar), file);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string New(string repo, string file) => Path.Combine(
        _root, "models", repo.Replace('/', Path.DirectorySeparatorChar), file);

    private sealed class QuietLog : ILogSink
    {
        public void Log(string message) { }
    }
}
