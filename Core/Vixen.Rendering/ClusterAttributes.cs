// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>A pixel's position inside a triangle, and how fast that position changes.</summary>
/// <param name="Weights">The barycentric weights, summing to one, perspective-corrected.</param>
/// <param name="Ddx">How they change across one pixel in x.</param>
/// <param name="Ddy">How they change across one pixel in y.</param>
/// <remarks>
///     <c>Barycentrics</c> in <c>Barycentrics.rvn</c>. Three weights rather than two because an
///     interpolation is a weighted sum of three corner values, and carrying the third explicitly is what
///     lets one struct interpolate a scalar, a vector or a matrix with no special case for which corner
///     is the origin.
/// </remarks>
public readonly record struct Barycentrics(Vector3 Weights, Vector3 Ddx, Vector3 Ddy);

/// <summary>
///     Recovering a pixel's attributes from the triangle it landed on — phase 5's reconstruction, and
///     the definition both the resolve and its tests are held against.
/// </summary>
/// <remarks>
///     <para>
///         <b>Phase 5 of <c>docs/plan/22-virtualized-geometry.md</c>.</b> A visibility buffer stores
///         <em>which</em> triangle covered a pixel and nothing else, so everything an interpolator
///         would have handed the fragment stage — the world position, the normal, the texture
///         coordinate, and the rate at which the coordinate changes — has to be worked out again from
///         the triangle's three corners and the pixel's own position.
///     </para>
///     <para>
///         <b>The mirror exists because two of the three steps fail silently.</b> Solving the weights
///         in screen space and using them directly is the classic affine-texturing error: the image is
///         plausible, straight lines bend, and a texture swims across a floor as the camera moves.
///         Correcting the weights but not their <em>derivatives</em> is subtler still — the picture is
///         then right and only the mip selection is wrong, which reads as a texture that is slightly
///         too sharp at grazing angles. Neither is a crash and neither is visible in a still.
///         Improvement 4 of the plan is exactly this argument.
///     </para>
///     <para>
///         <b>Analytic, not from a quad.</b> A resolve dispatched over tiles has no quad to take
///         derivatives against — and even a fragment-stage resolve, which does, would be taking them
///         across a neighbour that may belong to a different triangle of a different cluster. That is
///         wrong precisely at the silhouettes where it is most visible, which is why the plan says the
///         gradients come from the triangle plane rather than from <c>ddx</c>.
///     </para>
/// </remarks>
public static class ClusterAttributes {
    /// <summary>Below which the solve's reciprocal is not finite enough to use.</summary>
    /// <remarks>
    ///     On the reciprocal the solve needs rather than on the area, because that is the number that
    ///     blows up. A cluster can hold a zero-area triangle — a surplus corner from the raster, or a
    ///     collapse the simplifier made exactly degenerate — and a pixel is never covered by one, so
    ///     this guards the arithmetic rather than deciding anything visible.
    /// </remarks>
    public const float Epsilon = 1e-12f;

