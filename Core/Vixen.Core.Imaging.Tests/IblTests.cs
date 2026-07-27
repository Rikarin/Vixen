// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>
///     Image-based lighting is unusually good to test, because most of it has an answer that can be
///     worked out on paper. A uniform environment of radiance L lights a surface with irradiance πL
///     whatever way it faces; the solid angles of a cube's texels sum to 4π and nothing else; at
///     roughness zero the split-sum's two terms are Schlick's Fresnel split in half and sum to one.
///     None of those are comparisons of this code against itself, which is what makes them worth
///     more than the round trips.
/// </summary>
public sealed class IblTests {
    /// <summary>
    ///     <para>
    ///         The sharpest check on the cube geometry there is. A cube map's texels do not cover
    ///         equal amounts of sky — the one at the centre of a face covers about five times what
    ///         the one at its corner does — and every integral over an environment is wrong by that
    ///         factor if it pretends otherwise.
    ///     </para>
    ///     <para>
    ///         Summed over six faces, they have to come to the surface area of the unit sphere. Any
    ///         mistake in the texel-centre positions, the face extent or the projection's exponent
    ///         moves this number, and almost nothing else in the file would notice.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(64)]
    public void AllTheSolidAnglesOfACubeSumToTheWholeSphere(int size) {
        var total = 0.0;

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    total += CubeMap.SolidAngleOfTexel(x, y, size);
                }
            }
        }

        // Exactly, at every size: the formula telescopes over a face to four corner terms, and over
        // six faces to 4π. The midpoint approximation this replaced was 1.5% out on a 4×4 face and
        // 91% out on a 1×1 one, which is the difference between a tolerance and an equality.
        Assert.Equal(4 * Math.PI, total, 5);
    }

    /// <summary>And they are not equal, which is the reason the weighting exists at all.</summary>
    [Fact]
    public void ATexelAtTheCentreOfAFaceCoversMoreSkyThanOneAtItsCorner() {
        var centre = CubeMap.SolidAngleOfTexel(16, 16, 32);
        var corner = CubeMap.SolidAngleOfTexel(0, 0, 32);

        Assert.True(centre > corner * 4f, $"the centre covers {centre} and the corner {corner}");
    }

    /// <summary>
    ///     Every texel's direction has to come back to the texel it came from, or a prefiltered cube
    ///     is a rearrangement of the environment rather than a convolution of it.
    /// </summary>
    [Fact]
    public void EveryTexelsDirectionLandsBackOnThatTexel() {
        const int size = 8;

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var direction = CubeMap.DirectionOfTexel(face, x, y, size);
                    var (backFace, u, v) = CubeMap.FaceOf(direction);

                    Assert.Equal(face, backFace);
                    Assert.Equal(x, (int)((u + 1f) * 0.5f * size));
                    Assert.Equal(y, (int)((v + 1f) * 0.5f * size));
                }
            }
        }
    }

    /// <summary>
    ///     And the six face centres point along the six axes, in the order every graphics API and
    ///     KTX2 store them. Getting this order wrong turns a sky box inside out.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, -1, 0, 0)]
    [InlineData(2, 0, 1, 0)]
    [InlineData(3, 0, -1, 0)]
    [InlineData(4, 0, 0, 1)]
    [InlineData(5, 0, 0, -1)]
    public void EachFaceCentrePointsAlongItsAxis(int face, float x, float y, float z) {
        var direction = CubeMap.DirectionOf(face, 0f, 0f);

        Assert.Equal(x, direction.X, 5);
        Assert.Equal(y, direction.Y, 5);
        Assert.Equal(z, direction.Z, 5);
    }

    /// <summary>
    ///     <para>
    ///         The exact one. A surface under a uniform environment of radiance L receives irradiance
    ///         πL however it is turned, so the quantity a shader multiplies by albedo — irradiance
    ///         over π — is exactly L. Every constant in the projection and the evaluation has to be
    ///         right for this to come out, and any of them being wrong gives an answer that is a
    ///         plausible-looking multiple of the truth.
    ///     </para>
    ///     <para>
    ///         The classic failure it catches is a missing or doubled π, which produces a scene that
    ///         is uniformly 3.14 times too bright or too dark and gets compensated for by turning all
    ///         the lights down.
    ///     </para>
    /// </summary>
    [Fact]
    public void AUniformEnvironmentLightsEverythingWithExactlyItsOwnRadiance() {
        var radiance = new Vector3(0.5f, 1f, 2f);
        var harmonics = SphericalHarmonicsL2.Project(Uniform(16, radiance));

        foreach (var normal in Directions()) {
            var irradiance = harmonics.Irradiance(normal);

            Assert.Equal(radiance.X, irradiance.X, 3);
            Assert.Equal(radiance.Y, irradiance.Y, 3);
            Assert.Equal(radiance.Z, irradiance.Z, 3);
        }
    }

    /// <summary>
    ///     And a uniform environment puts all of its energy in the first coefficient: the other eight
    ///     describe how the sky varies, and it does not.
    /// </summary>
    [Fact]
    public void AUniformEnvironmentHasNothingInItsHigherBands() {
        var harmonics = SphericalHarmonicsL2.Project(Uniform(16, Vector3.One));

        Assert.True(harmonics.Coefficients[0].Y > 3f, "the constant band should hold 4π · 0.282");

        for (var index = 1; index < SphericalHarmonicsL2.Count; index++) {
            Assert.Equal(0f, harmonics.Coefficients[index].Length(), 3);
        }
    }

    /// <summary>
    ///     The basis is orthonormal over the sphere, and the discrete sum over a cube's texels has to
    ///     reproduce that or every projection is subtly cross-contaminated. This is the check that
    ///     the solid-angle weighting and the basis constants agree with each other.
    /// </summary>
    [Fact]
    public void TheBasisIsOrthonormalWhenSummedOverACube() {
        const int size = 32;
        var products = new double[SphericalHarmonicsL2.Count, SphericalHarmonicsL2.Count];
        Span<float> basis = stackalloc float[SphericalHarmonicsL2.Count];

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    SphericalHarmonicsL2.Evaluate(CubeMap.DirectionOfTexel(face, x, y, size), basis);
                    var solidAngle = CubeMap.SolidAngleOfTexel(x, y, size);

                    for (var row = 0; row < SphericalHarmonicsL2.Count; row++) {
                        for (var column = 0; column < SphericalHarmonicsL2.Count; column++) {
                            products[row, column] += basis[row] * basis[column] * solidAngle;
                        }
                    }
                }
            }
        }

        for (var row = 0; row < SphericalHarmonicsL2.Count; row++) {
            for (var column = 0; column < SphericalHarmonicsL2.Count; column++) {
                Assert.Equal(row == column ? 1.0 : 0.0, products[row, column], 2);
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         Projecting a basis function gives back its own coefficient and nothing else. This is
    ///         orthonormality asserted of <see cref="SphericalHarmonicsL2.Project" /> rather than of
    ///         the basis, and it is the only test here that notices whether the projection actually
    ///         uses each texel's own solid angle.
    ///     </para>
    ///     <para>
    ///         It exists because replacing the per-texel weight with a uniform 4π/6N² — the mistake
    ///         anyone would make — left every other test in this file green. A constant environment
    ///         cannot see the difference, because any weighting that sums to 4π integrates a constant
    ///         correctly; it takes an environment that varies across a face before the corners being
    ///         over-counted shows up.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void ProjectingABasisFunctionGivesBackThatOneCoefficientAndNoOther(int which) {
        const int size = 32;
        var cube = new TextureData(PixelFormat.Rgba32Float, size, size, levelCount: 1, faceCount: CubeMap.Faces);
        Span<float> basis = stackalloc float[SphericalHarmonicsL2.Count];

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    SphericalHarmonicsL2.Evaluate(CubeMap.DirectionOfTexel(face, x, y, size), basis);
                    CubeMap.WriteTexel(cube, 0, face, x, y, new(basis[which], 0f, 0f));
                }
            }
        }

        var harmonics = SphericalHarmonicsL2.Project(cube);

        for (var index = 0; index < SphericalHarmonicsL2.Count; index++) {
            Assert.Equal(index == which ? 1f : 0f, harmonics.Coefficients[index].X, 2);
        }
    }

    /// <summary>
    ///     A sky lit on one side only lights a surface facing it and not one facing away — the
    ///     directional half of what a probe is for, and the check that the first-band coefficients
    ///     carry a direction rather than a magnitude.
    /// </summary>
    [Fact]
    public void ASkyBrightOnOneSideLightsTheSurfacesFacingIt() {
        var cube = Uniform(16, Vector3.Zero);

        for (var y = 0; y < 16; y++) {
            for (var x = 0; x < 16; x++) {
                CubeMap.WriteTexel(cube, 0, 2, x, y, Vector3.One);
            }
        }

        var harmonics = SphericalHarmonicsL2.Project(cube);

        // Face 2 is +Y, so a surface facing up sees the light and one facing down does not.
        var up = harmonics.Irradiance(Vector3.UnitY).Y;
        var down = harmonics.Irradiance(-Vector3.UnitY).Y;

        Assert.True(up > 0.3f, $"a surface facing the lit side received {up}");
        Assert.True(down < up * 0.1f, $"a surface facing away received {down} against {up}");
    }

    /// <summary>
    ///     Roughness zero is a mirror, and a mirror reflects what is there. Level zero is copied
    ///     rather than integrated, so this is exact rather than close.
    /// </summary>
    [Fact]
    public void TheFirstPrefilteredLevelIsTheEnvironmentItself() {
        var cube = Noise(8);

        var prefiltered = EnvironmentPrefilter.Specular(cube, levelCount: 4, samples: 16);

        Assert.Equal(cube.Level(0).ToArray(), prefiltered.Level(0).ToArray());
    }

    /// <summary>
    ///     <para>
    ///         Energy conservation, and the second exactly-checkable fact here. Convolving a uniform
    ///         environment with any lobe gives back the same uniform environment: every sample the
    ///         importance sampler takes returns the same radiance, so their weighted mean is that
    ///         radiance whatever the weights are.
    ///     </para>
    ///     <para>
    ///         It fails if the weights are not normalised or if the sampling drifts off the sphere.
    ///         It does <i>not</i> catch a missing horizon test, which was the claim a first version
    ///         of this comment made: with the same radiance coming back from every sample, negative
    ///         weights cancel in the numerator and the denominator alike and the ratio is unchanged.
    ///         PrefilteringNeverLeavesTheRangeTheEnvironmentOccupies is the test for that.
    ///     </para>
    /// </summary>
    [Fact]
    public void PrefilteringAUniformEnvironmentChangesNothingAtAnyRoughness() {
        var radiance = new Vector3(0.25f, 0.5f, 4f);

        var prefiltered = EnvironmentPrefilter.Specular(Uniform(8, radiance), levelCount: 4, samples: 32);

        for (var level = 0; level < prefiltered.LevelCount; level++) {
            var size = prefiltered.Levels[level].Width;

            for (var face = 0; face < CubeMap.Faces; face++) {
                for (var y = 0; y < size; y++) {
                    for (var x = 0; x < size; x++) {
                        var value = CubeMap.ReadTexel(prefiltered, level, face, x, y);

                        Assert.Equal(radiance.X, value.X, 4);
                        Assert.Equal(radiance.Y, value.Y, 4);
                        Assert.Equal(radiance.Z, value.Z, 4);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         A prefiltered value is a weighted average of the environment, and every weight is a
    ///         cosine above the horizon — so the answer can never leave the range the environment
    ///         itself occupies. An environment of nothing but zeros and ones must prefilter to values
    ///         between zero and one, at every roughness and in every direction.
    ///     </para>
    ///     <para>
    ///         This exists because dropping the horizon test — counting the samples whose reflected
    ///         direction goes below the surface, with the negative cosines that come with them — left
    ///         every other test here green. A uniform environment cannot catch it: the same radiance
    ///         comes back from every sample, so the negative weights cancel in the numerator and the
    ///         denominator alike and the ratio is unchanged. It takes an environment that varies.
    ///     </para>
    /// </summary>
    [Fact]
    public void PrefilteringNeverLeavesTheRangeTheEnvironmentOccupies() {
        const int size = 8;
        var cube = new TextureData(PixelFormat.Rgba32Float, size, size, levelCount: 1, faceCount: CubeMap.Faces);

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    // One face of sky, the rest black: the hardest case for a weighted mean, because
                    // most directions see nothing at all and a negative weight has nothing to cancel
                    // against.
                    CubeMap.WriteTexel(cube, 0, face, x, y, face == 2 ? Vector3.One : Vector3.Zero);
                }
            }
        }

        var prefiltered = EnvironmentPrefilter.Specular(cube, levelCount: 4, samples: 64);

        for (var level = 0; level < prefiltered.LevelCount; level++) {
            var levelSize = prefiltered.Levels[level].Width;

            for (var face = 0; face < CubeMap.Faces; face++) {
                for (var y = 0; y < levelSize; y++) {
                    for (var x = 0; x < levelSize; x++) {
                        var value = CubeMap.ReadTexel(prefiltered, level, face, x, y).X;
                        Assert.InRange(value, 0f, 1f);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Roughness runs from zero at the largest level to one at the smallest, and a shader picks
    ///     its level from a material's roughness by inverting exactly this.
    /// </summary>
    [Fact]
    public void RoughnessRunsFromZeroToOneAcrossTheChain() {
        Assert.Equal(0f, EnvironmentPrefilter.RoughnessOf(0, 5));
        Assert.Equal(0.5f, EnvironmentPrefilter.RoughnessOf(2, 5));
        Assert.Equal(1f, EnvironmentPrefilter.RoughnessOf(4, 5));

        // A single level is a mirror and there is no step to divide by.
        Assert.Equal(0f, EnvironmentPrefilter.RoughnessOf(0, 1));
    }

    [Fact]
    public void PrefilteringSomethingThatIsNotACubeMapIsRefused() {
        var failure = Assert.Throws<ArgumentException>(
            () => EnvironmentPrefilter.Specular(new(PixelFormat.Rgba16Float, 8, 8, levelCount: 1))
        );

        Assert.Contains("six faces", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentInAFormatWithNoHeadroomIsRefused() {
        var failure = Assert.Throws<ArgumentException>(
            () => EnvironmentPrefilter.Specular(new(PixelFormat.Rgba8UNorm, 8, 8, levelCount: 1, faceCount: 6))
        );

        Assert.Contains("radiance", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <para>
    ///         The third analytic fact. At roughness zero the GGX lobe collapses to a single
    ///         direction and the geometry term goes to one, so what the integral computes is
    ///         Schlick's Fresnel taken apart into the piece that scales F0 and the piece that
    ///         replaces it — 1 − (1 − cosθ)⁵ and (1 − cosθ)⁵.
    ///     </para>
    ///     <para>
    ///         Both are asserted against the closed form rather than against each other, so a wrong
    ///         geometry term, a wrong sampling distribution or a missing Jacobian all show up.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(0.9f)]
    [InlineData(1f)]
    public void AtRoughnessZeroTheTableIsSchlicksFresnelSplitInTwo(float cosine) {
        var (scale, bias) = BrdfLut.Integrate(cosine, roughness: 0f, samples: 64);
        var fresnel = MathF.Pow(1f - cosine, 5f);

        Assert.Equal(1f - fresnel, scale, 4);
        Assert.Equal(fresnel, bias, 4);
        Assert.Equal(1f, scale + bias, 4);
    }

    /// <summary>
    ///     And everywhere else the two together cannot exceed one, because a surface cannot reflect
    ///     more light than reaches it. A table that broke this would make rough metals glow.
    /// </summary>
    [Fact]
    public void TheTableNeverReflectsMoreLightThanArrives() {
        var table = BrdfLut.Generate(size: 32, samples: 128);

        for (var y = 0; y < 32; y++) {
            for (var x = 0; x < 32; x++) {
                var (scale, bias) = BrdfLut.Read(table, x, y);

                Assert.InRange(scale, 0f, 1f);
                Assert.InRange(bias, 0f, 1f);
                Assert.True(scale + bias <= 1.01f, $"cell {x},{y} sums to {scale + bias}");
            }
        }
    }

    /// <summary>
    ///     A rougher surface reflects less of what arrives head-on, because more of the lobe falls
    ///     below the horizon and is lost. The table has to be monotonic in that direction or a
    ///     material's specular response would brighten as it was roughened.
    /// </summary>
    [Fact]
    public void ARougherSurfaceReflectsLessOfWhatArrivesHeadOn() {
        var smooth = BrdfLut.Integrate(cosine: 1f, roughness: 0.1f, samples: 256);
        var rough = BrdfLut.Integrate(cosine: 1f, roughness: 0.9f, samples: 256);

        Assert.True(
            smooth.Scale + smooth.Bias > rough.Scale + rough.Bias,
            $"smooth reflects {smooth.Scale + smooth.Bias} and rough {rough.Scale + rough.Bias}"
        );
    }

    [Fact]
    public void TheTableIsTwoChannelsBecauseItOnlyEverHoldsTwoNumbers() =>
        Assert.Equal(PixelFormat.Rg16Float, BrdfLut.Generate(size: 4, samples: 8).Format);

    static TextureData Uniform(int size, Vector3 radiance) {
        var cube = new TextureData(PixelFormat.Rgba32Float, size, size, levelCount: 1, faceCount: CubeMap.Faces);

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    CubeMap.WriteTexel(cube, 0, face, x, y, radiance);
                }
            }
        }

        return cube;
    }

    static TextureData Noise(int size) {
        var cube = new TextureData(PixelFormat.Rgba32Float, size, size, levelCount: 1, faceCount: CubeMap.Faces);
        var value = 0;

        for (var face = 0; face < CubeMap.Faces; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    value = ((value * 1103515245) + 12345) & 0x7FFFFFFF;
                    CubeMap.WriteTexel(cube, 0, face, x, y, new(value % 1000 / 100f, x, y));
                }
            }
        }

        return cube;
    }

    static IEnumerable<Vector3> Directions() => [
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
        Vector3.Normalize(Vector3.One), Vector3.Normalize(new(0.3f, -0.8f, 0.5f))
    ];
}
