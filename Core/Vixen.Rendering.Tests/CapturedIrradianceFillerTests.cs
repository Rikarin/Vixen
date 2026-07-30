// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 19 § L2's filler B, and its third exit criterion: it agrees with filler A.
/// </summary>
/// <remarks>
///     <para>
///         <b>The agreement is the point, not a nicety.</b> § 3's whole architecture is one storage
///         layer that does not know what filled it — which is what lets § 7 promise a target with no
///         compute the same lighting model as a desktop. That promise is only worth anything if the
///         two fillers put the same numbers in the same bricks, and until this existed it was a claim
///         about a shader nobody had run twice.
///     </para>
///     <para>
///         <b>Against a directional sky rather than a uniform one.</b> A uniform environment agrees for
///         the trivial reason that both integrate a constant — the three linear coefficients are zero
///         on both sides and any transposition or sign error among them is invisible. A sky that
///         varies with direction makes them small nonzero numbers that two different quadratures have
///         to arrive at independently.
///     </para>
///     <para>
///         Nothing here renders. A capture is data, so the projection is checked with no device — the
///         same line <see cref="TracedIrradianceFiller" /> draws between what a probe sees and how it
///         is stored.
///     </para>
/// </remarks>
public class CapturedIrradianceFillerTests {
    /// <summary>How big the captured cubes are.</summary>
    /// <remarks>
    ///     Sixteen a face, which is 1536 texels against filler A's sixty-four rays. Small for a bake
    ///     and far finer than what it is being compared to, which is the way round that matters: the
    ///     tolerance below is filler A's quadrature error, not this one's.
    /// </remarks>
    const int Size = 16;

    /// <summary>A sky that varies with direction, so the linear band is not zero.</summary>
    /// <remarks>
    ///     Different per channel and smooth. Smooth because both quadratures are integrating it and a
    ///     discontinuity would be resolved differently by 1536 texels and 64 rays — which would be a
    ///     disagreement about the sky rather than about the fillers.
    /// </remarks>
    static Vector3 Sky(Vector3 direction) =>
        new(
            0.5f + (0.4f * direction.Y),
            0.6f + (0.3f * direction.X),
            0.7f + (0.2f * direction.Z)
        );

    /// <summary>A capture of <see cref="Sky" /> from anywhere, since nothing occludes it.</summary>
    sealed class OpenSky : IIrradianceCaptureSource {
        public bool TryCapture(Vector3 position, out IrradianceCapture capture) {
            var cube = new CubeImage(Size);

            for (var face = 0; face < 6; face++) {
                for (var y = 0; y < Size; y++) {
                    for (var x = 0; x < Size; x++) {
                        cube.At((CubeFace)face, x, y) = Sky(cube.DirectionOf((CubeFace)face, x, y));
                    }
                }
            }

            capture = new(cube, 1f, 1f);

            return true;
        }
    }

