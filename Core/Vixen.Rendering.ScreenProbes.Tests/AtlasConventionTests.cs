// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The conventions the CPU atlas and the shader have to share.</summary>
/// <remarks>
///     <para>
///         Both sides are checked against arithmetic separately and both pass — which is exactly the
///         situation in which a shared convention can be wrong on one side and nothing notices. The
///         irradiance field's <c>SamplingConventionTests</c> is the precedent, and the half-texel is
///         again the case: dropped in the shader alone, every direction shifts half a texel, every
///         closed form on either side still passes, and every probe's lighting is subtly rotated.
///     </para>
///     <para>
///         <b>This does not execute the shader.</b> Raven has no interpreter, so what is checked is
///         that a texel-accurate emulation of the shader's arithmetic agrees with
///         <see cref="OctahedralMap" /> and <see cref="ScreenProbeLayout" />, and that the text of the
///         shader says what the emulation assumes — the first is the content, the second is a guard on
///         the text.
///     </para>
/// </remarks>
public class AtlasConventionTests {
    static string Atlas => Read("ScreenProbeAtlas.rvn");

    static string Trace => Read("ScreenProbeTrace.rvn");

    static string Math => Read("Math.rvn");

    /// <summary>
    ///     The shader's texel-centre-to-direction arithmetic, walked in C#, reaches the direction
    ///     <see cref="OctahedralMap.Direction" /> answers — for every texel of the map.
    /// </summary>
    [Fact]
    public void TheShadersDirectionIsThisSides() {
        const int Resolution = 8;

        for (var y = 0; y < Resolution; y++) {
            for (var x = 0; x < Resolution; x++) {
                var expected = OctahedralMap.Direction(new(x, y), Resolution);
                var walked = AsTheShaderWould(x, y, Resolution);

                Assert.True(
                    (expected - walked).Length() < 1e-6f,
                    $"texel {x},{y}: this side says {expected} and the shader's arithmetic reaches {walked}"
                );
            }
        }
    }

    /// <summary>Both sides start a probe's map at probe times resolution.</summary>
    [Fact]
    public void BothSidesPutAProbesMapAtProbeTimesResolution() {
        var layout = new ScreenProbeLayout(new(64, 48));

        Assert.Equal(new Int2(24, 16), layout.AtlasOrigin(new(3, 2)));
        Assert.Contains("return int2(probe.x * resolution, probe.y * resolution)", Atlas, StringComparison.Ordinal);
    }

