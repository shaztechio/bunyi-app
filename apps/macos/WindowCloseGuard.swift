//
//  WindowCloseGuard.swift
//  Bunyi
//
//  Intercepts the window's red close button. SwiftUI has no "should close"
//  hook, so this installs an NSWindowDelegate (forwarding everything else to
//  SwiftUI's own delegate). When `isBusy` is true, closing asks for
//  confirmation; confirming runs `onConfirmedClose` (which stops the work).
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

    final class Coordinator: NSObject, NSWindowDelegate {
        var isBusy: () -> Bool
        var onConfirmedClose: () -> Void
        private weak var window: NSWindow?
        private weak var previousDelegate: NSWindowDelegate?

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
            guard isBusy() else { return true }
            let alert = NSAlert()
            alert.messageText = "Stop the current operation?"
            alert.informativeText =
                "Something is still running. Closing this window will stop it."
            alert.addButton(withTitle: "Keep Working")
            alert.addButton(withTitle: "Stop and Close")
            alert.buttons.last?.hasDestructiveAction = true
            let confirmed = alert.runModal() == .alertSecondButtonReturn
            if confirmed { onConfirmedClose() }
            return confirmed
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
