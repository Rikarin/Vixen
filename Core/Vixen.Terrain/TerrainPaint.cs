// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>
///     The paint kernels: what a stamp does to the layer weights under it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four tools over the selected target layer — [docs/plan/31 § The paint tools].</b>
///         Paint raises its weight and lowers the others proportionally, Smooth averages, Flatten
///         sets it to the brush strength, and Noise scatters it. They are the sculpt tools' shape
///         applied to a different target, which is [§ D12]'s argument for one brush.
///     </para>
///     <para>
///         ⚠ <b>Every one of these goes through <see cref="TerrainWeights.Paint" /> rather than
///         writing a channel.</b> That method is where the sum-to-one invariant lives — raise one
///         layer and the rest come down in proportion, by largest remainder — and a kernel that wrote
///         the byte itself would be a second implementation of the rule, disagreeing with the first
///         by a unit or two per sample. Which is exactly the drift <see cref="TerrainWeights.Verify" />
///         reports and nobody can explain.
///     </para>
///     <para>
///         ⚠ <b>The target layer is an index and the tools do not check it against the brush.</b> A
///         paint layer has no lock and no generator — that is the <em>edit</em> layer stack, which is
///         a different thing with a similar name. What refuses a paint stroke is having no target
///         selected, and that is the editor's question.
///     </para>
/// </remarks>
public static class TerrainPaint {
    /// <summary>Which samples a stamp of this brush can reach.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <returns>The rectangle, clipped to the terrain.</returns>
    public static TerrainRect AffectedRect(
        in TerrainDescription description,
        in TerrainBrush brush,
        in BrushStamp stamp
    ) => TerrainSculpt.AffectedRect(description, brush, stamp);

    /// <summary>Raises a layer's weight under a stamp, lowering the others proportionally.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="amount">
    ///     How much to add at full weight, in weight units. Negative removes it.
    /// </param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    public static TerrainRect Paint(
        Terrain terrain,
        int layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        int amount,
        IBrushMask? mask = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = AffectedRect(terrain.Description, brush, stamp);

        Apply(
            terrain, brush, stamp, mask, rect,
            (x, z, weight) => terrain.Weights.Paint(layer, x, z, (int)MathF.Round(amount * weight))
        );

        return rect;
    }

    /// <summary>Averages a layer's weight under a stamp with its neighbours.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     ⚠ <b>Reads a snapshot, not the channel it is writing</b> — <see cref="TerrainSculpt.Smooth" />'s
    ///     reason, and worse here: a paint write moves <em>every</em> layer at the sample, so
    ///     smoothing in place would average against weights the redistribution had already changed
    ///     twice.
    /// </remarks>
    public static TerrainRect Smooth(
        Terrain terrain,
        int layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        IBrushMask? mask = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = AffectedRect(terrain.Description, brush, stamp);
        var read = rect.Grow(TerrainSculpt.NeighbourMargin);
        var snapshot = Snapshot(terrain, layer, read);

        Apply(
            terrain, brush, stamp, mask, rect,
            (x, z, weight) => {
                var total = 0;

                for (var dz = -1; dz <= 1; dz++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        total += Sample(snapshot, read, x + dx, z + dz);
                    }
                }

                var average = total / 9;
                var current = terrain.Weights.WeightAt(layer, x, z);

                terrain.Weights.Paint(layer, x, z, (int)MathF.Round((average - current) * weight));
            }
        );

        return rect;
    }

    /// <summary>Sets a layer's weight under a stamp to a target coverage.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="target">What the weight becomes at full brush weight, 0…1.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     The brush's strength is the target rather than a rate, which is what makes this tool
    ///     different from holding Paint down: repeated strokes converge on the coverage asked for
    ///     instead of climbing past it.
    /// </remarks>
    public static TerrainRect Flatten(
        Terrain terrain,
        int layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float target,
        IBrushMask? mask = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = AffectedRect(terrain.Description, brush, stamp);
        var wanted = Math.Clamp(target, 0f, 1f) * TerrainWeights.Total;

        Apply(
            terrain, brush, stamp, mask, rect,
            (x, z, weight) => {
                var current = terrain.Weights.WeightAt(layer, x, z);

                terrain.Weights.Paint(layer, x, z, (int)MathF.Round((wanted - current) * weight));
            }
        );

        return rect;
    }

    /// <summary>Scatters a layer's weight under a stamp.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="amount">How far the noise moves the weight, in weight units.</param>
    /// <param name="settings">The noise's shape.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     The same value noise the sculpt tool uses, so a noisy boundary between two grounds lines
    ///     up with a noisy ridge sculpted at the same frequency — which is the whole point of having
    ///     one noise rather than two.
    /// </remarks>
    public static TerrainRect Noise(
        Terrain terrain,
        int layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        int amount,
        in TerrainNoise settings,
        IBrushMask? mask = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = AffectedRect(terrain.Description, brush, stamp);
        var noise = settings;

        Apply(
            terrain, brush, stamp, mask, rect,
            (x, z, weight) =>
                terrain.Weights.Paint(layer, x, z, (int)MathF.Round(noise.At(x, z) * amount * weight))
        );

        return rect;
    }

    static void Apply(
        Terrain terrain,
        in TerrainBrush brush,
        in BrushStamp stamp,
        IBrushMask? mask,
        TerrainRect rect,
        Action<int, int, float> write
    ) {
        if (rect.IsEmpty) {
            return;
        }

        var description = terrain.Description;

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var weight = brush.WeightAt(
                    new Vector2(x * description.MetresPerQuad, z * description.MetresPerQuad),
                    stamp,
                    mask
                );

                if (weight > 0f) {
                    write(x, z, weight);
                }
            }
        }

        // ⚠ The weights, not the heights — but the same tiles. A weightmap upload is per tile for the
        // reason a height upload is, and the renderer reads the same dirty set.
        terrain.Invalidate(rect);
    }

    static byte[] Snapshot(Terrain terrain, int layer, TerrainRect rect) {
        var buffer = new byte[Math.Max(1, rect.Count)];
        var bounds = new TerrainRect(0, 0, terrain.Description.SamplesX, terrain.Description.SamplesZ);
        var clipped = rect.Clip(bounds);

        for (var z = 0; z < rect.Height; z++) {
            for (var x = 0; x < rect.Width; x++) {
                // Clamped rather than clipped, so a stamp at the edge reads the boundary sample
                // repeatedly and smooths as if the terrain continued — TerrainSculpt's rule.
                buffer[(z * rect.Width) + x] = terrain.Weights.WeightAt(
                    layer,
                    Math.Clamp(rect.X + x, clipped.X, Math.Max(clipped.X, clipped.EndX - 1)),
                    Math.Clamp(rect.Z + z, clipped.Z, Math.Max(clipped.Z, clipped.EndZ - 1))
                );
            }
        }

        return buffer;
    }

    static int Sample(byte[] snapshot, TerrainRect rect, int x, int z) {
        var localX = Math.Clamp(x - rect.X, 0, rect.Width - 1);
        var localZ = Math.Clamp(z - rect.Z, 0, rect.Height - 1);

        return snapshot[(localZ * rect.Width) + localX];
    }
}
