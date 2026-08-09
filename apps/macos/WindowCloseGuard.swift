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
//  WindowCloseGuard.swift
//  Bunyi
//
//  Intercepts the window's red close button. SwiftUI has no "should close"
//  hook, so this installs an NSWindowDelegate (forwarding everything else to
//  SwiftUI's own delegate). When `isBusy` is true, closing asks for
//  confirmation; confirming runs `onConfirmedClose` (which stops the work)
//  and then keeps the window open until the work has actually stopped.
//

import AppKit
import SwiftUI

struct WindowCloseGuard: NSViewRepresentable {
    var isBusy: () -> Bool
    var onConfirmedClose: () -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { context.coordinator.attach(to: view.window) }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.isBusy = isBusy
        context.coordinator.onConfirmedClose = onConfirmedClose
        // Re-assert on every state change in case SwiftUI reclaimed the
        // delegate; `isBusy` changing during work drives frequent updates.
        context.coordinator.attach(to: nsView.window)
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(isBusy: isBusy, onConfirmedClose: onConfirmedClose)
    }

    /// Main-actor isolated: every NSWindowDelegate callback arrives on the
    /// main thread, and the wait below hops back to it. Saying so is what lets
    /// the deferred close capture `self` without Swift 6 calling it a race.
    @MainActor
    final class Coordinator: NSObject, NSWindowDelegate {
        var isBusy: () -> Bool
        var onConfirmedClose: () -> Void
        private weak var window: NSWindow?
        private weak var previousDelegate: NSWindowDelegate?
        /// Set once the wait is over, so the programmatic close is not mistaken
        /// for the user pressing the red button again.
        private var isClosing = false
        /// Set while waiting for the engine to stop. Without it, pressing the
        /// red button again during the wait asks a second time, runs the stop
        /// again, and starts a second polling chain racing the first.
        private var isWaitingToClose = false
        /// Stop is cooperative: the engine may take a moment, and if something
        /// goes wrong it might never report idle. Closing anyway after this
        /// beats trapping the user in a window that will not shut.
        private static let stopTimeout: TimeInterval = 15

        init(isBusy: @escaping () -> Bool, onConfirmedClose: @escaping () -> Void) {
            self.isBusy = isBusy
            self.onConfirmedClose = onConfirmedClose
        }

        func attach(to window: NSWindow?) {
            guard let window else { return }
            self.window = window
            if window.delegate !== self {
                previousDelegate = window.delegate
                window.delegate = self
            }
        }

        func windowShouldClose(_ sender: NSWindow) -> Bool {
            if isClosing { return true }
            // Already stopping and closing: pressing the button again asks for
            // something that is already happening.
            if isWaitingToClose { return false }
            guard isBusy() else { return true }

            let alert = NSAlert()
            alert.messageText = "Stop the current operation?"
            alert.informativeText = """
                Something is still running. The window will close once it has \
                stopped, or after \(Int(Self.stopTimeout)) seconds.
                """
            alert.addButton(withTitle: "Keep Working")
            alert.addButton(withTitle: "Stop and Close")
            alert.buttons.last?.hasDestructiveAction = true
            guard alert.runModal() == .alertSecondButtonReturn else { return false }

            // Stop first, close after. Returning true here would tear the
            // window down while the engine was still generating: the status
            // says "Stopping…" for a reason, and closing on top of it hides
            // work that is still running and still holding the model.
            isWaitingToClose = true
            onConfirmedClose()
            waitForIdle(then: sender, since: Date())
            return false
        }

        /// Polls rather than observing: `isBusy` is a closure over SwiftUI
        /// state, and this object is an AppKit delegate with no view to update.
        private func waitForIdle(then window: NSWindow, since started: Date) {
            if isBusy(), Date().timeIntervalSince(started) < Self.stopTimeout {
                Task { @MainActor [weak self, weak window] in
                    try? await Task.sleep(for: .milliseconds(100))
                    guard let self, let window else { return }
                    self.waitForIdle(then: window, since: started)
                }
                return
            }
            isWaitingToClose = false
            isClosing = true
            window.close()
        }

        // Forward every other NSWindowDelegate call to SwiftUI's delegate.
        override func responds(to aSelector: Selector!) -> Bool {
            super.responds(to: aSelector)
                || (previousDelegate?.responds(to: aSelector) ?? false)
        }

        override func forwardingTarget(for aSelector: Selector!) -> Any? {
            if super.responds(to: aSelector) { return nil }
            return previousDelegate
        }
    }
}
