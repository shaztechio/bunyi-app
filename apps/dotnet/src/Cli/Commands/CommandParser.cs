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

using Bunyi.Cli.Protocol;
using Bunyi.Core;
using Bunyi.Core.Engine;

namespace Bunyi.Cli.Commands;

public static class CommandParser
{
    private static readonly HashSet<string> Flags = ["json", "jsonl", "one-shot", "require-server", "help", "version", "stdin", "auto-transcribe", "all", "deep", "detach"];
    private static readonly HashSet<string> Globals = ["json", "jsonl", "one-shot", "require-server", "help", "config"];
    private static readonly Dictionary<string, string[]> Options = new()
    {
        ["help"] = [], ["version"] = [], ["play"] = [],
        ["generate.preset"] = ["text", "text-file", "stdin", "speaker", "style", "language", "detach"],
        ["generate.design"] = ["text", "text-file", "stdin", "voice", "language", "detach"],
        ["generate.clone"] = ["text", "text-file", "stdin", "reference", "transcript", "auto-transcribe", "saved-voice", "language", "detach"],
        ["transcribe"] = ["detach", "language"], ["speakers"] = ["detach"],
        ["models.list"] = [], ["models.status"] = ["mode"], ["models.download"] = ["mode", "all", "detach"],
        ["models.verify"] = ["mode", "detach"], ["models.remove"] = ["mode"],
        ["voices.list"] = [], ["voices.add"] = ["name", "reference", "transcript", "auto-transcribe", "detach"], ["voices.remove"] = [],
        ["history.list"] = [], ["history.show"] = [], ["history.remove"] = [],
        ["doctor"] = ["mode", "deep", "detach"], ["backup.create"] = ["detach"], ["backup.restore"] = ["detach"],
        ["config.list"] = [], ["config.get"] = [], ["config.set"] = [],
        ["logs.path"] = [], ["logs.tail"] = ["lines"], ["logs.clear"] = [],
        ["server.run"] = [], ["server.start"] = [], ["server.status"] = [], ["server.preload"] = ["mode", "detach"], ["server.unload"] = ["detach"], ["server.stop"] = [],
        ["jobs.status"] = [], ["jobs.follow"] = [], ["jobs.cancel"] = []
    };

