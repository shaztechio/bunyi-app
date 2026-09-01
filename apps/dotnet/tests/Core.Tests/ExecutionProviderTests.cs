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

using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Which execution provider the talker runs on, and how that is decided (#143).
/// </summary>
public class ExecutionProviderTests
{
    [Theory]
    [InlineData("cuda", ExecutionProviderChoice.Cuda)]
    [InlineData("CUDA", ExecutionProviderChoice.Cuda)]
    [InlineData("  cuda  ", ExecutionProviderChoice.Cuda)]
    [InlineData("cpu", ExecutionProviderChoice.Cpu)]
    public void BUNYI_EP_forces_a_provider(string value, ExecutionProviderChoice expected)
    {
        // It survives as an override rather than the mechanism: forcing either
        // way is worth keeping for debugging, and forcing CPU is how someone
        // rules the GPU out of a bug report.
        Assert.Equal(expected, OnnxRuntimeEnv.ParseOverride(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("directml")]
    [InlineData("gpu")]
    public void Anything_else_means_detect(string? value)
    {
        // Null is "decide for yourself", NOT "use the CPU". The old code mapped
        // every unrecognised value to CPU, which is why the 3.7x was reachable
        // only by someone who already knew the magic word.
        Assert.Null(OnnxRuntimeEnv.ParseOverride(value));
    }

    [Fact]
    public void Detection_answers_rather_than_throwing()
    {
        // It runs on every machine the app starts on, most of which have no
        // NVIDIA card and some of which have a broken CUDA install. A detector
        // that throws on those turns "no acceleration" into "no app".
        var loads = OnnxRuntimeEnv.CudaLoads();

        // The value is the machine's, not ours to assert. That it is a value at
        // all is the whole point.
        Assert.True(loads || !loads);
    }

    [Fact]
    public void Detection_is_false_without_the_CUDA_provider_shipped()
    {
        // The CPU build carries no onnxruntime_providers_cuda.dll, so there is
        // nothing to load however good the card is. This is what CI asserts,
        // and what a normal user's build does.
        //
        // Deliberately not asserted the other way round: on a machine with the
        // CUDA build AND the toolkit this is true, and pinning it to false
        // would make a correct machine fail the suite.
        if (OnnxRuntimeEnv.CudaLoads())
        {
            Assert.True(
                OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
                "CUDA cannot load anywhere but Windows and Linux");
        }
    }

    [Fact]
    public void The_vocoder_is_never_offered_the_GPU()
    {
        // Not configurable, and this is the test that says so out loud: the
        // exported vocoder dies on node_pad_1 under both GPU providers with a
        // negative tensor dimension. See RESEARCH-ONNX.md.
        Assert.True(OnnxRuntimeEnv.VocoderRunsOnCpu);
    }

    [Fact]
    public void CPU_options_are_always_available()
    {
        // The fallback path has to work on a machine where nothing else does.
        using var options = OnnxRuntimeEnv.CreateSessionOptions(ExecutionProviderChoice.Cpu);

        Assert.NotNull(options);
    }
}