    /// <summary>Both sides put a map's texels eight across, and the workgroup is that number.</summary>
    /// <remarks>
    ///     Three copies of one number, each forced: the layout's default, the shader's constant, and
    ///     the compute group size — an invocation is a texel precisely when the group is the map.
    /// </remarks>
    [Fact]
    public void TheMapResolutionAgreesEverywhere() {
        Assert.Equal(8, new ScreenProbeLayout(new(64, 48)).MapResolution);
        Assert.Contains("const val MapResolution = 8", Atlas, StringComparison.Ordinal);
        Assert.Contains("[ComputeShader(8, 8, 1)]", Trace, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The half-texel, in the line that would silently drop it — a direction is the centre of its
    ///     texel, not its corner.
    /// </summary>
    [Fact]
    public void TheShaderPutsDirectionsAtTexelCentres() {
        Assert.Contains("(float(texel.x) + 0.5f) / float(resolution) * 2f - 1f", Atlas, StringComparison.Ordinal);
    }

    /// <summary>The shader folds through the library's one octahedral decode, not a copy.</summary>
    /// <remarks>
    ///     One fold in the library, whatever it is folding — the G-buffer normals and the probe maps
    ///     read through the same function, so they cannot disagree about which corner is the south
    ///     pole. The C# side's agreement with that fold is pinned by
    ///     <see cref="OctahedralMapTests.TheFoldIsTheRavenLibrarys" />.
    /// </remarks>
    [Fact]
    public void TheShaderReusesTheLibrarysFold() {
        Assert.Contains("Math.DecodeOctahedral(TexelCentre(texel, resolution))", Atlas, StringComparison.Ordinal);
        Assert.DoesNotContain("SignedOne", Atlas, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the library's fold is still the one <see cref="AsTheShaderWould" /> emulates — a change
    ///     to <c>Math.DecodeOctahedral</c> has to fail here, not silently strand the emulation.
    /// </summary>
    [Fact]
    public void TheLibrarysFoldIsTheOneEmulatedHere() {
        Assert.Contains("val z = 1f - abs(encoded.x) - abs(encoded.y)", Math, StringComparison.Ordinal);
        Assert.Contains("val x = SignedOne(encoded.x) * (1f - abs(encoded.y))", Math, StringComparison.Ordinal);
        Assert.Contains("val y = SignedOne(encoded.y) * (1f - abs(encoded.x))", Math, StringComparison.Ordinal);
        Assert.Contains("static func SignedOne(x: float): float => x >= 0f ? 1f : -1f", Math, StringComparison.Ordinal);
    }

    /// <summary>A gathered texel carries one in alpha, a buried probe's zero — the readback's validity.</summary>
    [Fact]
    public void AlphaIsTheValidityMark() {
        Assert.Contains("radianceAtlas.Store(atlasTexel, float4(0f, 0f, 0f, 0f))", Trace, StringComparison.Ordinal);
        Assert.Contains("radianceAtlas.Store(atlasTexel, float4(arriving, 1f))", Trace, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The resolve's basis constants are the ones this side's projection uses — raw, with no
    ///     cosine fold, because it projects radiance rather than evaluating irradiance.
    /// </summary>
    [Fact]
    public void TheResolvesBasisConstantsAreThisSides() {
        var resolve = Read("ScreenProbeResolve.rvn");

        Span<float> basis = stackalloc float[Vixen.Core.Imaging.SphericalHarmonicsL1.Count];

        Vixen.Core.Imaging.SphericalHarmonicsL1.Evaluate(new(0f, 1f, 0f), basis);

        Assert.Contains("const val Constant = 0.282095f", resolve, StringComparison.Ordinal);
        Assert.Contains("const val LinearBasis = 0.488603f", resolve, StringComparison.Ordinal);
        Assert.Equal(0.282095f, basis[0], 6);
        Assert.Equal(0.488603f, basis[1], 6);
    }

    /// <summary>
    ///     The upsample's lattice walk, emulated texel-accurately, lands on
    ///     <see cref="ScreenProbeLayout.Bilinear" />'s probes and weights — for every pixel of a
    ///     viewport whose grid clamps on both axes.
    /// </summary>
    /// <remarks>
    ///     The pixel-centre convention is the half-texel of this pass: the shader's
    ///     <c>uv · viewport</c> is <c>pixel + 0.5</c>, and its <c>latticeOrigin</c> is the host's
    ///     integer halving of the tile plus the same half — get either wrong and every pixel reads its
    ///     probes half a pixel off, which no closed form on either side notices.
    /// </remarks>
    [Fact]
    public void TheUpsamplesLatticeWalkIsThisSides() {
        var layout = new ScreenProbeLayout(new(33, 17));
        var origin = (layout.TileSize / 2) + 0.5f;

        Span<ScreenProbeTap> taps = stackalloc ScreenProbeTap[4];

        for (var y = 0; y < 17; y++) {
            for (var x = 0; x < 33; x++) {
                layout.Bilinear(new(x, y), taps);

                var ax = Along(x + 0.5f, origin, layout.TileSize, layout.GridSize.X);
                var ay = Along(y + 0.5f, origin, layout.TileSize, layout.GridSize.Y);

                Span<(Int2 Probe, float Weight)> walked = [
                    (new((int)ax.X, (int)ay.X), (1f - ax.Z) * (1f - ay.Z)),
                    (new((int)ax.Y, (int)ay.X), ax.Z * (1f - ay.Z)),
                    (new((int)ax.X, (int)ay.Y), (1f - ax.Z) * ay.Z),
                    (new((int)ax.Y, (int)ay.Y), ax.Z * ay.Z)
                ];

                foreach (var tap in taps) {
                    if (tap.Weight <= 0f) {
                        continue;
                    }

                    var matched = 0f;

                    foreach (var (probe, weight) in walked) {
                        if (probe == tap.Probe) {
                            matched += weight;
                        }
                    }

                    Assert.True(
                        MathF.Abs(matched - Weight(taps, tap.Probe)) < 1e-5f,
                        $"pixel {x},{y}: probe {tap.Probe} carries {Weight(taps, tap.Probe)} here and {matched} in the shader's walk"
                    );
                }
            }
        }

        // ScreenProbeUpsample.Along, written out — floor, clamp low, clamp high, fractional weight.
        static Vector3 Along(float pixelCentre, float latticeOrigin, float tileSize, float probes) {
            var continuous = (pixelCentre - latticeOrigin) / tileSize;
            var low = MathF.Floor(continuous);

            if (low < 0f) {
                return new(0f, 0f, 0f);
            }

            if (low >= probes - 1f) {
                return new(probes - 1f, probes - 1f, 0f);
            }

            return new(low, low + 1f, continuous - low);
        }

        static float Weight(ReadOnlySpan<ScreenProbeTap> taps, Int2 probe) {
            var total = 0f;

            foreach (var tap in taps) {
                if (tap.Probe == probe) {
                    total += tap.Weight;
                }
            }

            return total;
        }
    }

    /// <summary>The upsample's drift-prone lines, guarded by text like the others.</summary>
    [Fact]
    public void TheUpsampleSaysWhatTheWalkAssumes() {
        var upsample = Read("ScreenProbeUpsample.rvn");

        Assert.Contains("val pixelCentre = uv * viewport", upsample, StringComparison.Ordinal);
        Assert.Contains("val continuous = (pixelCentre - latticeOrigin) / tileSize", upsample, StringComparison.Ordinal);

        // The colour-major unpack, and the sky's reversed-depth test — the two lines whose wrong
        // versions pass every closed form.
        Assert.Contains("return IrradianceProbe(a.rgb, l1m1, l10, l11, a.a, r.a)", upsample, StringComparison.Ordinal);
        Assert.Contains("if (deviceDepth <= 0f)", upsample, StringComparison.Ordinal);
    }

    /// <summary>The shader's arithmetic, written out in the order the shader writes it.</summary>
    /// <remarks>
    ///     <c>TexelCentre</c> as the .rvn spells it, then <c>Math.DecodeOctahedral</c> as
    ///     <c>Math.rvn</c> spells it — including <c>SignedOne</c>'s tie at zero and
    ///     <c>SafeNormalize</c> rather than a bare normalise.
    /// </remarks>
    static Vector3 AsTheShaderWould(int x, int y, int resolution) {
        // val u = (float(texel.x) + 0.5f) / float(resolution) * 2f - 1f
        var u = ((x + 0.5f) / resolution * 2f) - 1f;
        var v = ((y + 0.5f) / resolution * 2f) - 1f;

        // val z = 1f - abs(encoded.x) - abs(encoded.y)
        var z = 1f - MathF.Abs(u) - MathF.Abs(v);

        Vector3 direction;

        if (z >= 0f) {
            direction = new(u, v, z);
        } else {
            direction = new(
                (u >= 0f ? 1f : -1f) * (1f - MathF.Abs(v)),
                (v >= 0f ? 1f : -1f) * (1f - MathF.Abs(u)),
                z
            );
        }

        // SafeNormalize: v * rsqrt(dot(v, v)) above the epsilon, which every texel centre is.
        var lengthSquared = Vector3.Dot(direction, direction);

        Assert.True(lengthSquared > 1e-12f, "a texel centre decoded to nothing, which the map cannot do");

        return direction * (1f / MathF.Sqrt(lengthSquared));
    }

    static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", name));
}
