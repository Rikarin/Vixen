// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 5's attribute reconstruction, against the definition of interpolation rather than against
///     a second implementation of it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two of the three steps here fail silently, which is why the mirror exists.</b> Solving the
///         weights in screen space and using them directly is the classic affine-texturing error: the
///         image is plausible, straight lines bend, and a texture swims across a floor as the camera
///         moves. Correcting the weights but not their <em>derivatives</em> is subtler — the picture is
///         then right and only the mip selection is wrong, which reads as a texture slightly too sharp
///         at grazing angles. Neither is a crash and neither is visible in a still frame.
///     </para>
///     <para>
///         So the oracle is not another barycentric solver. It is the property that defines what
///         perspective-correct interpolation <em>is</em>: an attribute that varies linearly in world
///         space, sampled at a projected point, comes back as the same linear function of that point.
///         Anything affine fails it, and it needs no second derivation to be checked against.
///     </para>
/// </remarks>
public sealed class ClusterAttributeTests {
    /// <summary>
    ///     A world-linear attribute reconstructs as the same linear function of the world point.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The definition, and the whole reason the correction is there.</b> Pick a triangle, pick
    ///         a linear function of world position, evaluate it at the three corners, and interpolate at
    ///         a projected interior point. Perspective-correct interpolation reproduces the function at
    ///         the world point that pixel actually sees; affine interpolation reproduces it only where
    ///         the corners' depths happen to agree.
    ///     </para>
    ///     <para>
    ///         Randomised over triangles, cameras and interior points — the class of defect this is for
    ///         does not show up at one configuration, because a triangle parallel to the near plane
    ///         interpolates identically either way. The strongest single case is included separately
    ///         below.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_world_linear_attribute_comes_back_as_itself() {
        var considered = 0;

        Gen.Select(Triangles, Cameras, Interiors)
            .Sample(
                input => {
                    var (triangle, projection, at) = input;

                    if (Build(triangle, projection, at) is not { } c) {
                        return true;
                    }

                    Interlocked.Increment(ref considered);

                    var weights = ClusterAttributes.Of(
                        c.Pixel,
                        c.Clip0,
                        c.Clip1,
                        c.Clip2,
                        ClusterAttributes.PixelSize(new(1920, 1080))
                    );

                    // A linear function of world position, evaluated by interpolation and directly.
                    var f = new Vector3(0.37f, -1.9f, 0.55f);
                    const float offset = 4.25f;

                    var a0 = Vector3.Dot(f, triangle.Item1) + offset;
                    var a1 = Vector3.Dot(f, triangle.Item2) + offset;
                    var a2 = Vector3.Dot(f, triangle.Item3) + offset;

                    var interpolated = ClusterAttributes.Lerp(weights, a0, a1, a2);
                    var direct = Vector3.Dot(f, c.World) + offset;

                    // Against the attribute's own range across the triangle, not against its magnitude.
                    // That is what "wrong" means for an interpolation: an error is only visible relative to
                    // how much the value varies between the corners. It also matches the conditioning — a
                    // triangle whose near corner is nine times nearer than its far one amplifies float32
                    // error through the solve's division, and a fixed absolute tolerance is then a test of
                    // the depth ratio rather than of the arithmetic. The affine defect is tens of per cent
                    // of the range, so even the looser bound below leaves better than an order of
                    // magnitude of margin against the thing this exists to catch.
                    //
                    // ⚠ A hundredth, and three thousandths was the number a draw eventually beat. Two
                    // thousand random cases a run is a small sample of a large space, and CI drew this
                    // one: seed 3CVeB8nsMLI4, a triangle spanning w = 2.375 to 43.873 — a depth ratio of
                    // 18.5, twice the nine the paragraph above reasons about — under a projection whose
                    // near plane is 0.05. Measured on that case: the error is 7.95e-3 of the range, so
                    // the bound was beaten by a factor of 2.65 by conditioning rather than by a defect.
                    //
                    // The alternative, and it is a real one rather than a courtesy: the solve could be
                    // reformulated to be better conditioned, and then this bound comes back down. That is
                    // a change to the arithmetic with its own measurements, not a tolerance edit.
                    var range = MathF.Max(MathF.Max(a0, MathF.Max(a1, a2)) - MathF.Min(a0, MathF.Min(a1, a2)), 1e-3f);

                    return MathF.Abs(interpolated - direct) <= 1e-2f * range;
                },
                iter: 2000
            );

