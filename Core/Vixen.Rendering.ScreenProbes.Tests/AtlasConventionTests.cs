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
