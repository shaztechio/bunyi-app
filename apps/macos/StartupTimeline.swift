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
/// Two phases, because two are all that can honestly be measured from inside a
/// SwiftUI app: everything before the app's first line — dyld resolving the MLX
/// and Metal frameworks, which is the bulk of a cold launch — and everything
/// from there to the first frame. There is no hook between them to mark; a
/// third phase was tried and could only ever report zero.
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

    /// Start the clock. Call from the first line of app code that runs;
    /// everything before it is attributed to `before main`.
    ///
    /// Exists because the instance is a lazy `static let`, created on first
    /// access. Without an explicit call the first access is whatever records
    /// the first phase, so that phase starts and ends at the same instant and
    /// is always zero — which is what shipped, reporting `launch 0 ms` on a
    /// launch that took 1.2 seconds. Naming the call is what makes the moment
    /// the clock starts a decision rather than a side effect of ordering.
    func begin() {}

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
        // The total is the sum of the *rounded* parts, not a rounding of the
        // summed durations. Rounding the same quantity twice makes the printed
        // numbers disagree — 698 and 650 beside a total of 1347 — and this line
        // exists to be read in a bug report, where an arithmetic that does not
        // work invites the reader to wonder which phase is missing. A total a
        // millisecond off the true elapsed time is the cheaper error.
        var total = 0
        func add(_ name: String, _ took: Duration) {
            let ms = Self.milliseconds(took)
            parts.append("\(name) \(ms) ms")
            total += ms
        }
        if let beforeMain { add("before main", beforeMain) }
        for phase in phases { add(phase.name, phase.took) }
        // "at least" when the pre-main span could not be read. Reporting a lower
        // bound as though it were the figure is how a startup number ends up
        // arguing that the slow part does not exist.
        let bound = beforeMain == nil ? "at least " : ""
        guard let last = phases.last else {
            return "Startup: \(bound)\(total) ms."
        }
        return "Startup: \(bound)\(total) ms to \(last.name) — "
            + parts.joined(separator: ", ") + "."
    }

    /// Whole milliseconds. Returns the number rather than a formatted string so
    /// the caller can add them up; formatting one and parsing it back is how the
    /// two roundings got out of step in the first place.
    private static func milliseconds(_ duration: Duration) -> Int {
        let ms = Double(duration.components.seconds) * 1000
            + Double(duration.components.attoseconds) / 1e15
        return Int(ms.rounded())
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
