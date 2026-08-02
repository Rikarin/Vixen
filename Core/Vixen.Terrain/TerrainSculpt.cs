// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>
///     The sculpt kernels: what a stamp does to the ground under it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every one of these reads the composite and writes a layer</b>, and that pairing is
///         [docs/plan/31 § D4]'s warning made structural. Eroding a mountain on a layer above the
///         base has to read what the world <em>is</em>, so the flow is right, and write what this
///         layer <em>adds</em>, so the base survives. Reading the layer instead gives erosion that
///         erases everything below it; writing the composite instead gives an edit the next
///         invalidation discards.
///     </para>
///     <para>
///         <b>Pure functions over a terrain and a stamp.</b> No document, no undo, no dirty tracking
///         — the caller owns those, because it is the caller that knows whether this stamp is one of
///         four hundred in a drag.
///     </para>
/// </remarks>
public static class TerrainSculpt {
    /// <summary>How far a smoothing or erosion kernel reads beyond what it writes.</summary>
    /// <remarks>
    ///     One sample. It is the margin an undo record has to be grown by — see
    ///     <see cref="TerrainRect.Grow" /> — because a record sized to the write cannot restore what
    ///     the read will produce on the next pass.
    /// </remarks>
    public const int NeighbourMargin = 1;

    /// <summary>Which samples a stamp of this brush can reach.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <returns>The rectangle, clipped to the terrain.</returns>
    /// <remarks>
    ///     Public because a caller has to know it <em>before</em> applying anything: an undo record
    ///     holds what the ground was, and asking after the kernel has run records what the kernel
    ///     wrote. <see cref="TerrainStroke.Record" /> is the safe way to use it.
    /// </remarks>
    public static TerrainRect AffectedRect(
        in TerrainDescription description,
        in TerrainBrush brush,
        in BrushStamp stamp
    ) => SampleRectOf(description, brush.FootprintOf(stamp));

    /// <summary>Raises or lowers the ground under a stamp.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write. Must accept the brush.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="metres">How far to move the ground at full weight. Negative lowers.</param>
    /// <param name="mask">The brush's mask, for a shaped stamp.</param>
    /// <returns>The samples it wrote.</returns>
    public static TerrainRect Sculpt(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float metres,
        IBrushMask? mask = null
    ) {
        var (rect, description) = Begin(terrain, layer, brush, stamp);
        var step = metres / description.MetresPerStep;

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => layer.AddDelta(x, z, (int)MathF.Round(step * weight))
        );

