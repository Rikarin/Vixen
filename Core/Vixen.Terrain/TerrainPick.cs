// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>Where a ray met the ground.</summary>
/// <param name="Position">The point, in the terrain's own space, with Y in metres.</param>
/// <param name="Distance">How far along the ray it was.</param>
public readonly record struct TerrainHit(Vector3 Position, float Distance) {
    /// <summary>The point on the ground plane, which is what a brush is stamped at.</summary>
    /// <remarks>
    ///     ⚠ <b>XZ, and every brush in the two modes takes exactly this.</b> A stamp is a footprint on
    ///     the heightfield rather than a point on its surface — the whole of
    ///     <see cref="TerrainBrush.WeightAt" /> is a function of horizontal distance — so a tool that
    ///     carried the height around would be carrying a number nothing reads.
    /// </remarks>
    public Vector2 Ground => new(Position.X, Position.Z);
}

/// <summary>
///     A ray against the composited heightfield.
/// </summary>
/// <remarks>
///     <para>
///         <b>What turns a pointer into a brush position, and it is arithmetic rather than a
///         device.</b> The viewport has a camera and a pixel; everything after the ray is a function
///         of the samples, so it lives in the kernel with the rest of them and is a unit test rather
///         than something only a running editor can exercise. [docs/plan/31 § T3].
///     </para>
///     <para>
///         ⚠ <b>Against the bilinear surface, not against the triangles that are drawn.</b> Which two
///         triangles a quad is split into depends on the LOD level the patch was selected at and on
///         <c>TerrainGridPatch</c>'s alternating diagonal, so "the triangle under the pointer" is not
///         a question the kernel can answer and would give a different answer at two distances from
///         the camera. The bilinear surface passes through all four corner samples, so it agrees with
///         the mesh wherever the mesh has a vertex and differs inside a quad by at most the sag of the
///         diagonal — under a millimetre on ground a brush is being aimed at.
///     </para>
///     <para>
///         ⚠ <b>Holes are ignored, and that is the sculpt tools' behaviour rather than an
///         omission.</b> A hole is a bit on the visibility mask and the heights beneath it are still
///         there; a pick that refused to answer over one would make the hole tool unable to take a
///         hole back out. What wants the other answer is foliage placement, in [§ T5], and that is
///         its own question rather than a flag here.
///     </para>
/// </remarks>
public static class TerrainPick {
    /// <summary>How many times the crossing is bisected once it has been bracketed.</summary>
    /// <remarks>
    ///     Twenty-four halvings of half a quad, which on a one-metre grid is under a nanometre and is
    ///     therefore exact for anything a person aims at. It is a constant rather than a tolerance
    ///     because a fixed count is what makes the loop's cost the same for a terrain of any size.
    /// </remarks>
    public const int RefineSteps = 24;

    /// <summary>The ground under a horizontal position, interpolated between samples.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="x">Where, along X, in metres.</param>
    /// <param name="z">And along Z.</param>
    /// <returns>The height in metres. Clamped to the terrain's extent rather than refused.</returns>
    /// <remarks>
    ///     ⚠ <b>Reads <see cref="Terrain.CompositeAt" />, the definition, and not
    ///     <see cref="Terrain.Composite" />, the cache.</b> A pick happens in the middle of a drag,
    ///     which is exactly when the cache is stale — a stamp invalidates the tiles it touched and
    ///     <c>Resolve</c> runs once at the end of the frame — so reading the cache would aim the next
    ///     stamp of a stroke at the ground the stroke started from. It costs a walk of the layer
    ///     stack per sample, which for four bilinear reads a step is nothing.
    /// </remarks>
    public static float HeightAt(Terrain terrain, float x, float z) {
        ArgumentNullException.ThrowIfNull(terrain);

        var description = terrain.Description;
        var scale = description.MetresPerQuad;

        var sampleX = Math.Clamp(x / scale, 0f, description.SamplesX - 1f);
        var sampleZ = Math.Clamp(z / scale, 0f, description.SamplesZ - 1f);

        var x0 = (int)MathF.Floor(sampleX);
        var z0 = (int)MathF.Floor(sampleZ);
        var x1 = Math.Min(x0 + 1, description.SamplesX - 1);
        var z1 = Math.Min(z0 + 1, description.SamplesZ - 1);

        var fx = sampleX - x0;
        var fz = sampleZ - z0;

        var top = float.Lerp(terrain.CompositeAt(x0, z0), terrain.CompositeAt(x1, z0), fx);
        var bottom = float.Lerp(terrain.CompositeAt(x0, z1), terrain.CompositeAt(x1, z1), fx);

        return description.MinHeight
            + (float.Lerp(top, bottom, fz) / TerrainSamples.MaxHeight * description.HeightRange);
    }

