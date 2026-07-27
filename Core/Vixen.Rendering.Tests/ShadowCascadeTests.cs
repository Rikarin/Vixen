// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Cascade splitting and fitting — docs/plan/06 § Lighting, CSM.
/// </summary>
/// <remarks>
///     Cascaded shadow maps go wrong in two famous ways, and both are properties of these two
///     functions alone rather than of anything that renders. That makes them assertable with no
///     device: a cascade that resizes when the camera turns, and a cascade whose grid slides when it
///     moves, are the two causes of shadow-edge crawl, and each has a test below that fails if its
///     fix is removed.
/// </remarks>
public class ShadowCascadeTests {
    const float Fov = MathF.PI / 3f;
    const float Aspect = 16f / 9f;

    static readonly Vector3 Light = Vector3.Normalize(new(-0.4f, -1f, -0.3f));
    static readonly Vector3 Up = new(0f, 1f, 0f);

    // --- Splits -------------------------------------------------------------

    [Fact]
    public void Splits_ascend_and_end_at_the_shadow_distance() {
        Span<float> splits = stackalloc float[4];
        ShadowCascades.Split(0.1f, 150f, 0.75f, splits);

        for (var i = 1; i < splits.Length; i++) {
            Assert.True(splits[i] > splits[i - 1], $"split {i} is not past split {i - 1}");
        }

        Assert.Equal(150f, splits[^1]);
    }

    /// <summary>
    ///     The last split is the shadow distance exactly, not within a rounding error of it.
    /// </summary>
    /// <remarks>
    ///     A cascade that ends a millimetre short leaves a seam — and one that is in the same world
    ///     position every frame, which is precisely what makes it noticeable.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void The_last_split_is_the_shadow_distance_whatever_the_blend(float lambda) {
        Span<float> splits = stackalloc float[3];
        ShadowCascades.Split(0.5f, 200f, lambda, splits);

