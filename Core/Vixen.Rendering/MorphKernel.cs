// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>Applying blend-shape weights to a vertex stream — the arithmetic, written once.</summary>
/// <remarks>
///     <para>
///         <b>The CPU half of <c>Raven/Library/Pipeline/MorphScatter.rvn</c>, and the reason both exist.</b>
///         <c>MeshletPages</c> says it best about its own pair: two shaders that agree with a third
///         party agree with each other; two that are only ever compared with each other agree on
///         whatever they both get wrong. So the scatter is written here as ordinary C#, the compute
///         kernel is a transliteration of it, and <c>MorphScatterDeviceTests</c> holds the two to the
///         same numbers on a device.
///     </para>
///     <para>
///         <b>What it does is one line of arithmetic and one decision.</b> The line is
///         <c>v += w·Δ</c> per entry per active target. The decision — the one that makes this a
///         pre-pass rather than a loop in every vertex stage — is
///         <a href="../../docs/plan/33-character-creator.md">doc 33</a> § D4's: the morphed vertices
///         go in a buffer, and that buffer is what the shading pass, the shadow pass, the velocity
///         pass and the depth pre-pass all read. A vertex shader that morphed inline would do the work
///         four times and, worse, could disagree with itself between passes — which is the bug that
///         shows up as a face whose shadow does not match it.
///     </para>
///     <para>
///         ⚠ <b>Nothing here renormalises the morphed normal, and that is deliberate twice over.</b>
///         Once for correctness: a target may cancel a normal exactly — <c>Δn = −n</c> at full weight
///         is a legitimate authored shape — and normalising a zero vector is precisely the case
///         <c>Vector3.Normalize</c> answers with infinities, because its tolerance is an absolute
///         <c>1e-6</c> and not a relative one. Once for parity: <c>rsqrt</c> and <c>1/sqrt</c> are not
///         the same function, so a normalise here would be a divergence between the two processors
///         that has nothing to do with morphing. The consumer already does it safely —
///         <c>ForwardPlus</c>'s fragment stage calls <c>Math.SafeNormalize</c> on the interpolated
///         normal, whose tolerance is <c>1e-4</c> and whose degenerate answer is zero rather than a
///         NaN.
///     </para>
///     <para>
///         ⚠ <b>One dispatch per active target, not one per instance</b>, which is where the
///         implementation and § D4's sketch differ and it is worth saying why. Two targets may move
///         the same vertex — that is what a corrective <em>is</em> — so a single dispatch over the
///         concatenated entries would have two invocations read-modify-writing one vertex, and the
///         answer would depend on which of them won. Vixen's Raven has no float atomic to fix that
///         with. A target's own indices are distinct by construction, so a dispatch per target with a
///         barrier between is race-free and costs one dispatch per <em>active</em> shape — twenty for
///         a face, not one per instance. The single-dispatch form needs the deltas re-indexed by
///         vertex at import time, which is a format change and is named as owed rather than guessed
///         at here.
///     </para>
/// </remarks>
public static class MorphKernel {
    /// <summary>How many 32-bit words one entry occupies in the buffer the kernel reads.</summary>
    /// <remarks>
    ///     Four: the vertex index, then the six quantised components two to a word. Sixteen bytes,
    ///     which is the entry size <see cref="MorphTargetData.SizeInBytes" /> reports and the stride
    ///     <c>MorphScatter.rvn</c>'s <c>MorphEntry</c> has under std430.
    /// </remarks>
    public const int EntryWords = 4;

    /// <summary>Morphs a vertex stream by a set of weights.</summary>
    /// <param name="source">The mesh's own vertices, unmorphed.</param>
    /// <param name="targets">The mesh's blend shapes.</param>
    /// <param name="weights">One weight per target. Shorter is read as zero for the rest.</param>
    /// <param name="into">Where the morphed vertices go. May be <paramref name="source" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="targets" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="into" /> is shorter than the source.</exception>
    /// <remarks>
    ///     ⚠ <b>A weight of exactly zero is skipped rather than added</b>, which matches what the
    ///     dispatcher does: an inactive shape costs no dispatch, so the reference must not quietly
    ///     touch vertices the device never wrote. Every other weight is applied, including a negative
    ///     one — an exporter that authored a shape as the inverse of its neighbour relies on it.
    /// </remarks>
    public static void Apply(
        ReadOnlySpan<SurfaceVertex> source,
        IReadOnlyList<MorphTargetData> targets,
        ReadOnlySpan<float> weights,
        Span<SurfaceVertex> into
    ) {
        ArgumentNullException.ThrowIfNull(targets);

        if (into.Length < source.Length) {
            throw new ArgumentException(
                $"The mesh has {source.Length} vertices and the destination is {into.Length}.",
                nameof(into)
            );
        }

        source.CopyTo(into[..source.Length]);

        for (var index = 0; index < targets.Count; index++) {
            var weight = index < weights.Length ? weights[index] : 0f;

            if (weight != 0f) {
                Accumulate(targets[index], weight, into[..source.Length]);
            }
        }
    }

