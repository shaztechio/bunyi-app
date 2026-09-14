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
import BunyiMLXCore

@MainActor
enum DoctorCommand {
    static func run(
        _ request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState, engine existingEngine: TTSEngine? = nil
    ) async throws -> CLIMessage {
        let modes = request.value("mode").map {
            [ModelCommand.mode($0)]
        } ?? TTSMode.allCases
        let engine = existingEngine ?? TTSEngine()
        var reports: [CLIMessage] = []
        for mode in modes {
            if cancelled.value {
                throw CLIError("cancelled", "Doctor was cancelled.", exitCode: 5)
            }
            output.event(CLIProtocol.event(request, type: "checking", [
                "mode": ModelCommand.modeName(mode),
                "detail": "Checking \(mode.rawValue)",
            ]))
            let report = await Doctor.run(
                mode: mode, engine: engine, deep: request.has("deep"),
                shouldContinue: { !cancelled.value })
            if cancelled.value {
                throw CLIError("cancelled", "Doctor was cancelled.", exitCode: 5)
            }
            reports.append(reportMessage(report))
        }
        if reports.contains(where: { $0["hasBlockers"] as? Bool == true }) {
            var result = CLIProtocol.failure(
                request,
                CLIError(
                    "preflight_blocked",
                    "Doctor found blockers. See reports for details and actions.",
                    exitCode: 3))
            result["reports"] = reports
            return result
        }
        return CLIProtocol.result(request, ["reports": reports])
    }

    private static func reportMessage(_ report: DoctorReport) -> CLIMessage {
        [
            "mode": ModelCommand.modeName(report.mode),
            "hasBlockers": !report.blockers.isEmpty,
            "findings": report.findings.map { finding in
                [
                    "title": finding.title,
                    "detail": finding.detail,
                    "severity": severityName(finding.severity),
                ] as CLIMessage
            },
        ]
    }

    private static func severityName(_ severity: DoctorSeverity) -> String {
        switch severity {
        case .ok: "ok"
        case .warning: "warning"
        case .blocker: "blocker"
        }
    }
}
