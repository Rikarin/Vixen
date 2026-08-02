// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>What one cascade covers, and the transform that renders it.</summary>
/// <param name="ViewProjection">World to the cascade's light clip space.</param>
/// <param name="Centre">The centre of the sphere the cascade was fitted to, in world space.</param>
/// <param name="Radius">That sphere's radius, which is half the cascade's extent.</param>
/// <param name="Near">Where the cascade starts, as a distance from the camera.</param>
/// <param name="Far">Where it ends.</param>
public readonly record struct ShadowCascade(
    Matrix4x4 ViewProjection,
    Vector3 Centre,
    float Radius,
    float Near,
    float Far
);

/// <summary>
///     Where cascades are split, and how each one is fitted to the camera.
/// </summary>
/// <remarks>
///     <para>
///         Pure arithmetic with no device and no state, which is the point: cascaded shadow maps go
///         wrong in two specific, famous ways, and both are properties of these functions alone. That
///         makes them assertable without rendering anything.
///     </para>
///     <para>
///         <strong>Shimmering when the camera turns</strong> is fixed by
///         <see cref="Fit" /> bounding a <em>sphere</em> rather than the eight frustum corners. A
///         corner fit produces an extent that depends on the camera's orientation, so rotating on the
///         spot changes the cascade's size, which changes the texel grid, which makes every shadow
///         edge crawl. A sphere's radius depends only on the split distances and the field of view.
///     </para>
///     <para>
///         <strong>Shimmering when the camera moves</strong> is fixed by snapping the fitted centre to
///         whole texels of the shadow map. Without it, sub-texel translation slides the sampling grid
///         under static geometry and the same fixed edge flickers between two texel rows.
///     </para>
/// </remarks>
public static class ShadowCascades {
    /// <summary>The most cascades one call will produce.</summary>
    /// <remarks>
    ///     Four, which is what every shipping engine settled on. A fifth buys a little distant
    ///     resolution and costs a whole extra render of every caster, which is the wrong trade at the
    ///     range where the fourth cascade's texels are already metres across.
    /// </remarks>
    public const int MaxCascades = 4;