        Assert.Equal(200f, splits[^1]);
    }

    /// <summary>A blend of zero is a uniform split.</summary>
    [Fact]
    public void A_lambda_of_zero_splits_uniformly() {
        Span<float> splits = stackalloc float[4];
        ShadowCascades.Split(0f + 1f, 101f, 0f, splits);

        Assert.Equal(26f, splits[0], 3);
        Assert.Equal(51f, splits[1], 3);
        Assert.Equal(76f, splits[2], 3);
        Assert.Equal(101f, splits[3], 3);
    }

    /// <summary>
    ///     A blend of one is a logarithmic split, which puts far more resolution near the camera.
    /// </summary>
    /// <remarks>
    ///     What perspective projection actually asks for — texel density should fall as <c>1/z</c> —
    ///     and also why the default is not 1: the first boundary lands so close that almost the whole
    ///     first cascade covers a few metres.
    /// </remarks>
    [Fact]
    public void A_lambda_of_one_splits_logarithmically() {
        Span<float> logarithmic = stackalloc float[4];
        Span<float> uniform = stackalloc float[4];

        ShadowCascades.Split(1f, 10000f, 1f, logarithmic);
        ShadowCascades.Split(1f, 10000f, 0f, uniform);

        // 1 → 10 → 100 → 1000 → 10000.
        Assert.Equal(10f, logarithmic[0], 2);
        Assert.Equal(100f, logarithmic[1], 1);

        Assert.True(logarithmic[0] < uniform[0]);
    }

    // --- The two shimmer properties -----------------------------------------

    /// <summary>
    ///     Rotating the camera on the spot does not change a cascade's extent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reason <see cref="ShadowCascades.Fit" /> bounds a <em>sphere</em> rather than the
    ///         eight frustum corners. A corner fit gives an extent that depends on where the camera
    ///         is pointing, so turning on the spot resizes the cascade, which resizes its texels,
    ///         which makes every shadow edge in the scene crawl.
    ///     </para>
    ///     <para>
    ///         Twelve directions, including straight up and straight down, and the radius is one
    ///         number for all of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Turning_the_camera_does_not_resize_a_cascade() {
        var expected = Fit(new(0f, 0f, -1f)).Radius;

        for (var i = 0; i < 12; i++) {
            var angle = i * MathF.Tau / 12f;
            var forward = new Vector3(MathF.Sin(angle), MathF.Cos(angle) * 0.5f, MathF.Cos(angle));

            Assert.Equal(expected, Fit(forward).Radius, 4);
        }
    }

    /// <summary>
    ///     Moving the camera by less than one shadow texel does not move the projection at all.
    /// </summary>
    /// <remarks>
    ///     Snapping the fitted centre to the light's texel grid. Without it, sub-texel translation
    ///     slides the sampling grid under static geometry and a fixed shadow edge flickers between
    ///     two texel rows — the other half of the crawl, and the half that survives a sphere fit.
    /// </remarks>
    [Fact]
    public void Sub_texel_camera_movement_does_not_move_the_projection() {
        var baseline = Fit(new(0f, 0f, -1f), Vector3.Zero);
        var texel = 2f * baseline.Radius / 1024f;

        var nudged = Fit(new(0f, 0f, -1f), new Vector3(texel * 0.01f, 0f, 0f));

        Assert.Equal(baseline.Centre, nudged.Centre);
        Assert.Equal(baseline.ViewProjection, nudged.ViewProjection);
    }

    /// <summary>
    ///     Moving by more than a texel does move it, so the snap is a grid and not a freeze.
    /// </summary>
    /// <remarks>
    ///     The other direction of the previous test, and the reason it is worth having: a fit that
    ///     always returned the same matrix would pass that one and follow the camera nowhere.
    /// </remarks>
    [Fact]
    public void Movement_past_a_texel_does_move_it() {
        var baseline = Fit(new(0f, 0f, -1f), Vector3.Zero);
        var texel = 2f * baseline.Radius / 1024f;

        var moved = Fit(new(0f, 0f, -1f), new Vector3(texel * 40f, 0f, 0f));

        Assert.NotEqual(baseline.Centre, moved.Centre);
    }

    // --- The fit is actually a fit ------------------------------------------

    /// <summary>
    ///     Every corner of the camera's frustum slice is inside the cascade's projection.
    /// </summary>
    /// <remarks>
    ///     The claim that makes the other two worth having. A sphere fit that were merely stable and
    ///     did not enclose the slice would produce shadows that stop at a straight line across the
    ///     ground — asserted here against the eight corners the sphere was deliberately <em>not</em>
    ///     computed from, so the test and the implementation do not share their arithmetic.
    /// </remarks>
    [Theory]
    [InlineData(0.1f, 15f)]
    [InlineData(15f, 40f)]
    [InlineData(40f, 150f)]
    [InlineData(1f, 1000f)]
    public void The_slice_is_inside_the_cascade(float near, float far) {
        var eye = new Vector3(3f, 12f, -7f);
        var forward = Vector3.Normalize(new(0.3f, -0.2f, -1f));

        var cascade = ShadowCascades.Fit(eye, forward, Up, Light, Fov, Aspect, near, far, 1024);
        var frustum = new BoundingFrustum(cascade.ViewProjection);

        var right = Vector3.Normalize(Vector3.Cross(forward, Up));
        var up = Vector3.Cross(right, forward);

        var tanY = MathF.Tan(Fov * 0.5f);
        var tanX = tanY * Aspect;

        foreach (var depth in (ReadOnlySpan<float>)[near, far]) {
            for (var sx = -1; sx <= 1; sx += 2) {
                for (var sy = -1; sy <= 1; sy += 2) {
                    var corner = eye
                        + (forward * depth)
                        + (right * (sx * tanX * depth))
                        + (up * (sy * tanY * depth));

                    Assert.True(frustum.Contains(corner), $"corner ({sx},{sy}) at {depth} is outside");
                }
            }
        }
    }

    /// <summary>A cascade covers the one before it, so consecutive cascades leave no gap.</summary>
    [Fact]
    public void Consecutive_cascades_overlap_rather_than_leaving_a_gap() {
        Span<float> splits = stackalloc float[4];
        ShadowCascades.Split(0.1f, 150f, 0.75f, splits);

        var near = 0.1f;

        foreach (var far in splits) {
            var cascade = ShadowCascades.Fit(Vector3.Zero, new(0f, 0f, -1f), Up, Light, Fov, Aspect, near, far, 1024);

            Assert.Equal(near, cascade.Near);
            Assert.Equal(far, cascade.Far);
            near = far;
        }
    }

    // --- The atlas ----------------------------------------------------------

    [Fact]
    public void Atlas_tiles_do_not_overlap() {
        var seen = new HashSet<(int X, int Y)>();

        for (var i = 0; i < 4; i++) {
            var viewport = ShadowCascades.TileViewport(i, 4, 512);

            Assert.True(seen.Add(((int)viewport.X, (int)viewport.Y)));
            Assert.Equal(512f, viewport.Width);
            Assert.Equal(512f, viewport.Height);
        }

        Assert.Equal(new Int2(1024, 1024), ShadowCascades.AtlasSize(4, 512));
    }

    [Fact]
    public void One_cascade_fills_its_whole_atlas() {
        var (scale, offset) = ShadowCascades.AtlasTile(0, 1);

        Assert.Equal(new Vector2(1f, 1f), scale);
        Assert.Equal(new Vector2(0f, 0f), offset);
        Assert.Equal(new Int2(512, 512), ShadowCascades.AtlasSize(1, 512));
    }

    static ShadowCascade Fit(Vector3 forward, Vector3 eye = default) =>
        ShadowCascades.Fit(eye, forward, Up, Light, Fov, Aspect, 1f, 50f, 1024);
}
