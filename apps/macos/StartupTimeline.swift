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

//
//  StartupTimeline.swift
//  Bunyi
//
//  How long the app took to put a window on screen, phase by phase.
//

import Darwin
import Foundation

/// One line in the log (spec §8), so "it is slow to start" reaches a bug report
/// as a number with the slow phase named.
///
/// The phases are chosen to separate things that actually differ between
/// machines: everything before `main` — dyld resolving the MLX and Metal
/// frameworks, which is the bulk of it on a cold launch — then SwiftUI bringing
/// the app up, then the first frame. What this app's own code does is small
/// against those, and the point of splitting them is to show that rather than
/// assert it.
///
/// The clock is `ContinuousClock`, not wall time: a clock adjustment mid-launch
/// must not produce a negative phase. Only the span before `main` is wall time,
/// because there is no monotonic reading from before the process existed.
@MainActor
final class StartupTimeline {
    static let shared = StartupTimeline()

    private let started = ContinuousClock.now
    /// How long the process existed before this object did, or nil when it
    /// could not be read.
    private let beforeMain: Duration?
    private var phases: [(name: String, took: Duration)] = []
    private var mark: ContinuousClock.Instant
    private var reported = false

    private init() {
        beforeMain = Self.timeSinceProcessStart()
        mark = started
    }

    /// Close the phase that just finished.
    func note(_ name: String) {
        guard !reported else { return }
        let now = ContinuousClock.now
        phases.append((name, mark.duration(to: now)))
        mark = now
    }

    /// Close the last phase and write the line. Ignored after the first call:
    /// the first frame happens once, and a second line would be measuring
    /// something else while claiming to be startup.
    func report(finalPhase: String = "first frame") {
        guard !reported else { return }
        note(finalPhase)
        reported = true
        LogStore.shared.log(line)
    }

    /// The line `report()` writes. Separate so it can be checked without a log.
    var line: String {
        var parts: [String] = []
        var total: Duration = .zero
        if let beforeMain {
            parts.append("before main \(Self.ms(beforeMain))")
            total += beforeMain
        }
        for phase in phases {
            parts.append("\(phase.name) \(Self.ms(phase.took))")
            total += phase.took
        }
        // "at least" when the pre-main span could not be read. Reporting a lower
        // bound as though it were the figure is how a startup number ends up
        // arguing that the slow part does not exist.
        let bound = beforeMain == nil ? "at least " : ""
        guard let last = phases.last else {
            return "Startup: \(bound)\(Self.ms(total))."
        }
        return "Startup: \(bound)\(Self.ms(total)) to \(last.name) — "
            + parts.joined(separator: ", ") + "."
    }

    private static func ms(_ duration: Duration) -> String {
        let milliseconds = Double(duration.components.seconds) * 1000
            + Double(duration.components.attoseconds) / 1e15
        return "\(Int(milliseconds.rounded())) ms"
    }

    /// Wall time since the kernel started this process.
    ///
    /// The Darwin answer to reading `/proc` on Linux: `KERN_PROC_PID` reports
    /// the process's own start time, which is the only way to account for the
    /// dyld and runtime work that happens before any of this code runs — and on
    /// a cold launch that is most of the wait.
    ///
    /// Returns nil rather than zero if the call fails, so the line can say the
    /// total is a bound rather than quietly dropping the largest phase.
    private static func timeSinceProcessStart() -> Duration? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var name: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        let ok = sysctl(&name, u_int(name.count), &info, &size, nil, 0) == 0
        guard ok, size > 0 else { return nil }

        let start = info.kp_proc.p_starttime
        let startSeconds = Double(start.tv_sec) + Double(start.tv_usec) / 1e6
        let elapsed = Date().timeIntervalSince1970 - startSeconds
        // A clock that moved between process start and now can make this
        // negative. Unknown is honest; a negative phase is not.
        guard elapsed >= 0 else { return nil }
        return .seconds(elapsed)
    }
}
