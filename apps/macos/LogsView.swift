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
