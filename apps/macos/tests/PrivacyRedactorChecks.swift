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

@main
enum PrivacyRedactorChecks {
    static func main() {
        precondition(PrivacyRedactor.modelSource(
            "org/model") == "org/model")
        precondition(PrivacyRedactor.modelSource(
            "https://alice:secret@example.com:8443/models/qwen?token=abc#key")
            == "https://example.com:8443/models/qwen")
        precondition(PrivacyRedactor.modelSource(
            "https://example.com/models/qwen?X-Amz-Signature=secret")
            == "https://example.com/models/qwen")
    }
}
