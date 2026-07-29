// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>
///     The payload every probe of an irradiance field holds, checked the way the nine-coefficient
///     version is: against answers that can be worked out on paper.
/// </summary>
public class SphericalHarmonicsL1Tests {
    /// <summary>Enough uniform directions that the sphere is covered evenly, off a Fibonacci spiral.</summary>
    static Vector3[] Sphere(int count) {
        const float GoldenAngle = 2.399963f;
        var directions = new Vector3[count];

        for (var index = 0; index < count; index++) {
            var z = 1f - (2f * (index + 0.5f) / count);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            var angle = index * GoldenAngle;

            directions[index] = new(radius * MathF.Cos(angle), radius * MathF.Sin(angle), z);
        }

        return directions;
    }

    /// <summary>
    ///     <b>The exact test, and the one that catches the missing π.</b> A uniform environment of
    ///     radiance L lights every surface with irradiance πL, so <c>Irradiance</c> — which is that
    ///     over π — has to come back as exactly L whichever way the surface faces.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.25f)]
    [InlineData(7f)]
    public void AUniformEnvironmentLightsEverythingEqually(float radiance) {
        var directions = Sphere(4096);
        var solidAngle = 4f * MathF.PI / directions.Length;
        var probe = SphericalHarmonicsL1.Zero;

        foreach (var direction in directions) {
            probe = probe.Accumulated(direction, new(radiance), solidAngle);
        }

        foreach (var normal in (Vector3[]) [
            new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), Vector3.Normalize(new(1, 2, -3))
        ]) {
            var lit = probe.Irradiance(normal);

            Assert.Equal(radiance, lit.X, 0.002f);
            Assert.Equal(radiance, lit.Y, 0.002f);
            Assert.Equal(radiance, lit.Z, 0.002f);
        }
    }

    /// <summary>
    ///     Light from one side lights that side. Obvious, and the thing a sign error in the linear
    ///     band breaks while leaving the constant term — and therefore an average-looking probe —
    ///     entirely intact.
    /// </summary>
    [Fact]
    public void LightFromOneSideLightsThatSide() {
        var probe = SphericalHarmonicsL1.Zero;

        probe = probe.Accumulated(new(0, 1, 0), new(1f), 1f);

        var facing = probe.Irradiance(new(0, 1, 0));
        var away = probe.Irradiance(new(0, -1, 0));
        var across = probe.Irradiance(new(1, 0, 0));

        Assert.True(facing.X > across.X, $"facing the light was {facing.X} and across it {across.X}");
        Assert.True(across.X > away.X, $"across the light was {across.X} and away from it {away.X}");
    }

    /// <summary>
    ///     Each axis lights its own axis, so a probe cannot have X and Z swapped and still pass —
    ///     which is the mistake that survives every symmetric test.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(-1f, 0f, 0f)]
    public void EachDirectionLightsItsOwnDirectionMost(float x, float y, float z) {
        var light = new Vector3(x, y, z);
        var probe = SphericalHarmonicsL1.Zero;

        probe = probe.Accumulated(light, new(1f), 1f);

        var best = probe.Irradiance(light).X;

        foreach (var other in (Vector3[]) [
            new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)
        ]) {
            if (other == light) {
                continue;
            }

            Assert.True(best > probe.Irradiance(other).X, $"{light} was not brightest along itself");
        }
    }

    /// <summary>
    ///     <b>The basis is the nine-coefficient one truncated, not a second derivation of it.</b> Two
    ///     derivations is how a probe and a skylight end up disagreeing about which way <c>+Y</c> is,
    ///     which is the same argument that made a reflection probe's cube faces come out of the
    ///     shadow projection rather than out of a table.
    /// </summary>
    [Fact]
    public void TheBasisIsTheWiderOneTruncated() {
        Span<float> four = stackalloc float[SphericalHarmonicsL1.Count];
        Span<float> nine = stackalloc float[SphericalHarmonicsL2.Count];

        foreach (var direction in Sphere(64)) {
            SphericalHarmonicsL1.Evaluate(direction, four);
            SphericalHarmonicsL2.Evaluate(direction, nine);

            for (var index = 0; index < SphericalHarmonicsL1.Count; index++) {
                Assert.Equal(nine[index], four[index], 6);
            }
        }
    }

    /// <summary>
    ///     And narrowing a nine-coefficient probe keeps the four it shares. Truncation is exact
    ///     because the basis is orthonormal — dropping a band drops that band and changes no other.
    /// </summary>
    [Fact]
    public void NarrowingAWiderProbeKeepsWhatTheyShare() {
        var directions = Sphere(2048);
        var solidAngle = 4f * MathF.PI / directions.Length;

        var narrow = SphericalHarmonicsL1.Zero;
        Span<float> basis = stackalloc float[SphericalHarmonicsL2.Count];
        var coefficients = new Vector3[SphericalHarmonicsL2.Count];

        foreach (var direction in directions) {
            var radiance = new Vector3(MathF.Max(0f, direction.Y));

            narrow = narrow.Accumulated(direction, radiance, solidAngle);
            SphericalHarmonicsL2.Evaluate(direction, basis);

            for (var index = 0; index < SphericalHarmonicsL2.Count; index++) {
                coefficients[index] += radiance * (basis[index] * solidAngle);
            }
        }

        var narrowed = SphericalHarmonicsL1.From(new SphericalHarmonicsL2(coefficients));

        Assert.Equal(narrow.L00.X, narrowed.L00.X, 4);
        Assert.Equal(narrow.L1m1.X, narrowed.L1m1.X, 4);
        Assert.Equal(narrow.L10.X, narrowed.L10.X, 4);
        Assert.Equal(narrow.L11.X, narrowed.L11.X, 4);
    }

    /// <summary>
    ///     <b>The property the whole scheme rests on.</b> The projection is linear, so blending two
    ///     probes' coefficients is the projection of the blend of what they saw — which is what lets
    ///     a field interpolate between probes at all, and what lets a probe converge toward a new
    ///     answer over frames instead of jumping to it.
    /// </summary>
    [Fact]
    public void BlendingCoefficientsIsBlendingWhatTheySaw() {
        var directions = Sphere(1024);
        var solidAngle = 4f * MathF.PI / directions.Length;

        var red = SphericalHarmonicsL1.Zero;
        var blue = SphericalHarmonicsL1.Zero;
        var mixedDirectly = SphericalHarmonicsL1.Zero;

        foreach (var direction in directions) {
            var a = new Vector3(1f, 0f, 0f) * MathF.Max(0f, direction.X);
            var b = new Vector3(0f, 0f, 1f) * MathF.Max(0f, -direction.Z);

            red = red.Accumulated(direction, a, solidAngle);
            blue = blue.Accumulated(direction, b, solidAngle);
            mixedDirectly = mixedDirectly.Accumulated(direction, Vector3.Lerp(a, b, 0.25f), solidAngle);
        }

        var mixedAfterwards = SphericalHarmonicsL1.Lerp(red, blue, 0.25f);
        var normal = Vector3.Normalize(new(1, 1, -1));

        Assert.Equal(mixedDirectly.Irradiance(normal).X, mixedAfterwards.Irradiance(normal).X, 5);
        Assert.Equal(mixedDirectly.Irradiance(normal).Z, mixedAfterwards.Irradiance(normal).Z, 5);
    }

    [Fact]
    public void ScalingScalesWhatItLights() {
        var probe = SphericalHarmonicsL1.Zero;

        probe = probe.Accumulated(new(0, 1, 0), new(2f), 1f);

        var normal = new Vector3(0, 1, 0);

        Assert.Equal(probe.Irradiance(normal).X * 3f, probe.Scaled(3f).Irradiance(normal).X, 5);
    }

    [Fact]
    public void AProbeThatHasSeenNothingLightsNothing() {
        Assert.Equal(Vector3.Zero, SphericalHarmonicsL1.Zero.Irradiance(new(0, 1, 0)));
        Assert.Equal(SphericalHarmonicsL1.Zero, default);
    }

    [Fact]
    public void TooLittleRoomForTheBasisIsRejected() {
        Assert.Throws<ArgumentException>(() => {
            Span<float> basis = stackalloc float[3];
            SphericalHarmonicsL1.Evaluate(new(0, 1, 0), basis);
        });
    }
}
