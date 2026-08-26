// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>One blend shape: what a named expression does to the vertices it touches.</summary>
/// <remarks>
///     <para>
///         <a href="../../docs/plan/33-character-creator.md">Doc 33</a> § D4, which is where the shape
///         of this was decided and why it is <em>sparse</em>: a brow-raise moves a few hundred vertices
///         of a forty-thousand-vertex face, and the dense form would store thirty-nine thousand seven
///         hundred zero deltas per target. Twenty targets stored densely is more bytes than the mesh.
///     </para>
///     <para>
///         <b>An entry is a vertex index and two deltas, and the deltas are quantised against a range
///         this target carries.</b> Sixteen-bit signed normals against
///         <see cref="PositionScale" />/<see cref="NormalScale" /> rather than floats: a delta's
///         magnitude is bounded by the shape's own extent, so the quantum is a ten-thousandth of the
///         largest movement in <em>this</em> target — which for a face is a few micrometres — and the
///         entry is sixteen bytes instead of twenty-eight.
///     </para>
///     <para>
///         ⚠ <b>A per-target scale, and not a per-mesh one.</b> A model with a jaw-open worth ten
///         centimetres and an eyelid worth two millimetres shares one range only by giving the eyelid
///         fifty times the quantisation error it needs. The scale costs four bytes a target; sharing
///         one costs precision on every small shape, which is every shape that matters on a face.
///     </para>
///     <para>
///         ⚠ <b>The normal delta is stored as a delta and is not renormalised anywhere.</b> A morphed
///         normal is <c>n + Σ wᵢ Δnᵢ</c>, whose length is not one and is not meant to be — see
///         <see cref="MorphKernel" /> for why nothing in the pre-pass normalises it, and why that is
///         the safe choice rather than the lazy one.
///     </para>
///     <para>
///         <b>Sorted by index, ascending.</b> The scatter writes in index order, so a sorted entry list
///         is a coalesced write; and two targets sorted the same way are two runs a merge could walk in
///         step, which is what the union optimisation named in
///         <see cref="MorphKernel" />'s remarks would need.
///     </para>
/// </remarks>
[DataContract("MorphTarget")]
public sealed record MorphTargetData {
    /// <summary>The largest magnitude a quantised component can carry.</summary>
    /// <remarks>
    ///     32767 and not 32768, so that <c>+scale</c> and <c>−scale</c> are both exactly representable
    ///     and a delta equal to the target's own range round-trips bit-exactly. The asymmetric form
    ///     would make the positive extreme land a quantum short of where it was authored, which is the
    ///     one value a test is most likely to use.
    /// </remarks>
    public const int Quantum = 32767;

    /// <summary>What the shape is called — <c>browRaise</c>, <c>jawOpen</c>, an ARKit name.</summary>
    /// <remarks>
    ///     The name is the binding surface: an animation channel names a shape, not a slot, so that
    ///     re-exporting a mesh with the shapes in a different order does not silently re-target every
    ///     curve on the character.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which vertices this target moves, ascending.</summary>
    public int[] Indices { get; set; } = [];

    /// <summary>The largest position-delta component in this target, in the mesh's units.</summary>
    public float PositionScale { get; set; }

    /// <summary>Three quantised components per entry, against <see cref="PositionScale" />.</summary>
    public short[] Positions { get; set; } = [];

    /// <summary>The largest normal-delta component in this target.</summary>
    public float NormalScale { get; set; }

    /// <summary>
    ///     Three quantised components per entry against <see cref="NormalScale" />, or empty.
    /// </summary>
    /// <remarks>
    ///     Empty says the source carried no normal deltas — <see cref="MeshData" />'s rule, for the
    ///     same reason: an array of zeros says every normal delta is zero, which is a different claim
    ///     and one a compiler could not tell from a bug.
    /// </remarks>
    public short[] Normals { get; set; } = [];

    /// <summary>How many vertices this target moves.</summary>
    public int Count => Indices.Length;