    /// <summary>The same sky, as filler A reads it.</summary>
    sealed class OpenSkyRays : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => CapturedIrradianceFillerTests.Sky(direction);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>A world with nothing in it.</summary>
    sealed class Nothing : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Zero;
    }

    /// <summary>A source that refuses everything, for the skip path.</summary>
    sealed class Closed : IIrradianceCaptureSource {
        public bool TryCapture(Vector3 position, out IrradianceCapture capture) {
            capture = default;

            return false;
        }
    }

    /// <summary>
    ///     A uniform environment comes back as itself, which is the closed form both fillers share.
    /// </summary>
    [Theory]
    [InlineData(0.25f)]
    [InlineData(1f)]
    [InlineData(4f)]
    public void AUniformEnvironmentLightsEverythingWithItself(float radiance) {
        var filler = new CapturedIrradianceFiller(new OpenSky());
        var capture = new IrradianceCapture(CubeImage.Uniform(Size, new(radiance)), 1f, 1f);
        var probe = filler.Project(capture, IrradianceProbe.Empty);

        foreach (var normal in (Vector3[]) [Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, Vector3.Normalize(Vector3.One)]) {
            Assert.Equal(radiance, probe.Irradiance(normal).X, radiance * 0.01f);
        }
    }

    /// <summary>
    ///     <b>And a directional sky reaches the same probe as sixty-four rays do.</b>
    /// </summary>
    /// <remarks>
    ///     Doc 19 § L2's exit criterion, stated as a tolerance because that is what it asks for. Two
    ///     per cent, and the budget is filler A's: sixty-four Fibonacci directions estimating an
    ///     integral that 1536 solid-angle-weighted texels compute far more finely. A structural error —
    ///     a transposed coefficient, a missing solid angle, a basis normalised differently — is off by
    ///     tens of per cent, not two.
    /// </remarks>
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0.577f, 0.577f, 0.577f)]
    public void TheTwoFillersAgree(float x, float y, float z) {
        var normal = Vector3.Normalize(new(x, y, z));

        var captured = new CapturedIrradianceFiller(new OpenSky());
        var traced = new TracedIrradianceFiller(new Nothing(), new OpenSkyRays());

        Assert.True(captured.Project(Fresh(), IrradianceProbe.Empty) is var b);

        var a = traced.Trace(Vector3.Zero, IrradianceProbe.Empty);

        var left = a.Irradiance(normal);
        var right = b.Irradiance(normal);

        Assert.Equal(left.X, right.X, 0.02f);
        Assert.Equal(left.Y, right.Y, 0.02f);
        Assert.Equal(left.Z, right.Z, 0.02f);
    }

    /// <summary>
    ///     <b>A sky linear in direction has an exact answer, and the projection hits it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For a sky of <c>c + a·(d·ŷ)</c> the integrals close: the constant band is
    ///         <c>0.282095 · 4π · c</c> because ∫d·ŷ dΩ vanishes, and the y linear band is
    ///         <c>0.488603 · a · 4π/3</c> because ∫(d·ŷ)² dΩ does not. Evaluated against a normal, the
    ///         basis factors and the cosine lobe's two thirds collapse to <c>c + ⅔·a·(n·ŷ)</c>.
    ///     </para>
    ///     <para>
    ///         <b>This is the test the cross-filler comparison cannot be.</b> That one is bounded by
    ///         sixty-four Fibonacci rays and has to allow two per cent, which is loose enough to accept
    ///         a projection that weighted every texel equally instead of by its own solid angle — a
    ///         cube's corners subtend far less sky than its face centres, and on a smooth sky the
    ///         difference hides inside that budget. Half a per cent against a closed form does not.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.6f, 0.8f, 0f)]
    public void ALinearSkyProjectsToItsClosedForm(float x, float y, float z) {
        const float Constant = 0.5f;
        const float Slope = 0.4f;

        var normal = Vector3.Normalize(new(x, y, z));
        var cube = new CubeImage(Size);

        for (var face = 0; face < 6; face++) {
            for (var v = 0; v < Size; v++) {
                for (var u = 0; u < Size; u++) {
                    var direction = cube.DirectionOf((CubeFace)face, u, v);

                    cube.At((CubeFace)face, u, v) = new(Constant + (Slope * direction.Y));
                }
            }
        }

        var probe = new CapturedIrradianceFiller(new OpenSky()).Project(new(cube, 1f, 1f), IrradianceProbe.Empty);
        var expected = Constant + (2f / 3f * Slope * normal.Y);

        Assert.Equal(expected, probe.Irradiance(normal).X, 0.005f);
    }

    /// <summary>
    ///     <b>One lit texel is worth exactly its own solid angle, which is where the weighting shows.</b>
    /// </summary>
    /// <param name="corner">Whether to light a texel at a face's corner or at its centre.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every other test here passes with the solid angles thrown away</b>, and finding that
    ///         out is what this test is. Cube symmetry makes uniform weights <i>exact</i> for the two
    ///         bands an L1 payload has: the weights sum to 4π by construction so the constant band is
    ///         right, and Σ(d·ŷ)² over a cube is a third of the texel count by the same symmetry that
    ///         makes Σd·d equal to it — so the linear band is right too. A smooth sky, a linear sky and
    ///         a face-uniform sky are all blind to it.
    ///     </para>
    ///     <para>
    ///         What is not blind is a single texel, because then the answer <i>is</i> its weight. A
    ///         corner texel subtends about a fifth of what a centre one does, so lighting one and
    ///         asking what the probe holds separates the two by that factor rather than by a rounding.
    ///     </para>
    ///     <para>
    ///         The weighting stays because it is right and because the symmetry that rescues it is a
    ///         property of this payload rather than of the projection — an L2 band, or a cube that was
    ///         not square, would have no such luck.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneLitTexelIsWorthItsOwnSolidAngle(bool corner) {
        const float Bright = 1000f;

        var cube = new CubeImage(Size);
        var at = corner ? 0 : Size / 2;

        cube.At(CubeFace.PositiveY, at, at) = new(Bright);

        var probe = new CapturedIrradianceFiller(new OpenSky()).Project(new(cube, 1f, 1f), IrradianceProbe.Empty);

        // The constant band alone: radiance times solid angle times the basis, and the basis again on
        // the way out. The linear band contributes along the texel's own direction and is subtracted
        // out by asking about the normal it is perpendicular to.
        var solid = cube.SolidAngleOf(at, at);
        var direction = cube.DirectionOf(CubeFace.PositiveY, at, at);
        var normal = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitX));

        Assert.Equal(Bright * solid * 0.282095f * 0.282095f, probe.Irradiance(normal).X, Bright * solid * 0.001f);
    }

    /// <summary>And the weights are an integral over the whole sphere, not an average.</summary>
    [Fact]
    public void TheTexelWeightsSumToTheWholeSphere() {
        var cube = new CubeImage(Size);
        var total = 0f;

        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                total += cube.SolidAngleOf(x, y);
            }
        }

        Assert.Equal(4f * MathF.PI, total * 6f, 0.01f);
    }

    /// <summary>A field filled by capture is a field the sampler can read.</summary>
    /// <remarks>
    ///     End to end rather than one probe: the budget, the cursor, the borders and the dilation are
    ///     the same machinery filler A uses, and the point of filler B is that it writes into it
    ///     unchanged.
    /// </remarks>
    [Fact]
    public void AFieldFilledByCaptureSamplesAsOne() {
        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(2));
        var filler = new CapturedIrradianceFiller(new OpenSky());

        field.AllocateAll();

        Assert.Equal(field.BrickCount, filler.Fill(field));
        Assert.Equal(0, filler.Skipped);

        field.Dilate();
        field.SyncBorders();

        // ⚠ Well inside the bounds. `Irradiance` biases along the normal before it samples, so a point
        // within a quarter of a probe spacing of the far face is a lookup that lands outside the field
        // and answers nothing — which is correct behaviour and a fixture measuring the wrong thing.
        //
        // Every direction of this sky is between 0.2 and 1.0, so anything a surface receives is too.
        foreach (var point in (Vector3[]) [Vector3.Zero, new(1f, -1f, 0.5f), new(-1.5f, 1.5f, 0f)]) {
            var lit = field.Irradiance(point, Vector3.UnitY);

            Assert.InRange(lit.X, 0.2f, 1f);
            Assert.InRange(lit.Y, 0.2f, 1f);
        }
    }

    /// <summary>
    ///     <b>A source that captures nothing leaves the probes alone and says how many.</b>
    /// </summary>
    /// <remarks>
    ///     A refusal and an empty capture are different answers: the first is a probe nobody asked
    ///     about and the second is one that saw darkness. Writing the second over a filled probe would
    ///     darken a field one brick at a time, and a bake that skipped half a scene silently is a
    ///     field with a dark half nobody can attribute.
    /// </remarks>
    [Fact]
    public void ARefusedCaptureLeavesTheProbeAsItWas() {
        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(2));

        field.AllocateAll();
        new CapturedIrradianceFiller(new OpenSky()).Fill(field);

        var before = field.GetProbe(field.Bricks.First(), 1, 1, 1);
        var refusing = new CapturedIrradianceFiller(new Closed());

        Assert.Equal(field.BrickCount, refusing.Fill(field));
        Assert.Equal(field.BrickCount * 64, refusing.Skipped);
        Assert.Equal(before, field.GetProbe(field.Bricks.First(), 1, 1, 1));
    }

    /// <summary>And hysteresis blends toward the new capture rather than away from it.</summary>
    /// <remarks>
    ///     The same direction <see cref="TracedIrradianceFiller" /> blends in, asserted because the two
    ///     are separate implementations of one convention and a sign flip here would look like a bake
    ///     that never converges.
    /// </remarks>
    [Fact]
    public void HysteresisKeepsMostOfTheOldAnswer() {
        var filler = new CapturedIrradianceFiller(new OpenSky()) { Hysteresis = 0.75f };
        var bright = new IrradianceCapture(CubeImage.Uniform(Size, new(4f)), 1f, 1f);

        var once = filler.Project(bright, IrradianceProbe.Empty);

        Assert.Equal(1f, once.Irradiance(Vector3.UnitY).X, 0.02f);
        Assert.Equal(0.25f, once.Validity, 0.01f);

        var twice = filler.Project(bright, once);

        Assert.Equal(1.75f, twice.Irradiance(Vector3.UnitY).X, 0.03f);
    }

    /// <summary>A capture of the directional sky, for the comparison above.</summary>
    static IrradianceCapture Fresh() {
        new OpenSky().TryCapture(Vector3.Zero, out var capture);

        return capture;
    }
}
