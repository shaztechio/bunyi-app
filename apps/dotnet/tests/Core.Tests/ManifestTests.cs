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

using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The path rules are a security boundary: a manifest names its own files and
/// those names become write destinations, from a server the user typed in and
/// nobody audited. Tested exhaustively for that reason.
/// </summary>
public class ManifestPathTests
{
    [Theory]
    [InlineData("config.json")]
    [InlineData("speech_tokenizer/config.json")]
    [InlineData("int4/talker_prefill.onnx.data")]
    [InlineData("a/b/c/d.bin")]
    [InlineData("file with spaces.json")]
    [InlineData("..leading-dots.json")]     // a component that merely starts with dots is fine
    [InlineData("weird...name")]
    public void Ordinary_relative_paths_are_accepted(string entry)
    {
        Assert.True(ManifestPath.TryNormalize(entry, out var safe));
        Assert.Equal(entry, safe);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData("~/.ssh/authorized_keys", "home-relative")]
    [InlineData("../outside.json", "parent")]
    [InlineData("a/../../outside.json", "parent in the middle")]
    [InlineData("./config.json", "current directory")]
    [InlineData("a/./b.json", "current directory in the middle")]
    [InlineData("a//b.json", "empty component")]
    [InlineData("trailing/", "trailing separator names no file")]
    [InlineData("..", "parent alone")]
    [InlineData(".", "current alone")]
    public void Escaping_entries_are_rejected(string entry, string why)
    {
        Assert.False(ManifestPath.TryNormalize(entry, out _), why);
    }

    [Theory]
    [InlineData(@"windows\path.json")]
    [InlineData(@"..\..\outside.json")]
    [InlineData("C:/Windows/System32/x.dll")]
    [InlineData("C:foo.json")]
    [InlineData("stream:name")]
    public void Backslashes_and_colons_are_rejected_on_every_platform(string entry)
    {
        // DATA-FORMATS is explicit that these are rejected everywhere, not only
        // where they are dangerous. Both are legal in a POSIX filename and
        // escape nothing on Linux — but on Windows "\" is a separator,
        // "C:/Windows" is drive-rooted and "C:foo" is drive-RELATIVE. An entry
        // that is inert on one implementation and traverses on another is the
        // failure the shared rule exists to prevent.
        Assert.False(ManifestPath.TryNormalize(entry, out _));
    }

    [Fact]
    public void Resolving_keeps_the_file_inside_the_model_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "bunyi-root");

        Assert.True(ManifestPath.TryResolve(root, "sub/file.json", out var full));
        Assert.StartsWith(Path.GetFullPath(root), full);
        Assert.EndsWith("file.json", full);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/absolute.json")]
    public void Resolving_refuses_anything_that_would_leave_the_folder(string entry)
    {
        var root = Path.Combine(Path.GetTempPath(), "bunyi-root");
        Assert.False(ManifestPath.TryResolve(root, entry, out _));
    }
}

/// <summary>
/// One parser reads both manifest formats, because DATA-FORMATS defines them as
/// the same format with the digest optional.
/// </summary>
public class ManifestParserTests
{
    [Fact]
    public void A_bare_path_list_is_read_as_paths_with_no_digests()
    {
        var result = ManifestParser.Parse("config.json\nmodel.onnx\ntokenizer.json");

        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, f => Assert.Null(f.Sha256));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Digest_lines_are_read_as_a_digest_and_a_path()
    {
        var digest = new string('a', 64);
        var result = ManifestParser.Parse($"{digest}  config.json");

        var file = Assert.Single(result.Files);
        Assert.Equal("config.json", file.RelativePath);
        Assert.Equal(digest, file.Sha256);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    public void Any_whitespace_separates_a_digest_from_its_path(string separator)
    {
        // Not the two spaces shasum happens to write: tools do emit tabs, and a
        // client splitting on a literal space reads such a line as a bare path
        // and skips verification SILENTLY — a file fetched unverified while the
        // manifest looked honoured.
        var digest = new string('b', 64);
        var result = ManifestParser.Parse($"{digest}{separator}model.onnx");

        var file = Assert.Single(result.Files);
        Assert.Equal("model.onnx", file.RelativePath);
        Assert.Equal(digest, file.Sha256);
    }

    [Fact]
    public void A_binary_mode_star_is_stripped_from_the_path()
    {
        // shasum marks a binary read with a leading '*'. Harmless to it, a 404
        // to us.
        var digest = new string('c', 64);
        var result = ManifestParser.Parse($"{digest} *model.onnx");

        Assert.Equal("model.onnx", Assert.Single(result.Files).RelativePath);
    }

    [Fact]
    public void Digests_are_stored_lowercase_so_comparison_is_predictable()
    {
        var result = ManifestParser.Parse($"{new string('A', 64)}  model.onnx");

        Assert.Equal(new string('a', 64), Assert.Single(result.Files).Sha256);
    }

    [Fact]
    public void A_token_that_is_not_exactly_64_hex_digits_makes_the_line_a_bare_path()
    {
        // This is what lets one parser read both files, and what makes a
        // digest-less line legal in either.
        var result = ManifestParser.Parse("abc123  not a digest.json");

        var file = Assert.Single(result.Files);
        Assert.Equal("abc123  not a digest.json", file.RelativePath);
        Assert.Null(file.Sha256);
    }

    [Fact]
    public void Blank_lines_and_comments_are_ignored()
    {
        var result = ManifestParser.Parse("# a comment\n\nconfig.json\n\n   \n# another\nmodel.onnx");

        Assert.Equal(["config.json", "model.onnx"], result.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Carriage_returns_do_not_become_part_of_the_path()
    {
        // A manifest authored on Windows, or served from one.
        var result = ManifestParser.Parse("config.json\r\nmodel.onnx\r\n");

        Assert.Equal(["config.json", "model.onnx"], result.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void Unsafe_entries_are_dropped_and_reported_rather_than_failing_the_manifest()
    {
        // §3b: the download continues, since one bad line should not cost a
        // multi-gigabyte refetch.
        var result = ManifestParser.Parse("config.json\n../escape.json\nmodel.onnx");

        Assert.Equal(["config.json", "model.onnx"], result.Files.Select(f => f.RelativePath));
        Assert.Equal("../escape.json", Assert.Single(result.Rejected));
    }

    [Fact]
    public void An_unsafe_entry_is_rejected_before_its_digest_is_considered()
    {
        var result = ManifestParser.Parse($"{new string('d', 64)}  ../escape.json");

        Assert.Empty(result.Files);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void Paths_differing_only_in_case_are_treated_as_a_duplicate()
    {
        // They resolve to one destination on Windows and two on Linux. The same
        // manifest producing a different model folder per platform is exactly
        // what DATA-FORMATS' interchangeability rests on not happening.
        var result = ManifestParser.Parse("Model.onnx\nmodel.onnx");

        Assert.Equal("Model.onnx", Assert.Single(result.Files).RelativePath);
        Assert.Equal("model.onnx", Assert.Single(result.Rejected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Nothing_in_gives_nothing_out(string? text) =>
        Assert.Empty(ManifestParser.Parse(text).Files);
}