        return rect;
    }

    /// <summary>Pulls the ground under a stamp towards a height.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="target">The height to flatten to, in metres.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     The delta needed is measured against the <em>composite</em> and added to the layer's
    ///     existing delta, so flattening on a layer above a mountain flattens the mountain rather
    ///     than flattening this layer's contribution to it.
    /// </remarks>
    public static TerrainRect Flatten(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float target,
        IBrushMask? mask = null
    ) {
        var (rect, description) = Begin(terrain, layer, brush, stamp);
        var stored = description.StoreHeight(target);

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => {
                var current = terrain.CompositeAt(x, z);
                var wanted = (int)MathF.Round(current + ((stored - current) * weight));
                layer.AddDelta(x, z, wanted - current);
            }
        );

        return rect;
    }

    /// <summary>Averages the ground under a stamp with its neighbours.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     ⚠ <b>Reads a snapshot, not the terrain it is writing.</b> Smoothing in place makes the
    ///     result depend on the order the samples are visited — the second sample averages a
    ///     neighbour the first has already moved — which is a directional smear that shows up as a
    ///     ridge running diagonally across every smoothed area.
    /// </remarks>
    public static TerrainRect Smooth(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        IBrushMask? mask = null
    ) {
        var (rect, _) = Begin(terrain, layer, brush, stamp);
        var snapshot = Snapshot(terrain, rect.Grow(NeighbourMargin));
        var read = rect.Grow(NeighbourMargin);

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => {
                var total = 0;

                for (var dz = -1; dz <= 1; dz++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        total += Sample(snapshot, read, x + dx, z + dz);
                    }
                }

                var average = total / 9;
                var current = Sample(snapshot, read, x, z);
                layer.AddDelta(x, z, (int)MathF.Round((average - current) * weight));
            }
        );

        return rect;
    }

    /// <summary>Adds fractal noise to the ground under a stamp.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="amplitude">How far the noise moves the ground, in metres.</param>
    /// <param name="settings">The noise's shape.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    public static TerrainRect Noise(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float amplitude,
        in TerrainNoise settings,
        IBrushMask? mask = null
    ) {
        var (rect, description) = Begin(terrain, layer, brush, stamp);
        var step = amplitude / description.MetresPerStep;
        var noise = settings;

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => layer.AddDelta(x, z, (int)MathF.Round(noise.At(x, z) * step * weight))
        );

        return rect;
    }

    /// <summary>Slides material downhill wherever the slope exceeds the talus angle.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="talus">The steepest slope that holds, as a rise over one quad in metres.</param>
    /// <param name="rate">How much of the excess moves per pass, 0…1.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     Thermal erosion, the textbook form: what is steeper than the talus angle slides. One pass
    ///     per call, because a stroke is many calls and an artist holding the brush down is what
    ///     "more erosion" means — a loop inside would make the tool a batch job with a progress bar.
    /// </remarks>
    public static TerrainRect Erode(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float talus,
        float rate,
        IBrushMask? mask = null
    ) {
        var (rect, description) = Begin(terrain, layer, brush, stamp);
        var read = rect.Grow(NeighbourMargin);
        var snapshot = Snapshot(terrain, read);
        var threshold = Math.Max(0f, talus) / description.MetresPerStep;
        var flow = Math.Clamp(rate, 0f, 1f);

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => {
                var here = Sample(snapshot, read, x, z);
                var lowest = here;

                for (var dz = -1; dz <= 1; dz++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        if (dx != 0 || dz != 0) {
                            lowest = Math.Min(lowest, Sample(snapshot, read, x + dx, z + dz));
                        }
                    }
                }

                var drop = here - lowest;

                if (drop > threshold) {
                    // Half the excess, so material that leaves here has somewhere to arrive; taking
                    // all of it makes a step rather than a slope.
                    var moved = (drop - threshold) * 0.5f * flow * weight;
                    layer.AddDelta(x, z, -(int)MathF.Round(moved));
                }
            }
        );

        return rect;
    }

    /// <summary>Dissolves high ground and deposits it in the low ground beside it.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="rate">How much moves per pass, 0…1.</param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     <para>
    ///         Hydraulic erosion in its cheap form: every sample gives some of its height difference
    ///         to its lowest neighbour and takes some from its highest, which over a stroke carves
    ///         channels rather than merely rounding ridges the way <see cref="Erode" /> does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not mass-conserving and does not claim to be.</b> A true solver carries
    ///         sediment in water and needs a second field, a time step and a stability condition;
    ///         this is a brush an artist holds down. The moment somebody asks for the solver it has
    ///         failed [§ Where the line goes]'s test.
    ///     </para>
    /// </remarks>
    public static TerrainRect Hydro(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        float rate,
        IBrushMask? mask = null
    ) {
        var (rect, _) = Begin(terrain, layer, brush, stamp);
        var read = rect.Grow(NeighbourMargin);
        var snapshot = Snapshot(terrain, read);
        var flow = Math.Clamp(rate, 0f, 1f);

        Apply(
            terrain, layer, brush, stamp, mask, rect,
            (x, z, weight) => {
                var here = Sample(snapshot, read, x, z);
                var lowest = here;
                var highest = here;

                for (var dz = -1; dz <= 1; dz++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        if (dx == 0 && dz == 0) {
                            continue;
                        }

                        var neighbour = Sample(snapshot, read, x + dx, z + dz);
                        lowest = Math.Min(lowest, neighbour);
                        highest = Math.Max(highest, neighbour);
                    }
                }

                // Loses what runs off downhill, gains what runs in from above, weighted so a sample
                // in a channel deepens and one on a shoulder fills.
                var lost = (here - lowest) * 0.25f;
                var gained = (highest - here) * 0.05f;

                layer.AddDelta(x, z, -(int)MathF.Round((lost - gained) * flow * weight));
            }
        );

        return rect;
    }

    /// <summary>Raises or lowers a straight ramp between two points.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which layer to write.</param>
    /// <param name="from">Where the ramp starts, in world XZ.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="fromHeight">The height at the start, in metres.</param>
    /// <param name="toHeight">The height at the end, in metres.</param>
    /// <param name="halfWidth">How far the ramp reaches either side of the line, in metres.</param>
    /// <param name="sideFalloff">
    ///     How much of the half-width is falloff rather than flat, 0…1.
    /// </param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     Not a stamp, which is why it does not take a brush: a ramp is defined by two picked points
    ///     and is applied once. The cosine-blended side falloff is Unreal's, and it is the shape that
    ///     makes a ramp read as cut into the hill rather than laid on top of it.
    /// </remarks>
    public static TerrainRect Ramp(
        Terrain terrain,
        TerrainEditLayer layer,
        Vector2 from,
        Vector2 to,
        float fromHeight,
        float toHeight,
        float halfWidth,
        float sideFalloff = 0.5f
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);

        if (!layer.AcceptsBrush || !(halfWidth > 0f)) {
            return TerrainRect.Empty;
        }

        var description = terrain.Description;
        var axis = to - from;
        var length = axis.Length();

        if (!(length > 0f)) {
            return TerrainRect.Empty;
        }

        var direction = axis / length;
        var reach = halfWidth + length;

        var rect = SampleRect(
            description,
            new(MathF.Min(from.X, to.X) - reach, MathF.Min(from.Y, to.Y) - reach),
            new(MathF.Max(from.X, to.X) + reach, MathF.Max(from.Y, to.Y) + reach)
        );

        var falloff = Math.Clamp(sideFalloff, 0f, 1f);
        var flat = halfWidth * (1f - falloff);

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var point = new Vector2(x * description.MetresPerQuad, z * description.MetresPerQuad);
                var along = Vector2.Dot(point - from, direction);

                if (along < 0f || along > length) {
                    continue;
                }

                var across = MathF.Abs(Vector2.Dot(point - from, new Vector2(-direction.Y, direction.X)));

                if (across >= halfWidth) {
                    continue;
                }

                var weight = across <= flat || falloff <= 0f
                    ? 1f
                    : 0.5f * (1f + MathF.Cos(MathF.PI * ((across - flat) / (halfWidth - flat))));

                var wanted = description.StoreHeight(
                    fromHeight + ((toHeight - fromHeight) * (along / length))
                );

                var current = terrain.CompositeAt(x, z);
                layer.AddDelta(x, z, (int)MathF.Round((wanted - current) * weight));
            }
        }

        terrain.Invalidate(rect);
        return rect;
    }

    /// <summary>Punches or fills holes under a stamp.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="hole">Whether the samples become holes.</param>
    /// <param name="threshold">How much weight is needed to change a sample, 0…1.</param>
    /// <returns>The samples it wrote.</returns>
    /// <remarks>
    ///     Thresholded rather than blended, because a hole is a bit. The threshold is what stops the
    ///     soft edge of the brush from punching a ragged fringe two samples wider than the artist
    ///     aimed at.
    /// </remarks>
    public static TerrainRect PaintHoles(
        Terrain terrain,
        in TerrainBrush brush,
        in BrushStamp stamp,
        bool hole,
        float threshold = 0.5f
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = SampleRectOf(terrain.Description, brush.FootprintOf(stamp));

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                if (WeightAt(terrain.Description, brush, stamp, null, x, z) >= threshold) {
                    terrain.Holes.SetHole(x, z, hole);
                }
            }
        }

        return rect;
    }

    /// <summary>Paints a weight layer under a stamp.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="paintLayer">Which weight layer.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="stamp">Where it landed.</param>
    /// <param name="amount">
    ///     How much to add at full weight, in weight units. Negative removes it.
    /// </param>
    /// <param name="mask">The brush's mask.</param>
    /// <returns>The samples it wrote.</returns>
    public static TerrainRect Paint(
        Terrain terrain,
        int paintLayer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        int amount,
        IBrushMask? mask = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        var rect = SampleRectOf(terrain.Description, brush.FootprintOf(stamp));

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var weight = WeightAt(terrain.Description, brush, stamp, mask, x, z);

                if (weight > 0f) {
                    terrain.Weights.Paint(paintLayer, x, z, (int)MathF.Round(amount * weight));
                }
            }
        }

        return rect;
    }

    static (TerrainRect Rect, TerrainDescription Description) Begin(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);

        return (
            layer.AcceptsBrush ? SampleRectOf(terrain.Description, brush.FootprintOf(stamp)) : TerrainRect.Empty,
            terrain.Description
        );
    }

    static void Apply(
        Terrain terrain,
        TerrainEditLayer layer,
        in TerrainBrush brush,
        in BrushStamp stamp,
        IBrushMask? mask,
        TerrainRect rect,
        Action<int, int, float> write
    ) {
        if (rect.IsEmpty) {
            return;
        }

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                var weight = WeightAt(terrain.Description, brush, stamp, mask, x, z);

                if (weight > 0f) {
                    write(x, z, weight);
                }
            }
        }

        terrain.Invalidate(rect);
    }

    static float WeightAt(
        in TerrainDescription description,
        in TerrainBrush brush,
        in BrushStamp stamp,
        IBrushMask? mask,
        int x,
        int z
    ) =>
        brush.WeightAt(
            new(x * description.MetresPerQuad, z * description.MetresPerQuad),
            stamp,
            mask
        );

    static ushort[] Snapshot(Terrain terrain, TerrainRect rect) {
        var clipped = rect.Clip(terrain.Composite.Bounds);
        var buffer = new ushort[Math.Max(1, rect.Count)];

        for (var z = 0; z < rect.Height; z++) {
            for (var x = 0; x < rect.Width; x++) {
                // Clamped rather than clipped, so a stamp at the edge reads the boundary sample
                // repeatedly and smooths as if the terrain continued flat — the same rule
                // TerrainSamples' indexer uses, applied to the copy.
                buffer[(z * rect.Width) + x] = terrain.Composite[
                    Math.Clamp(rect.X + x, clipped.X, Math.Max(clipped.X, clipped.EndX - 1)),
                    Math.Clamp(rect.Z + z, clipped.Z, Math.Max(clipped.Z, clipped.EndZ - 1))
                ];
            }
        }

        return buffer;
    }

    static int Sample(ushort[] snapshot, TerrainRect rect, int x, int z) {
        var localX = Math.Clamp(x - rect.X, 0, rect.Width - 1);
        var localZ = Math.Clamp(z - rect.Z, 0, rect.Height - 1);
        return snapshot[(localZ * rect.Width) + localX];
    }

    static TerrainRect SampleRectOf(in TerrainDescription description, BrushFootprint footprint) =>
        SampleRect(description, footprint.Minimum, footprint.Maximum);

    static TerrainRect SampleRect(in TerrainDescription description, Vector2 minimum, Vector2 maximum) {
        var scale = description.MetresPerQuad;

        var x = Math.Max(0, (int)MathF.Floor(minimum.X / scale));
        var z = Math.Max(0, (int)MathF.Floor(minimum.Y / scale));
        var endX = Math.Min(description.SamplesX, (int)MathF.Ceiling(maximum.X / scale) + 1);
        var endZ = Math.Min(description.SamplesZ, (int)MathF.Ceiling(maximum.Y / scale) + 1);

        return endX <= x || endZ <= z ? TerrainRect.Empty : new(x, z, endX - x, endZ - z);
    }
}

