// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.ScreenProbes.Tests;

/// <summary>The mapping between directions and texels, held against its own geometry.</summary>
public class OctahedralMapTests {
    /// <summary>
    ///     The fold's convention, pinned texel by texel — the same convention as the Raven library's
    ///     <c>Math.EncodeOctahedral</c>, and the reason these are hand-written values rather than
    ///     roundtrips: a roundtrip passes with the axes swapped, the hemispheres exchanged, or both.
    /// </summary>
    [Fact]
    public void TheFoldIsTheRavenLibrarys() {
        Assert.Equal(new Vector2(0f, 0f), OctahedralMap.Encode(new(0f, 0f, 1f)));
        Assert.Equal(new Vector2(1f, 0f), OctahedralMap.Encode(new(1f, 0f, 0f)));
        Assert.Equal(new Vector2(0f, -1f), OctahedralMap.Encode(new(0f, -1f, 0f)));

        // A zero component still picks a hemisphere — positive, like SignedOne.
        Assert.Equal(new Vector2(1f, 1f), OctahedralMap.Encode(new(0f, 0f, -1f)));

        Same(new(0f, 0f, 1f), OctahedralMap.Decode(new(0f, 0f)));
        Same(new(1f, 0f, 0f), OctahedralMap.Decode(new(1f, 0f)));
        Same(new(0f, 0f, -1f), OctahedralMap.Decode(new(1f, 1f)));
        Same(new(0f, 0f, -1f), OctahedralMap.Decode(new(-1f, 1f)));
    }

    /// <summary>Every direction survives the square, both hemispheres included.</summary>
    [Fact]
    public void EncodeAndDecodeAreInverses() {
        foreach (var direction in Directions(256)) {
            Same(direction, OctahedralMap.Decode(OctahedralMap.Encode(direction)));
        }
    }

    /// <summary>A texel's centre direction lands back in that texel, at every resolution in use.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void ATexelsOwnDirectionLandsInIt(int resolution) {
        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                var texel = new Int2(x, y);

                Assert.Equal(texel, OctahedralMap.Texel(OctahedralMap.Direction(texel, resolution), resolution));
            }
        }
    }

    /// <summary>An odd resolution's centre texel looks straight up the z axis.</summary>
    [Fact]
    public void TheCentreTexelOfAnOddMapIsForward() {
        Same(new(0f, 0f, 1f), OctahedralMap.Direction(new(2, 2), 5));
    }

    /// <summary>
    ///     The texels of a map cover the sphere exactly once: their solid angles sum to 4π.
    /// </summary>
    /// <remarks>
    ///     This is the assertion the exact computation exists for. A Jacobian-at-the-centre
    ///     approximation misses 4π by whole percents at these resolutions, and every percent it
    ///     misses is a projection that comes out that much dark — the same failure the cube capture's
    ///     texel weights guarded against, on a different parameterisation.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void SolidAnglesTileTheSphere(int resolution) {
        var total = 0.0;

        foreach (var weight in OctahedralMap.SolidAngles(resolution).Span) {
            Assert.True(weight > 0f, "a texel of the sphere cannot stand for nothing");
            total += weight;
        }

        Assert.Equal(4.0 * Math.PI, total, 4.0 * Math.PI * 1e-5);
    }

    /// <summary>The map's mirror symmetries are the table's.</summary>
    [Fact]
    public void SolidAnglesAreSymmetric() {
        const int Resolution = 8;

        var weights = OctahedralMap.SolidAngles(Resolution).Span;

        for (var y = 0; y < Resolution; y++) {
            for (var x = 0; x < Resolution; x++) {
                var mirroredX = weights[(y * Resolution) + (Resolution - 1 - x)];
                var swapped = weights[(x * Resolution) + y];

                Assert.Equal(weights[(y * Resolution) + x], mirroredX, 1e-6f);
                Assert.Equal(weights[(y * Resolution) + x], swapped, 1e-6f);
            }
        }
    }

    /// <summary>
    ///     Weighted by its solid angle, the sphere of texel directions points nowhere — which is what
    ///     makes the projection of a constant come out constant.
    /// </summary>
    [Fact]
    public void WeightedDirectionsSumToNothing() {
        const int Resolution = 8;

        var weights = OctahedralMap.SolidAngles(Resolution).Span;
        var sum = Vector3.Zero;

        for (var y = 0; y < Resolution; y++) {
            for (var x = 0; x < Resolution; x++) {
                sum += OctahedralMap.Direction(new(x, y), Resolution) * weights[(y * Resolution) + x];
            }
        }

        Assert.True(sum.Length() < 1e-4f, $"the weighted directions sum to {sum}, so a constant sky would gain a direction");
    }

    [Fact]
    public void OutOfRangeIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => OctahedralMap.Direction(new(8, 0), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => OctahedralMap.Direction(new(0, -1), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => OctahedralMap.SolidAngle(new(0, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OctahedralMap.Texel(new(0f, 0f, 1f), 0));
    }

    /// <summary>A Fibonacci sphere — even coverage, no seed, both hemispheres.</summary>
    internal static IEnumerable<Vector3> Directions(int count) {
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));

        for (var index = 0; index < count; index++) {
            var y = 1f - (2f * (index + 0.5f) / count);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (y * y)));
            var angle = golden * index;

            yield return Vector3.Normalize(new(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle)));
        }
    }

    static void Same(Vector3 expected, Vector3 actual) {
        Assert.True(
            (expected - actual).Length() < 1e-5f,
            $"expected {expected} and got {actual}"
        );
    }
}