    /// <summary>
    ///     Splits <c>[near, far]</c> into <paramref name="distances" /> cascade boundaries.
    /// </summary>
    /// <param name="near">Where the first cascade starts.</param>
    /// <param name="far">Where the last one ends — the shadow distance, not the camera's far plane.</param>
    /// <param name="lambda">
    ///     How far to blend from a uniform split (<c>0</c>) toward a logarithmic one (<c>1</c>).
    /// </param>
    /// <param name="distances">
    ///     Filled with the <em>ends</em> of each cascade, ascending. Its length is the cascade count.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The practical split scheme (Zhang et al.): a logarithmic split is what perspective
    ///         projection actually asks for — texel density should fall as <c>1/z</c> — but it puts
    ///         the first boundary absurdly close to the eye, so almost the whole first cascade covers
    ///         a few metres. A uniform split wastes the near range instead. The blend is the usual
    ///         answer and <c>0.75</c> the usual constant.
    ///     </para>
    ///     <para>
    ///         Note <paramref name="far" /> is the shadow distance rather than the camera's far
    ///         plane. Cascades sized to a two-kilometre view distance would spend their entire budget
    ///         on terrain nobody can see a shadow on.
    ///     </para>
    /// </remarks>
    public static void Split(float near, float far, float lambda, Span<float> distances) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(near);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(far, near);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distances.Length, MaxCascades);

        var count = distances.Length;
        var ratio = far / near;
        var blend = Math.Clamp(lambda, 0f, 1f);

        for (var i = 0; i < count; i++) {
            var t = (i + 1) / (float)count;

            var logarithmic = near * MathF.Pow(ratio, t);
            var uniform = near + ((far - near) * t);

            distances[i] = (blend * logarithmic) + ((1f - blend) * uniform);
        }

        // The last boundary is the shadow distance exactly. The blend above lands within a rounding
        // error of it, and a cascade that ends a millimetre short leaves a seam that is visible
        // precisely because it is at the same place in every frame.
        distances[count - 1] = far;
    }

    /// <summary>
    ///     The bounding sphere of one slice of the camera's frustum.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Computed from the split distances and the field of view alone — never from the eight
    ///         corners. That is what makes the radius independent of where the camera is pointing,
    ///         and therefore what stops the cascade resizing as it turns.
    ///     </para>
    ///     <para>
    ///         Exposed separately from <see cref="Fit" /> because a caller that caches shadow maps
    ///         needs to ask "does the cascade I already have still cover the slice" without building
    ///         a projection to find out.
    ///     </para>
    /// </remarks>
    public static (Vector3 Centre, float Radius) Sphere(
        Vector3 eye,
        Vector3 forward,
        float fieldOfView,
        float aspectRatio,
        float near,
        float far
    ) {
        var view = Vector3.Normalize(forward);

        var tanY = MathF.Tan(fieldOfView * 0.5f);
        var tanX = tanY * aspectRatio;
        var spread = (tanX * tanX) + (tanY * tanY);

        var distance = (near + far) * 0.5f * (1f + spread);

        if (distance > far) {
            // The slice is long relative to the cone's width: the smallest enclosing sphere is the
            // one through the far corners, centred on the far plane. Without this the centre runs
            // past the slice and the radius grows without bound.
            distance = far;
        }

        var radius = MathF.Sqrt((far * far * spread) + ((distance - far) * (distance - far)));

        return (eye + (view * distance), radius);
    }

    /// <summary>Whether a cascade still covers a slice's sphere entirely.</summary>
    /// <remarks>
    ///     What makes a cached shadow map possible. A cascade fitted with slack stays valid while the
    ///     camera moves inside it, so its map does not have to be redrawn — and without that, a
    ///     cascade re-fits the moment the camera moves a texel and nothing is ever worth caching.
    /// </remarks>
    public static bool Covers(in ShadowCascade cascade, Vector3 centre, float radius) =>
        Vector3.Distance(cascade.Centre, centre) + radius <= cascade.Radius;

    /// <summary>
    ///     Fits one cascade to a slice of the camera's frustum and returns its light transform.
    /// </summary>
    /// <param name="eye">The camera's position.</param>
    /// <param name="forward">Where it looks. Need not be normalised.</param>
    /// <param name="up">
    ///     Its approximate up direction — a tie-break, and only for a light pointing straight down.
    ///     The fitted basis is otherwise the light's alone, because a basis that follows the camera
    ///     turns the shadow map under stationary geometry every time the player looks around.
    /// </param>
    /// <param name="lightDirection">
    ///     The direction light travels — away from the sun, toward the scene.
    /// </param>
    /// <param name="fieldOfView">The camera's vertical field of view in radians.</param>
    /// <param name="aspectRatio">Its width divided by its height.</param>
    /// <param name="near">Where this cascade starts.</param>
    /// <param name="far">Where it ends.</param>
    /// <param name="resolution">The shadow map's side in texels, which sets the snapping grid.</param>
    /// <param name="extrusion">
    ///     How far behind the fitted sphere to place the light's near plane, so that casters outside
    ///     the cascade still cast into it.
    /// </param>
    /// <param name="slack">
    ///     How much larger than the slice's own sphere to cut the cascade, as a fraction.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <paramref name="slack" /> buys <em>stability</em> with resolution, and it is the whole
    ///         reason a shadow map can be cached: a cascade cut twenty per cent wide stays valid
    ///         while the camera moves inside the margin, so its content does not have to be redrawn.
    ///         With no slack a cascade re-fits the moment the camera moves a texel, and nothing is
    ///         ever worth keeping.
    ///     </para>
    ///     <para>
    ///         The cost is real and is paid every frame: at twenty per cent the same texels cover
    ///         1.44 times the area, so everything in shadow is that much coarser. It is worth it when
    ///         the alternative is re-rendering a level's worth of static geometry four times a frame,
    ///         and not otherwise.
    ///     </para>
    /// </remarks>
    public static ShadowCascade Fit(
        Vector3 eye,
        Vector3 forward,
        Vector3 up,
        Vector3 lightDirection,
        float fieldOfView,
        float aspectRatio,
        float near,
        float far,
        int resolution,
        float extrusion = 50f,
        float slack = 0f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);

        var light = Vector3.Normalize(lightDirection);
        var (centre, tight) = Sphere(eye, forward, fieldOfView, aspectRatio, near, far);
        var radius = tight * (1f + MathF.Max(slack, 0f));

        // A light basis that does not depend on the camera. Deriving it from the camera would put the
        // camera's orientation into the light's own texel grid, so the shadow map would turn under
        // stationary geometry as the player looks around — the sphere fit makes the cascade the same
        // *size* whichever way the camera points, and this is what makes it the same *shape*.
        //
        // ⚠ This block said all of that and then did the opposite. The guard below reads as "the
        // caller's up, for the degenerate case", and its condition is the degenerate case's negation:
        // for any light that is not nearly parallel to `up` — which is every ordinary sun — the
        // camera's up won, and the shadows rotated with the camera.
        var reference = new Vector3(0f, 1f, 0f);

        // `up` is consulted only where the world's is useless, which is a light pointing very nearly
        // straight down. Any choice is arbitrary there, and a stable arbitrary one is better than one
        // that flips — so the caller's is preferred to a second world axis when it is usable at all.
        if (MathF.Abs(Vector3.Dot(light, reference)) > 0.99f) {
            reference = up.LengthSquared() > 0f && MathF.Abs(Vector3.Dot(light, Vector3.Normalize(up))) < 0.99f
                ? Vector3.Normalize(up)
                : new Vector3(0f, 0f, 1f);
        }

        var lightRight = Vector3.Normalize(Vector3.Cross(reference, -light));
        var lightUp = Vector3.Cross(-light, lightRight);

        // Two texels of margin around the sphere, because snapping moves the centre by up to one
        // texel and a corner exactly on the sphere would otherwise fall outside the volume — a
        // shadow that is clipped along one edge of the cascade, which reads as a straight-line seam
        // in the middle of the ground. The margin is a function of the radius and the resolution
        // only, so it does not reintroduce the orientation dependence the sphere fit removed.
        var extent = radius + (4f * radius / resolution);
        var texel = 2f * extent / resolution;

        // Snap the centre to whole texels *of the light's grid*, not of the world's. A camera
        // movement smaller than one texel must not move the projection at all — otherwise the
        // sampling grid slides under stationary geometry and every shadow edge crawls.
        //
        // All three axes, not just the two the grid is made of. Depth does not crawl, so snapping it
        // buys nothing on its own — but leaving it unsnapped leaves the light's near plane sliding,
        // which means the matrix changes when nothing visible does, and "the projection is identical"
        // stops being something a test can state. The margin computed above absorbs the extra texel.
        var snapped =
            (lightRight * Quantise(Vector3.Dot(centre, lightRight), texel))
            + (lightUp * Quantise(Vector3.Dot(centre, lightUp), texel))
            + (light * Quantise(Vector3.Dot(centre, light), texel));

        var origin = snapped - (light * (radius + extrusion));

        var lightView = Matrix4x4.LookAt(origin, snapped, reference);
        var projection = Matrix4x4.OrthographicOffCenter(
            -extent,
            extent,
            -extent,
            extent,
            0.0625f,
            (2f * extent) + extrusion
        );

        return new(lightView * projection, snapped, radius, near, far);
    }

    /// <summary>
    ///     The scale and offset that map a cascade's clip-space XY into its tile of an atlas.
    /// </summary>
    /// <param name="index">Which cascade.</param>
    /// <param name="count">How many share the atlas.</param>
    /// <remarks>
    ///     One texture with a tile per cascade rather than an array, because it is what the widest
    ///     range of hardware can render into with one pass and a viewport change, and because
    ///     sampling one texture with an offset costs a shader one binding rather than four.
    /// </remarks>
    public static (Vector2 Scale, Vector2 Offset) AtlasTile(int index, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var columns = count <= 1 ? 1 : 2;
        var rows = (count + columns - 1) / columns;

        var scale = new Vector2(1f / columns, 1f / rows);
        var offset = new Vector2(index % columns * scale.X, index / columns * scale.Y);

        return (scale, offset);
    }

    /// <summary>
    ///     Which cascade shades a fragment at this distance.
    /// </summary>
    /// <param name="viewDepth">How far in front of the camera the fragment is.</param>
    /// <param name="splits">The distances each cascade covers up to, near first.</param>
    /// <returns>The index of the nearest cascade that still reaches it, or the last.</returns>
    /// <remarks>
    ///     <para>
    ///         The host's copy of <c>ForwardPlus.CascadeOf</c>, duplicated for the reason
    ///         <see cref="ClusterGrid" />'s mirror is: the selection is what makes an atlas cascade at
    ///         all, and a copy that can be evaluated on the CPU is what lets a test say the cascade a
    ///         fragment picks is one whose projection contains it.
    ///     </para>
    ///     <para>
    ///         The <em>nearest</em> that covers it, not the largest: the cascades are fitted to
    ///         successive slices, so the first whose split a fragment is inside is the
    ///         highest-resolution map that has it. Falling through to the last is what a fragment
    ///         past the shadow distance gets, which is why the shader fades there rather than
    ///         trusting it.
    ///     </para>
    /// </remarks>
    public static int CascadeOf(float viewDepth, ReadOnlySpan<float> splits) {
        for (var i = 0; i < splits.Length; i++) {
            if (viewDepth <= splits[i]) {
                return i;
            }
        }

        return Math.Max(splits.Length - 1, 0);
    }

    /// <summary>
    ///     A cascade's matrix, projecting straight into its tile of the atlas.
    /// </summary>
    /// <param name="cascade">The cascade.</param>
    /// <param name="index">Which tile it occupies.</param>
    /// <param name="count">How many share the atlas.</param>
    /// <remarks>
    ///     <para>
    ///         What a shading pass needs and what <see cref="AtlasTile" /> alone does not give it. A
    ///         shader that has one matrix and one atlas does <c>NdcToUv(cascade · p)</c>, which
    ///         addresses the <em>whole</em> texture — so with four tiles in it every lookup reads a
    ///         quarter of the way into the wrong one. Folding the tile's scale and offset into the
    ///         matrix costs the shader nothing and is not a thing a host can be asked to remember.
    ///     </para>
    ///     <para>
    ///         The tile is in UV terms and the matrix produces clip coordinates, so the scale passes
    ///         through and the offset becomes <c>NdcToUv</c> inverted, applied and re-applied. Depth
    ///         is untouched: a tile moves where a texel is, not how far away it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And y is not x, because <c>NdcToUv</c> negates y and does not negate x.</b> The
    ///         translation was <c>2·offset + scale − 1</c> on both axes, which is right for a UV that
    ///         maps y straight through and inverts the tile <em>row</em> for the one a shader
    ///         actually computes — so a four-cascade atlas had cascade zero reading cascade two's
    ///         tile, and reading a plausible depth out of it. That negation was added to
    ///         <c>Transform.NdcToUv</c> after this was written, and nothing failed: the test here
    ///         mapped y straight through too, so the fold and the assertion agreed with each other
    ///         and neither agreed with the lookup.
    ///     </para>
    /// </remarks>
    public static Matrix4x4 AtlasProjection(in ShadowCascade cascade, int index, int count) {
        var (scale, offset) = AtlasTile(index, count);
        return ShadowProjections.Tile(cascade.ViewProjection, scale, offset);
    }

    /// <summary>Where an atlas tile sits, in texels.</summary>
    /// <param name="index">Which cascade.</param>
    /// <param name="count">How many share the atlas.</param>
    /// <param name="resolution">One tile's side in texels.</param>
    public static Viewport TileViewport(int index, int count, int resolution) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var columns = count <= 1 ? 1 : 2;

        return new(index % columns * resolution, index / columns * resolution, resolution, resolution);
    }

    /// <summary>How big an atlas holding <paramref name="count" /> tiles has to be, in texels.</summary>
    public static Int2 AtlasSize(int count, int resolution) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var columns = count <= 1 ? 1 : 2;
        var rows = (count + columns - 1) / columns;

        return new(columns * resolution, rows * resolution);
    }

    /// <summary>The last whole multiple of a grid step at or below <paramref name="value" />.</summary>
    static float Quantise(float value, float step) =>
        step <= 0f ? value : MathF.Floor(value / step) * step;
}
