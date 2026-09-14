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

import Foundation

enum CLICommandParser {
    private static let flags: Set<String> = [
        "json", "jsonl", "one-shot", "require-server", "help", "version",
        "stdin", "auto-transcribe", "all", "deep", "detach",
    ]
    private static let globals: Set<String> = [
        "json", "jsonl", "one-shot", "require-server", "help",
    ]
    private static let allowed: [String: Set<String>] = [
        "help": [], "version": [], "play": [],
        "generate.preset": ["text", "text-file", "stdin", "speaker", "style", "language", "detach"],
        "generate.design": ["text", "text-file", "stdin", "voice", "language", "detach"],
        "generate.clone": ["text", "text-file", "stdin", "reference", "transcript", "auto-transcribe", "saved-voice", "language", "detach"],
        "transcribe": ["detach", "language"], "speakers": ["detach"],
        "models.list": [], "models.status": ["mode"],
        "models.download": ["mode", "all", "detach"],
        "models.verify": ["mode", "detach"], "models.remove": ["mode"],
        "voices.list": [],
        "voices.add": ["name", "reference", "transcript", "auto-transcribe", "detach"],
        "voices.remove": [], "history.list": [], "history.show": [], "history.remove": [],
        "doctor": ["mode", "deep", "detach"], "backup.create": ["detach"],
        "backup.restore": ["detach"], "config.list": [], "config.get": [],
        "config.set": [], "logs.path": [], "logs.tail": ["lines"], "logs.clear": [],
        "server.run": [], "server.start": [], "server.status": [],
        "server.preload": ["mode", "detach"], "server.unload": ["detach"],
        "server.stop": [], "jobs.status": [], "jobs.follow": [], "jobs.cancel": [],
    ]
    private static let groups: Set<String> = [
        "generate", "models", "voices", "history", "backup", "config", "logs",
        "server", "jobs",
    ]
    private static let languages: Set<String> = [
        "auto", "english", "chinese", "japanese", "korean", "german",
        "french", "russian", "portuguese", "spanish", "italian",
    ]

    static func parse(_ input: [String]) throws -> CLIRequest {
        var options: [String: String?] = [:]
        var words: [String] = []
        var index = 0
        while index < input.count {
            var argument = input[index]
            if argument == "--" {
                words.append(contentsOf: input.dropFirst(index + 1))
                break
            }
            if argument == "-h" { argument = "--help" }
            guard argument.hasPrefix("--") else {
                words.append(argument)
                index += 1
                continue
            }
            let pair = argument.dropFirst(2).split(separator: "=", maxSplits: 1,
                                                   omittingEmptySubsequences: false)
            let name = String(pair[0])
            guard !options.keys.contains(name) else {
                throw invalid("Option --\(name) was provided more than once.")
            }
            if flags.contains(name) {
                guard pair.count == 1 else {
                    throw invalid("Option --\(name) does not take a value.")
                }
                options[name] = .some(nil)
            } else if pair.count == 2 {
                options[name] = String(pair[1])
            } else if index + 1 < input.count, !input[index + 1].hasPrefix("--") {
                index += 1
                options[name] = input[index]
            } else {
                throw invalid("Option --\(name) needs a value.")
            }
            index += 1
        }

        try exclusive(options, "json", "jsonl")
        try exclusive(options, "one-shot", "require-server")
        if options.keys.contains("detach"), options.keys.contains("one-shot") {
            throw invalid("--detach requires a server and cannot use --one-shot.")
        }

        let versionFlag = options.removeValue(forKey: "version") != nil
        if versionFlag, !words.isEmpty {
            throw invalid("--version cannot be combined with a command.")
        }
        var operation = words.first ?? (versionFlag ? "version" : "help")
        var consumed = words.isEmpty ? 0 : 1
        if groups.contains(operation) {
            if words.count < 2, options.keys.contains("help") {
                operation = "help"
            } else if words.count < 2 {
                throw invalid("\(operation) needs a subcommand. Use bunyi --help.")
            } else {
                operation += "." + words[1]
                consumed = 2
            }
        }
        guard let commandOptions = allowed[operation] else {
            throw invalid("Unknown command '\(operation)'. Use bunyi --help.")
        }
        if operation == "play", options.keys.contains("require-server") {
            throw invalid("Playback runs locally and cannot use --require-server.")
        }
        if (operation.hasPrefix("server.") || operation.hasPrefix("jobs.")),
           options.keys.contains("one-shot") {
            throw invalid("Server and job commands cannot use --one-shot.")
        }
        for name in options.keys where !globals.contains(name) && !commandOptions.contains(name) {
            throw invalid("Option --\(name) is not valid for \(operation).")
        }
        if options.keys.contains("help") {
            return CLIRequest(operation: "help", arguments: options,
                              operationID: OperationID.make())
        }

        let positional = Array(words.dropFirst(consumed))
        let expected: Int
        switch operation {
        case "config.set": expected = 2
        case "play", "transcribe", "voices.remove", "history.show", "history.remove",
             "backup.create", "backup.restore", "config.get", "jobs.status",
             "jobs.follow", "jobs.cancel": expected = 1
        default: expected = 0
        }
        guard positional.count == expected else {
            throw invalid("\(operation) requires \(expected) positional argument(s). Use bunyi --help.")
        }
        if expected > 0 { options["target"] = positional[0] }
        if expected > 1 { options["value"] = positional[1] }
        if let value = options["mode"] ?? nil { _ = try mode(value) }
        if ["models.verify", "models.remove", "server.preload"].contains(operation),
           !options.keys.contains("mode") {
            throw invalid("--mode preset|design|clone is required.")
        }
        if operation == "models.download",
           options.keys.contains("all") == options.keys.contains("mode") {
            throw invalid("Choose exactly one of --all or --mode preset|design|clone.")
        }
        return CLIRequest(operation: operation, arguments: options,
                          operationID: OperationID.make())
    }

