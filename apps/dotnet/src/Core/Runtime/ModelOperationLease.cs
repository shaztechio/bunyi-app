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

namespace Bunyi.Core.Runtime;

public sealed class BunyiBusyException(string modelsRoot, Exception? inner = null)
    : IOException("Another Bunyi operation owns this models folder. Unload its model or wait for it to finish.", inner)
{
    public string ModelsRoot { get; } = modelsRoot;
}

/// <summary>An OS-released, cross-process lease. Never delete the lock file: doing so races existing owners.</summary>
public sealed class ModelOperationLease : IDisposable
{
    private FileStream? _stream;
    private ModelOperationLease(FileStream stream) => _stream = stream;

    public static ModelOperationLease Acquire(string modelsRoot)
    {
        var root = Path.GetFullPath(modelsRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".bunyi-operation.lock");
        try
        {
            return new ModelOperationLease(new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None));
        }
        // Windows sharing/lock violations; Unix flock EAGAIN/EWOULDBLOCK.
        // Disk failures remain filesystem errors rather than being labelled busy.
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
        { throw new BunyiBusyException(root, ex); }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
