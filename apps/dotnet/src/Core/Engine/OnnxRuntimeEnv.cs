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

namespace Bunyi.Core.Engine;

/// <summary>Which execution provider the talker graphs run on.</summary>
public enum ExecutionProviderChoice
{
    /// <summary>Works everywhere with no driver story. The shipping default.</summary>
    Cpu,

    /// <summary>
    /// NVIDIA, opt-in.
    /// </summary>
    /// <remarks>
    /// Measured at 3.7x faster than CPU and 5.8 GB lighter, which is the
    /// difference between a feature people use and one they abandon. Not the
    /// default because it needs a CUDA runtime the audience in §'s terms cannot
    /// be asked to install.
    /// </remarks>
    Cuda,
}

/// <summary>
/// How ONNX Runtime sessions are configured (see apps/dotnet/RESEARCH-ONNX.md).
/// </summary>
public static class OnnxRuntimeEnv
{
    /// <summary>
    /// Whether the vocoder may share the talker's execution provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It may not, and this is deliberately not configurable.</b> The
    /// exported vocoder only runs under the CPU provider: DirectML and CUDA
    /// both fail on the same <c>node_pad_1</c>, CUDA reporting a negative
    /// tensor dimension, and in both cases the talker had already produced
    /// every frame correctly. It looks like a shape bug in the export that
    /// happens to be harmless under the CPU kernel.
    /// </para>
    /// <para>
    /// So the vocoder gets its own CPU session whatever the talker uses. That
    /// is the configuration rather than a workaround, and it is why the
    /// pipeline taking a separate vocoder session factory matters.
    /// </para>
    /// </remarks>
    public const bool VocoderRunsOnCpu = true;

    /// <summary>
    /// The provider chosen at build time.
    /// </summary>
    /// <remarks>
    /// DirectML is deliberately absent. On an RTX 4090 it was slower than plain
    /// CPU in every configuration that worked, and crashed in the ones that did
    /// not — so it is not offered, and ONNX Runtime is not held at the 1.24.4
    /// ceiling that package would impose.
    /// </remarks>
    public static ExecutionProviderChoice Current { get; } = Parse(
        Environment.GetEnvironmentVariable("BUNYI_EP"));

    internal static ExecutionProviderChoice Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "cuda" => ExecutionProviderChoice.Cuda,
            _ => ExecutionProviderChoice.Cpu,
        };
}
