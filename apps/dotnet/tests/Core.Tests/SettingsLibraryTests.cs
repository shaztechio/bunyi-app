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

using System.Text.Json;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Models;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>Saved model configurations (spec §3a, DATA-FORMATS).</summary>
public sealed class ModelConfigLibraryTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    private string ConfigPath => Path.Combine(_folder, "configs.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private ModelConfigLibrary New() => new(_log, ConfigPath);

    [Fact]
    public void A_first_run_has_no_saved_configurations()
    {
        Assert.Empty(New().Configs);
    }

    [Fact]
    public void The_built_in_mirror_is_listed_but_never_saved()
    {
        // §3a permits a built-in entry and gates it: "A platform ships this
        // only if its mirror publishes manifest.sha256". It does now, for all
        // three ONNX prefixes, and all three modes have been downloaded and
        // generated from them (#100) — so the entry is here.
        //
        // Listed shows it; Configs does not, because Configs is what is on
        // disk. A built-in written to configs.json would become a saved
        // configuration the next time it loaded, editable and deletable and no
        // longer tracking whatever the app ships.
        var library = New();

        Assert.Empty(library.Configs);
        var listed = Assert.Single(library.Listed);
        Assert.Equal("Bunyi mirror", listed.Name);
        Assert.True(listed.IsBuiltIn);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void The_built_in_points_at_the_onnx_prefixes()
    {
        // Named here so a typo cannot ship silently: an endorsed source that
        // 404s is worse than no entry at all, and nothing else in the app
        // would notice these strings being wrong until a user pressed
        // Generate.
        var mirror = ModelConfigLibrary.BunyiMirror;

        Assert.Equal("https://models.bunyi.app/onnx/customvoice", mirror.For(TtsMode.PresetVoice));
        Assert.Equal("https://models.bunyi.app/onnx/voicedesign", mirror.For(TtsMode.VoiceDesign));
        Assert.Equal("https://models.bunyi.app/onnx/voiceclone", mirror.For(TtsMode.VoiceClone));

        // The MLX prefixes are a different weight set at the same host, and
        // pointing this app at them would 404 on every file.
        Assert.All(
            new[] { TtsMode.PresetVoice, TtsMode.VoiceDesign, TtsMode.VoiceClone },
            mode => Assert.Contains("/onnx/", mirror.For(mode), StringComparison.Ordinal));
    }

    [Fact]
    public void The_built_ins_identity_is_fixed_rather_than_generated()
    {
        // The row has to keep its identity across launches without being
        // written to disk. A fresh Guid each time would make the list treat it
        // as a different row on every read.
        Assert.Equal(
            new Guid("B0000000-0000-4000-A000-000000000001"),
            ModelConfigLibrary.BunyiMirror.Id);
    }

    [Fact]
    public void A_configuration_of_your_own_with_the_same_name_hides_the_built_in()
    {
        // Overriding is a Save under the same name: a real entry the user chose
        // to write beats one the app shipped.
        var library = New();
        library.Save("Bunyi mirror", "a/b", "c/d", "e/f");

        var listed = Assert.Single(library.Listed);
        Assert.False(listed.IsBuiltIn);
        Assert.Equal("a/b", listed.For(TtsMode.PresetVoice));
    }

    [Fact]
    public void Deleting_your_override_brings_the_built_in_back()
    {
        // It was hidden, not gone. The other direction of the rule above, and
        // the half that would rot silently: nothing else would notice a
        // built-in that stayed hidden after its override was removed.
        var library = New();
        library.Save("Bunyi mirror", "a/b", "c/d", "e/f");
        library.Delete(Assert.Single(library.Configs));

        var listed = Assert.Single(library.Listed);
        Assert.True(listed.IsBuiltIn);
        Assert.Equal("https://models.bunyi.app/onnx/customvoice", listed.For(TtsMode.PresetVoice));
    }

    [Fact]
    public void The_name_match_ignores_case_the_way_saving_does()
    {
        // Save already treats names as unique case-insensitively, so the
        // override has to as well or "bunyi mirror" would list twice.
        var library = New();
        library.Save("BUNYI MIRROR", "a/b", "c/d", "e/f");

        Assert.Single(library.Listed);
    }

    [Fact]
    public void Deleting_the_built_in_does_nothing_and_says_so()
    {
        // The UI hides its Delete button, so reaching here is a bug — but a
        // silent no-op that rewrote the file and logged a deletion which did
        // not happen would be worse than refusing out loud.
        var library = New();
        library.Delete(ModelConfigLibrary.BunyiMirror);

        Assert.Single(library.Listed);
        Assert.True(Assert.Single(library.Listed).IsBuiltIn);
        Assert.Contains(_log.Lines, l => l.Contains("built in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_built_in_never_reaches_configs_json()
    {
        // Saving something else must not persist it as a side effect.
        var library = New();
        library.Save("Mine", "a/b", "c/d", "e/f");

        var json = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("Bunyi mirror", json, StringComparison.Ordinal);
        Assert.DoesNotContain("models.bunyi.app", json, StringComparison.Ordinal);

        // And nothing derived leaks into the file either.
        Assert.DoesNotContain("uiltIn", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_survives_a_round_trip()
    {
        New().Save("Self-hosted",
            "https://models.example.com/customvoice",
            "https://models.example.com/voicedesign",
            "https://models.example.com/voiceclone");

        var config = Assert.Single(New().Configs);
        Assert.Equal("Self-hosted", config.Name);
        Assert.Equal("https://models.example.com/customvoice", config.For(TtsMode.PresetVoice));
        Assert.Equal("https://models.example.com/voicedesign", config.For(TtsMode.VoiceDesign));
        Assert.Equal("https://models.example.com/voiceclone", config.For(TtsMode.VoiceClone));
    }

    [Fact]
    public void The_persisted_shape_matches_the_spec()
    {
        New().Save("Self-hosted", "a", "b", "c");

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var entry = document.RootElement[0];

        foreach (var key in new[] { "id", "name", "presetVoice", "voiceDesign", "voiceClone", "savedAt" })
        {
            Assert.True(entry.TryGetProperty(key, out _), $"missing {key}");
        }
    }

    [Fact]
    public void Saving_over_a_name_replaces_it_rather_than_accumulating_duplicates()
    {
        // The values are long URLs; a list of near-identical ones is exactly
        // what this type exists to prevent.
        var library = New();
        library.Save("Mirror", "one", "", "");
        library.Save("Mirror", "two", "", "");

        var config = Assert.Single(library.Configs);
        Assert.Equal("two", config.For(TtsMode.PresetVoice));
    }

    [Fact]
    public void Names_are_unique_case_insensitively()
    {
        var library = New();
        library.Save("Mirror", "one", "", "");
        library.Save("MIRROR", "two", "", "");

        Assert.Single(library.Configs);
    }

    [Fact]
    public void An_empty_source_means_that_mode_uses_its_default()
    {
        // Same meaning a blank field has in Settings.
        New().Save("Partly set", "org/repo", "", "");

        var config = Assert.Single(New().Configs);
        Assert.Equal(string.Empty, config.For(TtsMode.VoiceDesign));
    }

    [Fact]
    public void Configurations_are_listed_alphabetically()
    {
        var library = New();
        library.Save("Zulu", "", "", "");
        library.Save("alpha", "", "", "");
        library.Save("Mike", "", "", "");

        Assert.Equal(["alpha", "Mike", "Zulu"], library.Configs.Select(c => c.Name));
    }

    [Fact]
    public void Deleting_removes_only_that_configuration()
    {
        var library = New();
        library.Save("One", "", "", "");
        var two = library.Save("Two", "", "", "");

        library.Delete(two);

        Assert.Equal("One", Assert.Single(library.Configs).Name);
    }

    [Fact]
    public void A_damaged_file_gives_an_empty_list_and_is_reported()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConfigPath, "{ not json");

        Assert.Empty(New().Configs);
        Assert.Contains(_log.Lines, l => l.Contains("Could not read saved model configurations"));
    }

    [Fact]
    public void Saving_leaves_no_temporary_file()
    {
        New().Save("One", "", "", "");

        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}

/// <summary>Listing and reclaiming downloaded models (spec §3d).</summary>
public sealed class DownloadedModelsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteModel(string relative, int bytes)
    {
        var folder = Path.Combine(_root, "models", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), new byte[bytes]);
    }

    [Fact]
    public void Nothing_downloaded_lists_nothing()
    {
        Assert.Empty(DownloadedModels.Read(_root));
    }

    [Fact]
    public void Hub_models_are_listed_by_org_and_repo()
    {
        WriteModel("elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX", 2048);

        var model = Assert.Single(DownloadedModels.Read(_root));

        Assert.Equal("elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX", model.Name);
        Assert.Equal(ModelOrigin.Hub, model.Origin);
        Assert.Equal("Hugging Face", model.OriginText());
    }

    [Fact]
    public void Self_hosted_models_are_listed_by_slug_and_named_as_the_users_own()
    {
        WriteModel("self-hosted/models.example.com-customvoice", 1024);

        var model = Assert.Single(DownloadedModels.Read(_root));

        Assert.Equal("models.example.com-customvoice", model.Name);
        Assert.Equal(ModelOrigin.SelfHosted, model.Origin);
        Assert.Equal("Your server", model.OriginText());
    }

    [Fact]
    public void The_largest_model_is_listed_first()
    {
        // Reclaiming space is the reason this list exists, so the thing worth
        // deleting is at the top.
        WriteModel("org/small", 1024);
        WriteModel("org/large", 8192);

        Assert.Equal("org/large", DownloadedModels.Read(_root)[0].Name);
    }

    [Fact]
    public void Sizes_are_reported_so_the_user_knows_what_deleting_reclaims()
    {
        WriteModel("org/repo", 2_500_000);

        var model = Assert.Single(DownloadedModels.Read(_root));

        Assert.Equal(2_500_000, model.SizeBytes);
        Assert.Equal("2.5 MB", model.SizeText());
        Assert.Equal(2_500_000, DownloadedModels.TotalBytes(_root));
    }

    [Fact]
    public void Deleting_a_model_removes_its_folder()
    {
        WriteModel("org/repo", 1024);
        var model = Assert.Single(DownloadedModels.Read(_root));

        Assert.True(DownloadedModels.TryDelete(model, _log));

        Assert.False(Directory.Exists(model.Folder));
        Assert.Empty(DownloadedModels.Read(_root));
    }

    [Fact]
    public void The_pre_download_command_names_the_repo_and_the_real_folder()
    {
        // §3d: shown with the actual folder path filled in, so it can be copied
        // and run.
        var command = DownloadedModels.PreDownloadCommand(
            new ModelSource.Repo("elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX"), _root);

        Assert.NotNull(command);
        Assert.StartsWith("hf download elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX", command);
        Assert.Contains("--local-dir", command);
        Assert.Contains(_root, command);
    }

    [Fact]
    public void A_mode_on_your_own_server_gets_no_command_rather_than_a_broken_one()
    {
        // There is no repository name to give the tool. Emitting a line with a
        // URL where a repo id belongs produces a command that cannot work —
        // which the macOS app shipped once and had to fix.
        var command = DownloadedModels.PreDownloadCommand(
            new ModelSource.BaseUrl(new Uri("https://models.example.com/customvoice")), _root);

        Assert.Null(command);
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}