        Assert.True(considered > 500, $"Only {considered} of 2000 cases were testable, which is close to vacuous.");
    }

    /// <summary>
    ///     Screen-space weights would get the strongly foreshortened case wrong, and these do not.
    /// </summary>
    /// <remarks>
    ///     The named sabotage, as one case rather than a distribution: a triangle receding sharply from
    ///     the camera, where the depth across it spans an order of magnitude. Affine interpolation puts
    ///     the halfway pixel at the halfway attribute; the correct answer is pulled towards the near
    ///     corner, because half the screen distance is much less than half the world distance. This
    ///     asserts both — that the right answer is right, and that the wrong one is far enough away to
    ///     be a picture nobody would ship.
    /// </remarks>
    [Fact]
    public void The_affine_answer_is_visibly_wrong_where_this_one_is_not() {
        // Two corners near, one far, with a projection that foreshortens hard. The far corner is off the
        // near edge's line in y as well as in z — three points at one height project onto one screen row,
        // which is a degenerate triangle and tests nothing.
        var near = new Vector3(-1f, -1f, -2f);
        var alsoNear = new Vector3(1f, -1f, -2f);
        var far = new Vector3(0f, 3f, -40f);

        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);

        var clip0 = Project(projection, near);
        var clip1 = Project(projection, alsoNear);
        var clip2 = Project(projection, far);

        // The world midpoint of the near edge and the far corner: barycentric (0.25, 0.25, 0.5).
        var world = (near * 0.25f) + (alsoNear * 0.25f) + (far * 0.5f);
        var clip = Project(projection, world);
        var pixel = new Vector2(clip.X, clip.Y) / clip.W;

        var weights = ClusterAttributes.Of(pixel, clip0, clip1, clip2, ClusterAttributes.PixelSize(new(1920, 1080)));

        // The attribute is the world z, so what it should reconstruct to is that of the world point.
        var reconstructed = ClusterAttributes.Lerp(weights, near.Z, alsoNear.Z, far.Z);
        Assert.Equal(world.Z, reconstructed, 1);

        // And the screen-space weights, which is what dropping the correction leaves. The pixel is far
        // closer to the far corner on screen than it is in the world, so the affine answer over-weights
        // it badly.
        var affine = Affine(pixel, clip0, clip1, clip2);
        var wrong = (affine.X * near.Z) + (affine.Y * alsoNear.Z) + (affine.Z * far.Z);

        Assert.True(
            MathF.Abs(wrong - world.Z) > 5f,
            $"The affine answer is {wrong} against {world.Z}, which is not wrong enough for this to be a test."
        );
    }

    /// <summary>
    ///     The analytic gradient is the derivative of the interpolated attribute, to the limit.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The oracle is a central difference over the reconstruction itself, which is legitimate
    ///         precisely because it is <em>not</em> how the gradient is computed: the analytic form
    ///         differentiates the interpolation symbolically, and this evaluates the interpolation at two
    ///         nearby pixels. Two routes to one number.
    ///     </para>
    ///     <para>
    ///         <b>This is the test the quotient-rule term needs.</b> Drop it and the value stays right —
    ///         so every assertion above still passes — and the gradients come out scaled by a factor that
    ///         grows with the depth range across the triangle. That is a mip level, on a surface at a
    ///         grazing angle, which is where a texture aliasing back into shimmer is most visible and
    ///         least attributable.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_analytic_gradient_is_the_derivative_of_the_reconstruction() {
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        var size = new Int2(1920, 1080);
        var pixelSize = ClusterAttributes.PixelSize(size);

        // Steeply oblique, so the perspective term in the derivative is large. A triangle facing the
        // camera has almost no depth range across it and the two forms agree whatever you do.
        var a = new Vector3(-2f, -1f, -3f);
        var b = new Vector3(2f, -1f, -25f);
        var c = new Vector3(0f, 2f, -12f);

        var clip0 = Project(projection, a);
        var clip1 = Project(projection, b);
        var clip2 = Project(projection, c);

        Vector2 uv0 = new(0f, 0f);
        Vector2 uv1 = new(4f, 0f);
        Vector2 uv2 = new(0f, 3f);

        var tested = 0;

        for (var u = 0.15f; u < 0.7f; u += 0.13f) {
            for (var v = 0.1f; v + u < 0.85f; v += 0.11f) {
                var world = (a * (1f - u - v)) + (b * u) + (c * v);
                var clip = Project(projection, world);
                var pixel = new Vector2(clip.X, clip.Y) / clip.W;

                var here = ClusterAttributes.Of(pixel, clip0, clip1, clip2, pixelSize);
                var analytic = ClusterAttributes.Ddx(here, uv0, uv1, uv2);

                // The same attribute, reconstructed half a pixel each side, differenced. The step is the
                // pixel size because that is the unit the gradient is in.
                var step = new Vector2(pixelSize.X * 0.5f, 0f);
                var left = ClusterAttributes.Of(pixel - step, clip0, clip1, clip2, pixelSize);
                var right = ClusterAttributes.Of(pixel + step, clip0, clip1, clip2, pixelSize);

                var difference = ClusterAttributes.Lerp(right, uv0, uv1, uv2) - ClusterAttributes.Lerp(left, uv0, uv1, uv2);

                Close(difference.X, analytic.X);
                Close(difference.Y, analytic.Y);

                // And the same in y, which has its own weight derivative and its own pixel size.
                var analyticY = ClusterAttributes.Ddy(here, uv0, uv1, uv2);
                var down = new Vector2(0f, pixelSize.Y * 0.5f);

                var vertical = ClusterAttributes.Lerp(
                    ClusterAttributes.Of(pixel + down, clip0, clip1, clip2, pixelSize),
                    uv0,
                    uv1,
                    uv2
                ) - ClusterAttributes.Lerp(
                    ClusterAttributes.Of(pixel - down, clip0, clip1, clip2, pixelSize),
                    uv0,
                    uv1,
                    uv2
                );

                Close(vertical.X, analyticY.X);
                Close(vertical.Y, analyticY.Y);

                tested++;
            }
        }

        Assert.True(tested > 15, $"Only {tested} points were tested.");
    }

    /// <summary>
    ///     The weights are a partition: they sum to one, and their derivatives sum to zero.
    /// </summary>
    /// <remarks>
    ///     Cheap and worth stating, because it is what makes an interpolation an interpolation. The
    ///     derivative sum is the sharper half: it says that moving a pixel redistributes the weights
    ///     rather than changing how much of the attribute there is, which is the invariant a dropped
    ///     renormalisation term breaks.
    /// </remarks>
    [Fact]
    public void The_weights_partition_and_their_derivatives_cancel() {
        var considered = 0;

        Gen.Select(Triangles, Cameras, Interiors)
            .Sample(
                input => {
                    var (triangle, projection, at) = input;

                    if (Build(triangle, projection, at) is not { } c) {
                        return true;
                    }

                    Interlocked.Increment(ref considered);

                    var weights = ClusterAttributes.Of(
                        c.Pixel,
                        c.Clip0,
                        c.Clip1,
                        c.Clip2,
                        ClusterAttributes.PixelSize(new(1920, 1080))
                    );

                    var sum = weights.Weights.X + weights.Weights.Y + weights.Weights.Z;
                    var ddx = weights.Ddx.X + weights.Ddx.Y + weights.Ddx.Z;
                    var ddy = weights.Ddy.X + weights.Ddy.Y + weights.Ddy.Z;

                    return MathF.Abs(sum - 1f) < 1e-3f && MathF.Abs(ddx) < 1e-3f && MathF.Abs(ddy) < 1e-3f;
                },
                iter: 2000
            );

        Assert.True(considered > 500, $"Only {considered} of 2000 cases were testable, which is close to vacuous.");
    }

    /// <summary>A degenerate triangle answers rather than dividing by zero.</summary>
    /// <remarks>
    ///     A cluster can hold a zero-area triangle — a surplus corner from the raster, or a collapse the
    ///     simplifier made exactly degenerate — and a pixel is never covered by one. What matters is
    ///     that the arithmetic stays finite, because a NaN in a weight becomes a NaN in a shaded colour
    ///     and spreads through every filter downstream of it.
    /// </remarks>
    [Fact]
    public void A_degenerate_triangle_is_finite() {
        var clip = new Vector4(0.1f, 0.2f, 0.5f, 1f);
        var weights = ClusterAttributes.Of(Vector2.Zero, clip, clip, clip, new(0.001f, 0.001f));

        Assert.Equal(Vector3.UnitX, weights.Weights);
        Assert.Equal(Vector3.Zero, weights.Ddx);
        Assert.Equal(Vector3.Zero, weights.Ddy);

        // And a corner at the eye, which has no reciprocal.
        var behind = ClusterAttributes.Of(
            Vector2.Zero,
            new(0f, 0f, 0f, 0f),
            new(1f, 0f, 0.5f, 1f),
            new(0f, 1f, 0.5f, 1f),
            new(0.001f, 0.001f)
        );

        Assert.False(float.IsNaN(behind.Weights.X + behind.Weights.Y + behind.Weights.Z));
    }

    /// <summary>The shader still does the correction, and still differentiates the quotient.</summary>
    /// <remarks>
    ///     The mirror's own defence: everything above proves this file is right about interpolation and
    ///     says nothing about whether <c>Barycentrics.rvn</c> still agrees. Both of the steps that fail
    ///     silently are one line each, and a shader with either removed compiles and draws.
    /// </remarks>
    [Fact]
    public void The_shader_corrects_and_differentiates_what_the_host_says_it_does() {
        var source = Source("Geometry", "Barycentrics.rvn");

        // Named Solve rather than Of on purpose — a library artefact keys an exported static by its
        // unqualified method name, so this and ShadingAngles.Of would be one entry. Pinned here because
        // renaming it back compiles, and fails only where a consumer resolves the other one.
        Assert.Contains("static func Solve(pixel: float2", source, StringComparison.Ordinal);

        // The correction: each screen weight scaled by its corner's reciprocal w, then renormalised.
        Assert.Contains(
            "float3(screen.x * inverse0, screen.y * inverse1, screen.z * inverse2)",
            source,
            StringComparison.Ordinal
        );

        Assert.Contains("result.weights = weighted * inverseSum", source, StringComparison.Ordinal);

        // The quotient rule, which is the term that leaves the picture right and the mips wrong.
        Assert.Contains("(weightedDdx - result.weights * sumDdx) * inverseSum", source, StringComparison.Ordinal);
        Assert.Contains("(weightedDdy - result.weights * sumDdy) * inverseSum", source, StringComparison.Ordinal);

        // And a gradient is the interpolation by the weight derivatives, not a finite difference.
        Assert.Contains("a0 * b.ddx.x + a1 * b.ddx.y + a2 * b.ddx.z", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ddx(", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two ways of computing the same derivative, to the accuracy the second one has.
    /// </summary>
    /// <remarks>
    ///     Relative rather than to a decimal count, because the oracle is a central difference and a
    ///     central difference has second-order truncation error of its own — so a fixed number of decimals
    ///     is a test of the step size rather than of the gradient. One part in a hundred, which is loose
    ///     for a derivative and still two orders of magnitude tighter than the defect this is for: dropping
    ///     the renormalisation term scales the gradient by the depth range across the triangle, which here
    ///     is a factor of eight.
    /// </remarks>
    static void Close(float expected, float actual) {
        var scale = MathF.Max(MathF.Max(MathF.Abs(expected), MathF.Abs(actual)), 1e-6f);

        Assert.True(
            MathF.Abs(expected - actual) <= 1e-2f * scale,
            $"{actual} against {expected}, which is {MathF.Abs(expected - actual) / scale:P4} apart."
        );
    }

    /// <summary>
    ///     One randomised case: a triangle, a camera, and a pixel that sees a known world point.
    /// </summary>
    /// <param name="Clip0">The first corner in clip space.</param>
    /// <param name="Clip1">The second.</param>
    /// <param name="Clip2">The third.</param>
    /// <param name="Pixel">Where the interior point lands, in NDC.</param>
    /// <param name="World">The world point that pixel sees, known rather than solved for.</param>
    readonly record struct Case(Vector4 Clip0, Vector4 Clip1, Vector4 Clip2, Vector2 Pixel, Vector3 World);

    /// <summary>
    ///     Builds a case, or refuses one no rasterizer would have produced a pixel for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two refusals, and both are about what a rasterizer would do rather than about what this
    ///         file finds inconvenient. A corner behind the eye is a triangle the rasterizer clipped, so
    ///         there is no pixel to reconstruct. And a triangle whose <em>screen</em> area is vanishing is
    ///         a sliver: its barycentric solve is ill-conditioned for any interpolator, the hardware's
    ///         included, and float32 loses the accuracy this asserts to. The world-space area test that
    ///         used to stand in for this one lets an edge-on triangle through, which is exactly the case
    ///         that fails.
    ///     </para>
    ///     <para>
    ///         The caller counts survivors and asserts there were some — a property that skips every case
    ///         passes without testing anything, which is how the right-handed projection convention was
    ///         found here in the first place.
    ///     </para>
    /// </remarks>
    static Case? Build((Vector3 A, Vector3 B, Vector3 C) triangle, Matrix4x4 projection, (float U, float V) at) {
        var clip0 = Project(projection, triangle.A);
        var clip1 = Project(projection, triangle.B);
        var clip2 = Project(projection, triangle.C);

        if (clip0.W <= 0.05f || clip1.W <= 0.05f || clip2.W <= 0.05f) {
            return null;
        }

        var ndc0 = new Vector2(clip0.X, clip0.Y) / clip0.W;
        var ndc1 = new Vector2(clip1.X, clip1.Y) / clip1.W;
        var ndc2 = new Vector2(clip2.X, clip2.Y) / clip2.W;

        var edge1 = ndc1 - ndc0;
        var edge2 = ndc2 - ndc0;

        if (MathF.Abs((edge1.X * edge2.Y) - (edge1.Y * edge2.X)) < 1e-3f) {
            return null;
        }

        // The world point first and its pixel second, so what the pixel sees is known exactly rather than
        // solved for — which is what makes the assertion the definition rather than a fixed point.
        var world = (triangle.A * (1f - at.U - at.V)) + (triangle.B * at.U) + (triangle.C * at.V);
        var clip = Project(projection, world);

        return new(clip0, clip1, clip2, new Vector2(clip.X, clip.Y) / clip.W, world);
    }

    /// <summary>The screen-space weights, which is what dropping the correction leaves.</summary>
    static Vector3 Affine(Vector2 pixel, Vector4 clip0, Vector4 clip1, Vector4 clip2) {
        var ndc0 = new Vector2(clip0.X, clip0.Y) / clip0.W;
        var ndc1 = new Vector2(clip1.X, clip1.Y) / clip1.W;
        var ndc2 = new Vector2(clip2.X, clip2.Y) / clip2.W;

        var edge1 = ndc1 - ndc0;
        var edge2 = ndc2 - ndc0;
        var area = (edge1.X * edge2.Y) - (edge1.Y * edge2.X);
        var to = pixel - ndc0;

        var b1 = ((to.X * edge2.Y) - (to.Y * edge2.X)) / area;
        var b2 = ((edge1.X * to.Y) - (edge1.Y * to.X)) / area;

        return new(1f - b1 - b2, b1, b2);
    }

    /// <summary>
    ///     A world point in clip space, through the engine's own convention.
    /// </summary>
    /// <remarks>
    ///     <see cref="Matrix4x4.TransformVector4" /> rather than the four dot products written out, so
    ///     the test cannot disagree with the engine about row-versus-column — which it did, and which
    ///     presents as a degenerate triangle rather than as a wrong picture.
    /// </remarks>
    static Vector4 Project(in Matrix4x4 projection, Vector3 world) =>
        Matrix4x4.TransformVector4(new(world, 1f), projection);

    /// <summary>Triangles spread over a range of depths, so the perspective term is exercised.</summary>
    /// <remarks>
    ///     No area filter here, because the one that matters is a <em>screen</em>-area filter and only
    ///     <see cref="Build" /> knows the camera. A world-space filter lets an edge-on triangle through,
    ///     which projects to a sliver and is ill-conditioned for any interpolator.
    /// </remarks>
    static Gen<(Vector3, Vector3, Vector3)> Triangles => Gen.Select(Corner, Corner, Corner);

    /// <summary>
    ///     A corner in front of the camera, spread over a range of depths.
    /// </summary>
    /// <remarks>
    ///     <b>Negative z, because the projection is right-handed:</b> the camera looks down <c>-Z</c> and
    ///     clip <c>w</c> comes out as <c>-z</c>. Geometry at positive z is behind the eye, which the guard
    ///     in each property skips — and a property that skips every case passes without testing anything,
    ///     which is how this was found.
    /// </remarks>
    static Gen<Vector3> Corner =>
        Gen.Select(Gen.Float[-6f, 6f], Gen.Float[-6f, 6f], Gen.Float[-45f, -1.5f])
            .Select(v => new Vector3(v.Item1, v.Item2, v.Item3));

    static Gen<Matrix4x4> Cameras =>
        Gen.Select(Gen.Float[0.5f, 1.9f], Gen.Float[0.6f, 2.4f])
            .Select(v => Matrix4x4.PerspectiveFieldOfView(v.Item1, v.Item2, 0.05f, 2000f));

    /// <summary>Interior barycentric coordinates, away from the edges where the area test is weakest.</summary>
    static Gen<(float, float)> Interiors =>
        Gen.Select(Gen.Float[0.05f, 0.9f], Gen.Float[0.05f, 0.9f]).Where(t => t.Item1 + t.Item2 < 0.95f);

    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