    /// <summary>
    ///     A pixel's barycentric weights on a triangle whose corners are in clip space.
    /// </summary>
    /// <param name="pixel">Where the pixel is, in normalised device coordinates.</param>
    /// <param name="clip0">The first corner, in clip space.</param>
    /// <param name="clip1">The second.</param>
    /// <param name="clip2">The third.</param>
    /// <param name="pixelSize">How much of NDC one pixel spans, in x and y.</param>
    /// <returns>The weights, and their screen-space derivatives.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The screen-space solve, then the perspective correction, and the order matters.</b>
    ///         Screen space is where the pixel is, so the weights are solved there — but an attribute
    ///         varies linearly in <em>world</em> space and a projection is not affine. The fix is one
    ///         division per corner by that corner's clip <c>w</c>, renormalised, which is what a
    ///         rasterizer's interpolator does and what this reproduces.
    ///     </para>
    ///     <para>
    ///         The derivatives then follow from the quotient rule, because the renormalising denominator
    ///         is itself a function of the pixel. Dropping that term leaves gradients that are wrong by
    ///         a factor that grows with the depth range across the triangle — the mip-selection error
    ///         above.
    ///     </para>
    ///     <para>
    ///         A transliteration of <c>Barycentric.Solve</c>, deliberately step for step.
    ///     </para>
    /// </remarks>
    public static Barycentrics Of(
        Vector2 pixel,
        Vector4 clip0,
        Vector4 clip1,
        Vector4 clip2,
        Vector2 pixelSize
    ) {
        var degenerate = new Barycentrics(Vector3.UnitX, Vector3.Zero, Vector3.Zero);

        var inverse0 = Reciprocal(clip0.W);
        var inverse1 = Reciprocal(clip1.W);
        var inverse2 = Reciprocal(clip2.W);

        var ndc0 = new Vector2(clip0.X, clip0.Y) * inverse0;
        var ndc1 = new Vector2(clip1.X, clip1.Y) * inverse1;
        var ndc2 = new Vector2(clip2.X, clip2.Y) * inverse2;

        // Twice the signed area of the screen triangle. The sign carries the winding and cancels in
        // every ratio below, so nothing here has to know which way the triangle faces.
        var edge1 = ndc1 - ndc0;
        var edge2 = ndc2 - ndc0;
        var area = (edge1.X * edge2.Y) - (edge1.Y * edge2.X);

        if (MathF.Abs(area) < Epsilon) {
            return degenerate;
        }

        var scale = 1f / area;
        var to = pixel - ndc0;
        var b1 = ((to.X * edge2.Y) - (to.Y * edge2.X)) * scale;
        var b2 = ((edge1.X * to.Y) - (edge1.Y * to.X)) * scale;

        var screen = new Vector3(1f - b1 - b2, b1, b2);

        var d1 = new Vector2(edge2.Y, -edge2.X) * scale;
        var d2 = new Vector2(-edge1.Y, edge1.X) * scale;

        var screenDdx = new Vector3(-d1.X - d2.X, d1.X, d2.X) * pixelSize.X;
        var screenDdy = new Vector3(-d1.Y - d2.Y, d1.Y, d2.Y) * pixelSize.Y;

        var reciprocals = new Vector3(inverse0, inverse1, inverse2);
        var weighted = screen * reciprocals;
        var sum = weighted.X + weighted.Y + weighted.Z;

        if (MathF.Abs(sum) < Epsilon) {
            return degenerate;
        }

        var inverseSum = 1f / sum;
        var weights = weighted * inverseSum;

        var weightedDdx = screenDdx * reciprocals;
        var weightedDdy = screenDdy * reciprocals;
        var sumDdx = weightedDdx.X + weightedDdx.Y + weightedDdx.Z;
        var sumDdy = weightedDdy.X + weightedDdy.Y + weightedDdy.Z;

        return new(
            weights,
            (weightedDdx - (weights * sumDdx)) * inverseSum,
            (weightedDdy - (weights * sumDdy)) * inverseSum
        );
    }

    /// <summary>One scalar attribute, interpolated.</summary>
    public static float Lerp(in Barycentrics b, float a0, float a1, float a2) =>
        (a0 * b.Weights.X) + (a1 * b.Weights.Y) + (a2 * b.Weights.Z);

    /// <summary>A two-component attribute, interpolated.</summary>
    public static Vector2 Lerp(in Barycentrics b, Vector2 a0, Vector2 a1, Vector2 a2) =>
        (a0 * b.Weights.X) + (a1 * b.Weights.Y) + (a2 * b.Weights.Z);

    /// <summary>A three-component attribute, interpolated.</summary>
    public static Vector3 Lerp(in Barycentrics b, Vector3 a0, Vector3 a1, Vector3 a2) =>
        (a0 * b.Weights.X) + (a1 * b.Weights.Y) + (a2 * b.Weights.Z);

    /// <summary>A four-component attribute, interpolated.</summary>
    public static Vector4 Lerp(in Barycentrics b, Vector4 a0, Vector4 a1, Vector4 a2) =>
        (a0 * b.Weights.X) + (a1 * b.Weights.Y) + (a2 * b.Weights.Z);

    /// <summary>How a two-component attribute changes across one pixel in x.</summary>
    /// <remarks>
    ///     The same weighted sum with the weight derivatives, which is the whole reason
    ///     <see cref="Barycentrics" /> carries them: interpolation is linear in the weights, so the
    ///     derivative of the interpolated value is the interpolation of the corner values by the
    ///     derivative weights. No finite difference and no neighbouring pixel.
    /// </remarks>
    public static Vector2 Ddx(in Barycentrics b, Vector2 a0, Vector2 a1, Vector2 a2) =>
        (a0 * b.Ddx.X) + (a1 * b.Ddx.Y) + (a2 * b.Ddx.Z);

    /// <summary>How it changes across one pixel in y.</summary>
    public static Vector2 Ddy(in Barycentrics b, Vector2 a0, Vector2 a1, Vector2 a2) =>
        (a0 * b.Ddy.X) + (a1 * b.Ddy.Y) + (a2 * b.Ddy.Z);

    /// <summary>How much of NDC one pixel spans, for a view of a size.</summary>
    /// <param name="size">The view, in pixels.</param>
    /// <remarks>
    ///     Two units of NDC across the whole view in each axis, so one pixel is two over the extent.
    ///     Written here rather than at the caller because it is what every gradient is scaled by, and a
    ///     factor of two in it is a mip level.
    /// </remarks>
    public static Vector2 PixelSize(Int2 size) =>
        new(size.X > 0 ? 2f / size.X : 0f, size.Y > 0 ? 2f / size.Y : 0f);

    static float Reciprocal(float value) => MathF.Abs(value) < GpuCulling.ClipEpsilon ? 0f : 1f / value;
}
