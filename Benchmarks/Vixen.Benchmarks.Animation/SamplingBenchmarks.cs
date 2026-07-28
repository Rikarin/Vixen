// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Core.Mathematics;

namespace Vixen.Benchmarks.Animation;

/// <summary>
///     What a clip costs to sample, against how many keys its tracks carry.
/// </summary>
/// <remarks>
///     <para>
///         The number the key index exists for. A track's lookup is a table read and about one
///         comparison whatever its length, so <see cref="Sequential" /> should be flat as
///         <see cref="Keys" /> grows — where a binary search would climb with its logarithm and a
///         cursor would be flat forwards and terrible on <see cref="Random" />.
///     </para>
///     <para>
///         <see cref="Random" /> is not a contrived case. A blend tree crossing a threshold, a
///         transition starting a state at an offset, and a scrub in the editor all seek, and a
///         cursor is at its worst in exactly those places.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class SamplingBenchmarks {
    Skeleton skeleton = null!;
    AnimationClip clip = null!;
    BoneTransform[] pose = null!;
    float[] seeks = null!;
    float time;
    int cursor;

    /// <summary>How many keys each of the clip's tracks carries.</summary>
    /// <remarks>
    ///     Thirty is a one-second clip at thirty hertz. Nine hundred is a thirty-second cutscene,
    ///     which is where a lookup that grows with the key count starts to be worth naming.
    /// </remarks>
    [Params(30, 300, 900)]
    public int Keys { get; set; }

    [GlobalSetup]
    public void Setup() {
        skeleton = Rigs.Humanoid();
        clip = AnimationClip.Create(Rigs.Clip(skeleton, "Walk", Keys / 30f, 30), skeleton);
        pose = new BoneTransform[skeleton.JointCount];
        seeks = new float[1024];

        // A fixed pseudo-random walk over the clip, generated once so every run seeks identically.
        var state = 12345u;

        for (var index = 0; index < seeks.Length; index++) {
            state = (state * 1664525u) + 1013904223u;
            seeks[index] = state / (float)uint.MaxValue * clip.Duration;
        }
    }

    /// <summary>Playing forwards, which is what a frame does.</summary>
    [Benchmark(Baseline = true)]
    public void Sequential() {
        time = MathUtil.Repeat(time + (1f / 60f), clip.Duration);
        clip.Sample(time, pose);
    }

    /// <summary>Seeking, which is what a transition offset and an editor scrub do.</summary>
    /// <remarks>
    ///     One <c>Sample</c> per operation, the same as <see cref="Sequential" />, so the two are
    ///     directly comparable. Any difference between them is the cost of not knowing where the
    ///     last sample landed — which is the whole of what a cursor would have bought.
    /// </remarks>
    [Benchmark]
    public void Random() {
        cursor = (cursor + 1) & (seeks.Length - 1);
        clip.Sample(seeks[cursor], pose);
    }
}
