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

using Microsoft.ML.OnnxRuntime;

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
    /// The provider the talker graphs run on, decided once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Detected, not configured.</b> It was read from <c>BUNYI_EP</c>, which
    /// made a measured 3.7x reachable only by someone who read the source and
    /// found the variable. The variable survives as an override — forcing
    /// either provider while debugging is worth keeping — but it is no longer
    /// how the answer is normally arrived at.
    /// </para>
    /// <para>
    /// DirectML is deliberately absent. On an RTX 4090 it was slower than plain
    /// CPU in every configuration that worked, and crashed in the ones that did
    /// not — so it is not offered, and ONNX Runtime is not held at the 1.24.4
    /// ceiling that package would impose.
    /// </para>
    /// </remarks>
    public static ExecutionProviderChoice Current =>
        _cudaFailed ? ExecutionProviderChoice.Cpu : Decided.Value;

    /// <summary>
    /// Set when a CUDA session throws after detection said it would not.
    /// </summary>
    /// <remarks>
    /// Detection can be right about the provider loading and still be wrong
    /// about this machine finishing a run on it. A generation must never fail
    /// because an accelerator was unavailable, so the first failure drops the
    /// whole process to the CPU rather than failing the run — and it is
    /// remembered, because retrying per session would pay the same cost again
    /// on every model load.
    /// </remarks>
    private static volatile bool _cudaFailed;

    private static readonly Lazy<ExecutionProviderChoice> Decided =
        new(Decide, isThreadSafe: true);

    /// <summary>Records that CUDA failed in use, dropping to the CPU.</summary>
    public static void CudaFailed() => _cudaFailed = true;

    private static ExecutionProviderChoice Decide() =>
        ParseOverride(Environment.GetEnvironmentVariable("BUNYI_EP"))
        ?? (CudaLoads() ? ExecutionProviderChoice.Cuda : ExecutionProviderChoice.Cpu);

    /// <summary>The override, or null to detect.</summary>
    internal static ExecutionProviderChoice? ParseOverride(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "cuda" => ExecutionProviderChoice.Cuda,
            "cpu" => ExecutionProviderChoice.Cpu,
            _ => null,
        };

    /// <summary>
    /// Whether this machine can actually build a CUDA session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cheap gate first, then the only answer that counts. <b>The cheap gate is
    /// not sufficient on its own</b>: with the CUDA Toolkit off the PATH,
    /// <c>GetAvailableProviders</c> still lists
    /// <c>CUDAExecutionProvider</c> and creating the options then fails with
    /// "Error loading onnxruntime_providers_cuda.dll which depends on
    /// cublasLt64_13.dll which is missing". Measured on an RTX 4090; a check
    /// that trusted the list would enable CUDA on every machine that has the
    /// provider shipped beside it, which is every machine running the CUDA
    /// build.
    /// </para>
    /// <para>
    /// Building the options is itself definitive — it is what loads the
    /// provider library — so no model file is needed to ask.
    /// </para>
    /// </remarks>
    internal static bool CudaLoads()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;

        try
        {
            if (!OrtEnv.Instance().GetAvailableProviders()
                .Contains("CUDAExecutionProvider", StringComparer.Ordinal))
            {
                return false;
            }

            using var options = SessionOptions.MakeSessionOptionWithCudaProvider(0);
            return true;
        }
        catch (Exception)
        {
            // Every failure means the same thing here — the provider is not
            // usable on this machine — and none of them should stop the app.
            return false;
        }
    }

    /// <summary>Session options for a provider, for callers that own sessions.</summary>
    public static SessionOptions CreateSessionOptions(ExecutionProviderChoice choice) =>
        choice == ExecutionProviderChoice.Cuda
            ? SessionOptions.MakeSessionOptionWithCudaProvider(0)
            : new SessionOptions();
}
