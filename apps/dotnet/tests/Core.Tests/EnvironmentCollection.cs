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

using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Tests that read or write environment variables, run one at a time.
/// </summary>
/// <remarks>
/// Environment variables are process-global, and xunit runs test classes in
/// parallel. Two classes touching <c>XDG_DATA_HOME</c> — one setting it to a
/// temporary trash root, another asserting the default it resolves to — will
/// stomp on each other, and did: three tests failed on Linux, including one
/// that had passed for two milestones. Windows never showed it, because
/// nothing there consults XDG.
/// </remarks>
[CollectionDefinition("environment")]
public sealed class EnvironmentCollection;