    static func normalize(_ request: CLIRequest, stdin: Data = Data()) throws -> CLIRequest {
        var result = request
        if let language = result.value("language"), !languages.contains(language) {
            throw invalid("Unsupported language. Use: \(languages.sorted().joined(separator: ", ")).")
        }
        if result.operation.hasPrefix("generate.") {
            let sources = ["text", "text-file", "stdin"].filter(result.has)
            guard !sources.isEmpty else {
                throw missing("Provide text using --text, --text-file, or --stdin.")
            }
            guard sources.count == 1 else {
                throw invalid("Choose exactly one of --text, --text-file, or --stdin.")
            }
            if let path = result.value("text-file") {
                result.arguments["text"] = try String(
                    contentsOf: readableFile(path), encoding: .utf8)
            } else if result.has("stdin") {
                result.arguments["text"] = String(data: stdin, encoding: .utf8) ?? ""
            }
            result.arguments.removeValue(forKey: "text-file")
            result.arguments.removeValue(forKey: "stdin")
            try required(result, "text")
            if result.operation == "generate.design" { try required(result, "voice") }
            if result.operation == "generate.clone" { try validateReference(result, allowSaved: true) }
        }
        if result.operation == "voices.add" {
            try required(result, "name")
            try validateReference(result, allowSaved: false)
        }
        if let path = result.value("reference") {
            result.arguments["reference"] = try readableFile(path).path
        }
        if ["play", "transcribe", "backup.restore"].contains(result.operation),
           let path = result.value("target") {
            result.arguments["target"] = try readableFile(path).path
        }
        if ["history.show", "history.remove", "backup.create"].contains(result.operation),
           let path = result.value("target") {
            result.arguments["target"] = absolute(path).path
        }
        return result
    }

    static func mode(_ value: String) throws -> String {
        guard ["preset", "design", "clone"].contains(value) else {
            throw invalid("Mode must be preset, design, or clone.")
        }
        return value
    }

    private static func validateReference(_ request: CLIRequest,
                                          allowSaved: Bool) throws {
        if allowSaved, request.has("saved-voice") {
            guard let value = request.value("saved-voice"), UUID(uuidString: value) != nil else {
                throw invalid("--saved-voice needs the exact voice ID from voices list.")
            }
            if ["reference", "transcript", "auto-transcribe"].contains(where: request.has) {
                throw invalid("--saved-voice cannot be combined with reference or transcript options.")
            }
            return
        }
        try required(request, "reference")
        if request.has("transcript"), request.has("auto-transcribe") {
            throw invalid("Choose --transcript or --auto-transcribe.")
        }
        if !request.has("auto-transcribe") { try required(request, "transcript") }
    }

    private static func required(_ request: CLIRequest, _ name: String) throws {
        guard let value = request.value(name),
              !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw missing("Provide --\(name).")
        }
    }

    private static func readableFile(_ path: String) throws -> URL {
        let url = absolute(path)
        guard FileManager.default.isReadableFile(atPath: url.path),
              (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else {
            throw missing("Cannot read input file: \(url.path)")
        }
        return url
    }

    private static func absolute(_ path: String) -> URL {
        URL(fileURLWithPath: path, relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath))
            .standardizedFileURL
    }

    private static func exclusive(_ options: [String: String?], _ first: String,
                                  _ second: String) throws {
        if options.keys.contains(first), options.keys.contains(second) {
            throw invalid("--\(first) and --\(second) are mutually exclusive.")
        }
    }

    static func invalid(_ message: String) -> CLIError {
        CLIError("invalid_arguments", message)
    }

    static func missing(_ message: String) -> CLIError {
        CLIError("missing_input", message, exitCode: 3)
    }

    static let help = """
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
        Global: --json | --jsonl; --one-shot | --require-server
        Long operations: --detach (requires an already-running server).
        MODE: preset | design | clone.
        """
}
