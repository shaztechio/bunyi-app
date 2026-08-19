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

namespace Bunyi.Core.Audio;

/// <summary>
/// The log-mel spectrogram the speaker encoder is fed (spec §4).
/// </summary>
/// <remarks>
/// <para>
/// The reference pipeline computes this with librosa, so this has to agree with
/// librosa rather than merely be a reasonable mel spectrogram. Everything below
/// follows what that library actually does — the Slaney mel scale rather than
/// HTK's, its area normalisation, a periodic Hann window — because a filterbank
/// that is close but not the same produces a speaker embedding that is close but
/// not the same, and the clone that follows sounds plausible and is quietly
/// worse. That failure has no symptom to notice, which is why the tests check
/// against numbers librosa itself produced.
/// </para>
/// <para>
/// The parameters are the model's, not ours, and are fixed: 24 kHz, a 1024-point
/// transform every 256 samples, 128 bands from 0 to 12 kHz, uncentred.
/// </para>
/// </remarks>
public static class MelSpectrogram
{
    public const int SampleRate = 24_000;
    public const int FftSize = 1024;
    public const int HopLength = 256;
    public const int WindowLength = 1024;
    public const int MelCount = 128;
    public const double FMin = 0.0;
    public const double FMax = 12_000.0;

    /// <summary>The floor applied before the logarithm, from the reference.</summary>
    public const double Floor = 1e-5;

    /// <summary>Bins in a real spectrum of <see cref="FftSize"/> points.</summary>
    internal const int SpectrumBins = (FftSize / 2) + 1;

    private static readonly double[] Window = HannPeriodic(WindowLength);
    private static readonly double[][] Filters = BuildFilterbank();

    /// <summary>
    /// The log-mel of one mono 24 kHz clip, as the encoder wants it.
    /// </summary>
    /// <remarks>
    /// Uncentred, so the first frame starts at sample zero and nothing is padded:
    /// a clip shorter than one window has no frames at all rather than a frame
    /// of mostly silence.
    /// </remarks>
    public static LogMel Compute(ReadOnlySpan<float> audio)
    {
        var frames = audio.Length < FftSize ? 0 : ((audio.Length - FftSize) / HopLength) + 1;
        var values = new float[frames * MelCount];

        var re = new double[FftSize];
        var im = new double[FftSize];
        var power = new double[SpectrumBins];

        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * HopLength;

            for (var i = 0; i < FftSize; i++)
            {
                re[i] = audio[offset + i] * Window[i];
                im[i] = 0.0;
            }

            Fft(re, im);

            // Power, not magnitude: librosa's default for a mel spectrogram.
            for (var bin = 0; bin < SpectrumBins; bin++)
            {
                power[bin] = (re[bin] * re[bin]) + (im[bin] * im[bin]);
            }

            for (var mel = 0; mel < MelCount; mel++)
            {
                var filter = Filters[mel];
                var sum = 0.0;
                for (var bin = 0; bin < SpectrumBins; bin++)
                {
                    sum += filter[bin] * power[bin];
                }

                values[(frame * MelCount) + mel] = (float)Math.Log(Math.Max(sum, Floor));
            }
        }

        return new LogMel(values, frames, MelCount);
    }

    /// <summary>
    /// A Hann window of the periodic kind — the one for spectral analysis.
    /// </summary>
    /// <remarks>
    /// Divided by <c>n</c> rather than <c>n - 1</c>. The symmetric variant is a
    /// different window, and using it would tilt every frame slightly.
    /// </remarks>
    internal static double[] HannPeriodic(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / n));
        }

        return w;
    }

    /// <summary>
    /// Hertz to mels on the Slaney scale: linear below 1 kHz, logarithmic above.
    /// </summary>
    /// <remarks>
    /// Not the HTK formula. The two disagree across the whole range and librosa
    /// defaults to this one, so the model was trained against this one.
    /// </remarks>
    internal static double HzToMel(double hz)
    {
        const double FSp = 200.0 / 3.0;
        const double MinLogHz = 1000.0;
        const double MinLogMel = MinLogHz / FSp;
        var logStep = Math.Log(6.4) / 27.0;

        return hz < MinLogHz
            ? hz / FSp
            : MinLogMel + (Math.Log(hz / MinLogHz) / logStep);
    }

    /// <summary>The inverse of <see cref="HzToMel"/>.</summary>
    internal static double MelToHz(double mel)
    {
        const double FSp = 200.0 / 3.0;
        const double MinLogHz = 1000.0;
        const double MinLogMel = MinLogHz / FSp;
        var logStep = Math.Log(6.4) / 27.0;

        return mel < MinLogMel
            ? mel * FSp
            : MinLogHz * Math.Exp(logStep * (mel - MinLogMel));
    }

    /// <summary>
    /// The triangular filterbank, one row per band.
    /// </summary>
    /// <remarks>
    /// Each band spans three points on a mel-even grid and is scaled by its
    /// width in hertz — librosa's "slaney" normalisation, which keeps every
    /// band's area equal instead of its peak. Peak normalisation is the common
    /// alternative and would make the low bands far too loud.
    /// </remarks>
    internal static double[][] BuildFilterbank()
    {
        var binFrequency = new double[SpectrumBins];
        for (var i = 0; i < SpectrumBins; i++)
        {
            binFrequency[i] = i * SampleRate / (double)FftSize;
        }

        // MelCount + 2 edges: every band needs a neighbour on each side.
        var edges = new double[MelCount + 2];
        var lo = HzToMel(FMin);
        var hi = HzToMel(FMax);
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = MelToHz(lo + ((hi - lo) * i / (edges.Length - 1)));
        }

        var filters = new double[MelCount][];
        for (var mel = 0; mel < MelCount; mel++)
        {
            var row = new double[SpectrumBins];
            var lowerWidth = edges[mel + 1] - edges[mel];
            var upperWidth = edges[mel + 2] - edges[mel + 1];
            var scale = 2.0 / (edges[mel + 2] - edges[mel]);

            for (var bin = 0; bin < SpectrumBins; bin++)
            {
                var rising = (binFrequency[bin] - edges[mel]) / lowerWidth;
                var falling = (edges[mel + 2] - binFrequency[bin]) / upperWidth;
                var weight = Math.Min(rising, falling);
                if (weight > 0.0) row[bin] = weight * scale;
            }

            filters[mel] = row;
        }

        return filters;
    }

    /// <summary>
    /// In-place radix-2 FFT.
    /// </summary>
    /// <remarks>
    /// Written here rather than taken from a package: it is thirty lines, the
    /// size is always a power of two, and one fewer dependency in the path
    /// between a recording and a voice.
    /// </remarks>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len / 2;
            var angle = -2.0 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);

            for (var start = 0; start < n; start += len)
            {
                var curRe = 1.0;
                var curIm = 0.0;

                for (var k = 0; k < half; k++)
                {
                    var aRe = re[start + k];
                    var aIm = im[start + k];
                    var bRe = (re[start + k + half] * curRe) - (im[start + k + half] * curIm);
                    var bIm = (re[start + k + half] * curIm) + (im[start + k + half] * curRe);

                    re[start + k] = aRe + bRe;
                    im[start + k] = aIm + bIm;
                    re[start + k + half] = aRe - bRe;
                    im[start + k + half] = aIm - bIm;

                    var nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }
    }
}

/// <summary>
/// A log-mel spectrogram, laid out as the encoder's tensor wants it.
/// </summary>
/// <param name="Values">Row-major, frame by frame.</param>
public sealed record LogMel(float[] Values, int Frames, int Bins)
{
    public float this[int frame, int bin] => Values[(frame * Bins) + bin];
}
