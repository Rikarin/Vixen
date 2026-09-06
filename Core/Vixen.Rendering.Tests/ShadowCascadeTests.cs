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

    // --- Selecting one ------------------------------------------------------

    /// <summary>
    ///     The cascade a fragment selects is one whose projection contains that fragment.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The round trip a cascaded atlas rests on, and the claim neither half can make alone.
    ///         The shader picks a cascade from its own view depth; the host fitted that cascade to a
    ///         slice of the frustum. If the two disagree — an off-by-one in the comparison, splits
    ///         published in the wrong order — a fragment projects outside the tile it was sent to and
    ///         comes back unshadowed, which reads as a shadow distance shorter than the setting
    ///         rather than as a mismatch.
    ///     </para>
    ///     <para>
    ///         Off-axis as well as down the middle, because the slices are cut by distance and the
    ///         cascades are fitted to spheres around them: a fragment at the edge of the screen is
    ///         further from the camera than its depth, and the sphere is what has to cover it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_cascade_a_fragment_selects_contains_it() {
        const float shadowDistance = 150f;

        Span<float> splits = stackalloc float[4];
        ShadowCascades.Split(0.1f, shadowDistance, 0.75f, splits);

        var eye = new Vector3(2f, 6f, -3f);
        var forward = Vector3.Normalize(new(0.2f, -0.1f, -1f));
        var right = Vector3.Normalize(Vector3.Cross(forward, Up));
        var up = Vector3.Cross(right, forward);

        var fitted = new BoundingFrustum[splits.Length];
        var near = 0.1f;

        for (var i = 0; i < splits.Length; i++) {
            var cascade = ShadowCascades.Fit(eye, forward, Up, Light, Fov, Aspect, near, splits[i], 1024);
            fitted[i] = new(cascade.ViewProjection);
            near = splits[i];
        }

        var tanY = MathF.Tan(Fov * 0.5f);
        var tanX = tanY * Aspect;

        foreach (var depth in (float[])[0.5f, 3f, 12f, 30f, 90f, 149f]) {
            var index = ShadowCascades.CascadeOf(depth, splits);

            // Its own slice, which is the point: the nearest cascade that still reaches it.
            Assert.True(depth <= splits[index], $"{depth} selected cascade {index}, which ends at {splits[index]}");
            Assert.True(index == 0 || depth > splits[index - 1], $"{depth} skipped cascade {index - 1}");

            foreach (var (sx, sy) in (( int X, int Y)[])[(0, 0), (1, 1), (-1, 1), (1, -1), (-1, -1)]) {
                var point = eye
                    + (forward * depth)
                    + (right * (sx * tanX * depth))
                    + (up * (sy * tanY * depth));

                Assert.True(
                    fitted[index].Contains(point),
                    $"a fragment at {depth} on ({sx},{sy}) selected cascade {index} and is outside it"
                );
            }
        }
    }

    /// <summary>Past the last split a fragment falls through to the last cascade.</summary>
    /// <remarks>
    ///     Rather than to none, which has no index to be. What stops that being a hard line across
    ///     the ground is <c>Lighting.CascadeFade</c>, which ramps the shadow out over the last
    ///     cascade's final metres — the shader's business, and the reason this may fall through at
    ///     all.
    /// </remarks>
    [Fact]
    public void Past_the_last_split_a_fragment_takes_the_last_cascade() {
        Span<float> splits = stackalloc float[4];
        ShadowCascades.Split(0.1f, 150f, 0.75f, splits);

        Assert.Equal(3, ShadowCascades.CascadeOf(150f, splits));
        Assert.Equal(3, ShadowCascades.CascadeOf(1_000f, splits));
        Assert.Equal(0, ShadowCascades.CascadeOf(0f, splits));
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

    /// <summary>
    ///     A cascade's atlas matrix lands a point in that cascade's tile and nowhere else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What a shading pass is given, and the reason it is given a composed matrix rather than
    ///         the cascade's own: a shader with one atlas and one matrix does <c>NdcToUv(M · p)</c>,
    ///         which addresses the whole texture. With four tiles in it, the raw matrix sends every
    ///         lookup a quarter of the way into the wrong one — and reads a plausible depth from it,
    ///         which is why nothing about the result looks like a mismatch.
    ///     </para>
    ///     <para>
    ///         Checked against the tile the atlas says it is, rather than against numbers typed here:
    ///         the composed lookup must be exactly the plain one mapped into that rectangle.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_atlas_matrix_lands_in_its_own_tile() {
        var cascade = Fit(new(0f, 0f, 1f));

        foreach (var index in (int[])[0, 1, 2, 3]) {
            var (scale, offset) = ShadowCascades.AtlasTile(index, 4);
            var atlas = ShadowCascades.AtlasProjection(cascade, index, 4);

            foreach (var point in Points(cascade)) {
                var plain = Uv(cascade.ViewProjection, point);
                var tiled = Uv(atlas, point);

                Assert.Equal(offset.X + (scale.X * plain.X), tiled.X, 4);
                Assert.Equal(offset.Y + (scale.Y * plain.Y), tiled.Y, 4);

                // And inside the tile, which is the claim a driver would otherwise make for us by
                // sampling somebody else's depth.
                Assert.InRange(tiled.X, offset.X - 1e-4f, offset.X + scale.X + 1e-4f);
                Assert.InRange(tiled.Y, offset.Y - 1e-4f, offset.Y + scale.Y + 1e-4f);
            }
        }
    }

    /// <summary>
    ///     The tile a lookup lands in is the tile the viewport drew into.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim the two halves of an atlas have to share, and the one nothing asserted.</b>
    ///         <see cref="ShadowCascades.TileViewport" /> decides where a cascade is <em>rendered</em>
    ///         and <see cref="ShadowCascades.AtlasTile" /> decides where it is <em>read</em>, and the
    ///         two are separate functions that happen to agree — until one of the conventions
    ///         underneath them moves, which is what happened when <c>Transform.NdcToUv</c> gained its
    ///         y negation.
    ///     </para>
    ///     <para>
    ///         Both are stated in texels here, from the top-left, because that is what a Vulkan
    ///         framebuffer and a sampled image both use — and it is the step where a sign gets lost.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_atlas_matrix_lands_in_the_tile_its_viewport_drew_into() {
        const int Resolution = 512;
        var cascade = Fit(new(0f, 0f, 1f));
        var atlas = ShadowCascades.AtlasSize(4, Resolution);

        foreach (var index in (int[])[0, 1, 2, 3]) {
            var viewport = ShadowCascades.TileViewport(index, 4, Resolution);
            var matrix = ShadowCascades.AtlasProjection(cascade, index, 4);

            foreach (var point in Points(cascade)) {
                var uv = Uv(matrix, point);
                var texel = new Vector2(uv.X * atlas.X, uv.Y * atlas.Y);

                Assert.InRange(texel.X, viewport.X - 0.5f, viewport.X + viewport.Width + 0.5f);
                Assert.InRange(texel.Y, viewport.Y - 0.5f, viewport.Y + viewport.Height + 0.5f);
            }
        }
    }

    /// <summary>
    ///     Turning the camera does not turn the shadow.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The light's basis is the light's.</b> Two cameras at the same place looking the same
    ///         way see the same sphere, so they must be fitted with the same matrix however they are
    ///         rolled — and a basis taken from the camera's <c>up</c> instead puts the camera's
    ///         orientation into the light's texel grid, which turns the shadow map under stationary
    ///         geometry as the player looks around.
    ///     </para>
    ///     <para>
    ///         It is not subtle once it is on screen and it is invisible in every other assertion here:
    ///         the sphere is the right size, the snapping is stable, the cascade covers its slice, and
    ///         the shadows rotate.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Rolling_the_camera_does_not_turn_the_light() {
        var forward = Vector3.Normalize(new Vector3(0.3f, -0.2f, 1f));
        var right = Vector3.Normalize(Vector3.Cross(forward, Up));

        var upright = ShadowCascades.Fit(Vector3.Zero, forward, Up, Light, Fov, Aspect, 1f, 50f, 1024);

        // The same camera, rolled about its own forward — and then some. Every one of these sees an
        // identical frustum, so every one of them must be fitted identically.
        foreach (var angle in (float[])[0.1f, 0.7f, 1.5f, 3f]) {
            var rolled = Vector3.Normalize(
                (Vector3.Normalize(Vector3.Cross(right, forward)) * MathF.Cos(angle)) + (right * MathF.Sin(angle))
            );

            var turned = ShadowCascades.Fit(Vector3.Zero, forward, rolled, Light, Fov, Aspect, 1f, 50f, 1024);

            Assert.Equal(upright.ViewProjection, turned.ViewProjection);
        }
    }

    /// <summary>
    ///     Orbiting the camera translates the cascade and never turns it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The stronger form of the claim above, and the one that matches what a third-person
    ///         camera actually does: turning it does not merely roll it, it swings the eye around the
    ///         character and points it somewhere new. The cascade is <em>supposed</em> to follow —
    ///         it is fitted to the frustum — so its centre moves and its snapped origin with it.
    ///     </para>
    ///     <para>
    ///         What must not move is the basis. The upper 3×3 of the fitted matrix is the light's
    ///         rotation into shadow space, and if any of it follows the camera then the shadow map's
    ///         texel grid turns under stationary geometry, which is what "the shadows rotate when I
    ///         rotate the camera" looks like from inside the game.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Orbiting_the_camera_moves_the_cascade_without_turning_it() {
        var target = new Vector3(0f, 1f, 0f);
        Matrix4x4? basis = null;

        foreach (var yaw in (float[])[0f, 0.6f, 1.9f, 3.4f, 5.1f]) {
            var eye = target + new Vector3(MathF.Sin(yaw) * 6f, 2.5f, MathF.Cos(yaw) * 6f);
            var fitted = ShadowCascades.Fit(eye, target - eye, Up, Light, Fov, Aspect, 1f, 50f, 1024);

            // The rotation and scale alone — the translation is the cascade following the camera,
            // which is the whole point of fitting one.
            var m = fitted.ViewProjection;

            var rotation = new Matrix4x4(
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                0f, 0f, 0f, 1f
            );

            basis ??= rotation;

            // To seven places rather than exactly: the orthographic extent is a function of the
            // sphere's radius, which is recomputed per orbit step and lands a few ulps apart. A basis
            // that followed the camera would differ in the third place, not the seventh.
            AssertClose(basis.Value, rotation);
        }
    }

    /// <summary>Two matrices, element by element, to seven places.</summary>
    static void AssertClose(in Matrix4x4 expected, in Matrix4x4 actual) {
        Assert.Equal(expected.M11, actual.M11, 7);
        Assert.Equal(expected.M12, actual.M12, 7);
        Assert.Equal(expected.M13, actual.M13, 7);
        Assert.Equal(expected.M21, actual.M21, 7);
        Assert.Equal(expected.M22, actual.M22, 7);
        Assert.Equal(expected.M23, actual.M23, 7);
        Assert.Equal(expected.M31, actual.M31, 7);
        Assert.Equal(expected.M32, actual.M32, 7);
        Assert.Equal(expected.M33, actual.M33, 7);
    }

    /// <summary>One cascade filling the atlas is left exactly as it was.</summary>
    /// <remarks>
    ///     The case that has to stay free: a single-cascade atlas is one tile at the origin, so the
    ///     composition is the identity and a project that never asked for cascades pays nothing for
    ///     the ones it did not ask for.
    /// </remarks>
    [Fact]
    public void One_cascade_is_composed_with_nothing() {
        var cascade = Fit(new(0f, 0f, 1f));
        var atlas = ShadowCascades.AtlasProjection(cascade, 0, 1);

        foreach (var point in Points(cascade)) {
            var plain = Uv(cascade.ViewProjection, point);
            var tiled = Uv(atlas, point);

            Assert.Equal(plain.X, tiled.X, 5);
            Assert.Equal(plain.Y, tiled.Y, 5);
        }
    }

    /// <summary>A spread of points inside a cascade, so the claim is not about one of them.</summary>
    static IEnumerable<Vector3> Points(ShadowCascade cascade) {
        var radius = cascade.Radius * 0.5f;

        yield return cascade.Centre;
        yield return cascade.Centre + new Vector3(radius, 0f, 0f);
        yield return cascade.Centre - new Vector3(0f, radius, 0f);
        yield return cascade.Centre + new Vector3(radius * 0.3f, -radius * 0.7f, radius * 0.2f);
    }

    /// <summary>Where a world point lands in a projection's UV, the way the shader computes it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Transform.NdcToUv</c> exactly, y negated — and it used not to be.</b> This helper
    ///     mapped y straight through, which made every assertion below a statement about a convention
    ///     no shader uses: the atlas fold and the test agreed with each other and neither agreed with
    ///     the lookup. That is how a tile row could be inverted for four days without a failure.
    /// </remarks>
    static Vector2 Uv(in Matrix4x4 matrix, Vector3 point) {
        var clip = Matrix4x4.TransformVector4(new(point, 1f), matrix);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        return new((ndc.X * 0.5f) + 0.5f, (-ndc.Y * 0.5f) + 0.5f);
    }

    static ShadowCascade Fit(Vector3 forward, Vector3 eye = default) =>
        ShadowCascades.Fit(eye, forward, Up, Light, Fov, Aspect, 1f, 50f, 1024);
}
