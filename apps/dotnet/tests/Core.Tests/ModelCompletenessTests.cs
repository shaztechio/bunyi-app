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
/// The ONNX family's completeness rule from /spec/DATA-FORMATS.md.
/// </summary>
/// <remarks>
/// This decides whether the app goes to the network. Getting it wrong in one
/// direction re-downloads gigabytes on every launch; in the other it loads a
/// half-downloaded model and fails at inference, pointing at nothing.
/// </remarks>
public sealed class ModelCompletenessTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private static ModelLayout Layout { get; } = new(
        "test",
        [
            new ModelFile("embeddings/config.json", Required: true),
            new ModelFile("model.onnx", Required: true),
            new ModelFile("model.onnx.data", Required: true),
            new ModelFile("tokenizer/vocab.json"),
        ]);

    public ModelCompletenessTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private void Write(string relative, int bytes = 16)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private void WriteAllRequired()
    {
        Write("embeddings/config.json");
        Write("model.onnx");
        Write("model.onnx.data", 1024);
    }

    [Fact]
    public void A_folder_with_every_required_file_is_complete()
    {
        WriteAllRequired();

        Assert.True(ModelDownloader.Inspect(_folder, Layout).IsComplete);
    }

    [Fact]
    public void An_optional_file_missing_does_not_make_a_model_incomplete()
    {
        // Deliberate: single-shard repos lack an index, and an absent tokenizer
        // is backfilled. Treating those as incomplete would re-download the
        // whole model on every launch.
        WriteAllRequired();

        Assert.True(ModelDownloader.Inspect(_folder, Layout).IsComplete);
    }

    [Fact]
    public void A_missing_required_file_is_named()
    {
        Write("model.onnx");
        Write("model.onnx.data", 1024);

        var state = ModelDownloader.Inspect(_folder, Layout);

        Assert.False(state.IsComplete);
        Assert.Contains("embeddings/config.json", state.Missing);
    }

    [Fact]
    public void A_config_in_a_subfolder_counts_because_the_layout_says_where_it_is()
    {
        // The MLX rule required a TOP-LEVEL config.json. The published
        // preset-voice export has none — its config is embeddings/config.json —
        // so that clause had to go, replaced by the per-export list.
        WriteAllRequired();

        Assert.True(ModelDownloader.Inspect(_folder, Layout).IsComplete);
        Assert.False(File.Exists(Path.Combine(_folder, "config.json")));
    }

    [Fact]
    public void A_graph_without_its_external_data_is_incomplete()
    {
        // The clause that earns its keep. A .onnx is megabytes and its .data is
        // gigabytes, so an interrupted download usually leaves the small half —
        // and every other check would pass.
        Write("embeddings/config.json");
        Write("model.onnx");

        var state = ModelDownloader.Inspect(_folder, Layout);

        Assert.False(state.IsComplete);
        Assert.Contains("model.onnx.data", state.Missing.Concat(state.Partial));
    }

    [Fact]
    public void An_empty_file_does_not_count_as_present()
    {
        // A zero-byte file is what an interrupted create leaves behind.
        WriteAllRequired();
        File.WriteAllBytes(Path.Combine(_folder, "model.onnx"), []);

        var state = ModelDownloader.Inspect(_folder, Layout);

        Assert.False(state.IsComplete);
        Assert.Contains("model.onnx", state.Missing);
    }

    [Fact]
    public void An_interrupted_transfer_anywhere_in_the_tree_makes_it_incomplete()
    {
        WriteAllRequired();
        Write("tokenizer/vocab.json.incomplete");

        var state = ModelDownloader.Inspect(_folder, Layout);

        Assert.False(state.IsComplete);
        Assert.Contains(state.Partial, p => p.Contains("vocab.json.incomplete"));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_incomplete_rather_than_an_error()
    {
        var missing = Path.Combine(_folder, "nope");

        var state = ModelDownloader.Inspect(missing, Layout);

        Assert.False(state.IsComplete);
        Assert.NotEmpty(state.Missing);
    }

    [Fact]
    public void The_description_names_what_is_wrong()
    {
        // It goes in the log, where it is the only clue about why a download
        // started again.
        Write("model.onnx");

        var state = ModelDownloader.Inspect(_folder, Layout);

        Assert.Contains("missing", state.Describe());
        Assert.Contains("config.json", state.Describe());
    }

    [Fact]
    public void The_shipping_preset_layout_pairs_every_graph_with_its_data()
    {
        var pairs = ModelLayout.PresetVoice.ExternalDataPairs.ToList();

        Assert.Contains(("talker_prefill.onnx", "talker_prefill.onnx.data"), pairs);
        Assert.Contains(("talker_decode.onnx", "talker_decode.onnx.data"), pairs);
        Assert.Contains(("vocoder.onnx", "vocoder.onnx.data"), pairs);

        // code_predictor.onnx ships its weights inside the graph, so it has no
        // sibling and must not be expected to.
        Assert.DoesNotContain(pairs, p => p.Graph == "code_predictor.onnx");
    }
}

public class SlugTests
{
    [Theory]
    [InlineData("https://models.example.com/customvoice", "models.example.com-customvoice")]
    [InlineData("https://models.bunyi.app/voicedesign", "models.bunyi.app-voicedesign")]
    [InlineData("http://192.168.1.10:8080/models", "192.168.1.10-models")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.com/a/b/c", "example.com-a-b-c")]
    public void A_base_url_becomes_the_folder_name_the_spec_pins(string url, string expected) =>
        Assert.Equal(expected, ModelDownloader.Slug(new Uri(url)));

    [Fact]
    public void The_slug_never_escapes_its_folder()
    {
        // It becomes a directory name under the models root, so it must not
        // contain a separator whatever the URL looked like.
        var slug = ModelDownloader.Slug(new Uri("https://example.com/../../etc"));

        Assert.DoesNotContain('/', slug);
        Assert.DoesNotContain('\\', slug);
    }
}
