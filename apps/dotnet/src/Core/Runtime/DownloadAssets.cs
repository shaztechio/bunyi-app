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

using Bunyi.Core.Models;

namespace Bunyi.Core.Runtime;

public sealed record DownloadAsset(string Id, ModelSource Source, ModelLayout Layout);
public sealed record DownloadedAsset(string Id, string Folder, string Source, bool IsComplete);
public sealed record AggregateDownloadProgress(DownloadPhase Phase, string ItemId,
    int ItemIndex, int ItemCount, long BytesCompleted, long? BytesTotal,
    double RateBytesPerSecond = 0, double? EtaSeconds = null, string? CurrentFile = null,
    long ItemBytesCompleted = 0, long? ItemBytesTotal = null,
    DownloadProgress? Download = null);
