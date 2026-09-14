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

/// Removes authentication material while retaining enough of a model source
/// to diagnose which host and path failed.
public enum PrivacyRedactor {
    public static func modelSource(_ value: String) -> String {
        guard var parts = URLComponents(string: value), parts.scheme != nil else {
            return value
        }
        parts.user = nil
        parts.password = nil
        parts.query = nil
        parts.fragment = nil
        return parts.string ?? "[redacted URL]"
    }
}