/// <summary>The shape of the noise the Noise tool adds.</summary>
/// <param name="Octaves">How many layers of detail.</param>
/// <param name="Frequency">How many samples one period of the coarsest octave spans, inverted.</param>
/// <param name="Lacunarity">How much finer each octave is than the last.</param>
/// <param name="Gain">How much quieter each octave is than the last.</param>
/// <param name="Ridged">Whether to fold the noise about zero, which makes ridges instead of hills.</param>
/// <param name="Seed">What the lattice derives from.</param>
/// <remarks>
///     Value noise rather than gradient noise, for the reason [docs/plan/26] gives for the camera's
///     shake: <b>the range of value noise is exactly the range of its lattice</b>, so an amplitude
///     declared as three metres never exceeds three metres. A gradient-noise peak is a number you
///     look up and hope for, and an artist sculpting near a building cannot work with "three metres,
///     except occasionally".
/// </remarks>
public readonly record struct TerrainNoise(
    int Octaves = 4,
    float Frequency = 0.02f,
    float Lacunarity = 2f,
    float Gain = 0.5f,
    bool Ridged = false,
    uint Seed = 0x9E3779B9u
) {
    /// <summary>The noise at a sample, in −1…1 — or 0…1 when <see cref="Ridged" />.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The value.</returns>
    public float At(int x, int z) {
        var octaves = Math.Clamp(Octaves, 1, 12);
        var frequency = Frequency > 0f ? Frequency : 0.02f;

        var total = 0f;
        var amplitude = 1f;
        var normaliser = 0f;

        for (var octave = 0; octave < octaves; octave++) {
            var value = Lattice(x * frequency, z * frequency, Seed + (uint)octave);

            total += (Ridged ? 1f - MathF.Abs(value) : value) * amplitude;
            normaliser += amplitude;

            frequency *= Lacunarity <= 0f ? 2f : Lacunarity;
            amplitude *= Math.Clamp(Gain, 0f, 1f);
        }

        return normaliser > 0f ? total / normaliser : 0f;
    }

    static float Lattice(float x, float z, uint seed) {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);

        var fx = Smoothstep(x - x0);
        var fz = Smoothstep(z - z0);

        var a = Corner(x0, z0, seed);
        var b = Corner(x0 + 1, z0, seed);
        var c = Corner(x0, z0 + 1, seed);
        var d = Corner(x0 + 1, z0 + 1, seed);

        return (((a * (1f - fx)) + (b * fx)) * (1f - fz)) + (((c * (1f - fx)) + (d * fx)) * fz);
    }

    static float Smoothstep(float t) => t * t * (3f - (2f * t));

    static float Corner(int x, int z, uint seed) {
        var hash = (uint)x * 0x9E3779B1u;
        hash ^= (uint)z * 0x85EBCA77u;
        hash ^= seed * 0xC2B2AE3Du;

        hash ^= hash >> 15;
        hash *= 0x2545F491u;
        hash ^= hash >> 13;

        return (hash / (float)uint.MaxValue * 2f) - 1f;
    }
}
