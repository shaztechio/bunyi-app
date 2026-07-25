// Zip backup/restore of the models folder, stored (no compression),
// with progress + stop and volume-aware save. Mirrors macOS BackupManager.
// Spec: /spec/FEATURES.md §6, /spec/DATA-FORMATS.md (backup archive).
namespace Qwen3TtsStudio.Core;

public sealed class BackupManager(LogStore log)
{
    /// <summary>
    /// Archive the models folder to a single .zip, STORED (no compression):
    /// weights are incompressible, so storing is far faster and lets a
    /// determinate bar track the archive. Off the UI thread; cancellable.
    /// .NET: use System.IO.Compression with CompressionLevel.NoCompression.
    /// </summary>
    public Task BackupAsync(string modelsFolder, string destinationZip, IProgress<double> progress, CancellationToken ct)
        => throw new NotImplementedException("Spec §6.");

    /// <summary>
    /// Unpack and merge per-repo into the models folder, skipping repos
    /// already present (never clobber). Validate a models/ tree exists first.
    /// </summary>
    public Task RestoreAsync(string sourceZip, string modelsFolder, IProgress<double> progress, CancellationToken ct)
        => throw new NotImplementedException("Spec §6.");
}
