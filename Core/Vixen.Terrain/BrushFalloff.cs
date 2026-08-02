// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>How a stamp's weight drops off between its plateau and its edge.</summary>
/// <remarks>
///     The four both reference editors offer, and the reason all four exist is that they are not
///     interchangeable at the same strength: <see cref="Smooth" /> blends into what is already there,
///     <see cref="Linear" /> makes a cone, <see cref="Spherical" /> makes a dome, and
///     <see cref="Tip" /> makes a point. An artist picks the shape of the thing they are making.
/// </remarks>
public enum BrushFalloffKind {
    /// <summary>Smoothstep. Zero slope at both ends, so strokes overlap without a visible ridge.</summary>
    Smooth,

    /// <summary>Straight. A cone, and the only one whose slope is constant.</summary>
    Linear,

    /// <summary>A quarter circle: full at the plateau, vertical at the edge. A dome.</summary>
    Spherical,

    /// <summary>The inverse of <see cref="Spherical" />: vertical at the plateau. A point.</summary>
    Tip
}

/// <summary>The four falloff curves, as functions of one number.</summary>
/// <remarks>
///     <para>
///         Separated from <see cref="TerrainBrush" /> so the curve can be tested as arithmetic — each
///         one is asserted to start at one, end at zero and never rise in between, which is what
///         every consumer assumes and none of them checks.
///     </para>
///     <para>
///         ⚠ <b>The parameter is distance across the falloff band, not across the brush.</b> Zero is
///         the outer edge of the plateau and one is the outer edge of the brush;
///         <see cref="TerrainBrush.WeightAt" /> is what converts a radius into it. Passing a fraction
///         of the radius instead produces a brush whose plateau is not flat, which reads as a soft
///         brush that will not make a flat surface.
///     </para>
/// </remarks>
public static class BrushFalloff {
    /// <summary>Evaluates a falloff curve.</summary>
    /// <param name="kind">Which curve.</param>
    /// <param name="t">
    ///     How far across the falloff band, 0 at the plateau and 1 at the edge. Clamped.
    /// </param>
    /// <returns>The weight, 1 at the plateau falling to 0 at the edge.</returns>
    public static float Evaluate(BrushFalloffKind kind, float t) {
        t = float.IsNaN(t) ? 1f : Math.Clamp(t, 0f, 1f);

        return kind switch {
            BrushFalloffKind.Smooth => 1f - (t * t * (3f - (2f * t))),
            BrushFalloffKind.Linear => 1f - t,
            BrushFalloffKind.Spherical => MathF.Sqrt(Math.Max(0f, 1f - (t * t))),

            // 1 − the quarter circle measured from the other end. Vertical where Spherical is flat,
            // which is what makes it come to a point rather than a dome.
            BrushFalloffKind.Tip => 1f - MathF.Sqrt(Math.Max(0f, 1f - ((1f - t) * (1f - t)))),
            _ => 1f - t
        };
    }
}
