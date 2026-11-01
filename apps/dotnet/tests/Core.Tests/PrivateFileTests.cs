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

using Bunyi.Core.Infrastructure;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class PrivateFileTests
{
    [Fact]
    public void Sensitive_writes_and_appends_preserve_contents_and_restrict_existing_files()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            PrivateFile.WriteAllText(path, "secret");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            }
            PrivateFile.AppendAllText(path, " appended");
            Assert.Equal("secret appended", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            PrivateFile.WriteAllText(path, "replacement");
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
