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

using Bunyi.Core.Diagnostics;
using Bunyi.Core.Runtime;

namespace Bunyi.Core.Models;

public sealed partial class ModelDownloader
{
    private sealed record AssetPlan(DownloadAsset Asset, string Folder, bool Complete,
        IReadOnlyList<ModelFile> Files, IReadOnlyDictionary<string, long> Sizes,
        long? Total, long ExistingBytes, long RequiredSpace);

    /// <summary>Plans all assets before any transfer, then downloads sequentially without loading.</summary>
    public async Task<IReadOnlyList<DownloadedAsset>> DownloadAssetsAsync(
        IReadOnlyList<DownloadAsset> assets, string modelsRoot,
        IProgress<AggregateDownloadProgress>? progress, CancellationToken ct,
        ISystemProbe? probe = null)
    {
        var plans = new List<AssetPlan>();
        long completed = 0;
        for (var i = 0; i < assets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var asset = assets[i];
            var folder = FolderFor(asset.Source, modelsRoot);
            progress?.Report(new(DownloadPhase.Resolving, asset.Id, i + 1, assets.Count, completed, null));
            if (Inspect(folder, asset.Layout).IsComplete)
            {
                var bytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Sum(p => new FileInfo(p).Length);
                plans.Add(new(asset, folder, true, [], new Dictionary<string, long>(), bytes, bytes, 0));
                completed += bytes;
                continue;
            }
            var files = await ResolveFileListAsync(asset.Source, asset.Layout, null, ct).ConfigureAwait(false);
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            long requiredSpace = 0;
            long reusableBytes = 0;
            foreach (var file in files)
            {
                progress?.Report(new(DownloadPhase.Sizing, asset.Id, i + 1, assets.Count,
                    completed, null, CurrentFile: file.RelativePath));
                var size = await _files.SizeOfAsync(UriFor(asset.Source, file.RelativePath), ct).ConfigureAwait(false);
                if (size is not { } length) continue;
                sizes[file.RelativePath] = length;
                // Conservatively allow a full replacement when the old file cannot be verified.
                var destination = Path.Combine(folder, file.RelativePath);
                var reusable = File.Exists(destination) && new FileInfo(destination).Length == length;
                if (reusable && file.Sha256 is not null)
                    reusable = string.Equals(await HttpFileDownloader.Sha256OfFileAsync(destination, ct)
                        .ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!reusable) requiredSpace += length;
                else reusableBytes += length;
            }
            if (sizes.Count != files.Count)
                requiredSpace = Math.Max(requiredSpace, Math.Max(0, asset.Layout.ApproxDownloadBytes - reusableBytes));
            plans.Add(new(asset, folder, false, files, sizes,
                sizes.Count == files.Count ? sizes.Values.Sum() : null, 0, requiredSpace));
        }
        var required = plans.Sum(p => p.RequiredSpace);
        var available = (probe ?? new SystemProbe()).FreeSpaceBytes(modelsRoot);
        if (available is { } free && free < required)
            throw new IOException($"Not enough disk space for the models: at least {required} bytes needed, {free} available.");
        long? total = plans.All(p => p.Total.HasValue) ? plans.Sum(p => p.Total!.Value) : null;
        var results = new List<DownloadedAsset>();
        var highWater = completed;
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            var index = i + 1;
            long itemCompleted = plan.ExistingBytes;
            void Report(DownloadPhase phase, double rate = 0, string? file = null,
                DownloadProgress? download = null)
            {
                highWater = Math.Max(highWater, completed + (plan.Complete ? 0 : itemCompleted));
                var eta = total.HasValue && rate > 0 ? Math.Max(0, total.Value - highWater) / rate : (double?)null;
                progress?.Report(new(phase, plan.Asset.Id, index, plans.Count, highWater, total,
                    rate, eta, file, itemCompleted, plan.Total, download));
            }
            if (!plan.Complete)
            {
                await DownloadAllAsync(plan.Asset.Source, plan.Files, plan.Folder,
                    new AssetProgress(p =>
                    {
                        itemCompleted = Math.Max(itemCompleted, p.BytesReceived + p.BytesReused);
                        Report(p.Phase, p.BytesPerSecond, p.CurrentFile, p);
                    }), ct, plan.Sizes).ConfigureAwait(false);
            }
            Report(DownloadPhase.Verifying);
            var state = Inspect(plan.Folder, plan.Asset.Layout);
            if (!state.IsComplete) throw new InvalidOperationException("The model is not complete: " + state.Describe());
            Report(DownloadPhase.Done);
            if (!plan.Complete) completed += itemCompleted;
            results.Add(new(plan.Asset.Id, plan.Folder, plan.Asset.Source switch
            {
                ModelSource.Repo repo => repo.Id,
                ModelSource.BaseUrl url => url.Url.AbsoluteUri,
                _ => throw new InvalidOperationException("Unknown source."),
            }, true));
        }
        return results;
    }

    private sealed class AssetProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