    /// <summary>Adds one target's deltas, weighted, into a vertex stream already holding the base.</summary>
    /// <param name="target">The shape.</param>
    /// <param name="weight">How much of it.</param>
    /// <param name="into">The stream, one vertex per mesh vertex.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The target names a vertex the stream does not have.
    /// </exception>
    /// <remarks>
    ///     This is the body of the compute kernel, one invocation per entry, in C#. Keeping it
    ///     separate from <see cref="Apply" /> is what lets the device test compare a single dispatch
    ///     against a single call instead of against a whole frame's worth of them.
    /// </remarks>
    public static void Accumulate(MorphTargetData target, float weight, Span<SurfaceVertex> into) {
        ArgumentNullException.ThrowIfNull(target);

        for (var entry = 0; entry < target.Count; entry++) {
            var vertex = target.Indices[entry];

            if ((uint)vertex >= (uint)into.Length) {
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    $"'{target.Name}' moves vertex {vertex} and the mesh has {into.Length}. The target "
                    + "belongs to a different mesh, or to a version of it from before an edit changed "
                    + "the vertex count."
                );
            }

            into[vertex].Position += target.PositionDelta(entry) * weight;
            into[vertex].Normal += target.NormalDelta(entry) * weight;
        }
    }

    /// <summary>One target's entries in the layout <c>MorphScatter.rvn</c> reads.</summary>
    /// <param name="target">The shape.</param>
    /// <returns>Four words per entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public static uint[] Pack(MorphTargetData target) {
        ArgumentNullException.ThrowIfNull(target);

        var words = new uint[target.Count * EntryWords];
        Pack(target, words);

        return words;
    }

    /// <summary>Writes one target's entries into a span, in the layout <c>MorphScatter.rvn</c> reads.</summary>
    /// <param name="target">The shape.</param>
    /// <param name="into">At least <c>Count × <see cref="EntryWords" /></c> words.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="into" /> is too short.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The quantised components go across the wire unchanged, and that is the point.</b>
    ///         The kernel dequantises with the same two steps this side does, from the same sixteen
    ///         bits — so the only thing left for the two processors to disagree about is float
    ///         multiplication, which they do not. Unpacking to floats here instead would make the
    ///         device buffer 28 bytes an entry rather than 16 and would move the rounding to a place
    ///         the test could not see.
    ///     </para>
    ///     <para>
    ///         A target with no normal deltas packs zeros into their half. Empty means "the source had
    ///         none" to a compiler; to a dispatch it has to mean a delta, and the delta is zero.
    ///     </para>
    /// </remarks>
    public static void Pack(MorphTargetData target, Span<uint> into) {
        ArgumentNullException.ThrowIfNull(target);

        var needed = target.Count * EntryWords;

        if (into.Length < needed) {
            throw new ArgumentException(
                $"'{target.Name}' has {target.Count} entries and needs {needed} words; the span is "
                + $"{into.Length}.",
                nameof(into)
            );
        }

        for (var entry = 0; entry < target.Count; entry++) {
            var at = entry * EntryWords;
            var normals = target.HasNormals;

            into[at + 0] = (uint)target.Indices[entry];

            into[at + 1] = Word(target.Positions[(entry * 3) + 0], target.Positions[(entry * 3) + 1]);

            into[at + 2] = Word(
                target.Positions[(entry * 3) + 2],
                normals ? target.Normals[(entry * 3) + 0] : (short)0
            );

            into[at + 3] = normals
                ? Word(target.Normals[(entry * 3) + 1], target.Normals[(entry * 3) + 2])
                : 0u;
        }
    }

    /// <summary>What one quantised unit of a scale is worth, which is the kernel's multiplier.</summary>
    /// <param name="scale">A target's <see cref="MorphTargetData.PositionScale" /> or normal scale.</param>
    /// <returns>The step.</returns>
    /// <remarks>
    ///     Computed here and uploaded, rather than the scale being uploaded and divided on the device.
    ///     One division on the host is one float both processors then agree about exactly; two
    ///     divisions is two chances for them not to.
    /// </remarks>
    public static float Step(float scale) => scale / MorphTargetData.Quantum;

    /// <summary>Two signed shorts in one word, low half first.</summary>
    static uint Word(short low, short high) => (ushort)low | ((uint)(ushort)high << 16);
}