    /// <summary>Whether it carries normal deltas as well as position deltas.</summary>
    public bool HasNormals => Normals.Length > 0;

    /// <summary>What this target costs, resident, not counting its name.</summary>
    /// <remarks>
    ///     Sixteen bytes an entry with normals, ten without: four for the index, six for each
    ///     quantised triple. Doc 33's cost line, made checkable rather than asserted — a head with
    ///     twenty targets each touching four thousand vertices is 1.28 MB, which is why the answer to
    ///     "is it resident" is yes and the answer for a crowd is one shared mesh.
    /// </remarks>
    public long SizeInBytes =>
        (Indices.Length * (long)sizeof(int))
        + (Positions.Length * (long)sizeof(short))
        + (Normals.Length * (long)sizeof(short));

    /// <summary>The position delta of one entry, dequantised.</summary>
    /// <param name="entry">Which entry, in <c>[0, <see cref="Count" />)</c>.</param>
    /// <returns>The delta, in the mesh's units.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry is outside the target.</exception>
    public Vector3 PositionDelta(int entry) {
        ArgumentOutOfRangeException.ThrowIfNegative(entry);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entry, Count);

        return Dequantize(Positions, entry, PositionScale);
    }

    /// <summary>The normal delta of one entry, dequantised. Zero where there are none.</summary>
    /// <param name="entry">Which entry, in <c>[0, <see cref="Count" />)</c>.</param>
    /// <returns>The delta.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry is outside the target.</exception>
    public Vector3 NormalDelta(int entry) {
        ArgumentOutOfRangeException.ThrowIfNegative(entry);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entry, Count);

        return HasNormals ? Dequantize(Normals, entry, NormalScale) : Vector3.Zero;
    }

    /// <summary>Builds a target from deltas that are already sparse.</summary>
    /// <param name="name">What the shape is called.</param>
    /// <param name="indices">The vertices it moves, ascending.</param>
    /// <param name="positions">One position delta per index.</param>
    /// <param name="normals">One normal delta per index, or empty.</param>
    /// <returns>The quantised target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is null.</exception>
    /// <exception cref="ArgumentException">The spans disagree in length.</exception>
    public static MorphTargetData Encode(
        string name,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals
    ) {
        ArgumentNullException.ThrowIfNull(name);

        if (positions.Length != indices.Length) {
            throw new ArgumentException(
                $"'{name}' has {indices.Length} indices and {positions.Length} position deltas.",
                nameof(positions)
            );
        }

        if (normals.Length != 0 && normals.Length != indices.Length) {
            throw new ArgumentException(
                $"'{name}' has {indices.Length} indices and {normals.Length} normal deltas. A target "
                + "carries a normal delta for every vertex it moves, or none at all.",
                nameof(normals)
            );
        }

        var positionScale = Range(positions);
        var normalScale = Range(normals);

        return new() {
            Name = name,
            Indices = indices.ToArray(),
            PositionScale = positionScale,
            Positions = Quantize(positions, positionScale),
            NormalScale = normalScale,
            Normals = normals.Length == 0 ? [] : Quantize(normals, normalScale)
        };
    }

    /// <summary>Builds a target from one delta per vertex, dropping the ones that do not move.</summary>
    /// <param name="name">What the shape is called.</param>
    /// <param name="positions">One position delta per mesh vertex.</param>
    /// <param name="normals">One normal delta per mesh vertex, or empty.</param>
    /// <param name="threshold">
    ///     How far a vertex has to move to be kept, in the mesh's units. A vertex under it in
    ///     <em>both</em> deltas is dropped.
    /// </param>
    /// <returns>The sparse quantised target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="normals" /> disagrees in length.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The threshold is absolute and is the caller's, because there is no scale-free
    ///         answer.</b> A millimetre is a rounding error on a building and the whole of an eyelid
    ///         shape; an importer knows the unit scale it applied and this does not. What it must not
    ///         be is zero-by-default: an exporter writes a delta for every vertex of the mesh, so a
    ///         zero threshold keeps every one of them and there is nothing sparse about the result.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both deltas are tested, not just the position.</b> A shape that only re-shades —
    ///         a crease that darkens without moving — is a normal-only target, and testing the
    ///         position alone would throw all of it away and leave an empty target that looks like a
    ///         file with no shapes in it.
    ///     </para>
    /// </remarks>
    public static MorphTargetData Sparsify(
        string name,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        float threshold
    ) {
        ArgumentNullException.ThrowIfNull(name);

        if (normals.Length != 0 && normals.Length != positions.Length) {
            throw new ArgumentException(
                $"'{name}' has {positions.Length} position deltas and {normals.Length} normal deltas.",
                nameof(normals)
            );
        }

        var squared = threshold * threshold;

        List<int> kept = [];

        for (var index = 0; index < positions.Length; index++) {
            var moves = positions[index].LengthSquared() > squared;
            var reshades = normals.Length != 0 && normals[index].LengthSquared() > squared;

            if (moves || reshades) {
                kept.Add(index);
            }
        }

        var keptPositions = new Vector3[kept.Count];
        var keptNormals = normals.Length == 0 ? [] : new Vector3[kept.Count];

        for (var entry = 0; entry < kept.Count; entry++) {
            keptPositions[entry] = positions[kept[entry]];

            if (normals.Length != 0) {
                keptNormals[entry] = normals[kept[entry]];
            }
        }

        return Encode(name, [.. kept], keptPositions, keptNormals);
    }

    /// <summary>The largest absolute component over a run of deltas, which is the target's range.</summary>
    static float Range(ReadOnlySpan<Vector3> deltas) {
        var range = 0f;

        foreach (var delta in deltas) {
            range = MathF.Max(range, MathF.Abs(delta.X));
            range = MathF.Max(range, MathF.Abs(delta.Y));
            range = MathF.Max(range, MathF.Abs(delta.Z));
        }

        return range;
    }

    /// <summary>Three signed shorts per delta, against a range.</summary>
    /// <remarks>
    ///     ⚠ A zero range is a target whose deltas are all zero, and dividing by it would write
    ///     <c>NaN</c> into every entry — which decodes back to <c>NaN</c> and turns the whole mesh
    ///     inside out the moment the weight leaves zero. Zeros are what a zero range quantises to.
    /// </remarks>
    static short[] Quantize(ReadOnlySpan<Vector3> deltas, float range) {
        var packed = new short[deltas.Length * 3];

        if (range <= 0f) {
            return packed;
        }

        var scale = Quantum / range;

        for (var index = 0; index < deltas.Length; index++) {
            packed[(index * 3) + 0] = Component(deltas[index].X, scale);
            packed[(index * 3) + 1] = Component(deltas[index].Y, scale);
            packed[(index * 3) + 2] = Component(deltas[index].Z, scale);
        }

        return packed;
    }

    static short Component(float value, float scale) =>
        (short)Math.Clamp((int)MathF.Round(value * scale), -Quantum, Quantum);

    /// <summary>
    ///     One entry's triple, dequantised — and the arithmetic the compute kernel transliterates.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>component × (scale / Quantum)</c>, in that association, on both processors.</b> The
    ///     other one — <c>(component × scale) / Quantum</c> — is a different float, because the
    ///     intermediate rounds differently, and the difference shows as a parity test that fails in the
    ///     last bit on some entries and not others. <c>MorphScatter.rvn</c> spells it the same way for the
    ///     same reason.
    /// </remarks>
    static Vector3 Dequantize(short[] packed, int entry, float scale) {
        var step = scale / Quantum;

        return new(
            packed[(entry * 3) + 0] * step,
            packed[(entry * 3) + 1] * step,
            packed[(entry * 3) + 2] * step
        );
    }
}
