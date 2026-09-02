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

using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Where a tokenizer gets its chat specials from, and what happens when it has none.
/// </summary>
/// <remarks>
/// The preset-voice export ships a bare <c>vocab.json</c> and <c>merges.txt</c>
/// with no <c>added_tokens.json</c> or <c>tokenizer.json</c>, and carries the
/// ids of <c>&lt;|im_start|&gt;</c> and <c>&lt;|im_end|&gt;</c> in its config
/// instead. Loaded without them, the tokenizer split each special into its
/// characters — a sequence of plausible length that primed the model with
/// nonsense, measured as 153 greedy frames for a three-second sentence. These
/// tests are that bug, pinned.
/// </remarks>
public sealed class TokenizerSpecialsTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public TokenizerSpecialsTests()
    {
        Directory.CreateDirectory(_folder);

        // Enough vocabulary to tokenize the specials as characters if nothing
        // stops it — which is the failure being guarded against.
        var vocabulary = new Dictionary<string, int>();
        var next = 0;
        foreach (var c in "<|im_startend>abc") vocabulary.TryAdd(c.ToString(), next++);
        vocabulary["Ġ"] = next++;
        vocabulary["Ċ"] = next++;

        File.WriteAllText(
            Path.Combine(_folder, "vocab.json"),
            System.Text.Json.JsonSerializer.Serialize(vocabulary));
        File.WriteAllText(Path.Combine(_folder, "merges.txt"), "#version: 0.2\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static readonly Dictionary<string, int> Specials = new()
    {
        ["<|im_start|>"] = 250,
        ["<|im_end|>"] = 251,
    };

    [Fact]
    public void A_folder_with_no_specials_is_refused_rather_than_loaded()
    {
        // Loud, not lenient: the alternative is a tokenizer that works on every
        // ordinary word and quietly wrecks the one template that matters.
        var error = Assert.Throws<InvalidDataException>(() => QwenTokenizer.Load(_folder));

        Assert.Contains("<|im_start|>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Specials_from_the_config_fill_the_gap()
    {
        var tokenizer = QwenTokenizer.Load(_folder, Specials);

        // One token each, not a run of characters.
        Assert.Equal([250, 251], tokenizer.Encode("<|im_start|><|im_end|>"));
    }

    [Fact]
    public void The_folders_own_specials_win_when_it_has_them()
    {
        // added_tokens.json is the export's word on the matter; a caller's
        // fallback is for exports that have no word.
        File.WriteAllText(
            Path.Combine(_folder, "added_tokens.json"),
            """{"<|im_start|>": 900, "<|im_end|>": 901}""");

        var tokenizer = QwenTokenizer.Load(_folder, Specials);

        Assert.Equal([900, 901], tokenizer.Encode("<|im_start|><|im_end|>"));
    }
}