    /// <summary>Casts a ray at the ground.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="origin">Where the ray starts, in the terrain's own space.</param>
    /// <param name="direction">Which way it points. Need not be normalised.</param>
    /// <param name="hit">Where it landed.</param>
    /// <param name="maximum">How far to look, in metres.</param>
    /// <returns>Whether it hit.</returns>
    /// <remarks>
    ///     <para>
    ///         Clipped to the terrain's box first — which is tight, because the stored range
    ///         <em>is</em> <see cref="TerrainDescription.MinHeight" />…<see cref="TerrainDescription.MaxHeight" />
    ///         by construction — and then marched at half a quad, which is the largest step that
    ///         cannot pass over a quad without sampling it. The crossing is then bisected.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A ray that starts underground hits at once, at its origin.</b> The alternative is
    ///         to march until it comes out, which for a camera inside a mountain aims the brush at
    ///         whatever is on the far side — and a pointer over solid ground meaning "here" is what
    ///         every reference toolset does.
    ///     </para>
    /// </remarks>
    public static bool Cast(
        Terrain terrain,
        Vector3 origin,
        Vector3 direction,
        out TerrainHit hit,
        float maximum = float.PositiveInfinity
    ) {
        ArgumentNullException.ThrowIfNull(terrain);

        hit = default;

        var length = direction.Length();

        if (!(length > 0f) || !(maximum > 0f)) {
            return false;
        }

        var step = direction / length;
        var description = terrain.Description;

        if (!Clip(description, origin, step, maximum, out var near, out var far)) {
            return false;
        }

        var above = Above(terrain, origin + (step * near));

        if (above <= 0f) {
            return Landed(terrain, origin, step, near, out hit);
        }

        // Half a quad of horizontal travel per step. A ray that is nearly vertical covers no ground
        // at all, so the whole of its span through the box is one interval and one bisection.
        var planar = MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
        var advance = planar > 1e-6f ? 0.5f * description.MetresPerQuad / planar : far - near;

        for (var at = near; at < far;) {
            var next = MathF.Min(at + advance, far);
            var there = Above(terrain, origin + (step * next));

            if (there <= 0f) {
                return Landed(terrain, origin, step, Bisect(terrain, origin, step, at, next), out hit);
            }

            at = next;

            // `next` saturates at `far`, so the loop would otherwise sit on the last interval for
            // ever once the step overshot the exit.
            if (next >= far) {
                break;
            }
        }

        return false;
    }

    /// <summary>How far above the ground a point is, in metres. Negative is under it.</summary>
    static float Above(Terrain terrain, Vector3 point) => point.Y - HeightAt(terrain, point.X, point.Z);

    /// <summary>Narrows a bracketed crossing down to <see cref="RefineSteps" /> halvings.</summary>
    static float Bisect(Terrain terrain, Vector3 origin, Vector3 step, float outside, float inside) {
        for (var i = 0; i < RefineSteps; i++) {
            var middle = (outside + inside) * 0.5f;

            if (Above(terrain, origin + (step * middle)) > 0f) {
                outside = middle;
            } else {
                inside = middle;
            }
        }

        return inside;
    }

    /// <summary>Builds the hit, putting its Y exactly on the surface rather than near it.</summary>
    /// <remarks>
    ///     ⚠ <b>The height is re-read rather than taken from the marched point.</b> Twenty-four
    ///     bisections leave the point a hair under the surface by construction — the loop keeps the
    ///     <em>inside</em> end — and a placement tool that put an object at that Y would sink every
    ///     one of them by the same invisible amount.
    /// </remarks>
    static bool Landed(Terrain terrain, Vector3 origin, Vector3 step, float distance, out TerrainHit hit) {
        var point = origin + (step * distance);

        hit = new(new(point.X, HeightAt(terrain, point.X, point.Z), point.Z), distance);
        return true;
    }

    /// <summary>The span of the ray inside the terrain's box, if any.</summary>
    static bool Clip(
        in TerrainDescription description,
        Vector3 origin,
        Vector3 step,
        float maximum,
        out float near,
        out float far
    ) {
        near = 0f;
        far = maximum;

        return Slab(origin.X, step.X, 0f, description.WidthX, ref near, ref far)
            && Slab(origin.Y, step.Y, description.MinHeight, description.MaxHeight, ref near, ref far)
            && Slab(origin.Z, step.Z, 0f, description.WidthZ, ref near, ref far);
    }

    static bool Slab(float origin, float step, float low, float high, ref float near, ref float far) {
        if (MathF.Abs(step) < 1e-9f) {
            return origin >= low && origin <= high;
        }

        var first = (low - origin) / step;
        var second = (high - origin) / step;

        if (first > second) {
            (first, second) = (second, first);
        }

        near = MathF.Max(near, first);
        far = MathF.Min(far, second);

        return near <= far;
    }
}
