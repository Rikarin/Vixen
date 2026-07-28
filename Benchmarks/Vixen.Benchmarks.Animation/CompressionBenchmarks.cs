// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Rendering;

namespace Vixen.Benchmarks.Animation;

/// <summary>
///     What key reduction takes out of a clip an exporter emitted a key a frame for.
/// </summary>
/// <remarks>
///     A build-time pass, so the time is the least interesting column here — what matters is
///     <see cref="Ratio" />, which is how much of the clip survives and therefore how much memory
///     and cache traffic every character playing it pays for. The time is measured anyway because a
///     content build over a thousand clips is somebody's afternoon.
/// </remarks>
[MemoryDiagnoser]
public class CompressionBenchmarks {
    AnimationClipData data = null!;

    /// <summary>How long the clip is, in seconds, at thirty keys a second.</summary>
    [Params(1, 10)]
    public int Seconds { get; set; }

    /// <summary>How much of the clip's keys survive, as a percentage. Reported, not timed.</summary>
    public float Ratio { get; private set; }

    [GlobalSetup]
    public void Setup() {
        var skeleton = Rigs.Humanoid();
        data = Rigs.Clip(skeleton, "Walk", Seconds, 30);

        AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out var report);
        Ratio = report.Ratio * 100f;
    }

    [Benchmark(Baseline = true)]
    public AnimationClipData Default() =>
        AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out _);

    [Benchmark]
    public AnimationClipData Aggressive() =>
        AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Aggressive, out _);
}
