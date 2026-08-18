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

using Bunyi.Core;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Spec §3a: "Scheme decides: http://|https:// => base URL, else repo ID.
/// Blank => the built-in default for that mode."
///
/// ModelSource.Parse was the only working logic in the scaffold, so it is what
/// the test project is proved against. The rule is worth pinning: a value that
/// is read as a repo ID when the user meant a URL sends the app to the Hub to
/// look up a repo named "https", and the error that follows says nothing about
/// the real mistake.
/// </summary>
public class ModelSourceTests
{
    private const string Default = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Blank_falls_back_to_the_mode_default(string value)
    {
        var source = ModelSource.Parse(value, Default);

        var repo = Assert.IsType<ModelSource.Repo>(source);
        Assert.Equal(Default, repo.Id);
    }

    [Theory]
    [InlineData("https://models.example.com/customvoice")]
    [InlineData("http://192.168.1.10:8080/models")]
    [InlineData("HTTPS://Models.Example.COM/x")]   // scheme test is case-insensitive
    public void An_http_scheme_means_a_self_hosted_base_url(string value)
    {
        var source = ModelSource.Parse(value, Default);

        var baseUrl = Assert.IsType<ModelSource.BaseUrl>(source);
        Assert.Equal(value.Trim(), baseUrl.Url.OriginalString);
    }

    [Theory]
    [InlineData("elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX")]
    [InlineData("wavekat/Qwen3-TTS-0.6B-Base-ONNX")]
    [InlineData("ftp://example.com/models")]       // only http(s) is a base URL
    [InlineData("models.example.com/customvoice")] // a host with no scheme is not one either
    public void Anything_else_is_a_repo_id(string value)
    {
        var source = ModelSource.Parse(value, Default);

        var repo = Assert.IsType<ModelSource.Repo>(source);
        Assert.Equal(value, repo.Id);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        var source = ModelSource.Parse("  some-org/some-repo\t", Default);

        var repo = Assert.IsType<ModelSource.Repo>(source);
        Assert.Equal("some-org/some-repo", repo.Id);
    }
}