    public static CommandRequest Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var words = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--") { words.AddRange(args.Skip(i + 1)); break; }
            if (arg is "-h") arg = "--help";
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { words.Add(arg); continue; }
            var pair = arg[2..].Split('=', 2);
            var name = pair[0];
            if (options.ContainsKey(name)) throw Invalid($"Option --{name} was provided more than once.");
            if (Flags.Contains(name))
            {
                if (pair.Length != 1) throw Invalid($"Option --{name} does not take a value.");
                options[name] = null;
            }
            else
            {
                if (pair.Length == 2) options[name] = pair[1];
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[name] = args[++i];
                else throw Invalid($"Option --{name} needs a value.");
            }
        }
        Exclusive(options, "json", "jsonl");
        Exclusive(options, "one-shot", "require-server");
        if (options.ContainsKey("detach") && options.ContainsKey("one-shot")) throw Invalid("--detach requires a server and cannot use --one-shot.");
        var operation = words.Count == 0 ? (options.Remove("version") ? "version" : "help") : words[0];
        var consumed = words.Count == 0 ? 0 : 1;
        if (operation is "generate" or "models" or "voices" or "history" or "backup" or "config" or "logs" or "server" or "jobs")
        {
            if (words.Count < 2 && options.ContainsKey("help")) operation = "help";
            else if (words.Count < 2) throw Invalid($"{operation} needs a subcommand. Use bunyi --help.");
            else { operation += "." + words[1]; consumed = 2; }
        }
        if (!Options.TryGetValue(operation, out var allowed)) throw Invalid($"Unknown command '{operation}'. Use bunyi --help.");
        if (operation == "play" && options.ContainsKey("require-server")) throw Invalid("Playback runs locally and cannot use --require-server.");
        if ((operation.StartsWith("server.", StringComparison.Ordinal) || operation.StartsWith("jobs.", StringComparison.Ordinal)) && options.ContainsKey("one-shot")) throw Invalid("Server and job commands cannot use --one-shot.");
        foreach (var name in options.Keys)
            if (!Globals.Contains(name) && !allowed.Contains(name)) throw Invalid($"Option --{name} is not valid for {operation}.");
        if (options.ContainsKey("help")) return new("help", options, Guid.NewGuid().ToString("N"));
        var positional = words.Skip(consumed).ToArray();
        var expected = operation switch
        {
            "config.set" => 2,
            "play" or "transcribe" or "voices.remove" or "history.show" or "history.remove" or "backup.create" or "backup.restore" or "config.get" or "jobs.status" or "jobs.follow" or "jobs.cancel" => 1,
            _ => 0
        };
        if (positional.Length != expected) throw Invalid($"{operation} requires {expected} positional argument(s). Use bunyi --help.");
        if (expected > 0) options["target"] = positional[0];
        if (expected > 1) options["value"] = positional[1];
        if (options.TryGetValue("mode", out var mode)) _ = Mode(mode);
        if (operation is "models.verify" or "models.remove" or "server.preload" && !options.ContainsKey("mode")) throw Invalid("--mode preset|design|clone is required.");
        if (operation == "models.download" && options.ContainsKey("all") == options.ContainsKey("mode")) throw Invalid("Choose exactly one of --all or --mode preset|design|clone.");
        return new(operation, options, Guid.NewGuid().ToString("N"));
    }

    public static async Task NormalizeInputsAsync(CommandRequest request, TextReader input, CancellationToken ct)
    {
        var args = request.Arguments;
        if (request.Has("language") && !Languages.All.Contains(request.Get("language"))) throw Invalid("Unsupported language. Use: " + string.Join(", ", Languages.All));
        if (request.Operation.StartsWith("generate.", StringComparison.Ordinal))
        {
            var count = new[] { "text", "text-file", "stdin" }.Count(request.Has);
            if (count == 0) throw Missing("Provide text using --text, --text-file, or --stdin.");
            if (count != 1) throw Invalid("Choose exactly one of --text, --text-file, or --stdin.");
            if (request.Has("text-file")) args["text"] = await File.ReadAllTextAsync(ReadableFile(request.Get("text-file")!), ct);
            if (request.Has("stdin")) args["text"] = await input.ReadToEndAsync(ct);
            args.Remove("text-file"); args.Remove("stdin");
            Required(request, "text");
            if (request.Operation == "generate.design") Required(request, "voice");
            if (request.Operation == "generate.clone") ValidateReference(request, allowSaved: true);
        }
        if (request.Operation == "voices.add") { Required(request, "name"); ValidateReference(request, allowSaved: false); }
        if (request.Has("reference")) args["reference"] = ReadableFile(request.Get("reference")!);
        if (request.Operation is "play" or "transcribe" or "backup.restore") args["target"] = ReadableFile(request.Get("target")!);
        if (request.Operation is "history.show" or "history.remove" or "backup.create") args["target"] = Path.GetFullPath(request.Get("target")!);
        if (request.Has("config")) args["config"] = Path.GetFullPath(request.Get("config")!);
    }

    private static void ValidateReference(CommandRequest request, bool allowSaved)
    {
        if (allowSaved && request.Has("saved-voice"))
        {
            if (!Guid.TryParse(request.Get("saved-voice"), out _)) throw Invalid("--saved-voice needs the exact voice ID from voices list.");
            if (new[] { "reference", "transcript", "auto-transcribe" }.Any(request.Has)) throw Invalid("--saved-voice cannot be combined with reference or transcript options.");
            return;
        }
        Required(request, "reference");
        if (request.Has("transcript") && request.Has("auto-transcribe")) throw Invalid("Choose --transcript or --auto-transcribe.");
        if (!request.Has("auto-transcribe")) Required(request, "transcript");
    }

    public static string ReadableFile(string path)
    {
        var absolute = Path.GetFullPath(path);
        try { using var stream = File.Open(absolute, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Missing($"Cannot read input file: {absolute}"); }
        return absolute;
    }

    public static bool ServerCapable(CommandRequest request) => request.Operation.StartsWith("generate.", StringComparison.Ordinal)
        || request.Operation is "transcribe" or "speakers" or "models.download" or "models.verify" or "models.remove" or "models.status"
        or "doctor" or "backup.create" or "backup.restore" or "server.preload" or "server.unload" || request.Operation == "voices.add";

    public static TtsMode Mode(string? value) => value switch
    {
        "preset" => TtsMode.PresetVoice, "design" => TtsMode.VoiceDesign, "clone" => TtsMode.VoiceClone,
        _ => throw Invalid("Mode must be preset, design, or clone.")
    };
    public static string ModeName(TtsMode value) => value switch { TtsMode.PresetVoice => "preset", TtsMode.VoiceDesign => "design", _ => "clone" };
    public static void Required(CommandRequest request, string key) { if (string.IsNullOrWhiteSpace(request.Get(key))) throw Missing($"Provide --{key}."); }
    private static void Exclusive(Dictionary<string, string?> args, string a, string b) { if (args.ContainsKey(a) && args.ContainsKey(b)) throw Invalid($"--{a} and --{b} are mutually exclusive."); }
    public static CliException Invalid(string message) => new("invalid_arguments", message);
    public static CliException Missing(string message) => new("missing_input", message, 3);

    public const string Help = """
        Bunyi — local speech generation for people and agents
        bunyi generate preset (--text TEXT | --text-file FILE | --stdin) [--speaker Ryan] [--style TEXT]
        bunyi generate design (--text TEXT | --text-file FILE | --stdin) --voice DESCRIPTION
        bunyi generate clone (--text TEXT | --text-file FILE | --stdin) (--reference FILE (--transcript TEXT | --auto-transcribe) | --saved-voice ID)
          All generation modes: [--language auto|english|chinese|japanese|korean|german|french|russian|portuguese|spanish|italian]
        bunyi transcribe AUDIO [--language LANGUAGE] | speakers
        bunyi play AUDIO [--json | --jsonl] (local WAV/MP3/FLAC playback; waits until finished; Ctrl+C stops)
        bunyi models list | status [--mode MODE] | download (--all | --mode MODE) | verify --mode MODE | remove --mode MODE
        bunyi voices list | add --name NAME --reference FILE (--transcript TEXT | --auto-transcribe) | remove ID
        bunyi history list | show PATH | remove PATH
        bunyi doctor [--mode MODE] [--deep]
        bunyi backup create ZIP | restore ZIP
        bunyi config list | get KEY | set KEY VALUE
          Keys: modelsFolder, unloadOnModeSwitch, modelSource.preset, modelSource.design, modelSource.clone
        bunyi logs path | tail [--lines COUNT] | clear
        bunyi server run | start | status | preload --mode MODE | unload | stop
        bunyi jobs status ID | follow ID | cancel ID
        bunyi version | --help
        Global: --json | --jsonl; --one-shot | --require-server; --config FILE
        Long operations: --detach (requires an already-running server).
        MODE: preset | design | clone. BUNYI_DATA_DIR isolates data and configuration.
        """;
}
