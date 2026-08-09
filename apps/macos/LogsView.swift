//
//  LogsView.swift
//  Bunyi
//

import AppKit
import SwiftUI

struct LogsView: View {
    private let store = LogStore.shared

    var body: some View {
        Group {
            if store.entries.isEmpty {
                Text("No log messages yet.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(store.entries) { entry in
                                Text("\(entry.date.formatted(date: .omitted, time: .standard))  \(entry.message)")
                                    .font(.system(.caption, design: .monospaced))
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .id(entry.id)
                            }
                        }
                        .padding(10)
                        .textSelection(.enabled)
                    }
                    .defaultScrollAnchor(.bottom)
                    .onChange(of: store.entries.count) {
                        if let last = store.entries.last {
                            proxy.scrollTo(last.id, anchor: .bottom)
                        }
                    }
                }
            }
        }
        .frame(minWidth: 520, minHeight: 300)
        .toolbar {
            ToolbarItem {
                Button("Copy") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(store.text, forType: .string)
                }
                .disabled(store.entries.isEmpty)
            }
            ToolbarItem {
                Button("Clear") { store.clear() }
                    .disabled(store.entries.isEmpty)
            }
        }
        .navigationTitle("Logs")
    }
}

#Preview {
    LogsView()
}
